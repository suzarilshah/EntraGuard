using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Auth;

/// <summary>
/// Establishes whether the caller authenticated with a second factor.
///
/// The obvious approach does not work, and failing quietly is what made it expensive: the
/// <c>amr</c> claim cannot be delivered in an access token. Entra emits it in ID tokens and
/// SAML assertions only, so adding it to an app registration's access-token optional claims
/// is accepted, stored, displayed in the manifest — and silently ignored. The check looked
/// configured and could never have passed.
///
/// So the evidence comes from the ID token, which does carry it. That token is NOT trusted
/// because the browser sent it: it is validated here — Entra's signature, the expected
/// audience, a real Entra issuer, an unexpired lifetime — and then cross-checked to be the
/// same person as the access token that authenticated the request. A caller who forges one
/// or replays somebody else's gets nothing, because the subject would not match.
///
/// The alternative Microsoft intends for this is a Conditional Access authentication
/// context and the <c>acrs</c> claim, which IS available in access tokens. It needs Entra
/// ID P1 and per-tenant policy configuration, so it cannot be the only path for a
/// multitenant application whose users' tenants configure nothing. <see cref="HasAcrs"/>
/// accepts it where it exists.
/// </summary>
public sealed class MfaEvidence(IOptions<Configuration.EntraGuardOptions> options, ILogger<MfaEvidence> logger)
{
    /// <summary>
    /// Metadata is fetched once and refreshed on Entra's own schedule.
    ///
    /// Built against "common" because sign-in is multitenant: the signing keys are shared,
    /// and pinning a tenant would reject every visitor.
    /// </summary>
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> Metadata =
        new("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

    private readonly IOptions<Configuration.EntraGuardOptions> _options = options;

    /// <summary>
    /// Did this caller prove multi-factor authentication?
    /// </summary>
    /// <param name="caller">Identity from the already-validated access token.</param>
    /// <param name="idToken">Raw ID token the browser supplied, or null.</param>
    /// <returns>
    /// A reason string when the answer is no, so the caller can be told which of several
    /// quite different problems they have. Null means yes.
    /// </returns>
    public async Task<string?> WhyNotMfaAsync(
        CallerIdentity caller, string? idToken, CancellationToken cancellationToken)
    {
        // Already proven by the access token: acrs is present when a tenant has configured
        // a Conditional Access authentication context. Nothing more is needed.
        if (caller.HasAcrs)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(idToken))
        {
            return "No identity token accompanied the request, so multi-factor "
                 + "authentication could not be verified. Sign out and sign in again.";
        }

        try
        {
            var configuration = await Metadata.GetConfigurationAsync(cancellationToken);

            var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                // An ID token's audience is the CLIENT id, not the api:// URI — a different
                // value from the access token's, and getting it wrong rejects every caller.
                ValidAudiences = [_options.Value.RpClientId],

                // Same breadth as the access token path: multitenant, and Entra issues both
                // v1 and v2 issuer forms depending on the registration.
                ValidateIssuer = true,
                IssuerValidator = (issuer, _, _) =>
                    (issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal)
                        && issuer.EndsWith("/v2.0", StringComparison.Ordinal))
                    || issuer.StartsWith("https://sts.windows.net/", StringComparison.Ordinal)
                        ? issuer
                        : throw new SecurityTokenInvalidIssuerException(issuer),

                IssuerSigningKeys = configuration.SigningKeys,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            });

            if (!result.IsValid)
            {
                logger.LogWarning(
                    "MFA evidence rejected for {Upn}: {Error}",
                    caller.Upn, result.Exception?.Message ?? "invalid token");

                return "The identity token could not be validated.";
            }

            // The token must belong to the SAME person the access token authenticated.
            // Without this, anybody could present a valid ID token belonging to someone
            // else who had done MFA and borrow their second factor.
            var claims = result.ClaimsIdentity;
            var oid = claims.FindFirst("oid")?.Value
                      ?? claims.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
            var tid = claims.FindFirst("tid")?.Value
                      ?? claims.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

            if (!string.Equals(oid, caller.ObjectId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tid, caller.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "MFA evidence for {Upn} belongs to a different subject — refused.", caller.Upn);

                return "The identity token belongs to a different account.";
            }

            var amr = claims.FindAll("amr").Select(c => c.Value).ToArray();
            var usedMfa = amr.Any(v => v.Contains("mfa", StringComparison.OrdinalIgnoreCase));

            logger.LogInformation(
                "MFA evidence for {Upn}: amr=[{Amr}] -> {Result}",
                caller.Upn, string.Join(",", amr), usedMfa ? "accepted" : "not multi-factor");

            return usedMfa
                ? null
                : "This sign-in did not use a second factor"
                  + (amr.Length > 0 ? $" (it reported: {string.Join(", ", amr)})" : string.Empty)
                  + ". Sign in again and complete multi-factor authentication.";
        }
        catch (Exception ex)
        {
            // Fails CLOSED. An unverifiable second factor is not a second factor.
            logger.LogWarning(ex, "Could not validate MFA evidence for {Upn}.", caller.Upn);
            return "Multi-factor authentication could not be verified.";
        }
    }
}
