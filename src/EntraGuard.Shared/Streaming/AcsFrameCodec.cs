using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EntraGuard.Shared.Streaming;

/// <summary>A decoded frame from the ACS bidirectional audio WebSocket.</summary>
public abstract record AcsFrame;

/// <summary>
/// One 20 ms slice of PCM audio from a call participant.
///
/// In unmixed mode each participant gets their own channel, so
/// <paramref name="ParticipantRawId"/> is what lets EntraGuard attribute coaching language
/// to the caller rather than the victim. That attribution comes from the media stream
/// itself, not from a model inference that could be wrong.
/// </summary>
public sealed record AudioDataFrame(
    ReadOnlyMemory<byte> Pcm,
    string ParticipantRawId,
    DateTimeOffset? Timestamp,
    bool IsSilent) : AcsFrame;

/// <summary>Stream format, sent once when streaming starts.</summary>
public sealed record AudioMetadataFrame(
    string SubscriptionId,
    string Encoding,
    int SampleRate,
    int Channels,
    int Length) : AcsFrame;

/// <summary>A keypad tone. Present only when <c>EnableDtmfTones</c> is set on the stream.</summary>
public sealed record DtmfFrame(string Tone) : AcsFrame;

/// <summary>
/// A frame that could not be decoded, or a kind this build does not recognise.
///
/// Represented rather than thrown: an exception on the media socket tears down a live
/// call, and Azure adding a new frame kind must not be a fatal event.
/// </summary>
public sealed record UnknownFrame(string Reason) : AcsFrame;

/// <summary>
/// Reassembles WebSocket messages that arrive as multiple fragments.
///
/// <see cref="System.Net.WebSockets.WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/>
/// fills the caller's buffer and sets <c>EndOfMessage</c> false when more remains. Decoding
/// each fragment independently — as the widely-copied ACS sample does — truncates larger
/// frames and corrupts any UTF-8 sequence straddling a fragment boundary. Buffering bytes
/// until the message completes, then decoding once, avoids both.
///
/// Not thread-safe: one instance per socket, which is the natural ownership anyway.
/// </summary>
public sealed class AcsMessageAssembler
{
    private readonly ArrayBufferWriter<byte> _buffer = new(initialCapacity: 4096);

    /// <summary>
    /// Append a received fragment.
    /// </summary>
    /// <returns>
    /// The complete message when <paramref name="endOfMessage"/> is true; otherwise null.
    /// </returns>
    public string? Append(ReadOnlySpan<byte> fragment, bool endOfMessage)
    {
        _buffer.Write(fragment);

        if (!endOfMessage)
        {
            return null;
        }

        // Decode once, over the whole message, so multi-byte characters split across
        // fragments reassemble correctly.
        var message = Encoding.UTF8.GetString(_buffer.WrittenSpan);
        _buffer.Clear();
        return message;
    }

    /// <summary>Discard any partial message. Used when a socket is reset mid-frame.</summary>
    public void Reset() => _buffer.Clear();
}

/// <summary>
/// Encodes and decodes the ACS media-streaming wire format.
///
/// Hand-rolled against the documented protocol rather than delegating to the SDK's
/// <c>StreamingData.Parse</c>, so the hot path stays allocation-light and — more
/// importantly — so this layer is unit-testable without an ACS connection. Every frame
/// shape here is covered by <c>AcsFrameCodecTests</c>.
/// </summary>
public static class AcsFrameCodec
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>
    /// Decode one complete WebSocket text message.
    /// Never throws: anything unrecognised becomes <see cref="UnknownFrame"/>.
    /// </summary>
    public static AcsFrame Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UnknownFrame("Empty message.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kindElement) ||
                kindElement.ValueKind != JsonValueKind.String)
            {
                return new UnknownFrame("Message has no 'kind' discriminator.");
            }

            return kindElement.GetString() switch
            {
                "AudioData" => DecodeAudioData(root),
                "AudioMetadata" => DecodeAudioMetadata(root),
                "DtmfData" => DecodeDtmf(root),
                var kind => new UnknownFrame($"Unhandled frame kind '{kind}'."),
            };
        }
        catch (JsonException ex)
        {
            return new UnknownFrame($"Malformed JSON: {ex.Message}");
        }
    }

    private static AcsFrame DecodeAudioData(JsonElement root)
    {
        if (!root.TryGetProperty("audioData", out var audio) || audio.ValueKind != JsonValueKind.Object)
        {
            return new UnknownFrame("AudioData frame has no 'audioData' payload.");
        }

        if (!audio.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
        {
            return new UnknownFrame("AudioData payload has no 'data' field.");
        }

        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(data.GetString() ?? string.Empty);
        }
        catch (FormatException)
        {
            return new UnknownFrame("AudioData 'data' is not valid base64.");
        }

        var participant = audio.TryGetProperty("participantRawID", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;

        DateTimeOffset? timestamp = audio.TryGetProperty("timestamp", out var t)
            && t.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(t.GetString(), out var parsed)
                ? parsed
                : null;

        var silent = audio.TryGetProperty("silent", out var s)
            && s.ValueKind == JsonValueKind.True;

        return new AudioDataFrame(pcm, participant, timestamp, silent);
    }

    private static AcsFrame DecodeAudioMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("audioMetadata", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            return new UnknownFrame("AudioMetadata frame has no 'audioMetadata' payload.");
        }

        return new AudioMetadataFrame(
            SubscriptionId: ReadString(meta, "subscriptionId"),
            Encoding: ReadString(meta, "encoding"),
            SampleRate: ReadInt(meta, "sampleRate"),
            Channels: ReadInt(meta, "channels"),
            Length: ReadInt(meta, "length"));
    }

    private static AcsFrame DecodeDtmf(JsonElement root)
    {
        if (!root.TryGetProperty("dtmfData", out var dtmf) || dtmf.ValueKind != JsonValueKind.Object)
        {
            return new UnknownFrame("DtmfData frame has no 'dtmfData' payload.");
        }

        return new DtmfFrame(ReadString(dtmf, "data"));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Encode PCM for playback back into the live call.
    ///
    /// This is what makes EntraGuard interventional rather than observational: the
    /// synthesised warning travels back down the same socket the call audio arrives on,
    /// so the user hears it while the attacker is still talking.
    /// </summary>
    public static string EncodeOutboundAudio(ReadOnlySpan<byte> pcm)
    {
        using var stream = new MemoryStream(pcm.Length * 2);
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "AudioData");
            writer.WriteStartObject("audioData");
            writer.WriteBase64String("data", pcm);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Tell ACS to stop playing queued outbound audio.
    /// Used when a call ends mid-warning, so the buffer does not outlive the call.
    /// </summary>
    public static string EncodeStopAudio() =>
        """{"kind":"StopAudio","stopAudio":{}}""";
}
