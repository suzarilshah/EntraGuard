using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Answers given letter by letter.
///
/// The retry prompt asks callers to "spell out anything unusual, letter by letter" — which is
/// the right instruction on a phone line, and was shipped without anything able to read the
/// result. A caller spelled MALAYSIA, the transcript came back as separate letters, and the
/// judge compared "M A L A Y S I A" against "Malaysia" and refused it.
///
/// Collapsing is deterministic on purpose. Asking the language model to spot spelling would
/// work most of the time, and "most of the time" is how a factor starts refusing people.
/// </summary>
public class SpelledOutAnswerTests
{
    [Theory]
    [InlineData("M A L A Y S I A", "MALAYSIA")]
    [InlineData("M. A. L. A. Y. S. I. A.", "MALAYSIA")]
    [InlineData("m-a-l-a-y-s-i-a", "MALAYSIA")]
    [InlineData("A I M A N", "AIMAN")]
    public void Letters_spoken_one_at_a_time_become_the_word(string spoken, string expected)
    {
        SpelledOutAnswer.Collapse(spoken).Should().Be(expected);
    }

    [Theory]
    [InlineData("Malaysia")]
    [InlineData("Petaling Jaya")]
    [InlineData("I was in Shah Alam")]
    [InlineData("Windows")]
    public void An_ordinary_answer_is_not_treated_as_spelling(string spoken)
    {
        // Returning null means "this was not spelled out" — the caller's words are judged
        // exactly as before. Mangling a normal answer into initials would refuse people who
        // did nothing unusual.
        SpelledOutAnswer.Collapse(spoken).Should().BeNull();
    }

    [Fact]
    public void A_stray_initial_is_not_a_spelling()
    {
        // "in KL" and similar must survive untouched. Two letters is an abbreviation; a
        // spelling is the whole answer given one letter at a time.
        SpelledOutAnswer.Collapse("K L").Should().BeNull();
    }

    [Fact]
    public void Spelling_mixed_with_words_takes_only_the_letters()
    {
        // People say "it's M, A, L, A, Y, S, I, A" — the lead-in is not part of the answer.
        SpelledOutAnswer.Collapse("it's M, A, L, A, Y, S, I, A").Should().Be("MALAYSIA");
    }

    [Fact]
    public void Nothing_spoken_collapses_to_nothing()
    {
        SpelledOutAnswer.Collapse("").Should().BeNull();
        SpelledOutAnswer.Collapse("   ").Should().BeNull();
    }
}
