using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class PreferencesTests
{
    [Fact]
    public async Task Preferences_survive_restart_reject_stale_updates_and_obey_policy()
    {
        var store = new MemoryStateStore(); var clock = new TestClock(); var policies = new TenantPolicyService(store, clock);
        var service = new PreferenceService(store, clock, policies); var owner = Owner.From(TestIdentity.Principal(clock));
        Assert.Null(await service.SaveAsync(owner, new(PreferredChannel: "phone", VerificationNotifications: false), default));
        Assert.NotNull(await service.SaveAsync(owner, new(PreferredChannel: "teams"), default));
        Assert.Equal("phone", (await new PreferenceService(store, clock, policies).GetAsync(owner, default)).PreferredChannel);
        var other = Owner.From(TestIdentity.Principal(clock, TestIdentity.Other));
        Assert.Equal("teams", (await service.GetAsync(other, default)).PreferredChannel);
        await policies.UpdateAsync(owner, new(AllowedChannels: ["teams"]), 1, default);
        Assert.NotNull(await service.SaveAsync(owner, new(Version: 2, PreferredChannel: "phone"), default));
    }

    [Fact]
    public async Task Notification_preferences_control_atomic_inbox_delivery_and_owner_only_reading()
    {
        var store = new MemoryStateStore(); var clock = new TestClock(); var policy = new TenantPolicyService(store, clock);
        var preferences = new PreferenceService(store, clock, policy);
        var session = (await new RpSessionService(store, clock, new ConfigurationBuilder().Build()).CreateAsync(TestIdentity.Principal(clock), default)).Session;
        var ledger = new VerificationLedger(store, clock);
        await preferences.SaveAsync(session.Owner, new(VerificationNotifications: false), default);
        var call = VerificationLedgerTests.Call(session.Owner, session, clock, "v1"); await ledger.BeginAsync(call, default);
        call.Result = VerificationResult.Failed; call.CompletedAt = clock.Now; await ledger.CompleteAsync(call, default);
        Assert.Empty((await preferences.InboxAsync(session.Owner, null, default)).Items);
        await preferences.SaveAsync(session.Owner, new(Version: 2, VerificationNotifications: true), default);
        call = VerificationLedgerTests.Call(session.Owner, session, clock, "v2"); await ledger.BeginAsync(call, default);
        call.Result = VerificationResult.Failed; call.CompletedAt = clock.Now; await ledger.CompleteAsync(call, default);
        var inbox = await preferences.InboxAsync(session.Owner, null, default); Assert.Single(inbox.Items);
        var notification = inbox.Items[0].Value<InboxNotification>();
        Assert.False(await preferences.MarkReadAsync(Owner.From(TestIdentity.Principal(clock, TestIdentity.Other)), notification.Id, default));
        Assert.True(await preferences.MarkReadAsync(session.Owner, notification.Id, default));
        Assert.NotNull((await preferences.InboxAsync(session.Owner, null, default)).Items[0].Value<InboxNotification>().ReadAt);
    }
}
