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

    // ── Follow-up probes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Malaysia, Selangor")]
    [InlineData("Selangor, Malaysia")]
    [InlineData("In Malaysia, Selangor")]
    public void A_probe_must_not_name_the_fact_the_caller_will_repeat(string answer)
    {
        // The failure this asserts against: a probe worded "And whereabouts in Malaysia,
        // roughly?" and a caller who repeats the country before answering it. Two of those
        // words are the question's own, one is new, and a correct answer is binned — after
        // which the caller hears that nothing was heard.
        //
        // Both halves are checked, because the fix is the WORDING and this documents why.
        IsEcho("And whereabouts in Malaysia, roughly?", answer)
            .Should().BeTrue("naming the country is what breaks it");

        IsEcho("And whereabouts, roughly?", answer)
            .Should().BeFalse("which is why the composed probe does not name it");
    }

    [Theory]
    [InlineData("Petaling Jaya")]
    [InlineData("Selangor")]
    [InlineData("I was in Selangor")]
    [InlineData("Near Kuala Lumpur")]
    public void Real_answers_to_the_location_probe_survive(string answer)
    {
        IsEcho("And whereabouts, roughly?", answer).Should().BeFalse();
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("A Windows laptop")]
    [InlineData("It was a Mac")]
    [InlineData("My laptop")]
    public void Real_answers_to_the_device_probe_survive(string answer)
    {
        // "And what kind of machine was that on?" discarded "It was a Mac" — was and that are
        // both the question's own words, leaving one novel one. Short questions carrying
        // common filler are the ones that collide with short answers.
        IsEcho("And what sort of device?", answer).Should().BeFalse();
    }

    [Theory]
    [InlineData("Edge")]
    [InlineData("Microsoft Edge")]
    [InlineData("It was Chrome")]
    public void Real_answers_to_the_browser_probe_survive(string answer)
    {
        IsEcho("And which browser were you using on it?", answer).Should().BeFalse();
    }
}
