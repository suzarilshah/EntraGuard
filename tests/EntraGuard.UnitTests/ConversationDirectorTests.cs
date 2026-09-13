using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Whether the call goes deeper, and how far.
///
/// Pure and deterministic for the same reason PolicyGate is: the model conducts the
/// conversation, but it does not get to decide how long to keep somebody on the phone or
/// what to ask them next.
/// </summary>
public class ConversationDirectorTests
{
    private static readonly FollowUpProbe Location =
        new("location", "And whereabouts in Malaysia, roughly?",
            ["Petaling Jaya", "Selangor"], ["Petaling Jaya", "Selangor"], 2);

    private static readonly FollowUpProbe Browser =
        new("device", "And which browser were you using on it?", ["Edge"], ["Edge"], 1);

    [Fact]
    public void A_country_only_answer_gets_asked_where_exactly()
    {
        // The case the whole design is for. "Malaysia" is true and nearly worthless.
        ConversationDirector.NextProbe([Location, Browser], "Malaysia", [], riskElevated: false)
            .Should().Be(Location);
    }

    [Fact]
    public void Someone_who_already_named_the_city_is_not_asked_again()
    {
        // They gave the strong answer first time. Asking again reads as not listening, and
        // there is nothing left to learn.
        ConversationDirector.NextProbe(
            [Location, Browser], "I was in Petaling Jaya", [], riskElevated: false)
            .Should().Be(Browser);
    }

    [Fact]
    public void Nothing_is_asked_when_the_caller_has_covered_everything()
    {
        ConversationDirector.NextProbe(
            [Location, Browser], "Petaling Jaya, on Edge", [], riskElevated: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_calm_call_gets_one_probe_and_stops()
    {
        // A genuine user's call must not run longer because a model found them interesting.
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location"], riskElevated: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_worrying_call_buys_a_second_probe()
    {
        // Rising risk buys MORE conversation, not colder conversation.
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location"], riskElevated: true)
            .Should().Be(Browser);
    }

    [Fact]
    public void Not_even_a_worrying_call_gets_a_third()
    {
        ConversationDirector.NextProbe(
            [Location, Browser], "Malaysia", ["location", "device"], riskElevated: true)
            .Should().BeNull();
    }

    [Fact]
    public void The_stronger_probe_goes_first()
    {
        // Budget is one on a calm call, so whichever is asked is the only one asked.
        ConversationDirector.NextProbe([Browser, Location], "Malaysia", [], riskElevated: false)
            .Should().Be(Location);
    }

    [Fact]
    public void One_probe_per_facet_even_when_several_were_composed()
    {
        // ComposeFollowUps builds a probe for the browser AND one for the OS, because which
        // half the caller left unsaid is not known until they speak. Asking both would ask
        // the same question twice in the caller's ears.
        var os = new FollowUpProbe(
            "device", "And what kind of machine was that on?", ["Windows"], ["Windows"], 1);

        ConversationDirector.NextProbe(
            [Browser, os], "Malaysia", ["device"], riskElevated: true)
            .Should().BeNull();
    }

    [Fact]
    public void A_caller_who_said_nothing_is_still_probed()
    {
        // Silence covers nothing, so every probe stays open. Reaching here means the primary
        // question was judged correct, so this is defensive rather than live — it must not
        // throw and must not treat empty as covering everything.
        ConversationDirector.NextProbe([Location, Browser], "", [], riskElevated: false)
            .Should().Be(Location);
    }

    [Fact]
    public void Having_nothing_to_ask_is_not_an_error()
    {
        ConversationDirector.NextProbe([], "Malaysia", [], riskElevated: true)
            .Should().BeNull();
    }

    [Fact]
    public void Coverage_ignores_how_the_caller_capitalised_it()
    {
        // Transcripts are not consistent about case, and a probe asked for something the
        // caller just said is the failure this check exists to prevent.
        ConversationDirector.Covers("i was in petaling jaya", ["Petaling Jaya"])
            .Should().BeTrue();
    }
}
