using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// How much a passed verification actually proved.
///
/// Distinct from the verdict, which is binary and already decided, and from VerificationRisk,
/// which asks how much the attempt looked like an attack. This asks what was established —
/// and it exists because "Passed" currently covers both a call that answered live telemetry
/// questions and matched an enrolled voice, and a call in a tenant without Entra ID P1 where
/// the two-digit number match was the entire check.
/// </summary>
public class VerificationAssuranceTests
{
    [Fact]
    public void A_verification_that_did_not_pass_proved_nothing()
    {
        VerificationAssurance.Evaluate(
            passed: false, knowledgeBacking: "telemetry", followUps: null,
            voiceOutcome: "Match", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.None);
    }

    [Fact]
    public void The_number_match_alone_is_Low()
    {
        // The tenant had no P1 and the user never enrolled, so no question was asked at all.
        // Access is still granted — refusing here locks out everyone who never enrolled — but
        // the relying party is entitled to know that is all that happened.
        var r = VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: null, followUps: null,
            voiceOutcome: "NotAssessed", endpointKind: "browser");

        r.Level.Should().Be(AssuranceLevel.Low);
        r.Basis.Should().Contain(b => b.Contains("number match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_stored_question_is_Substantial_and_not_High()
    {
        // A secret the user chose once and an attacker may already have researched. Better
        // than nothing, and not the same as a fact from this morning's sign-in.
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "directory", followUps: null,
            voiceOutcome: "NotAssessed", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.Substantial);
    }

    [Fact]
    public void Live_telemetry_with_nothing_corroborating_it_is_Substantial()
    {
        // Strong questions, but one channel. High wants a second, independent signal.
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry", followUps: [],
            voiceOutcome: "NotAssessed", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.Substantial);
    }

    [Fact]
    public void Live_telemetry_plus_a_confirmed_probe_is_High()
    {
        // The probe is the corroboration: it was invented during the call, so it cannot have
        // been researched, recorded, or answered by somebody who joined late.
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry", followUps:
                [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)],
            voiceOutcome: "NotAssessed", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.High);
    }

    [Fact]
    public void Live_telemetry_plus_a_matching_voice_is_High()
    {
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry+table", followUps: [],
            voiceOutcome: "Match", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.High);
    }

    [Fact]
    public void A_probe_that_was_asked_and_missed_does_not_corroborate()
    {
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry", followUps:
                [new FollowUpOutcome("device", "Which browser?", Answered: true, Correct: false)],
            voiceOutcome: "NotAssessed", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.Substantial);
    }

    [Fact]
    public void A_stored_question_cannot_reach_High_however_well_the_voice_matched()
    {
        // Voice corroborates WHO spoke. It cannot upgrade a researchable secret into a fact
        // the caller lived through this morning, and stacking two weak-in-different-ways
        // signals into the top level is how a scale stops meaning anything.
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "table", followUps:
                [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)],
            voiceOutcome: "Match", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.Substantial);
    }

    [Fact]
    public void A_voice_mismatch_never_corroborates()
    {
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry", followUps: [],
            voiceOutcome: "Mismatch", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.Substantial);
    }

    [Fact]
    public void The_gaps_say_what_would_have_raised_it()
    {
        // "Why is this only Substantial?" must have an answer that survives being asked
        // twice, and that somebody can act on.
        var r = VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: null, followUps: null,
            voiceOutcome: "NotAssessed", endpointKind: "browser");

        r.Gaps.Should().NotBeEmpty();
        r.Gaps.Should().Contain(g => g.Contains("question", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_call_that_proved_everything_reports_no_gaps()
    {
        VerificationAssurance.Evaluate(
            passed: true, knowledgeBacking: "telemetry+directory", followUps:
                [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)],
            voiceOutcome: "Match", endpointKind: "teams")
            .Gaps.Should().BeEmpty();
    }

    [Fact]
    public void Assurance_never_contradicts_the_verdict()
    {
        // The scale reports; it does not decide. A failed verification is None regardless of
        // how much evidence the call happened to gather before it failed.
        VerificationAssurance.Evaluate(
            passed: false, knowledgeBacking: "telemetry+directory", followUps:
                [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)],
            voiceOutcome: "Match", endpointKind: "teams")
            .Level.Should().Be(AssuranceLevel.None);
    }
}
