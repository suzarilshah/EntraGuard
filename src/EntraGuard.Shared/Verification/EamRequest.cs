using System.Text.Json;

namespace EntraGuard.Shared.Verification;

/// <summary>
/// One authentication request from Microsoft Entra ID, after its token has been validated.
/// </summary>
/// <remarks>
/// A record rather than an <c>HttpContext</c> read in place, so the claim mapping that
/// decides whether a sign-in is granted can be tested without standing up a web server.
/// Everything here is either echoed back to Entra or used to find the user; nothing is
/// trusted that did not arrive inside the signed <c>id_token_hint</c>.
/// </remarks>
/// <param name="ClientId">The client ID Entra was configured with. Becomes our <c>aud</c>.</param>
/// <param name="RedirectUri">Where the response is POSTed. Must be a published Entra URL.</param>
/// <param name="Nonce">Entra's random value. Must be echoed in the response token.</param>
/// <param name="State">Entra's context. Echoed alongside the token when present.</param>
/// <param name="Subject">
/// The <c>sub</c> from the hint. Must be echoed EXACTLY — Entra rejects a response whose
/// subject does not match the request that started it, which is what stops a token minted
/// for one user being replayed to authenticate another.
/// </param>
/// <param name="ObjectId">The user's <c>oid</c>. Stable across applications within a tenant.</param>
/// <param name="TenantId">The user's <c>tid</c>. With ObjectId, identifies who to call.</param>
/// <param name="PreferredUsername">Display only. Not unique, per Microsoft.</param>
/// <param name="RequestedAcr">The <c>acr</c> values the request will accept, in preference order.</param>
/// <param name="RequestedAmr">The <c>amr</c> methods the request will accept.</param>
public sealed record EamRequest(
    string ClientId,
    string RedirectUri,
    string? Nonce,
    string? State,
    string Subject,
    string ObjectId,
    string TenantId,
    string? PreferredUsername,
    IReadOnlyList<string> RequestedAcr,
    IReadOnlyList<string> RequestedAmr)
{
    /// <summary>
    /// The redirect URIs Entra publishes for this flow, per cloud.
    /// </summary>
    /// <remarks>
    /// Checked rather than trusted. <c>redirect_uri</c> arrives in the request body, and
    /// posting a freshly minted identity token to whatever address the request asked for is
    /// how a provider becomes an oracle that signs assertions for anyone who can reach it.
    /// Microsoft publishes these three; nothing else is a legitimate destination.
    /// </remarks>
    public static readonly string[] PublishedRedirectUris =
    [
        "https://login.microsoftonline.com/common/federation/externalauthprovider",
        "https://login.microsoftonline.us/common/federation/externalauthprovider",
        "https://login.partner.microsoftonline.cn/common/federation/externalauthprovider",
    ];

    /// <summary>Is this a destination Microsoft actually publishes?</summary>
    public static bool IsPublishedRedirect(string? uri) =>
        uri is not null && PublishedRedirectUris.Contains(uri, StringComparer.Ordinal);
}

/// <summary>
/// Reads the OIDC <c>claims</c> request parameter.
/// </summary>
/// <remarks>
/// Its own type because it parses attacker-adjacent JSON — the parameter arrives in a form
/// POST — and because what it returns decides which assertion is permissible. Malformed
/// input yields empty lists rather than an exception: an unreadable request should end as a
/// declined sign-in, not a 500 that tells Entra the provider is broken.
/// </remarks>
public static class EamClaimsRequest
{
    /// <summary>
    /// Pull the requested <c>acr</c> and <c>amr</c> values out of the <c>claims</c> blob.
    /// </summary>
    /// <remarks>
    /// The shape Entra sends is
    /// <c>{"id_token":{"acr":{"essential":true,"values":[...]},"amr":{...}}}</c>. Only
    /// <c>id_token</c> is read; a <c>userinfo</c> section, if one ever appears, is not ours
    /// to honour.
    /// </remarks>
    public static (IReadOnlyList<string> Acr, IReadOnlyList<string> Amr) Parse(string? claims)
    {
        if (string.IsNullOrWhiteSpace(claims))
        {
            return ([], []);
        }

        try
        {
            using var document = JsonDocument.Parse(claims);

            // The root kind is checked before TryGetProperty, which throws
            // InvalidOperationException rather than returning false when the root is an
            // array or a bare value — a different exception type than the JsonException
            // caught below, so "[]" would have escaped as a 500.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id_token", out var idToken)
                || idToken.ValueKind != JsonValueKind.Object)
            {
                return ([], []);
            }

            return (Values(idToken, "acr"), Values(idToken, "amr"));
        }
        catch (JsonException)
        {
            return ([], []);
        }
    }

    private static IReadOnlyList<string> Values(JsonElement idToken, string name)
    {
        if (!idToken.TryGetProperty(name, out var claim)
            || claim.ValueKind != JsonValueKind.Object
            || !claim.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. values.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .Where(v => !string.IsNullOrEmpty(v))];
    }
}
