using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What the caller is actually asked out loud.
///
/// This exists because of a live failure: a town question, then the device question, then a
/// town question AGAIN — asked and then retried verbatim, because the second one wanted a
/// different city than the caller had just correctly given. From the caller's side the
/// system had stopped listening to an answer they had already provided.
///
/// The rule these enforce is not "two questions is enough". It is that every question must
/// be answerable by a truthful user. A question whose expected answer a genuine person would
/// never give is not a security control; it is a refusal with extra steps.
/// </summary>
public class TelemetryQuestionTests
{
    private static TelemetryChallenge.SignIn At(
        string? city, string? os = null, string? browser = null, int minutesAgo = 0) =>
        new(DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), city, null, "MY", "EntraGuard-RP", os, browser);

    [Fact]
    public void Only_one_question_asks_where_the_user_was()
    {
        // Two sign-ins from genuinely different cities — the case that used to produce a
        // second location question. IP geolocation moves one desk between neighbouring
        // cities across a week, so "a different city" is usually the same place renamed.
        var questions = TelemetryChallenge.Compose(
            [At("Petaling Jaya", os: "Windows"), At("Kuala Lumpur", minutesAgo: 90)], 3);

        questions.Count(q =>
            q.Question.Contains("town", StringComparison.OrdinalIgnoreCase)
            || q.Question.Contains("city", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1);
    }

    [Fact]
    public void Nothing_asks_for_the_application_name()
    {
        // appDisplayName is the app REGISTRATION name — "EntraGuard-RP" — which the user
        // knows as "Contoso Treasury" and would never say aloud.
        var questions = TelemetryChallenge.Compose([At("Petaling Jaya", os: "Windows")], 3);

        questions.Should().NotContain(q =>
            q.Question.Contains("application", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_city_the_state_and_the_country_are_all_accepted()
    {
        // Almost nobody answers with the exact suburb an IP resolves to.
        var questions = TelemetryChallenge.Compose(
            [new TelemetryChallenge.SignIn(
                DateTimeOffset.UtcNow, "Petaling Jaya", "Selangor", "MY", "EntraGuard-RP", null, null)], 3);

        questions.Should().ContainSingle();
        questions[0].ExpectedFacts.Should().Contain(["Petaling Jaya", "Selangor", "Malaysia"]);
    }

    [Fact]
    public void A_country_alone_is_still_a_question_worth_asking()
    {
        // Entra often resolves an IP to a country and no finer. "Malaysia" is a true and
        // sayable answer, so this is kept rather than discarded.
        var questions = TelemetryChallenge.Compose([At(null)], 3);

        questions.Should().ContainSingle();
        questions[0].ExpectedFacts.Should().Contain("Malaysia");
    }

    [Fact]
    public void A_sign_in_with_no_usable_detail_produces_no_questions()
    {
        // Better to fall back to the registered question than to ask something with no
        // correct answer.
        var blank = new TelemetryChallenge.SignIn(
            DateTimeOffset.UtcNow, null, null, null, "EntraGuard-RP", null, null);

        TelemetryChallenge.Compose([blank], 3).Should().BeEmpty();
    }
}
