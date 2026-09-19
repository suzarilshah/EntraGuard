using System.Security.Claims;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class TransactionApprovalTests
{
    private static async Task<(MemoryStateStore Store, PaymentService Payments, Owner Owner, RpSession Session, string Verification)> Setup(bool approver = true)
    {
        var clock = new TestClock(); var store = new MemoryStateStore();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TREASURY_DEMO_LEDGER"] = "true" }).Build();
        var principal = TestIdentity.Principal(clock);
        if (approver) ((ClaimsIdentity)principal.Identity!).AddClaim(new("roles", "EntraGuard.PaymentApprover"));
        var session = (await new RpSessionService(store, clock, config).CreateAsync(principal, default)).Session;
        var policies = new TenantPolicyService(store, clock); var payments = new PaymentService(store, policies, clock, config);
        await payments.ListAsync(session.Owner, default);
        var payment = (await payments.GetAsync(session.Owner, "PR-40192", default))!;
        var ledger = new VerificationLedger(store, clock);
        var call = VerificationLedgerTests.Call(session.Owner, session, clock, "vrf-payment");
        call.TransactionId = payment.Reference; call.TransactionDigest = payment.Digest(1);
        await ledger.BeginAsync(call, default);
        call.Result = VerificationResult.Passed; call.CompletedAt = clock.Now; call.AssuranceLevel = "Substantial";
        await ledger.CompleteAsync(call, default);
        return (store, payments, session.Owner, session, call.VerificationId);
    }

    [Fact]
    public async Task Concurrent_retries_commit_one_approval_and_one_consumption()
    {
        var f = await Setup();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Payments.ApproveAsync(f.Owner, f.Session.Id, "PR-40192", f.Verification, "request-one", default)));
        Assert.All(results, r => Assert.True(r.Approved));
        Assert.Single(results.Select(r => r.Receipt!.ApprovedAt).Distinct());
        var payment = (await f.Payments.GetAsync(f.Owner, "PR-40192", default))!;
        Assert.Equal("Approved", payment.Status); Assert.Equal(2, payment.Version); Assert.True(payment.DemoOnly);
        Assert.False((await f.Payments.ApproveAsync(f.Owner, f.Session.Id, "PR-40192", f.Verification, "request-two", default)).Approved);
    }

    [Fact]
    public async Task Changed_amount_or_different_payment_invalidates_bound_verification()
    {
        var f = await Setup();
        Assert.False((await f.Payments.ApproveAsync(f.Owner, f.Session.Id, "PR-40191", f.Verification, "request-one", default)).Approved);
        var row = (await f.Store.ReadAsync(f.Owner.TenantId, PaymentService.Row("PR-40192")))!;
        await f.Store.CommitAsync(f.Owner.TenantId, [StateWrite.Put(row.Id, row.Value<TreasuryPayment>() with { AmountMinor = 1, Version = 2 }, row.Version)]);
        Assert.False((await f.Payments.ApproveAsync(f.Owner, f.Session.Id, "PR-40192", f.Verification, "request-one", default)).Approved);
    }

    [Fact]
    public async Task Approver_role_and_current_session_are_required()
    {
        var f = await Setup(false);
        Assert.False((await f.Payments.ApproveAsync(f.Owner, f.Session.Id, "PR-40192", f.Verification, "request-one", default)).Approved);
        var authorized = await Setup();
        var row = (await authorized.Store.ReadAsync(authorized.Owner.TenantId, authorized.Owner.Row("session", authorized.Session.Id)))!;
        await authorized.Store.CommitAsync(authorized.Owner.TenantId, [StateWrite.Put(row.Id, row.Value<RpSession>() with { Revoked = true }, row.Version)]);
        Assert.False((await authorized.Payments.ApproveAsync(authorized.Owner, authorized.Session.Id, "PR-40192", authorized.Verification, "request-one", default)).Approved);
    }
}
