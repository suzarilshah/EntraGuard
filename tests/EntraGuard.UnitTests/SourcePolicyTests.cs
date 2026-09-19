using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class SourcePolicyTests
{
    [Fact]
    public void Only_correctly_answered_sign_in_facts_can_reach_high()
    {
        var directory = new QuestionEvidence("Directory", "manager", true, true);
        var failedSignIn = new QuestionEvidence("SignIn", "location", true, false);
        Assert.Equal(AssuranceLevel.Low, EvidenceAssurance.Evaluate(true, [directory, failedSignIn], [], "Match").Level);
        Assert.Equal(AssuranceLevel.Substantial, EvidenceAssurance.Evaluate(true, [new("Activity", "meeting", true, true)], [], "Match").Level);
        Assert.Equal(AssuranceLevel.High, EvidenceAssurance.Evaluate(true, [failedSignIn with { Correct = true }], [], "Match").Level);
        Assert.Equal(AssuranceLevel.None, EvidenceAssurance.Evaluate(false, [failedSignIn with { Correct = true }], [], "Match").Level);
    }

    [Fact]
    public async Task Policies_are_versioned_and_stale_writers_do_not_overwrite()
    {
        var clock = new TestClock(); var store = new MemoryStateStore(); var policies = new TenantPolicyService(store, clock);
        var owner = Owner.From(TestIdentity.Principal(clock));
        Assert.True(await policies.UpdateAsync(owner, new(MinimumAssurance: "Substantial"), 1, default));
        Assert.False(await policies.UpdateAsync(owner, new(MinimumAssurance: "Low"), 1, default));
        var policy = await policies.GetAsync(owner.TenantId);
        Assert.Equal(2, policy.Version); Assert.Equal("Substantial", policy.MinimumAssurance);
        var receipt = new VerificationReceipt("v", owner, "s", "app", clock.Now, clock.Now.AddMinutes(10), "teams",
            Result: "Passed", CompletedAt: clock.Now, AssuranceLevel: "High", PolicyVersion: 1);
        Assert.Contains("changed", policy.Refusal(receipt, clock.Now)!);
        Assert.Null(policy.Refusal(receipt with { PolicyVersion = 2 }, clock.Now));
        Assert.NotNull(policy.Refusal(receipt with { PolicyVersion = 2, AssuranceLevel = "Low" }, clock.Now));
    }

    [Fact]
    public void Mfa_recovery_rejects_missing_old_and_future_authentication_events()
    {
        var now = DateTimeOffset.UtcNow; var started = now.AddMinutes(-2);
        Assert.False(MfaEvidence.FreshEnough(null, started, now));
        Assert.False(MfaEvidence.FreshEnough(now.AddMinutes(-3).ToUnixTimeSeconds().ToString(), started, now));
        Assert.False(MfaEvidence.FreshEnough(now.AddHours(1).ToUnixTimeSeconds().ToString(), started, now));
        Assert.True(MfaEvidence.FreshEnough(now.ToUnixTimeSeconds().ToString(), started, now));
    }

    [Fact]
    public void Step_up_is_not_a_grant()
    {
        var v = VerificationLedgerTests.Call(Owner.From(TestIdentity.Principal(new TestClock())),
            new RpSession(Owner.From(TestIdentity.Principal(new TestClock())), "s", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), new TestClock(), "v");
        v.Result = VerificationResult.StepUpRequired;
        Assert.False(v.GrantsAccess);
    }
}
