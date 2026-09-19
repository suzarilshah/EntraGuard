using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EntraGuard.MediaService.Auth;

/// <summary>Who Entra says is signing in, once its token has been checked.</summary>
/// <param name="Subject">The <c>sub</c>, which the response must echo exactly.</param>
/// <param name="ObjectId">The <c>oid</c> — stable per user per tenant, so this is who we ring.</param>
/// <param name="TenantId">The <c>tid</c> — the user's home tenant.</param>
/// <param name="PreferredUsername">Display only. Microsoft states it is not unique.</param>
public sealed record EamHint(
    string Subject,
    string ObjectId,
    string TenantId,
    string? PreferredUsername);

/// <summary>
/// Validates the <c>id_token_hint</c> Entra presents when it asks for a second factor.
///
/// <para>
/// This signature is the only thing that makes the authorization endpoint safe to expose
/// without a session. Everything the flow goes on to do — whose phone rings, whose sign-in
/// is granted — comes from claims inside this token, so an unvalidated hint would let anyone
/// who can reach the endpoint nominate any user in any tenant.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime is deliberately NOT validated, and that is not a weakening.</b> Microsoft
/// issues the hint already expired, precisely so it cannot be replayed as a credential
/// anywhere else: "To prevent the token from being used for anything other than a hint, it's
/// issued in the expired state." Rejecting it for being expired would reject every genuine
/// request.
/// </para>
///
/// <para>
/// What still has to hold is checked: Microsoft's signature over the bytes, a real Entra
/// issuer matching the token's own tenant, and an audience equal to our client ID. The last
/// one is what stops a hint minted for a different provider being forwarded here.
/// </para>
/// </remarks>
public sealed class EamHintValidator(IConfiguration config, ILogger<EamHintValidator> logger)
{
    /// <summary>
    /// Built against "common" because this is multitenant by nature: an External
    /// Authentication Method serves every tenant that consented, and pinning one would
    /// reject the rest.
    /// </summary>
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> Metadata =
        new("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

    /// <summary>The client ID Entra was given for this integration; our expected audience.</summary>
    public string? ClientId => config["EAM_CLIENT_ID"];

    /// <summary>Validate the hint, or return null if it cannot be trusted.</summary>
    public async Task<EamHint?> ValidateAsync(string? idTokenHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idTokenHint) || string.IsNullOrWhiteSpace(ClientId))
        {
            return null;
        }

        try
        {
            var configuration = await Metadata.GetConfigurationAsync(cancellationToken);

            var result = await new JsonWebTokenHandler().ValidateTokenAsync(idTokenHint, new TokenValidationParameters
            {
                // The audience is the client ID of OUR integration. A hint addressed to a
                // different provider must not be accepted here.
                ValidAudiences = [ClientId],

                ValidateIssuer = true,
                IssuerValidator = (issuer, token, _) => VoiceProfileAuth.ValidateIssuer(
                    issuer, token is JsonWebToken jwt ? jwt.GetClaim("tid").Value : null),

                IssuerSigningKeys = configuration.SigningKeys,
                ValidateIssuerSigningKey = true,

                // See the remarks above: Entra sends this already expired, on purpose.
                ValidateLifetime = false,
            });

            if (!result.IsValid)
            {
                logger.LogWarning(
                    "EAM: id_token_hint rejected — {Error}",
                    result.Exception?.Message ?? "invalid token");
                return null;
            }

            var claims = result.ClaimsIdentity;
            var subject = claims.FindFirst("sub")?.Value;
            var oid = claims.FindFirst("oid")?.Value
                   ?? claims.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
            var tid = claims.FindFirst("tid")?.Value
                   ?? claims.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

            // Without all three there is nobody to call and nothing to echo. Microsoft
            // documents each as present; absent means something is wrong enough not to guess
            // about.
            if (string.IsNullOrEmpty(subject) || !Guid.TryParse(oid, out _) || !Guid.TryParse(tid, out _))
            {
                logger.LogWarning(
                    "EAM: id_token_hint validated but lacked sub/oid/tid; refusing rather than guessing.");
                return null;
            }

            return new EamHint(subject, oid!, tid!, claims.FindFirst("preferred_username")?.Value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EAM: could not validate the id_token_hint.");
            return null;
        }
    }
}
