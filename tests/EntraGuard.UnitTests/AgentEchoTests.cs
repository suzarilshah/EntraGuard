using System.Reflection;
using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Telling the agent's own voice from the caller's.
///
/// The first version of this test asked only "how many words are new?", and a one-word answer
/// can never have two new words — so every short answer was thrown away as an echo. A caller
/// said "Malaysia" twice, clearly, and was refused both times.
///
/// An echo REPEATS our words. If none of what came back is ours, it is not an echo, however
/// short it is.
/// </summary>
public class AgentEchoTests
{
    private static bool Echoes(string weSaid, string heard)
    {
        var agent = (VoiceAgent)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(VoiceAgent));

        typeof(VoiceAgent).GetField("_lastSpoken", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(agent, weSaid);

        return (bool)typeof(VoiceAgent)
            .GetMethod("EchoesWhatWeSaid", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(agent, [heard])!;
    }

    private const string Question =
        "Which town, city, or country were you in the last time you signed in?";

    [Theory]
    [InlineData("Malaysia")]
    [InlineData("Selangor")]
    [InlineData("Shah Alam")]
    [InlineData("Windows")]
    [InlineData("Edge")]
    public void A_short_answer_is_never_mistaken_for_an_echo(string answer)
    {
        // The failure this exists for. None of these words were spoken by the agent, so none
        // of them can be the agent's voice coming back — regardless of how few words they are.
        Echoes(Question, answer).Should().BeFalse();
    }

    [Theory]
    [InlineData("Which town, city, or country were you in the last time you signed in?")]
    [InlineData("which town city or country were you in")]
    public void Our_own_question_coming_back_is_an_echo(string heard)
    {
        Echoes(Question, heard).Should().BeTrue();
    }

    [Fact]
    public void An_answer_that_borrows_the_questions_words_still_counts_as_an_answer()
    {
        // People answer in the question's own words. "I was in Malaysia" reuses "was" and
        // "in", and it is plainly an answer.
        Echoes(Question, "I was in Malaysia").Should().BeFalse();
    }

    [Fact]
    public void Nothing_said_is_not_an_echo()
    {
        Echoes(Question, "").Should().BeFalse();
        Echoes("", "Malaysia").Should().BeFalse();
    }
}
