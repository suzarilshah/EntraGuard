using System.Security.Claims;

namespace EntraGuard.MediaService.Auth;

/// <summary>
/// Who is calling, according to a token Entra signed — never according to the request body.
/// </summary>
/// <param name="ObjectId">The <c>oid</c> claim. Immutable per user per tenant.</param>
/// <param name="TenantId">The <c>tid</c> claim.</param>
/// <param name="Upn">Best available human-readable name, for logs and audit rows only.</param>
/// <param name="UsedMfa">
/// Whether the <c>amr</c> claim records a multi-factor authentication.
/// </param>
public sealed record CallerIdentity(string ObjectId, string TenantId, string Upn, bool UsedMfa);

/// <summary>
/// Reads the caller out of validated claims.
///
/// The entire point of this type is that <c>objectId</c> stops being an input. Everywhere
/// else in this service a user is identified by a field the browser filled in, which means
/// anyone who can reach the endpoint can act as anyone — for a knowledge question that is
/// bad, and for voice enrolment it would be catastrophic: an attacker enrols their own
/// voice against your account, and from then on the biometric check confirms them and
/// refuses you.
///
/// So enrolment reads identity from here or it does not happen.
/// </summary>
public static class CallerIdentityExtensions
{
    /// <summary>
    /// Extract the caller, or null when the token lacks what identifies a person.
    /// </summary>
    public static CallerIdentity? Caller(this ClaimsPrincipal principal)
    {
        // "oid" on v2.0 tokens; the long-form URI is what the handler maps it to.
        var objectId =
            principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        var tenantId =
            principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(tenantId))
        {
            return null;
        }

        var upn =
            principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst("email")?.Value
            ?? objectId;

        // amr is an ARRAY claim, so a token with several methods yields several claims.
        // "mfa" appears when Entra considers the session multi-factor; "pwd" alone does not.
        var usedMfa = principal.FindAll("amr")
            .Any(c => c.Value.Contains("mfa", StringComparison.OrdinalIgnoreCase));

        return new CallerIdentity(objectId, tenantId, upn, usedMfa);
    }
}
