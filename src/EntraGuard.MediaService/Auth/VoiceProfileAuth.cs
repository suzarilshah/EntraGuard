using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace EntraGuard.MediaService.Auth;

/// <summary>
/// Token validation for the voice-profile endpoints.
///
/// This is the only authenticated surface in the service, and it is authenticated because
/// of what sits behind it. Every other endpoint identifies a user by a string in the request
/// body; for voice enrolment that would mean an attacker could enrol their own voice against
/// somebody else's account, after which the biometric check confirms the attacker and
/// refuses the owner. A stolen password can be changed. This cannot.
/// </summary>
public static class VoiceProfileAuth
{
    public const string Policy = "VoiceProfile";
    public const string Scheme = "EntraGuardVoice";

    /// <summary>Scope the token must carry.</summary>
    private const string RequiredScope = "VoiceProfile.Manage";

    public static IServiceCollection AddVoiceProfileAuth(
        this IServiceCollection services, string rpClientId)
    {
        services
            .AddAuthentication(Scheme)
            .AddJwtBearer(Scheme, options =>
            {
                options.MapInboundClaims = false;
                // "organizations" rather than a specific tenant. Sign-in is multitenant, so
                // a user's token is issued by THEIR directory — pinning ours would reject
                // every visitor, which is most of the intended audience.
                options.Authority = "https://login.microsoftonline.com/organizations/v2.0";

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // Both forms: v2.0 tokens for a custom API carry the bare app id or the
                    // api:// URI depending on how the client requested them.
                    ValidAudiences = [rpClientId, $"api://{rpClientId}"],

                    // Issuer is checked, but against a pattern rather than a fixed value,
                    // because a multitenant app legitimately sees one issuer per tenant.
                    // Turning validation off entirely would accept a token minted by any
                    // Microsoft-signed issuer for any audience we happen to match.
                    ValidateIssuer = true,

                    // BOTH issuer forms, because Entra legitimately mints either.
                    //
                    // v2.0 tokens carry https://login.microsoftonline.com/{tid}/v2.0. v1.0
                    // tokens carry https://sts.windows.net/{tid}/, and an app registration
                    // issues v1 unless requestedAccessTokenVersion says otherwise — which
                    // is the default, and is why real sign-ins were rejected while every
                    // synthetic test passed. The registration now asks for v2, but a
                    // validator that only accepts one form breaks again the moment any
                    // tenant or client produces the other.
                    IssuerValidator = (issuer, token, _) => ValidateIssuer(issuer,
                        token is JwtSecurityToken jwt ? jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value
                            : token is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken json ? json.GetClaim("tid").Value : null),

                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                };

                // A 401 from the framework carries no body and no reason. Without this the
                // only signal reaching anyone is an empty response, which is indistinguishable
                // from the endpoint not existing.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/hubs/live"))
                            context.Token = context.Request.Query["access_token"];
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("VoiceProfileAuth")
                            .LogWarning(
                                "Voice-profile token REJECTED: {Type}: {Message}",
                                context.Exception.GetType().Name, context.Exception.Message);
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var scp = context.Principal?.FindFirst("scp")?.Value ?? "(none)";
                        var aud = context.Principal?.FindFirst("aud")?.Value ?? "(none)";
                        var amr = string.Join(",", context.Principal?.FindAll("amr").Select(c => c.Value) ?? []);

                        context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("VoiceProfileAuth")
                            .LogInformation(
                                "Voice-profile token accepted: aud={Aud} scp={Scp} amr=[{Amr}]",
                                aud, scp, string.IsNullOrEmpty(amr) ? "absent" : amr);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy =>
            {
                policy.AuthenticationSchemes = [Scheme];
                policy.RequireAuthenticatedUser();

                // The scope must be present. A token issued to this app for Graph, or for
                // any other purpose, is not permission to touch a biometric.
                policy.RequireAssertion(context =>
                {
                    var scopes = context.User.FindFirst("scp")?.Value
                                 ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;

                    return scopes is not null
                        && scopes.Split(' ').Contains(RequiredScope, StringComparer.Ordinal);
                });
            });

        return services;
    }

    public static string ValidateIssuer(string issuer, string? tenant)
    {
        if (Guid.TryParse(tenant, out var id)
            && (issuer == $"https://login.microsoftonline.com/{id:D}/v2.0" || issuer == $"https://sts.windows.net/{id:D}/"))
            return issuer;
        throw new SecurityTokenInvalidIssuerException("Issuer must exactly match the token tenant.");
    }
}
