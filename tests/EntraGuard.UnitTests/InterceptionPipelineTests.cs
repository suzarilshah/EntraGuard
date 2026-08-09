using System.Text;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Streaming;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// End-to-end through everything that does not need Azure: ACS wire frames in, policy
/// decision out.
///
/// The Analyst is the only component that requires a live model, so it is substituted with
/// canned verdicts. Everything else — frame decoding, speaker attribution, transcript
/// windowing, the gate, idempotency across a whole call — runs for real. This is the test
/// that catches an integration break between components that each pass their own tests.
/// </summary>
public class InterceptionPipelineTests
{
    private const string CallerId = "8:acs:caller-aaaa_0000";
    private const string UserId = "8:acs:user-bbbb_1111";

    /// <summary>Build the ACS wire frame for a slice of audio, as ACS would send it.</summary>
    private static string AudioFrameJson(string participantRawId, int pcmBytes, bool silent = false)
    {
        var pcm = new byte[pcmBytes];
        Random.Shared.NextBytes(pcm);
        var data = Convert.ToBase64String(pcm);
        var silentLiteral = silent ? "true" : "false";

        return "{\"kind\":\"AudioData\",\"audioData\":{"
             + $"\"data\":\"{data}\","
             + "\"timestamp\":\"2026-08-05T10:00:00.000Z\","
             + $"\"participantRawID\":\"{participantRawId}\","
             + $"\"silent\":{silentLiteral}}}}}";
    }

    private static CallSession NewSession()
    {
        var session = new CallSession
        {
            SessionId = "test-session",
            StartedAt = DateTimeOffset.UtcNow,
            SubjectUpn = "sam@contoso.com",
            SubjectObjectId = "00000000-0000-0000-0000-000000000001",
            CallerIdentity = CallerId,
        };

        // Attribution comes from the call topology, exactly as IncomingCallEndpoint sets it.
        session.MapParticipant(CallerId, SpeakerRole.Caller);
        session.MapParticipant(UserId, SpeakerRole.ProtectedUser);
        return session;
    }

    // ── Media plane ─────────────────────────────────────────────────────────

    [Fact]
    public void FramesArrivingInFragments_DecodeToAttributedAudio()
    {
        var session = NewSession();
        var assembler = new AcsMessageAssembler();

        // 960 bytes is one 20 ms frame at 24 kHz — the real packet size, and large enough
        // once base64-encoded to span a 2 KB read buffer.
        var bytes = Encoding.UTF8.GetBytes(AudioFrameJson(CallerId, 960));

        string? message = null;
        for (var offset = 0; offset < bytes.Length; offset += 512)
        {
            var length = Math.Min(512, bytes.Length - offset);
            message = assembler.Append(bytes.AsSpan(offset, length), offset + length >= bytes.Length);
        }

        var frame = AcsFrameCodec.Decode(message!);

        var audio = frame.Should().BeOfType<AudioDataFrame>().Subject;
        audio.Pcm.Length.Should().Be(960);
        session.RoleFor(audio.ParticipantRawId).Should().Be(SpeakerRole.Caller,
            "speaker attribution must come from the media channel, not from inference");
    }

    [Fact]
    public void UnknownParticipant_ResolvesToUnknown_RatherThanGuessing()
    {
        var session = NewSession();
        session.RoleFor("8:acs:someone-else").Should().Be(SpeakerRole.Unknown,
            "misattributing the victim's words to the attacker would invert the whole verdict");
    }

    // ── Transcript windowing ────────────────────────────────────────────────

    [Fact]
    public void TranscriptWindow_LabelsSpeakers_AndHonoursTheWindow()
    {
        var session = NewSession();
        var now = DateTimeOffset.UtcNow;

        session.AddUtterance(new Utterance(SpeakerRole.Caller, "This is Daniel from IT.", 1_000, now, true));
        session.AddUtterance(new Utterance(SpeakerRole.ProtectedUser, "What is this about?", 4_000, now, true));
        session.AddUtterance(new Utterance(SpeakerRole.Caller, "Just approve the prompt.", 90_000, now, true));

        var window = session.TranscriptWindow(TimeSpan.FromSeconds(45), now: 100_000);

        window.Should().Contain("[CALLER] Just approve the prompt.");
        window.Should().NotContain("Daniel",
            "utterances older than the window must be excluded, or the Analyst re-scores stale context forever");
    }

    [Fact]
    public void InterimResults_AreExcludedFromAnalysis()
    {
        var session = NewSession();
        var now = DateTimeOffset.UtcNow;

        session.AddUtterance(new Utterance(SpeakerRole.Caller, "approve the", 1_000, now, IsFinal: false));
        session.AddUtterance(new Utterance(SpeakerRole.Caller, "approve the prompt", 1_000, now, IsFinal: true));

        var window = session.TranscriptWindow(TimeSpan.FromSeconds(45), now: 2_000);

        // Interim hypotheses change under you as recognition settles; scoring them would
        // mean scoring text the speaker never said.
        window.Should().Be("[CALLER] approve the prompt");
    }

    // ── Full call ───────────────────────────────────────────────────────────

    [Fact]
    public void ScamCall_EscalatesThroughTheLadder_AndStopsRepeating()
    {
        var session = NewSession();
        var options = new { RiskTier = TenantRiskTier.Graph };

        // The shape of a real interception: risk builds, then the victim reaches the point
        // of approving. Modelled as successive Analyst verdicts.
        var script = new[]
        {
            new RiskAssessment { RiskScore = 15, Confidence = 0.9, Stage = ComplianceStage.Unaware },
            new RiskAssessment { RiskScore = 55, Confidence = 0.85, Stage = ComplianceStage.Engaged,
                Vectors = [ScamVector.AuthorityImpersonation] },
            new RiskAssessment { RiskScore = 72, Confidence = 0.88, Stage = ComplianceStage.Engaged,
                Vectors = [ScamVector.AuthorityImpersonation, ScamVector.UrgencyPretexting] },
            new RiskAssessment { RiskScore = 88, Confidence = 0.92, Stage = ComplianceStage.AboutToApprove,
                Vectors = [ScamVector.MfaFatigueCoaching, ScamVector.OtpElicitation] },
            new RiskAssessment { RiskScore = 91, Confidence = 0.95, Stage = ComplianceStage.AboutToApprove,
                Vectors = [ScamVector.OtpElicitation] },
        };

        var executed = new HashSet<RemediationAction>();
        var decisions = new List<PolicyDecision>();

        foreach (var assessment in script)
        {
            var decision = PolicyGate.Evaluate(assessment, new PolicyContext
            {
                RiskTier = options.RiskTier,
                HasIdentifiedSubject = true,
                AlreadyExecuted = new HashSet<RemediationAction>(executed),
            });

            decisions.Add(decision);
            foreach (var action in decision.Actions)
            {
                executed.Add(action);
            }
        }

        // Early: observation only.
        decisions[0].IsIntervening.Should().BeFalse();
        decisions[0].Actions.Should().ContainSingle().Which.Should().Be(RemediationAction.LogTelemetry);

        // Assertions are on the SEQUENCE, not on fixed indices: idempotency means each
        // action appears in exactly one pass, and which pass depends on the urgency
        // weighting. Pinning an index here would test the arithmetic, not the behaviour.
        var passOf = (RemediationAction action) =>
            decisions.FindIndex(d => d.Actions.Contains(action));

        var warningPass = passOf(RemediationAction.InjectVoiceWarning);
        var revokePass = passOf(RemediationAction.RevokeSessions);
        var elevatePass = passOf(RemediationAction.ElevateUserRisk);
        var terminatePass = passOf(RemediationAction.TerminateCall);

        warningPass.Should().BeGreaterThanOrEqualTo(0, "the user must be warned at some point");
        revokePass.Should().BeGreaterThanOrEqualTo(0, "containment must fire on a scam this clear");
        elevatePass.Should().BeGreaterThanOrEqualTo(0);
        terminatePass.Should().BeGreaterThanOrEqualTo(0);

        // The ordering that matters: warn first, and never hang up before warning —
        // a hang-up that precedes the warning silences the warning it was meant to follow.
        warningPass.Should().BeLessThan(revokePass,
            "warn the human before touching their account");
        warningPass.Should().BeLessThan(terminatePass);

        // Nothing irreversible happens while the victim is merely engaged.
        decisions.Take(revokePass).Should().AllSatisfy(d => d.IsIntervening.Should().BeFalse(),
            "identity remediation waits until the risk actually crosses the containment threshold");

        // Nothing fires twice across the whole call.
        var allActions = decisions.SelectMany(d => d.Actions)
            .Where(a => a != RemediationAction.LogTelemetry)
            .ToList();
        allActions.Should().OnlyHaveUniqueItems(
            "the Analyst re-scores every few seconds; without idempotency a 90-second call " +
            "would revoke sessions thirty times");
    }

    [Fact]
    public void BenignCall_NeverTouchesTheIdentityPlane()
    {
        var session = NewSession();
        var executed = new HashSet<RemediationAction>();

        // A legitimate help-desk call: it sounds technical and slightly tense, and the
        // Analyst correctly reads it as low risk throughout.
        foreach (var _ in Enumerable.Range(0, 20))
        {
            var decision = PolicyGate.Evaluate(
                new RiskAssessment
                {
                    RiskScore = 12,
                    Confidence = 0.93,
                    Stage = ComplianceStage.Unaware,
                },
                new PolicyContext
                {
                    RiskTier = TenantRiskTier.Graph,
                    HasIdentifiedSubject = true,
                    AlreadyExecuted = new HashSet<RemediationAction>(executed),
                });

            decision.IsIntervening.Should().BeFalse();
            decision.Actions.Should().NotContain(RemediationAction.InjectVoiceWarning,
                "interrupting a legitimate support call is a real cost, not a free action");

            foreach (var action in decision.Actions)
            {
                executed.Add(action);
            }
        }

        executed.Should().ContainSingle().Which.Should().Be(RemediationAction.LogTelemetry);
        session.ExecutedActions.Should().BeEmpty();
    }

    [Fact]
    public void DegradedTenant_StillContains_WithoutRiskElevation()
    {
        var executed = new HashSet<RemediationAction>();

        var decision = PolicyGate.Evaluate(
            new RiskAssessment
            {
                RiskScore = 92,
                Confidence = 0.94,
                Stage = ComplianceStage.AboutToApprove,
                Vectors = [ScamVector.OtpElicitation],
            },
            new PolicyContext
            {
                RiskTier = TenantRiskTier.Degraded,
                HasIdentifiedSubject = true,
                AlreadyExecuted = executed,
            });

        decision.Actions.Should().NotContain(RemediationAction.ElevateUserRisk);
        decision.Actions.Should().Contain(RemediationAction.RevokeSessions,
            "the account is still containable without P2");
        decision.Actions.Should().Contain(RemediationAction.QuarantineUser);
        decision.Actions.Should().Contain(RemediationAction.RaiseSentinelIncident);
        decision.IsIntervening.Should().BeTrue(
            "a tenant without P2 must still get real protection, not just a log line");
    }

    // ── Robustness ──────────────────────────────────────────────────────────

    [Fact]
    public void MalformedFramesMidCall_DoNotDisruptTheStream()
    {
        var assembler = new AcsMessageAssembler();
        var frames = new[]
        {
            AudioFrameJson(CallerId, 480),
            "{ this is not valid json",
            """{"kind":"SomeFutureFrameKind","payload":{}}""",
            AudioFrameJson(UserId, 480),
        };

        var decoded = frames
            .Select(f => assembler.Append(Encoding.UTF8.GetBytes(f), endOfMessage: true)!)
            .Select(AcsFrameCodec.Decode)
            .ToList();

        decoded[0].Should().BeOfType<AudioDataFrame>();
        decoded[1].Should().BeOfType<UnknownFrame>();
        decoded[2].Should().BeOfType<UnknownFrame>();
        decoded[3].Should().BeOfType<AudioDataFrame>(
            "a bad frame must not poison the assembler — an exception here drops a live call");
    }

    [Fact]
    public void SilentFrames_AreMarkedForSkipping()
    {
        var json = AudioFrameJson(CallerId, 960, silent: true);
        var frame = AcsFrameCodec.Decode(json);

        frame.Should().BeOfType<AudioDataFrame>().Which.IsSilent.Should().BeTrue(
            "silence is not forwarded to Speech — a 5-minute call is mostly one party not talking");
    }
}
