using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What a caller is told when the call never got through.
///
/// These messages are the entire diagnosis for anyone who was not watching the logs at the
/// moment it happened. A raw ACS DiagCode is not a diagnosis: it was measured in the field
/// that "480#10037" reaches the user as "Verification failed" with no indication that the
/// fix is to open Teams.
///
/// The rule the existing cases already follow: only attach advice the code actually implies.
/// Diagnostics that guess send people to reconfigure tenants that were working.
/// </summary>
public class CallFailureDiagnosisTests
{
    [Fact]
    public void A_call_that_rang_but_got_no_code_is_not_reported_as_unanswered()
    {
        CallFailureDiagnosis.Describe(neverAnswered: false, "teams", 487, 0, "Cancelled")
            .Should().Be("The verification call ended before the code was entered.");
    }

    [Fact]
    public void With_no_information_from_ACS_it_says_only_what_it_knows()
    {
        CallFailureDiagnosis.Describe(neverAnswered: true, "teams", null, null, null)
            .Should().Be("The verification call ended before it was answered.");
    }

    [Fact]
    public void The_raw_ACS_code_is_kept_because_it_is_what_support_will_ask_for()
    {
        CallFailureDiagnosis.Describe(neverAnswered: true, "teams", 480, 10037, "no endpoints")
            .Should().Contain("480/10037").And.Contain("no endpoints");
    }

    [Fact]
    public void A_480_to_Teams_says_to_sign_in_to_Teams()
    {
        // The failure this whole class exists for. ACS reached the Teams side and was told
        // there was nobody to ring — which is NOT a federation problem and NOT a timeout,
        // and previously arrived with no advice at all.
        var reason = CallFailureDiagnosis.Describe(
            neverAnswered: true, "teams", 480, 10037,
            "Target user did not have any endpoints registered with ACS.");

        reason.Should().Contain("Teams");
        reason.Should().Contain("signed in");
        reason.Should().NotContain("federation", "480 is not a federation failure");
    }

    [Fact]
    public void A_480_to_a_soft_phone_says_to_reconnect_the_device_instead()
    {
        // Same ACS code, different fix. A browser or handset endpoint is one EntraGuard
        // registers itself, so the answer is to reconnect it, not to open Teams.
        var reason = CallFailureDiagnosis.Describe(
            neverAnswered: true, "browser", 480, 10037, "no endpoints");

        reason.Should().NotContain("Teams");
        reason.Should().Contain("Connect");
    }

    [Fact]
    public void Only_a_403_to_Teams_mentions_federation()
    {
        CallFailureDiagnosis.Describe(neverAnswered: true, "teams", 403, 10124, "Forbidden")
            .Should().Contain("federation");
    }

    [Fact]
    public void A_403_to_a_non_Teams_endpoint_does_not_mention_federation()
    {
        // Federation is a Teams concept. Attaching it elsewhere sends somebody to
        // reconfigure a tenant that has nothing to do with the failure.
        CallFailureDiagnosis.Describe(neverAnswered: true, "browser", 403, 0, "Forbidden")
            .Should().NotContain("federation");
    }

    [Fact]
    public void A_487_says_it_rang_and_nobody_picked_up()
    {
        var reason = CallFailureDiagnosis.Describe(neverAnswered: true, "teams", 487, 0, "Cancelled");
        reason.Should().Contain("rang");
        reason.Should().NotContain("federation");
        reason.Should().NotContain("signed in");
    }

    [Fact]
    public void An_unrecognised_code_gets_the_facts_and_no_invented_advice()
    {
        // Diagnostics that guess are worse than diagnostics that say less.
        var reason = CallFailureDiagnosis.Describe(neverAnswered: true, "teams", 503, 99, "Service unavailable");
        reason.Should().Contain("503/99");
        reason.Should().NotContain("federation");
        reason.Should().NotContain("signed in");
        reason.Should().NotContain("rang");
    }
}
