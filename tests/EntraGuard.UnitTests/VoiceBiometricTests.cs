using EntraGuard.MediaService.Agents;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The parts of voice verification that can be tested without a model or a phone call:
/// the sample-rate conversion the model depends on, and the decision it feeds.
/// </summary>
public sealed class VoiceBiometricTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), samples[i]);
        }
        return bytes;
    }

    [Fact]
    public void ThreeInputSamplesBecomeTwo()
    {
        // 24 kHz to 16 kHz is exactly 3:2. Anything else drifts over a call — and audio at
        // the wrong speed shifts formants, so a genuine user scores like an impostor and it
        // reads as the biometrics being inaccurate rather than as a bug.
        var input = Pcm(new short[30]);
        var output = AudioResampler.Downsample24To16(input);

        Assert.Equal(20 * 2, output.Length);
    }

    [Fact]
    public void DurationIsPreservedAcrossTheConversion()
    {
        // One second at 24 kHz must still be one second at 16 kHz.
        var oneSecond = Pcm(new short[AudioResampler.AcsSampleRate]);
        var output = AudioResampler.Downsample24To16(oneSecond);

        Assert.Equal(1.0, AudioResampler.Seconds(output.Length), precision: 3);
    }

    [Fact]
    public void AConstantSignalSurvivesUnchanged()
    {
        // Averaging must not attenuate. A resampler that quietly halves amplitude produces
        // embeddings from something the speaker never sounded like.
        var input = Pcm(Enumerable.Repeat((short)1000, 30).ToArray());
        var output = AudioResampler.Downsample24To16(input);

        for (var i = 0; i < output.Length; i += 2)
        {
            Assert.Equal(1000, BitConverter.ToInt16(output.AsSpan(i)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]   // one sample, not a whole group
    [InlineData(5)]   // two samples plus a stray byte
    public void PartialGroupsAndOddBytesProduceNoOutputRatherThanGarbage(int byteCount)
    {
        // Frames arrive every 20 ms and a buffer can end mid-group. Emitting a
        // half-constructed sample would put a click into the audio the model scores.
        Assert.Empty(AudioResampler.Downsample24To16(new byte[byteCount]));
    }

    [Fact]
    public void SilenceIsReportedAsUnvoiced()
    {
        // A caller who says nothing must not produce a confident embedding of a quiet room.
        Assert.Equal(0, AudioResampler.VoicedRatio(Pcm(new short[1000])));
    }

    [Fact]
    public void SpeechIsReportedAsVoiced()
    {
        var loud = Pcm(Enumerable.Repeat((short)8000, 1000).ToArray());
        Assert.Equal(1.0, AudioResampler.VoicedRatio(loud));
    }

    // ── Decision bands ──────────────────────────────────────────────────────

    [Fact]
    public void AStrongScoreMatches()
    {
        var decision = VoiceThresholds.Evaluate(0.72, seconds: 8, enforce: true);

        Assert.Equal(VoiceOutcome.Match, decision.Outcome);
        Assert.False(decision.RequiresStepUp);
    }

    [Fact]
    public void AMiddlingScoreEscalatesRatherThanGuessing()
    {
        // The reason there are three bands. Over a phone codec the genuine and impostor
        // distributions overlap, and forcing that overlap into a yes or a no either admits
        // a stranger or locks out the account's owner.
        var decision = VoiceThresholds.Evaluate(0.45, seconds: 8, enforce: true);

        Assert.Equal(VoiceOutcome.Inconclusive, decision.Outcome);
        Assert.True(decision.RequiresStepUp);
    }

    [Fact]
    public void AMismatchStepsUpAndDoesNotDenyOnItsOwn()
    {
        // Voice is never the thing that refuses a person. It asks for a stronger factor.
        var decision = VoiceThresholds.Evaluate(0.10, seconds: 8, enforce: true);

        Assert.Equal(VoiceOutcome.Mismatch, decision.Outcome);
        Assert.True(decision.RequiresStepUp);
    }

    [Fact]
    public void ObserveModeScoresButNeverActs()
    {
        // Thresholds are calibrated against real calls before they are allowed to bite.
        // Until then every band must be inert, including an outright mismatch.
        foreach (var score in new[] { 0.9, 0.45, 0.05 })
        {
            var decision = VoiceThresholds.Evaluate(score, seconds: 8, enforce: false);

            Assert.False(decision.RequiresStepUp);
            Assert.Contains("Observing only", decision.Reason);
        }
    }

    [Fact]
    public void TooLittleSpeechIsNotAssessedRatherThanScoredBadly()
    {
        // Two seconds of "yes" yields a number that looks as authoritative as one from ten
        // seconds and is far less reliable. Reporting it as a mismatch would refuse people
        // for answering briefly.
        var decision = VoiceThresholds.Evaluate(0.2, seconds: 1.5, enforce: true);

        Assert.Equal(VoiceOutcome.NotAssessed, decision.Outcome);
        Assert.False(decision.RequiresStepUp);
    }

    [Fact]
    public void NoProfileNeverBlocksAnyone()
    {
        // Most users will never enrol. Absence of a voiceprint must be invisible to them.
        var decision = VoiceThresholds.Evaluate(null, seconds: 30, enforce: true);

        Assert.Equal(VoiceOutcome.NotAssessed, decision.Outcome);
        Assert.False(decision.RequiresStepUp);
        Assert.Null(decision.Score);
    }

    // ── Template maths ──────────────────────────────────────────────────────

    [Fact]
    public void IdenticalEmbeddingsScoreOne()
    {
        var e = new[] { 0.3, -0.4, 0.5, 0.7 };
        Assert.Equal(1.0, VoiceprintClient.Similarity(e, e)!.Value, precision: 6);
    }

    [Fact]
    public void OppositeEmbeddingsScoreMinusOne()
    {
        var a = new[] { 0.3, -0.4, 0.5, 0.7 };
        var b = a.Select(v => -v).ToArray();

        Assert.Equal(-1.0, VoiceprintClient.Similarity(a, b)!.Value, precision: 6);
    }

    [Fact]
    public void MismatchedDimensionsScoreNothingRatherThanGuessing()
    {
        // Two different model versions would silently produce nonsense comparisons.
        Assert.Null(VoiceprintClient.Similarity(new[] { 1.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }));
    }

    [Fact]
    public void ATemplateIsAlwaysUnitLength()
    {
        // Everything downstream assumes it. A template of the wrong magnitude scores
        // uniformly low against everyone — including the person it belongs to.
        var template = VoiceprintClient.Average(
        [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
        ]);

        var length = Math.Sqrt(template.Sum(v => v * v));
        Assert.Equal(1.0, length, precision: 9);
    }

    [Fact]
    public void ATemplateSitsBetweenTheUtterancesItWasBuiltFrom()
    {
        var a = new[] { 1.0, 0.0 };
        var b = new[] { 0.0, 1.0 };
        var template = VoiceprintClient.Average([a, b]);

        // Equidistant from both, and closer to each than they are to one another.
        Assert.Equal(
            VoiceprintClient.Similarity(template, a)!.Value,
            VoiceprintClient.Similarity(template, b)!.Value,
            precision: 9);

        Assert.True(VoiceprintClient.Similarity(template, a)!.Value
                    > VoiceprintClient.Similarity(a, b)!.Value);
    }

    // ── Echo rejection ──────────────────────────────────────────────────────
    //
    // Mirrors VerificationCoordinator.IsEchoOf. The prompt comes back on the caller's own
    // channel and is transcribed as their speech; judging it spent both attempts in eight
    // seconds on words the user never said.

    private static bool IsEchoOf(string question, string spoken)
    {
        var asked = question.ToLowerInvariant()
            .Split([' ', ',', '?', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

        var said = spoken.ToLowerInvariant()
            .Split([' ', ',', '?', '.', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToArray();

        if (said.Length == 0 || asked.Count == 0) return false;
        return said.Count(asked.Contains) * 2 >= said.Length;
    }

    private const string Question = "Which town, city, or country were you in the last time you signed in?";

    [Theory]
    [InlineData("OK, which town, city or?")]
    [InlineData("which town city or country were you in")]
    [InlineData("the last time you signed in")]
    public void TheQuestionComingBackIsNotAnAnswer(string spoken)
    {
        Assert.True(IsEchoOf(Question, spoken));
    }

    [Theory]
    [InlineData("Petaling Jaya")]
    [InlineData("Malaysia")]
    [InlineData("I was in Selangor")]
    [InlineData("Kuala Lumpur I think")]
    public void RealAnswersAreNotMistakenForEchoes(string spoken)
    {
        // The guard must not eat genuine answers — that would refuse people silently, which
        // is worse than the problem it fixes.
        Assert.False(IsEchoOf(Question, spoken));
    }

    [Fact]
    public void AnAnswerSharingOneWordWithTheQuestionSurvives()
    {
        // "town" appears in both. A genuine answer shares the odd word by chance; it does
        // not consist of them.
        Assert.False(IsEchoOf(Question, "my home town is Ipoh"));
    }
}

/// <summary>
/// What the voiceprint buffer is allowed to contain.
///
/// A genuine enrolled speaker scored 0.0004 and 0.071 on real calls, against a calibration
/// range of 0.65 to 0.88 for real speakers. The model was not wrong: most of what it was given
/// genuinely was a different speaker — EntraGuard's own synthesised prompts, echoing back on
/// the callee's channel, which is mapped to the protected user.
/// </summary>
public class VoiceCaptureTests
{
    private static CallSession SessionWithUser(string rawId)
    {
        var session = new CallSession { SessionId = "s1", StartedAt = DateTimeOffset.UtcNow };
        session.MapParticipant(rawId, SpeakerRole.ProtectedUser);
        return session;
    }

    /// <summary>One second of 24 kHz PCM16, which is what ACS sends.</summary>
    private static byte[] Frame(int seconds = 1) =>
        new byte[AudioResampler.AcsSampleRate * 2 * seconds];

    [Fact]
    public void Nothing_is_kept_until_capture_is_opened()
    {
        // Starts closed. Everything before the first question is prompt and echo.
        var agent = new VoiceBiometricAgent(SessionWithUser("8:acs:user"), NullLogger.Instance);

        agent.Offer("8:acs:user", Frame(), AudioResampler.AcsSampleRate);

        agent.Seconds.Should().Be(0);
    }

    [Fact]
    public void Audio_offered_while_EntraGuard_speaks_is_dropped()
    {
        var agent = new VoiceBiometricAgent(SessionWithUser("8:acs:user"), NullLogger.Instance)
        {
            Accepting = true,
        };

        agent.Offer("8:acs:user", Frame(), AudioResampler.AcsSampleRate);
        var afterSpeech = agent.Seconds;

        agent.Accepting = false;
        agent.Offer("8:acs:user", Frame(4), AudioResampler.AcsSampleRate);

        agent.Seconds.Should().Be(afterSpeech, "four seconds of our own prompt must not count");
    }

    [Fact]
    public void The_cap_keeps_the_newest_audio_not_the_oldest()
    {
        // The user's answers arrive LAST. Dropping new frames once full discarded exactly the
        // speech this is meant to score.
        var agent = new VoiceBiometricAgent(SessionWithUser("8:acs:user"), NullLogger.Instance)
        {
            Accepting = true,
        };

        for (var i = 0; i < 70; i++)
        {
            agent.Offer("8:acs:user", Frame(), AudioResampler.AcsSampleRate);
        }

        agent.Seconds.Should().BeApproximately(60, 0.5, "the buffer is bounded");

        // A distinctive final second must survive the trim.
        var loud = new byte[AudioResampler.AcsSampleRate * 2];
        for (var i = 0; i < loud.Length; i += 2) { loud[i] = 0x11; loud[i + 1] = 0x22; }

        agent.Offer("8:acs:user", loud, AudioResampler.AcsSampleRate);

        var snapshot = agent.Snapshot();
        snapshot.Should().NotBeEmpty();
        snapshot[^1].Should().NotBe(0, "the most recent speech must still be there");
    }

    [Fact]
    public void Only_the_protected_user_is_ever_buffered()
    {
        var agent = new VoiceBiometricAgent(SessionWithUser("8:acs:user"), NullLogger.Instance)
        {
            Accepting = true,
        };

        agent.Offer("8:acs:someone-else", Frame(), AudioResampler.AcsSampleRate);

        agent.Seconds.Should().Be(0);
    }
}
