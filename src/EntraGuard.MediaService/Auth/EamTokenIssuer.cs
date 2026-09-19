using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Auth;

/// <summary>
/// Mints the signed <c>id_token</c> that tells Entra ID the second factor was satisfied.
///
/// <para>
/// This token IS the authentication decision. Entra validates its signature against our
/// published JWKS, checks the claims, and on that basis alone grants multifactor
/// authentication for the sign-in — to whatever application the user was reaching for.
/// </para>
/// </summary>
/// <remarks>
/// The JWT is assembled by hand rather than through a handler because the private key lives
/// in Key Vault and is never held here: the signing input is composed, hashed, and the digest
/// sent away to be signed. That is the point — there is no code path in this process that
/// could export the key, because the key was never in it.
/// </remarks>
public sealed class EamTokenIssuer(EamSigningKeys keys, TimeProvider time, ILogger<EamTokenIssuer> logger)
{
    /// <summary>
    /// How long the response token is valid.
    /// </summary>
    /// <remarks>
    /// Short by design. Entra validates it within seconds of receiving the form POST, so a
    /// longer life buys nothing and widens the window in which a token captured in transit
    /// still authenticates somebody. Five minutes covers clock skew and nothing else.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Issue the token asserting that <paramref name="request"/>'s user completed the call.
    /// </summary>
    /// <returns>The compact JWS, or null if nothing honest could be asserted.</returns>
    /// <remarks>
    /// Returns null rather than throwing when the request cannot be satisfied — when its
    /// <c>acr</c> asks for inherence and a telephone call cannot give it, for instance. That
    /// is a legitimate outcome, not a fault: the caller turns it into <c>access_denied</c>
    /// and Entra fails the sign-in exactly as the tenant's policy asked it to.
    /// </remarks>
    public async Task<string?> IssueAsync(
        EamRequest request,
        string issuer,
        CancellationToken cancellationToken)
    {
        var acr = EamClaims.SatisfiableAcr(request.RequestedAcr, FactorType.Possession);
        var amr = EamClaims.ChooseAmr(request.RequestedAmr);

        if (acr is null || amr is null)
        {
            // Not an error to be retried. The sign-in's first factor was of a type a phone
            // call cannot complement, and the only dishonest way out would be to claim the
            // voiceprint as an inherence factor while it runs in observe mode.
            logger.LogWarning(
                "EAM: cannot satisfy the request honestly — acr requested [{Acr}], amr requested "
              + "[{Amr}]. A telephone call proves possession and nothing else here.",
                string.Join(", ", request.RequestedAcr), string.Join(", ", request.RequestedAmr));
            return null;
        }

        var key = await keys.SigningKeyAsync(cancellationToken);
        if (key is null)
        {
            logger.LogError("EAM: no enabled signing key in Key Vault; cannot issue a response token.");
            return null;
        }

        var now = time.GetUtcNow();

        var header = new Dictionary<string, object>
        {
            ["typ"] = "JWT",
            ["alg"] = "RS256",
            ["kid"] = key.KeyId,
        };

        var payload = new Dictionary<string, object>
        {
            // Must match the discovery document character for character, including the
            // absence of a trailing slash and of an explicit :443.
            ["iss"] = issuer,

            // The client ID Entra was configured with — NOT the user's tenant.
            ["aud"] = request.ClientId,

            ["sub"] = request.Subject,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(Lifetime).ToUnixTimeSeconds(),
            ["acr"] = acr,

            // An array, and deliberately a single element. Entra's reference is explicit that
            // only one method claim should be returned, and the honest one is the call.
            ["amr"] = new[] { amr },
        };

        // Only when Entra sent one. An absent nonce echoed back as an empty string is a
        // different claim from no claim, and Entra checks it.
        if (!string.IsNullOrEmpty(request.Nonce))
        {
            payload["nonce"] = request.Nonce;
        }

        var signingInput =
            $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}."
          + $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}";

        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(signingInput));
        var signature = await keys.SignAsync(key, digest, cancellationToken);

        logger.LogInformation(
            "EAM: issued a response token for {Subject} (acr={Acr}, amr={Amr}, kid={Kid}).",
            request.Subject, acr, amr, key.KeyId);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] value) => EamSigningKeys.Base64Url(value);
}
