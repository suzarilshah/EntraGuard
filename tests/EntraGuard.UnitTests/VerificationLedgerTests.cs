using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class VerificationLedgerTests
{
    internal static VerificationSession Call(Owner owner, RpSession session, TestClock clock, string id) => new()
    {
        VerificationId = id, SubjectTenantId = owner.TenantId, SubjectObjectId = owner.ObjectId, SubjectUpn = owner.Upn,
        RpSessionId = session.Id, StartedAt = clock.Now, CalleeAcsId = "acs", MatchCode = "47", ViewerToken = "never-store-this",
        EndpointKind = "teams", ApplicationName = "Contoso Treasury",
    };

    [Fact]
    public async Task Receipt_is_durable_secret_free_and_completion_produces_one_outbox_item()
    {
        var clock = new TestClock(); var store = new MemoryStateStore();
        var issued = await new RpSessionService(store, clock, new ConfigurationBuilder().Build()).CreateAsync(TestIdentity.Principal(clock), default);
        var ledger = new VerificationLedger(store, clock); var v = Call(issued.Session.Owner, issued.Session, clock, "vrf-one");
        await ledger.BeginAsync(v, default);
        v.Result = VerificationResult.Passed; v.CompletedAt = clock.Now; v.AssuranceLevel = "Low";
        Assert.True(await ledger.CompleteAsync(v, default)); Assert.False(await ledger.CompleteAsync(v, default));
        var restarted = new VerificationLedger(store, clock);
        var receipt = await restarted.GetAsync(issued.Session.Owner, v.VerificationId);
        Assert.Equal("Passed", receipt!.Result);
        var stored = await store.ReadAsync(issued.Session.Owner.TenantId, VerificationLedger.RecordId(issued.Session.Owner, v.VerificationId));
        Assert.DoesNotContain("never-store-this", stored!.Json); Assert.DoesNotContain("matchCode", stored.Json, StringComparison.OrdinalIgnoreCase);
        var work = new List<PendingWork>(); await foreach (var item in store.WorkAsync("outbox")) work.Add(item);
        Assert.Single(work);
        Assert.Null(await restarted.GetAsync(Owner.From(TestIdentity.Principal(clock, TestIdentity.Other)), v.VerificationId));
    }

    [Fact]
    public async Task Grant_is_bound_to_requesting_session_and_repeat_is_idempotent()
    {
        var clock = new TestClock(); var store = new MemoryStateStore(); var config = new ConfigurationBuilder().Build();
        var sessions = new RpSessionService(store, clock, config); var a = await sessions.CreateAsync(TestIdentity.Principal(clock), default);
        var b = await sessions.CreateAsync(TestIdentity.Principal(clock), default);
        var ledger = new VerificationLedger(store, clock); var grants = new GrantService(store, ledger, clock, new TenantPolicyService(store, clock));
        var v = Call(a.Session.Owner, a.Session, clock, "vrf-one"); await ledger.BeginAsync(v, default);
        Assert.False((await grants.GrantAsync(a.Session.Owner, a.Session.Id, v.VerificationId, default)).Granted);
        v.Result = VerificationResult.Passed; v.CompletedAt = clock.Now; v.AssuranceLevel = "Low";
        await ledger.CompleteAsync(v, default);
        Assert.False((await grants.GrantAsync(a.Session.Owner, b.Session.Id, v.VerificationId, default)).Granted);
        Assert.True((await grants.GrantAsync(a.Session.Owner, a.Session.Id, v.VerificationId, default)).Granted);
        Assert.True((await grants.GrantAsync(a.Session.Owner, a.Session.Id, v.VerificationId, default)).Granted);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.False((await grants.CurrentAsync(a.Session.Owner, a.Session.Id, default)).Granted);
    }

    [Fact]
    public async Task History_is_owner_scoped_paginated_and_pending_calls_expire_without_a_grant()
    {
        var clock = new TestClock(); var store = new MemoryStateStore();
        var issued = await new RpSessionService(store, clock, new ConfigurationBuilder().Build()).CreateAsync(TestIdentity.Principal(clock), default);
        var ledger = new VerificationLedger(store, clock);
        for (var n = 0; n < 3; n++) { clock.Now = clock.Now.AddSeconds(1); await ledger.BeginAsync(Call(issued.Session.Owner, issued.Session, clock, "v" + n), default); }
        var first = await ledger.HistoryAsync(issued.Session.Owner, 2, null, default);
        Assert.Equal(2, first.Items.Count); Assert.Equal("v2", first.Items[0].Value<VerificationReceipt>().VerificationId);
        var second = await ledger.HistoryAsync(issued.Session.Owner, 2, first.ContinuationToken, default); Assert.Single(second.Items);
        Assert.Empty((await ledger.HistoryAsync(Owner.From(TestIdentity.Principal(clock, TestIdentity.Other)), 20, null, default)).Items);
        clock.Now = clock.Now.AddMinutes(16);
        await foreach (var work in store.WorkAsync("recovery")) await ledger.RecoverAsync(work, default);
        Assert.Equal("CallFailed", (await ledger.GetAsync(issued.Session.Owner, "v1"))!.Result);
    }
}
