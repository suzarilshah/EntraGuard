using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The step-up verification decision.
///
/// This is an authorization boundary — it decides whether someone gets into an application
/// holding payment runs — so it is tested the same way the policy gate is: exhaustively,
/// at every boundary, with the ordering property called out explicitly.
/// </summary>
public class VerificationAdjudicatorTests
{
    private static RiskAssessment Coercion(double risk = 85, double confidence = 0.9) => new()
    {
        RiskScore = risk,
        Confidence = confidence,
        Stage = ComplianceStage.AboutToApprove,
        Vectors = [ScamVector.MfaFatigueCoaching],
        Rationale = "Third party heard instructing the user which digits to press.",
    };

    // ── The happy path ──────────────────────────────────────────────────────

    [Fact]
    public void CorrectCode_WithNoCoercion_Passes()
    {
        var verdict = VerificationAdjudicator.Adjudicate("47", "47", attempts: 1, assessment: null);

        verdict.Result.Should().Be(VerificationResult.Passed);
    }

    [Fact]
    public void CorrectCode_WithBenignCallAudio_Passes()
    {
        var benign = new RiskAssessment
        {
            RiskScore = 5,
            Confidence = 0.95,
            Stage = ComplianceStage.Unaware,
        };

        VerificationAdjudicator.Adjudicate("47", "47", 1, benign)
            .Result.Should().Be(VerificationResult.Passed);
    }

    // ── Wrong code ──────────────────────────────────────────────────────────

    [Fact]
    public void WrongCode_BeforeMaxAttempts_IsInconclusive()
    {
        var verdict = VerificationAdjudicator.Adjudicate("47", "12", attempts: 1, assessment: null);

        verdict.Result.Should().BeNull("the user should be prompted again, not failed outright");
    }

    [Fact]
    public void WrongCode_AtMaxAttempts_Fails()
    {
        VerificationAdjudicator.Adjudicate("47", "12", VerificationAdjudicator.MaxAttempts, null)
            .Result.Should().Be(VerificationResult.Failed);
    }

    [Fact]
    public void RetryMessage_DoesNotRevealHowCloseTheEntryWas()
    {
        // "One digit off" would halve the effective keyspace of a two-digit code.
        var verdict = VerificationAdjudicator.Adjudicate("47", "48", attempts: 1, assessment: null);

        verdict.Reason.Should().NotContain("48");
        verdict.Reason.Should().NotContain("47");
        verdict.Reason.Should().NotContain("close");
    }

    // ── Coercion — the property that makes this factor different ────────────

    [Fact]
    public void CorrectCode_UnderCoercion_IsBlocked()
    {
        var verdict = VerificationAdjudicator.Adjudicate("47", "47", attempts: 1, assessment: Coercion());

        verdict.Result.Should().Be(VerificationResult.BlockedCoercion,
            "number matching proves possession of the phone, not that the holder is acting freely");
        verdict.Reason.Should().Contain("correct");
    }

    [Fact]
    public void CoercionIsCheckedBeforeTheCode()
    {
        // The ordering property, stated as a test. A coached user enters the RIGHT digits —
        // that is what coaching produces — so a code-first implementation would return
        // Passed and never consult the Analyst at all. This test fails if anyone reorders
        // those two checks.
        var correctUnderCoercion = VerificationAdjudicator.Adjudicate("47", "47", 1, Coercion());

        correctUnderCoercion.Result.Should().Be(VerificationResult.BlockedCoercion);
        correctUnderCoercion.Result.Should().NotBe(VerificationResult.Passed);
    }

    [Fact]
    public void WrongCode_UnderCoercion_ReportsCoercion_NotJustFailure()
    {
        // The operator needs to know a user was being coached even when the attempt would
        // have failed anyway — the coercion is the security event, not the typo.
        var verdict = VerificationAdjudicator.Adjudicate("47", "11", 1, Coercion());

        verdict.Result.Should().Be(VerificationResult.BlockedCoercion);
        verdict.Reason.Should().Contain("incorrect");
    }

    [Theory]
    [InlineData(59, 0.90, false)]
    [InlineData(60, 0.90, true)]
    [InlineData(85, 0.74, false)]
    [InlineData(85, 0.75, true)]
    public void CoercionRequiresBothRiskAndConfidence(double risk, double confidence, bool blocked)
    {
        var verdict = VerificationAdjudicator.Adjudicate("47", "47", 1, Coercion(risk, confidence));

        if (blocked)
        {
            verdict.Result.Should().Be(VerificationResult.BlockedCoercion);
        }
        else
        {
            verdict.Result.Should().Be(VerificationResult.Passed,
                "a tentative or low-risk reading must not deny a user who entered the right code");
        }
    }

    [Fact]
    public void NoAnalystVerdict_DoesNotBlock()
    {
        // A silent Analyst means "nothing observed", not "coercion". Failing closed here
        // would deny every verification whenever the model was unavailable.
        VerificationAdjudicator.Adjudicate("47", "47", 1, assessment: null)
            .Result.Should().Be(VerificationResult.Passed);
    }

    // ── Result semantics ────────────────────────────────────────────────────

    [Theory]
    [InlineData(VerificationResult.Passed, true)]
    [InlineData(VerificationResult.Failed, false)]
    [InlineData(VerificationResult.BlockedCoercion, false)]
    [InlineData(VerificationResult.Timeout, false)]
    [InlineData(VerificationResult.CallFailed, false)]
    [InlineData(VerificationResult.Pending, false)]
    public void OnlyPassedGrantsAccess(VerificationResult result, bool grants)
    {
        var session = new VerificationSession
        {
            VerificationId = "v1",
            StartedAt = DateTimeOffset.UtcNow,
            SubjectUpn = "user@contoso.com",
            CalleeAcsId = "8:acs:x",
            MatchCode = "47",
            Result = result,
        };

        session.GrantsAccess.Should().Be(grants,
            "access is granted on an explicit Passed, never on 'not obviously failed'");
    }

    [Fact]
    public void MatchCodeIsAlwaysTwoDigits()
    {
        // Readable from a screen and keyable mid-call. Also guards the RandomNumberGenerator
        // bounds: GetInt32(10, 100) is exclusive on the upper bound.
        for (var i = 0; i < 500; i++)
        {
            var code = EntraGuard.MediaService.Sessions.VerificationRegistry.NewMatchCode();
            code.Should().MatchRegex("^[1-9][0-9]$");
        }
    }
}

/// <summary>
/// Voice as a blocking factor.
///
/// These exist because this is the first check that can refuse a user who did everything
/// right — correct code, correct phone, no coaching — on the strength of a similarity score
/// from a model running over a phone codec. The failure mode is locking the account owner
/// out of their own money, so the conditions under which it is allowed to fire are pinned
/// down here rather than left to the call site.
/// </summary>
public class VoiceBlockingTests
{
    private const string Code = "42";

    private static EntraGuard.Shared.Voice.VoiceDecision Decide(
        double score, double seconds, bool enforce) =>
        EntraGuard.Shared.Voice.VoiceThresholds.Evaluate(score, seconds, enforce);

    [Fact]
    public void A_clear_mismatch_requires_server_validated_step_up_before_access()
    {
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, null, Decide(0.11, seconds: 8, enforce: true));

        verdict.Result.Should().Be(VerificationResult.StepUpRequired);
        verdict.Reason.Should().Contain("did not match");
    }

    [Fact]
    public void An_inconclusive_score_also_requires_step_up()
    {
        // "Not a pass" is the bar, not "proven to be somebody else".
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, null, Decide(0.45, seconds: 8, enforce: true));

        verdict.Result.Should().Be(VerificationResult.StepUpRequired);
    }

    [Fact]
    public void A_matching_voice_passes()
    {
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, null, Decide(0.82, seconds: 8, enforce: true));

        verdict.Result.Should().Be(VerificationResult.Passed);
    }

    [Fact]
    public void Observe_mode_records_the_mismatch_and_grants_access_anyway()
    {
        // The whole safety property of shipping this before calibration.
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, null, Decide(0.02, seconds: 8, enforce: false));

        verdict.Result.Should().Be(VerificationResult.Passed);
    }

    [Fact]
    public void No_enrolled_profile_never_blocks()
    {
        // Null is what ScoreVoiceAsync returns for no profile, an unreachable sidecar, or a
        // scoring exception. Every one of those must leave the verification untouched.
        var verdict = VerificationAdjudicator.Adjudicate(Code, Code, 1, null, null);

        verdict.Result.Should().Be(VerificationResult.Passed);
    }

    [Fact]
    public void Too_little_speech_never_blocks_even_in_enforce_mode()
    {
        // Two seconds of "yes" yields a confident-looking number that means nothing.
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, null, Decide(0.01, seconds: 1.5, enforce: true));

        verdict.Result.Should().Be(VerificationResult.Passed);
    }

    [Fact]
    public void Coercion_outranks_voice()
    {
        // Both would refuse; the audit trail must name the more serious one.
        var coerced = new RiskAssessment
        {
            RiskScore = 90,
            Confidence = 0.95,
            Stage = ComplianceStage.AboutToApprove,
            Vectors = [ScamVector.OtpElicitation],
        };

        var verdict = VerificationAdjudicator.Adjudicate(
            Code, Code, 1, coerced, Decide(0.01, seconds: 8, enforce: true));

        verdict.Result.Should().Be(VerificationResult.BlockedCoercion);
    }

    [Fact]
    public void A_wrong_code_is_still_reported_as_a_wrong_code()
    {
        // Voice must not relabel an ordinary failed entry as a biometric refusal.
        var verdict = VerificationAdjudicator.Adjudicate(
            Code, "99", 3, null, Decide(0.01, seconds: 8, enforce: true));

        verdict.Result.Should().Be(VerificationResult.Failed);
    }

    [Fact]
    public void A_blocked_voice_verification_does_not_grant_access()
    {
        // The property the relying party actually reads.
        var session = new VerificationSession
        {
            VerificationId = "v-voice",
            StartedAt = DateTimeOffset.UtcNow,
            SubjectUpn = "user@contoso.com",
            CalleeAcsId = "8:acs:x",
            MatchCode = Code,
            Result = VerificationResult.BlockedVoiceMismatch,
        };

        session.GrantsAccess.Should().BeFalse();
        session.IsComplete.Should().BeTrue();
    }
}

/// <summary>
/// The match code must not be readable by anyone but the browser that asked for it.
///
/// This is a regression test for a live disclosure, not a hypothetical: against the deployed
/// service, an anonymous GET /api/verify returned match code 96 for victim@contoso.com while
/// the call was still in flight. The projection below is the one that endpoint uses and the
/// one broadcast over SignalR to every connected client, so the default has to be redaction.
/// </summary>
public class MatchCodeDisclosureTests
{
    private static VerificationSession InFlight() => new()
    {
        VerificationId = "vrf-abc",
        StartedAt = DateTimeOffset.UtcNow,
        SubjectUpn = "victim@contoso.com",
        CalleeAcsId = "8:acs:x",
        MatchCode = "96",
        ViewerToken = "TOKEN",
        Result = VerificationResult.Pending,
    };

    private static string? CodeOf(object described) =>
        described.GetType().GetProperty("matchCode")!.GetValue(described) as string;

    private static string? TokenOf(object described) =>
        described.GetType().GetProperty("viewerToken")!.GetValue(described) as string;

    [Fact]
    public void The_default_projection_redacts_the_code()
    {
        // The endpoint that leaked, and the eight SignalR broadcasts, all use this overload.
        // Enumeration is the attack that mattered: an anonymous caller listing every
        // in-flight verification and reading the codes. If the default ever flips back, this
        // is what fails.
        //
        // The single-verification GET deliberately does NOT go through the default — knowing
        // an unguessable id is its capability, and requiring more than that broke the display
        // for any client that did not send a header.
        CodeOf(VerificationEndpoint.Describe(InFlight())).Should().BeNull();
    }

    [Fact]
    public void The_default_projection_redacts_the_viewer_token()
    {
        // Leaking the token is equivalent to leaking the code, one request later.
        TokenOf(VerificationEndpoint.Describe(InFlight())).Should().BeNull();
    }

    [Fact]
    public void The_starter_sees_the_code()
    {
        CodeOf(VerificationEndpoint.Describe(InFlight(), includeMatchCode: true)).Should().Be("96");
    }

    [Fact]
    public void A_completed_verification_never_returns_its_code_even_to_the_starter()
    {
        // A used auth secret in browser history and logs buys nothing.
        var done = InFlight();
        done.Result = VerificationResult.Passed;

        CodeOf(VerificationEndpoint.Describe(done, includeMatchCode: true)).Should().BeNull();
    }

    [Fact]
    public void Every_session_gets_a_distinct_unguessable_token()
    {
        var registry = new VerificationRegistry();

        var a = registry.Create("a@contoso.com", null, "8:acs:a", "Contoso Treasury");
        var b = registry.Create("b@contoso.com", null, "8:acs:b", "Contoso Treasury");

        a.ViewerToken.Should().NotBeNullOrEmpty();
        a.ViewerToken.Should().NotBe(b.ViewerToken);
        a.ViewerToken.Length.Should().BeGreaterThanOrEqualTo(64); // 32 bytes, hex encoded
    }
}

/// <summary>
/// The number on the user's screen must not depend on anything but the verification being
/// live.
///
/// It vanished into "··" mid-call three times across one session, each time for a different
/// incidental reason — a browser tab on an older bundle that sent no capability header, a
/// reloaded page that had lost it, and a container revision still draining that served older
/// code. All three were the same design error: the displayed code had been made contingent
/// on conditions unrelated to whether the code was still valid.
/// </summary>
public class MatchCodeAvailabilityTests
{
    private static VerificationSession Session(VerificationResult result) => new()
    {
        VerificationId = "vrf-live",
        StartedAt = DateTimeOffset.UtcNow,
        SubjectUpn = "user@contoso.com",
        CalleeAcsId = "8:acs:x",
        MatchCode = "73",
        ViewerToken = "TOKEN",
        Result = result,
    };

    private static string? CodeOf(object described) =>
        described.GetType().GetProperty("matchCode")!.GetValue(described) as string;

    [Fact]
    public void An_in_flight_verification_shows_its_code_to_the_holder_of_the_id()
    {
        // What the single-verification GET does when no token accompanies the request. The
        // id is 64 bits of CSPRNG and is never listed, broadcast, or logged, so knowing it
        // IS the capability — which is what this endpoint always ran on.
        CodeOf(VerificationEndpoint.Describe(Session(VerificationResult.Pending), includeMatchCode: true))
            .Should().Be("73");
    }

    [Theory]
    [InlineData(VerificationResult.Passed)]
    [InlineData(VerificationResult.Failed)]
    [InlineData(VerificationResult.BlockedCoercion)]
    [InlineData(VerificationResult.BlockedVoiceMismatch)]
    public void A_finished_verification_stops_showing_it(VerificationResult result)
    {
        // A spent auth secret has no business in logs or browser history.
        CodeOf(VerificationEndpoint.Describe(Session(result), includeMatchCode: true)).Should().BeNull();
    }

    [Fact]
    public void Enumeration_never_shows_it_regardless()
    {
        // The actual vulnerability: an anonymous caller listing every in-flight verification
        // and reading the codes. This is the guarantee that must never soften.
        CodeOf(VerificationEndpoint.Describe(Session(VerificationResult.Pending))).Should().BeNull();
    }
}
