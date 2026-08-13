using System.Reflection;
using EntraGuard.MediaService.Sessions;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The echo guard must throw away the question coming back, and nothing else.
///
/// It is deliberately asymmetric. Letting an echo through costs one attempt of three;
/// discarding a real answer costs every attempt and refuses somebody who answered correctly.
/// A caller who answered every question and was refused anyway is the failure this guards.
/// </summary>
public class EchoGuardTests
{
    private const string Question =
        "Which town, city, or country were you in the last time you signed in?";

    /// <summary>The privacy notice now spoken immediately before that question.</summary>
    private const string Notice =
        "Before we continue. Please make sure nobody can overhear you, and that nobody is "
      + "helping you answer. If someone is listening, move somewhere private now. ";

    private static bool IsEcho(string reference, string spoken) =>
        (bool)typeof(VerificationCoordinator)
            .GetMethod("IsEchoOf", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [reference, spoken])!;

    [Theory]
    [InlineData("Kuala Lumpur")]
    [InlineData("I signed in from Kuala Lumpur last time")]
    [InlineData("I was in Malaysia, in Selangor")]
    [InlineData("Windows, on the Edge browser")]
    public void Genuine_answers_are_kept(string answer)
    {
        IsEcho(Question, answer).Should().BeFalse();
    }

    [Fact]
    public void The_question_echoed_back_is_discarded()
    {
        IsEcho(Question, "which town city or country were you in").Should().BeTrue();
    }

    [Fact]
    public void An_answer_phrased_in_the_questions_own_words_is_still_an_answer()
    {
        // The exact reply that used to be discarded: signed, last and time are three of its
        // six words, which hit the fifty-percent line precisely. Kuala and Lumpur are content
        // the question never contained, and that is what settles it.
        IsEcho(Question, "I signed in from Kuala Lumpur last time").Should().BeFalse();
    }

    [Fact]
    public void A_partial_echo_that_adds_nothing_is_still_discarded()
    {
        IsEcho(Question, "the last time you signed in").Should().BeTrue();
    }

    [Fact]
    public void The_notice_is_never_used_as_the_reference()
    {
        // Folding the privacy notice in grows the word set from roughly nine to thirty-five,
        // which is why the guard measures the question alone regardless of what was spoken.
        IsEcho(Notice + Question, "I signed in from Kuala Lumpur last time").Should().BeFalse();
    }
}
