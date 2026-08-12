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
/// <param name="AmrPresent">
/// Whether the token carried an <c>amr</c> claim at all.
///
/// Distinguished from <see cref="UsedMfa"/> because the two failures need opposite fixes:
/// a claim saying "pwd" means sign in again with a second factor, while NO claim means the
/// app registration is not emitting the optional claim yet and no amount of re-authenticating
/// will help. Collapsing them sends the user round a loop that cannot terminate.
/// </param>
/// <param name="HasAcrs">
/// Whether the token carries an <c>acrs</c> claim — Conditional Access authentication
/// context. This IS deliverable in an access token, unlike amr, so where a tenant has
/// configured it, it is sufficient on its own.
/// </param>
public sealed record CallerIdentity(
    string ObjectId, string TenantId, string Upn, bool UsedMfa, bool AmrPresent, bool HasAcrs);

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
        var amr = principal.FindAll("amr").ToArray();
        var usedMfa = amr.Any(c => c.Value.Contains("mfa", StringComparison.OrdinalIgnoreCase));

        // acrs is what Microsoft actually supports for proving authentication strength to
        // an API. amr is read too, for the rare token that carries it, but the ID token is
        // where it reliably lives — see MfaEvidence.
        var hasAcrs = principal.FindAll("acrs").Any();

        return new CallerIdentity(objectId, tenantId, upn, usedMfa, amr.Length > 0, hasAcrs);
    }
}
