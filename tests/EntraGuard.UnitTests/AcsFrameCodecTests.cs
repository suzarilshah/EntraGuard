using System.Text;
using EntraGuard.Shared.Streaming;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Wire-format handling for the ACS bidirectional audio stream.
///
/// The widely-copied ACS sample decodes each WebSocket receive with
/// <c>Encoding.UTF8.GetString(buffer).TrimEnd('\0')</c> against a fixed 2 KB buffer. That
/// silently truncates any frame larger than the buffer and drops every fragment after the
/// first, which surfaces later as unexplained gaps in the transcript rather than as an
/// error. These tests exist to keep that bug out.
/// </summary>
public class AcsFrameCodecTests
{
    // ── Message assembly ────────────────────────────────────────────────────

    [Fact]
    public void SingleFragmentMessage_IsReturnedImmediately()
    {
        var assembler = new AcsMessageAssembler();
        var payload = Encoding.UTF8.GetBytes("""{"kind":"AudioMetadata"}""");

        var message = assembler.Append(payload, endOfMessage: true);

        message.Should().Be("""{"kind":"AudioMetadata"}""");
    }

    [Fact]
    public void FragmentedMessage_ReturnsNullUntilComplete()
    {
        var assembler = new AcsMessageAssembler();

        assembler.Append(Encoding.UTF8.GetBytes("""{"kind":"Aud"""), endOfMessage: false)
            .Should().BeNull("a partial frame is not yet parseable");

        assembler.Append(Encoding.UTF8.GetBytes("""ioMetadata"}"""), endOfMessage: true)
            .Should().Be("""{"kind":"AudioMetadata"}""");
    }

    [Fact]
    public void LargeMultiFragmentMessage_IsReassembledIntact()
    {
        // A 24 kHz frame is 960 bytes of PCM → ~1280 base64 chars, and ACS wraps that in
        // JSON with a timestamp and participant ID. Comfortably past a 2 KB read buffer.
        var assembler = new AcsMessageAssembler();
        var pcm = new byte[960];
        Random.Shared.NextBytes(pcm);
        var original = AcsFrameCodec.EncodeOutboundAudio(pcm);

        var bytes = Encoding.UTF8.GetBytes(original);
        const int chunkSize = 2048;
        string? assembled = null;

        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var isLast = offset + length >= bytes.Length;
            assembled = assembler.Append(bytes.AsSpan(offset, length), isLast);
        }

        assembled.Should().Be(original, "every fragment must survive reassembly");
    }

    [Fact]
    public void AssemblerResetsBetweenMessages()
    {
        var assembler = new AcsMessageAssembler();

        assembler.Append(Encoding.UTF8.GetBytes("first"), endOfMessage: true).Should().Be("first");
        assembler.Append(Encoding.UTF8.GetBytes("second"), endOfMessage: true).Should().Be("second",
            "a completed message must not leak into the next one");
    }

    [Fact]
    public void MultibyteCharactersSplitAcrossFragments_SurviveReassembly()
    {
        // Participant display names and transcript echoes are not ASCII-only. Decoding
        // each fragment separately would corrupt any UTF-8 sequence split across a boundary.
        var assembler = new AcsMessageAssembler();
        var original = """{"kind":"AudioData","name":"Zoë Ünicode 日本語"}""";
        var bytes = Encoding.UTF8.GetBytes(original);

        var split = bytes.Length / 2;
        assembler.Append(bytes.AsSpan(0, split), endOfMessage: false);
        var assembled = assembler.Append(bytes.AsSpan(split), endOfMessage: true);

        assembled.Should().Be(original);
    }

    // ── Inbound decoding ────────────────────────────────────────────────────

    [Fact]
    public void DecodesAudioDataFrame()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5 };
        var json = $$"""
        {
          "kind": "AudioData",
          "audioData": {
            "data": "{{Convert.ToBase64String(pcm)}}",
            "timestamp": "2026-08-05T10:15:30.500Z",
            "participantRawID": "8:acs:aaaa-bbbb_cccc",
            "silent": false
          }
        }
        """;

        var frame = AcsFrameCodec.Decode(json);

        var audio = frame.Should().BeOfType<AudioDataFrame>().Subject;
        audio.Pcm.ToArray().Should().Equal(pcm);
        audio.ParticipantRawId.Should().Be("8:acs:aaaa-bbbb_cccc");
        audio.IsSilent.Should().BeFalse();
    }

    [Fact]
    public void DecodesSilentFrame()
    {
        var json = """
        {"kind":"AudioData","audioData":{"data":"AAAA","participantRawID":"8:acs:x","silent":true}}
        """;

        var frame = AcsFrameCodec.Decode(json);

        frame.Should().BeOfType<AudioDataFrame>().Which.IsSilent.Should().BeTrue(
            "silent frames are skipped rather than pushed to Speech, to avoid paying for silence");
    }

    [Fact]
    public void DecodesAudioMetadataFrame()
    {
        var json = """
        {
          "kind": "AudioMetadata",
          "audioMetadata": {
            "subscriptionId": "sub-1",
            "encoding": "PCM",
            "sampleRate": 24000,
            "channels": 1,
            "length": 960
          }
        }
        """;

        var frame = AcsFrameCodec.Decode(json);

        var metadata = frame.Should().BeOfType<AudioMetadataFrame>().Subject;
        metadata.SampleRate.Should().Be(24000);
        metadata.Channels.Should().Be(1);
        metadata.Encoding.Should().Be("PCM");
    }

    [Fact]
    public void DecodesDtmfFrame()
    {
        // Keypad entry during an authentication call is a strong signal on its own: it is
        // how a victim reads back an OTP without ever saying the digits aloud.
        var json = """{"kind":"DtmfData","dtmfData":{"data":"7"}}""";

        var frame = AcsFrameCodec.Decode(json);

        frame.Should().BeOfType<DtmfFrame>().Which.Tone.Should().Be("7");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"kind":"SomethingAzureAddedLater"}""")]
    [InlineData("""{"kind":"AudioData"}""")]
    [InlineData("""{"kind":"AudioData","audioData":{"data":"!!!not base64!!!"}}""")]
    public void MalformedOrUnknownFrames_DecodeToUnknown_WithoutThrowing(string json)
    {
        // A parse exception on the media socket tears down the call. An unrecognised frame
        // must degrade to "ignore this one" — including frame kinds Azure adds in future.
        var act = () => AcsFrameCodec.Decode(json);

        act.Should().NotThrow();
        AcsFrameCodec.Decode(json).Should().BeOfType<UnknownFrame>();
    }

    // ── Outbound encoding ───────────────────────────────────────────────────

    [Fact]
    public void EncodesOutboundAudioInAcsWireFormat()
    {
        var pcm = new byte[] { 10, 20, 30 };

        var json = AcsFrameCodec.EncodeOutboundAudio(pcm);

        json.Should().Contain("\"kind\":\"AudioData\"");
        json.Should().Contain(Convert.ToBase64String(pcm));
    }

    [Fact]
    public void OutboundAudioRoundTripsThroughTheDecoder()
    {
        var pcm = new byte[960];
        Random.Shared.NextBytes(pcm);

        var decoded = AcsFrameCodec.Decode(AcsFrameCodec.EncodeOutboundAudio(pcm));

        decoded.Should().BeOfType<AudioDataFrame>().Which.Pcm.ToArray().Should().Equal(pcm);
    }

    [Fact]
    public void EncodesStopAudioSignal()
    {
        // Sent to cut off an in-flight warning when the call ends mid-playback.
        AcsFrameCodec.EncodeStopAudio().Should().Contain("StopAudio");
    }
}
