using EntraGuard.MediaService.Agents;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What the call asks AFTER an answer it has already accepted.
///
/// The location question accepts the city, the state OR the country, because Entra records
/// the city an IP resolves to and refusing a true answer refuses the genuine user. The cost
/// of that leniency is that "Malaysia" passes and is worth almost nothing — anyone who
/// dialled a +60 number can produce it. These probes buy that precision back, and because
/// the caller has already passed, they cannot refuse anybody.
/// </summary>
public class FollowUpProbeTests
{
    private static TelemetryChallenge.SignIn Sample(
        string? city = "Petaling Jaya", string? state = "Selangor",
        string? os = "Windows", string? browser = "Edge") =>
        new(DateTimeOffset.UtcNow, city, state, "MY", "EntraGuard-RP", os, browser);

    [Fact]
    public void The_location_probe_names_the_country_the_caller_would_have_said()
    {
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Should().Contain(p => p.Facet == "location");
        probes.Single(p => p.Facet == "location").Question
            .Should().Contain("Malaysia");
    }

    [Fact]
    public void The_location_probe_wants_the_city_or_the_state_and_not_the_country()
    {
        // The whole point: the country is what the primary question already accepted.
        var probe = TelemetryChallenge.ComposeFollowUps([Sample()])
            .Single(p => p.Facet == "location");

        probe.ExpectedFacts.Should().BeEquivalentTo(["Petaling Jaya", "Selangor"]);
        probe.ExpectedFacts.Should().NotContain("Malaysia");
    }

    [Fact]
    public void Location_is_stronger_than_device()
    {
        // Budget is small. A weak probe must not crowd out the one carrying the evidence.
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Single(p => p.Facet == "location").Strength
            .Should().BeGreaterThan(probes.First(p => p.Facet == "device").Strength);
    }

    [Fact]
    public void Both_halves_of_the_device_question_get_a_probe()
    {
        // The primary accepts the OS or the browser. Whichever the caller left unsaid is
        // the one worth asking for, and which that is is not known until they speak.
        var probes = TelemetryChallenge.ComposeFollowUps([Sample()]);

        probes.Where(p => p.Facet == "device")
            .SelectMany(p => p.ExpectedFacts)
            .Should().BeEquivalentTo(["Edge", "Windows"]);
    }

    [Fact]
    public void A_probe_never_asks_for_something_it_does_not_hold()
    {
        // A tenant with no location and no device detail is a normal outcome, not an error.
        TelemetryChallenge.ComposeFollowUps(
            [Sample(city: null, state: null, os: null, browser: null)])
            .Should().BeEmpty();
    }

    [Fact]
    public void Nothing_is_probed_when_the_directory_returned_nothing()
    {
        TelemetryChallenge.ComposeFollowUps([]).Should().BeEmpty();
    }

    [Fact]
    public void A_country_with_no_city_behind_it_gets_no_location_probe()
    {
        // Nothing finer to ask for. A probe with no correct answer is the exact failure
        // TelemetryChallenge.Compose already refuses to build.
        TelemetryChallenge.ComposeFollowUps([Sample(city: null, state: null)])
            .Should().NotContain(p => p.Facet == "location");
    }

    [Fact]
    public void No_probe_asks_about_the_time_of_day()
    {
        // Graph reports UTC. 09:00 in Kuala Lumpur is 01:00 UTC, so a time probe would
        // expect "night" and score a truthful "morning" as wrong.
        TelemetryChallenge.ComposeFollowUps([Sample()])
            .Should().NotContain(p =>
                p.Question.Contains("time", StringComparison.OrdinalIgnoreCase));
    }
}
