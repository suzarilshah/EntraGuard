using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Which questions a call asks, and how many must land.
///
/// This decides who is let in, so the rules are pinned rather than described. The two that
/// matter most are not about strictness: a challenge must never consist only of facts an
/// attacker could have read on LinkedIn before ringing, and a real person who blanks on one
/// question out of four must not be refused — that has already happened to the account owner
/// more than once and it is the expensive failure here.
/// </summary>
public class ChallengeSelectionTests
{
    /// <summary>Deterministic "shuffle" so a test asserts a rule, not a dice roll.</summary>
    private static IReadOnlyList<ChallengeCandidate> NoShuffle(IReadOnlyList<ChallengeCandidate> x) => x;

    private static ChallengeCandidate Fact(
        string facet, FactSource source = FactSource.Directory, int strength = 1) =>
        new(facet, $"Question about {facet}?", [facet], source, strength);

    // ── Selection ───────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_pool_produces_no_questions()
    {
        ChallengeSelection.Select([], 4, NoShuffle).Should().BeEmpty();
    }

    [Fact]
    public void It_never_asks_two_questions_about_the_same_thing()
    {
        // Three ways of asking "where were you" is one question and two irritations.
        var pool = new[]
        {
            Fact("location", FactSource.SignIn, 3),
            Fact("location", FactSource.SignIn, 2),
            Fact("location", FactSource.Directory),
            Fact("device", FactSource.SignIn, 2),
        };

        var chosen = ChallengeSelection.Select(pool, 4, NoShuffle);

        chosen.Select(c => c.Facet).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_challenge_is_never_built_only_from_researchable_facts()
    {
        // The rule that matters against the actual threat actor. Manager, department and
        // title are on the caller's public profile; somebody who researched them before
        // ringing the help desk already knows all three.
        var pool = new[]
        {
            Fact("manager"), Fact("department"), Fact("office"), Fact("title"),
            Fact("location", FactSource.SignIn, 3),
        };

        var chosen = ChallengeSelection.Select(pool, 4, NoShuffle);

        chosen.Should().Contain(c => c.Source != FactSource.Directory,
            "a call made entirely of directory facts asks only what an attacker prepared for");
    }

    [Fact]
    public void The_expiring_fact_survives_even_when_it_would_have_been_crowded_out()
    {
        // It is last in the pool and the count is small: without the explicit seat, the
        // directory facts ahead of it would fill the call.
        var pool = new[]
        {
            Fact("manager"), Fact("department"), Fact("office"),
            Fact("location", FactSource.SignIn, 3),
        };

        var chosen = ChallengeSelection.Select(pool, 3, NoShuffle);

        chosen.Should().HaveCount(3);
        chosen.Should().Contain(c => c.Source == FactSource.SignIn);
    }

    [Fact]
    public void It_asks_no_more_than_the_pool_can_honestly_supply()
    {
        var chosen = ChallengeSelection.Select([Fact("location", FactSource.SignIn, 3)], 4, NoShuffle);

        chosen.Should().HaveCount(1, "inventing a question would mean inventing an answer");
    }

    [Fact]
    public void The_strongest_question_is_asked_first()
    {
        var pool = new[]
        {
            Fact("manager", FactSource.Directory, 1),
            Fact("location", FactSource.SignIn, 3),
            Fact("device", FactSource.SignIn, 2),
        };

        var chosen = ChallengeSelection.Select(pool, 3, NoShuffle);

        chosen[0].Strength.Should().Be(3);
    }

    [Fact]
    public void Randomness_is_real_across_calls()
    {
        // A fixed set is a set an attacker can rehearse. Ten selections from a wide pool must
        // not produce ten identical calls.
        var pool = Enumerable.Range(0, 10)
            .Select(i => Fact($"facet{i}", i % 2 == 0 ? FactSource.SignIn : FactSource.Directory))
            .ToArray();

        var seen = Enumerable.Range(0, 10)
            .Select(_ => string.Join(",", ChallengeSelection.Select(pool, 4).Select(c => c.Facet)))
            .Distinct()
            .Count();

        seen.Should().BeGreaterThan(1, "the production shuffle must actually vary the call");
    }

    // ── How many must pass ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    public void One_miss_is_forgiven_once_three_or_more_are_asked(int asked, int required)
    {
        // Two or fewer require all of them: "most of two" is one, and one right answer out of
        // two is a coin toss.
        ChallengeSelection.Required(asked).Should().Be(required);
    }

    [Fact]
    public void Asking_more_and_forgiving_one_is_harder_than_asking_fewer()
    {
        // The property that makes this not a weakening: a guesser has to be right three times
        // rather than twice.
        ChallengeSelection.Required(4).Should().BeGreaterThan(ChallengeSelection.Required(2));
    }

    [Fact]
    public void A_caller_who_blanks_on_one_of_four_still_passes()
    {
        ChallengeSelection.Verdict(asked: 4, correct: 3, wrong: 1).Should().BeTrue();
    }

    [Fact]
    public void Two_wrong_out_of_four_refuses()
    {
        ChallengeSelection.Verdict(asked: 4, correct: 2, wrong: 2).Should().BeFalse();
    }

    [Fact]
    public void It_stays_undecided_while_answers_can_still_change_the_outcome()
    {
        ChallengeSelection.Verdict(asked: 4, correct: 1, wrong: 0).Should().BeNull();
        ChallengeSelection.Verdict(asked: 4, correct: 2, wrong: 1).Should().BeNull();
    }

    [Fact]
    public void It_refuses_as_soon_as_passing_became_impossible()
    {
        // Rather than asking two more questions whose answers cannot change anything.
        ChallengeSelection.Verdict(asked: 4, correct: 0, wrong: 2).Should().BeFalse();
    }

    [Fact]
    public void Passing_is_decided_the_moment_enough_are_right()
    {
        ChallengeSelection.Verdict(asked: 4, correct: 3, wrong: 0).Should().BeTrue();
    }
}
