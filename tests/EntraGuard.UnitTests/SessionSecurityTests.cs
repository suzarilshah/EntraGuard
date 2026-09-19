using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class SessionSecurityTests
{
    [Fact]
    public async Task Session_survives_service_reconstruction_and_cannot_be_moved_to_another_owner()
    {
        var store = new MemoryStateStore(); var clock = new TestClock(); var config = new ConfigurationBuilder().Build();
        var service = new RpSessionService(store, clock, config);
        var created = await service.CreateAsync(TestIdentity.Principal(clock), default);
        var restarted = new RpSessionService(store, clock, config);
        Assert.NotNull(await restarted.ValidateAsync(created.Token));
        Assert.Null(await restarted.ValidateAsync(created.Token.Replace(Guid.Parse(TestIdentity.Subject).ToString("N"), Guid.Parse(TestIdentity.Other).ToString("N"))));
        Assert.Null(await restarted.ValidateAsync(created.Token[..^1] + (created.Token[^1] == 'a' ? 'b' : 'a')));
    }

    [Fact]
    public async Task Logout_and_expiration_revoke_authority_server_side()
    {
        var clock = new TestClock(); var service = new RpSessionService(new MemoryStateStore(), clock, new ConfigurationBuilder().Build());
        var a = await service.CreateAsync(TestIdentity.Principal(clock), default);
        var b = await service.CreateAsync(TestIdentity.Principal(clock), default);
        await service.RevokeAsync(RpSessionService.Principal(a.Session), default);
        Assert.Null(await service.ValidateAsync(a.Token)); Assert.NotNull(await service.ValidateAsync(b.Token));
        clock.Now = clock.Now.AddHours(2);
        Assert.Null(await service.ValidateAsync(b.Token));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/other/v2.0")]
    [InlineData("https://sts.windows.net/not-a-tenant/")]
    [InlineData("https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111/extra/v2.0")]
    public void Issuer_must_match_exact_tenant(string issuer) =>
        Assert.Throws<SecurityTokenInvalidIssuerException>(() => VoiceProfileAuth.ValidateIssuer(issuer, TestIdentity.Tenant));

    [Fact]
    public void Callback_capability_is_bound_to_path_expiration_and_signature()
    {
        var clock = new TestClock(); var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CALLBACK_SIGNING_KEY"] = Convert.ToBase64String(new byte[32]) }).Build();
        var protection = new TransportProtection(config, clock);
        var signed = new Uri(protection.Url("https://example.test/api/callbacks/one"));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(signed.Query);
        Assert.True(protection.Validate(signed.AbsolutePath, query["expires"]!, query["signature"]!));
        Assert.False(protection.Validate("/api/callbacks/two", query["expires"]!, query["signature"]!));
        Assert.False(protection.Validate(signed.AbsolutePath, query["expires"]!, "BAD"));
        clock.Now = clock.Now.AddMinutes(22);
        Assert.False(protection.Validate(signed.AbsolutePath, query["expires"]!, query["signature"]!));
    }

    [Fact]
    public async Task Device_heartbeats_require_owner_session_and_registration_and_revoke_is_effective()
    {
        var clock = new TestClock(); var devices = new DeviceService(new MemoryStateStore(), clock);
        var owner = Owner.From(TestIdentity.Principal(clock));
        await devices.RegisterAsync(owner, "phone", "session-a", () => Task.FromResult("acs-a"), default);
        Assert.False(await devices.HeartbeatAsync(owner, "phone", "acs-a", "session-b", default));
        Assert.True(await devices.HeartbeatAsync(owner, "phone", "acs-a", "session-a", default));
        Assert.True(devices.Reachable((await devices.GetAsync(owner, "phone"))!));
        Assert.Null(await devices.GetAsync(Owner.From(TestIdentity.Principal(clock, TestIdentity.Other)), "phone"));
        await devices.RevokeAsync(owner, "phone", default);
        Assert.False(await devices.HeartbeatAsync(owner, "phone", "acs-a", "session-a", default));
    }
}
