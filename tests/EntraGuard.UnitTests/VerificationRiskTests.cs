using EntraGuard.MediaService.Endpoints;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// How risky a completed verification looked.
///
/// The scale exists for the attempts that PASSED. A sign-in granted after two wrong codes,
/// from an unmanaged browser, with a voice that did not quite match, is not the same event as
/// one answered first time on a managed Teams device — and an audit trail recording both as
/// "Passed" cannot tell a security team which one to look at.
/// </summary>
public class VerificationRiskTests
{
    private static VerificationRiskResult Clean() =>
        VerificationRisk.Score(0, "Match", attempts: 1, endpointKind: "teams");

    [Fact]
    public void A_clean_verification_scores_zero_and_says_why_it_is_empty()
    {
        var risk = Clean();

        risk.Score.Should().Be(0);
        risk.Band.Should().Be(RiskBand.Low);
        risk.Contributors.Should().BeEmpty("nothing contributing is a finding, not a gap");
    }

    [Fact]
    public void Coercion_is_the_heaviest_single_signal()
    {
        // Half the scale, because it is the only signal derived from what was actually said
        // rather than from how the mechanics went.
        VerificationRisk.Score(100, "Match", 1, "teams").Score.Should().Be(50);
    }

    [Theory]
    [InlineData("Mismatch", 30)]
    [InlineData("Inconclusive", 15)]
    [InlineData("NotAssessed", 5)]
    [InlineData("Match", 0)]
    public void Voice_outcomes_are_weighted_by_what_they_actually_mean(string outcome, double expected)
    {
        VerificationRisk.Score(0, outcome, 1, "teams").Score.Should().Be(expected);
    }

    [Fact]
    public void An_unassessed_voice_is_not_treated_as_innocence()
    {
        // It means the one check that asks WHO spoke did not run, so the attempt rests
        // entirely on factors a thief of the phone would also satisfy. Small, because the
        // usual cause is simply that nobody enrolled.
        VerificationRisk.Score(0, "NotAssessed", 1, "teams").Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void The_first_code_attempt_is_free()
    {
        VerificationRisk.Score(0, "Match", attempts: 1, "teams").Score.Should().Be(0);
        VerificationRisk.Score(0, "Match", attempts: 3, "teams").Score.Should().Be(20);
    }

    [Theory]
    [InlineData("teams", 0)]
    [InlineData("phone", 5)]
    [InlineData("browser", 10)]
    public void The_channel_changes_how_much_the_result_is_worth(string endpoint, double expected)
    {
        VerificationRisk.Score(0, "Match", 1, endpoint).Score.Should().Be(expected);
    }

    [Fact]
    public void Signals_accumulate_and_are_reported_largest_first()
    {
        var risk = VerificationRisk.Score(80, "Mismatch", attempts: 2, "browser", knowledgeAttempts: 2);

        risk.Score.Should().Be(95); // 40 coercion + 30 voice + 10 attempt + 10 browser + 5 knowledge
        risk.Band.Should().Be(RiskBand.High);
        risk.Contributors.Should().HaveCount(5);
        risk.Contributors[0].Should().Contain("Coercion");
    }

    [Fact]
    public void The_score_never_exceeds_the_scale()
    {
        VerificationRisk.Score(100, "Mismatch", attempts: 9, "browser", knowledgeAttempts: 9)
            .Score.Should().Be(100);
    }

    [Theory]
    [InlineData(0, RiskBand.Low)]
    [InlineData(24, RiskBand.Low)]
    [InlineData(25, RiskBand.Moderate)]
    [InlineData(49, RiskBand.Moderate)]
    [InlineData(50, RiskBand.Elevated)]
    [InlineData(74, RiskBand.Elevated)]
    [InlineData(75, RiskBand.High)]
    public void Bands_break_where_the_thresholds_say(double coercion, RiskBand expected)
    {
        // Driven through the coercion input so the boundaries are exercised as real scores.
        VerificationRisk.Score(coercion * 2, "Match", 1, "teams").Band.Should().Be(expected);
    }

    [Fact]
    public void Risk_does_not_change_who_gets_in()
    {
        // The property that makes this safe to ship. The adjudicator has no risk parameter at
        // all, so a maximal-risk verification with a correct code and no coercion still
        // passes. Enforcement is a separate decision that should follow evidence — and this
        // project has already locked its owner out by enforcing numbers nobody had measured.
        var verdict = VerificationAdjudicator.Adjudicate("42", "42", attempts: 1, assessment: null);

        verdict.Result.Should().Be(VerificationResult.Passed);

        VerificationRisk.Score(100, "Mismatch", 3, "browser", 3).Score.Should().Be(100);
        verdict.Result.Should().Be(VerificationResult.Passed, "scoring is not adjudication");
    }

    // ── Follow-up probes ─────────────────────────────────────────────────────

    [Fact]
    public void A_probe_nobody_could_answer_raises_the_score()
    {
        var vague = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
            [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false)]);

        vague.Score.Should().BeGreaterThan(Clean().Score);
    }

    [Fact]
    public void A_probe_answered_well_costs_nothing()
    {
        var sharp = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
            [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: true)]);

        sharp.Score.Should().Be(Clean().Score);
    }

    [Fact]
    public void Missing_every_probe_is_not_on_its_own_enough_to_flag_a_call()
    {
        // A probe cannot refuse anybody and must not be able to flag anybody either. People
        // forget which browser they used at eight in the morning. The signal is worth having
        // in company with others and worth nothing alone.
        var result = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
        [
            new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false),
            new FollowUpOutcome("device", "Which browser?", Answered: false, Correct: false),
        ]);

        result.Band.Should().Be(RiskBand.Low);
    }

    [Fact]
    public void A_missed_probe_says_which_one_it_was()
    {
        // "Why was I flagged?" must have an answer that survives being asked a second time.
        var result = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
            [new FollowUpOutcome("location", "Whereabouts?", Answered: true, Correct: false)]);

        result.Contributors.Should().Contain(c =>
            c.Contains("location", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Asking_no_probes_scores_exactly_as_before_probes_existed()
    {
        // Most calls will not need one. The mechanism must be invisible when it does nothing.
        VerificationRisk.Score(0, "Match", 1, "teams", followUps: [])
            .Score.Should().Be(Clean().Score);
    }

    [Fact]
    public void A_probe_the_caller_answered_wrongly_is_named_once_however_many_were_asked()
    {
        // Two device probes are composed because which half the caller left unsaid is not
        // known in advance. The contributor should read as one concern, not two.
        var result = VerificationRisk.Score(0, "Match", 1, "teams", followUps:
        [
            new FollowUpOutcome("device", "Which browser?", Answered: true, Correct: false),
            new FollowUpOutcome("device", "What machine?", Answered: true, Correct: false),
        ]);

        result.Contributors.Single(c => c.Contains("device"))
            .Should().NotContain("device or device");
    }
}
