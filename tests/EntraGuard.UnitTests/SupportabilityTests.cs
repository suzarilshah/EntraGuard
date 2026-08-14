using EntraGuard.Shared.Supportability;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The resilience primitive that sheds calls to a dead dependency.
///
/// These pin the two properties that make it safe to put on a live call path: it never
/// throws its own failure at the caller, and it never counts the call ending as the
/// dependency being broken.
/// </summary>
public class ResilientTests
{
    [Fact]
    public async Task A_working_dependency_is_called_once_and_returns_its_value()
    {
        var circuit = new Resilient("test");
        var calls = 0;

        var result = await circuit.RunAsync(_ => { calls++; return Task.FromResult(42); }, fallback: 0);

        result.Should().Be(42);
        calls.Should().Be(1, "a success must not be retried");
        circuit.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        var circuit = new Resilient("test", maxAttempts: 3);
        var calls = 0;

        var result = await circuit.RunAsync(
            _ =>
            {
                calls++;
                if (calls < 3) throw new HttpRequestException("connection reset");
                return Task.FromResult("recovered");
            },
            fallback: "gave up");

        result.Should().Be("recovered");
        calls.Should().Be(3);
        circuit.ConsecutiveFailures.Should().Be(0, "a run that ends in success is not a failure");
    }

    [Fact]
    public async Task Exhausted_attempts_return_the_fallback_rather_than_throwing()
    {
        // The property that makes this safe on a call path. A supportability mechanism that
        // can abort a live authentication is not an improvement on the problem it solves.
        var circuit = new Resilient("test", maxAttempts: 2);

        var result = await circuit.RunAsync<string>(
            _ => throw new HttpRequestException("down"), fallback: "fallback");

        result.Should().Be("fallback");
        circuit.LastError.Should().Contain("down");
    }

    [Fact]
    public async Task The_circuit_opens_after_repeated_failure_and_sheds_without_calling()
    {
        var circuit = new Resilient("test", failuresBeforeOpen: 2, maxAttempts: 1);

        for (var i = 0; i < 2; i++)
        {
            await circuit.RunAsync<string>(_ => throw new HttpRequestException("down"), "fallback");
        }

        circuit.IsOpen.Should().BeTrue();

        var called = false;
        var shed = string.Empty;

        var result = await circuit.RunAsync<string>(
            _ => { called = true; return Task.FromResult("live"); },
            fallback: "shed",
            onShed: reason => shed = reason);

        called.Should().BeFalse("an open circuit must not dial a dependency it knows is down");
        result.Should().Be("shed");
        shed.Should().Contain("circuit open", "shedding must be visible, not merely quiet");
    }

    [Fact]
    public async Task Cancelling_the_call_is_not_a_dependency_failure()
    {
        // A call ending mid-request must not count towards opening the circuit. Otherwise a
        // few hang-ups in a row would shed a perfectly healthy dependency for everyone else.
        var circuit = new Resilient("test");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await circuit.RunAsync<string>(
            token => { token.ThrowIfCancellationRequested(); return Task.FromResult("x"); },
            fallback: "fallback",
            cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        circuit.ConsecutiveFailures.Should().Be(0);
    }
}

/// <summary>
/// A fault must explain itself well enough that the next reader does not have to reconstruct
/// the reasoning from source. Every factory is checked for all four parts.
/// </summary>
public class FaultTests
{
    public static TheoryData<Fault> AllFaults() =>
    [
        Fault.TelemetryUnavailable("t", "o", "403", hadStoredQuestion: true),
        Fault.TelemetryUnavailable("t", "o", "403", hadStoredQuestion: false),
        Fault.VoiceNotAssessed("v1", "user@contoso.com", "only 1.4s of speech"),
        Fault.IngestionFailed("Custom-EntraGuard_Verification_CL", "403"),
        Fault.DependencyOpen(FaultComponent.Voice, "The scorer", "5 failures"),
    ];

    [Theory]
    [MemberData(nameof(AllFaults))]
    public void Every_fault_says_what_failed_what_it_meant_why_and_what_to_do(Fault fault)
    {
        fault.Code.Should().MatchRegex("^[a-z_]+\\.[a-z_]+$", "codes are grouped on, so they must be stable and uniform");
        fault.WhatFailed.Should().NotBeNullOrWhiteSpace();
        fault.UserImpact.Should().NotBeNullOrWhiteSpace("a fault nobody can relate to a person is noise");
        fault.ProbableCause.Should().NotBeNullOrWhiteSpace();
        fault.Remediation.Should().NotBeNullOrWhiteSpace("a fault with no next action wastes the reader's time");
    }

    [Fact]
    public void A_silent_downgrade_is_reported_as_degraded_not_as_success()
    {
        // The category that has cost this project the most: the call completes, the user gets
        // an answer, and a materially weaker mechanism was used without anything saying so.
        Fault.TelemetryUnavailable("t", "o", "403", hadStoredQuestion: true)
            .Severity.Should().Be(FaultSeverity.Degraded);
    }

    [Fact]
    public void The_impact_of_losing_telemetry_depends_on_whether_anything_replaced_it()
    {
        var withFallback = Fault.TelemetryUnavailable("t", "o", "403", hadStoredQuestion: true);
        var without = Fault.TelemetryUnavailable("t", "o", "403", hadStoredQuestion: false);

        withFallback.UserImpact.Should().NotBe(without.UserImpact);
        without.UserImpact.Should().Contain("number match", "with nothing to fall back to, that is all that remains");
    }
}
