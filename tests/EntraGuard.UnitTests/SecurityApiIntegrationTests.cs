using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Azure.Communication.Identity;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Endpoints;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EntraGuard.UnitTests;

public sealed class SecurityApiIntegrationTests
{
    private const string Audience = "44444444-4444-4444-8444-444444444444";
    private static readonly SymmetricSecurityKey Key = new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static string Token(string subject, string? scope = "VoiceProfile.Manage", bool admin = false, string? issuer = null)
    {
        var claims = new List<Claim> { new("oid", subject), new("tid", TestIdentity.Tenant), new("preferred_username", "test@example.test") };
        if (scope is not null) claims.Add(new("scp", scope));
        if (admin) claims.Add(new("roles", "EntraGuard.TenantAdmin"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? $"https://login.microsoftonline.com/{TestIdentity.Tenant}/v2.0",
            Audience, claims, DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(20), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }

    private static async Task<WebApplication> App()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["AZURE_TENANT_ID"] = TestIdentity.Tenant, ["CALLBACK_SIGNING_KEY"] = Convert.ToBase64String(new byte[32]), ["TREASURY_DEMO_LEDGER"] = "true" });
        builder.Services.AddVoiceProfileAuth(Audience).AddRequestAuthentication();
        builder.Services.PostConfigure<JwtBearerOptions>(VoiceProfileAuth.Scheme, options =>
        {
            options.Authority = null; options.MetadataAddress = string.Empty;
            options.Configuration = new OpenIdConnectConfiguration(); options.Configuration.SigningKeys.Add(Key);
            options.TokenValidationParameters.IssuerSigningKey = Key;
        });
        builder.Services.AddSingleton<TimeProvider>(new TestClock()); builder.Services.AddSingleton<IStateStore, MemoryStateStore>();
        builder.Services.AddSingleton<RpSessionService>(); builder.Services.AddSingleton<TransportProtection>();
        builder.Services.AddSingleton<VerificationLedger>(); builder.Services.AddSingleton<GrantService>();
        builder.Services.AddSingleton<TenantPolicyService>(); builder.Services.AddSingleton<PreferenceService>();
        builder.Services.AddSingleton<DeviceService>(); builder.Services.AddSingleton<PaymentService>();
        // Registered for endpoint binding only; tests here never call external Azure/Graph/MFA dependencies.
        builder.Services.AddSingleton<ReadinessService>(_ => throw new InvalidOperationException("Unexpected external call"));
        builder.Services.AddSingleton<StepUpService>(_ => throw new InvalidOperationException("Unexpected external call"));
        builder.Services.AddSingleton<CommunicationIdentityClient>(_ => throw new InvalidOperationException("Unexpected external call"));
        var app = builder.Build(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseMiddleware<ApiAccessMiddleware>();
        app.MapRpSessions(); app.MapAccount();
        app.MapGet("/api/verify/knowledge/{tenantId}/{objectId}", () => Results.Ok(new { authorized = true }));
        app.MapPost("/api/callbacks/{id}", () => Results.Ok());
        await app.StartAsync(); return app;
    }

    private static async Task<string> SignIn(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rp/session"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Real_bearer_validation_rejects_wrong_issuer_scope_and_id_token_like_credentials()
    {
        await using var app = await App(); using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/account/history")).StatusCode);
        foreach (var token in new[] { Token(TestIdentity.Subject, issuer: "https://sts.windows.net/another-tenant/"), Token(TestIdentity.Subject, "User.Read"), Token(TestIdentity.Subject, null, admin: true) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rp/session"); request.Headers.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Http_session_enforces_owner_policy_roles_grants_and_logout()
    {
        await using var app = await App(); using var alice = app.GetTestClient(); using var bob = app.GetTestClient();
        var token = await SignIn(alice, Token(TestIdentity.Subject)); alice.DefaultRequestHeaders.Add(RpSessionService.Header, token);
        bob.DefaultRequestHeaders.Add(RpSessionService.Header, await SignIn(bob, Token(TestIdentity.Other)));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/verify/knowledge/{TestIdentity.Tenant}/{TestIdentity.Other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/account/policy", new TenantPolicy())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/account/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/API/OPERATOR/SESSION/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.PostAsync("/API/CALLBACKS/one", null)).StatusCode);
        var session = (await app.Services.GetRequiredService<RpSessionService>().ValidateAsync(token))!;
        var ledger = app.Services.GetRequiredService<VerificationLedger>();
        var call = VerificationLedgerTests.Call(session.Owner, session, (TestClock)app.Services.GetRequiredService<TimeProvider>(), "v-http");
        await ledger.BeginAsync(call, default); call.Result = VerificationResult.Passed; call.CompletedAt = DateTimeOffset.UtcNow; call.AssuranceLevel = "Low"; await ledger.CompleteAsync(call, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostAsJsonAsync("/api/account/grant", new { verificationId = call.VerificationId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/account/grant", new { verificationId = call.VerificationId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/account/payments")).StatusCode);
        var history = await bob.GetFromJsonAsync<JsonElement>("/api/account/history"); Assert.Equal(0, history.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/rp/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alice.GetAsync("/api/account/history")).StatusCode);
    }

    [Fact]
    public async Task Http_callbacks_require_path_bound_signatures()
    {
        await using var app = await App(); using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/callbacks/one", null)).StatusCode);
        var url = app.Services.GetRequiredService<TransportProtection>().Url("http://localhost/api/callbacks/one");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(url.Replace("/one?", "/two?"), null)).StatusCode);
    }
}
