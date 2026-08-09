using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Tools;

/// <summary>
/// Graph credentials for a tenant that is not ours.
///
/// EntraGuard signs users in from any Entra tenant, but its own identity lives in one. A
/// managed identity is issued by its home directory and is meaningless in anyone else's, so
/// every app-only Graph call about a visiting user — read their sign-in history, read a
/// custom security attribute — failed with an access denial that looked like a permissions
/// bug and was actually an architectural one. The telemetry challenge silently fell back to
/// a stored question for exactly the users it was designed for.
///
/// The fix is workload identity federation. A multitenant application registration holds
/// the Graph permissions, and a federated credential lets the managed identity prove it is
/// itself and exchange that proof for a token as that application, in whichever tenant has
/// consented. No client secret exists anywhere: nothing to store in configuration, nothing
/// to rotate, nothing to leak.
///
/// A tenant that has not consented simply fails, and callers treat that as "no data" rather
/// than as an error. That is the honest behaviour — consent is the tenant's decision, and
/// EntraGuard should be usable without it, just with less to work from.
/// </summary>
public sealed class CrossTenantGraph(
    IOptions<EntraGuardOptions> options,
    TokenCredential homeCredential,
    ILogger<CrossTenantGraph> logger)
{
    /// <summary>
    /// Credentials are cached per tenant: each holds its own token cache, and rebuilding one
    /// per call would re-run the federated exchange on every question.
    /// </summary>
    private readonly ConcurrentDictionary<string, TokenCredential> _byTenant = new();

    /// <summary>Is cross-tenant access configured at all?</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.ServiceClientId);

    /// <summary>
    /// A credential that can call Graph as EntraGuard inside <paramref name="tenantId"/>.
    /// </summary>
    /// <returns>
    /// Null when federation is not configured, or when the tenant is our own — the home
    /// managed identity is already correct there, and routing it through an exchange would
    /// add a hop and a failure mode for nothing.
    /// </returns>
    public TokenCredential? For(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId) || !IsConfigured)
        {
            return null;
        }

        if (string.Equals(tenantId, options.Value.HomeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _byTenant.GetOrAdd(tenantId, tenant =>
        {
            logger.LogInformation("Building a federated Graph credential for tenant {TenantId}.", tenant);

            return new ClientAssertionCredential(
                tenant,
                options.Value.ServiceClientId,
                // The assertion IS the managed identity's own token. Requested fresh each
                // time rather than captured: assertions are short-lived by design, and a
                // stale one fails in a way that looks like a consent problem.
                async ct =>
                {
                    var token = await homeCredential.GetTokenAsync(
                        new TokenRequestContext(["api://AzureADTokenExchange/.default"]), ct);
                    return token.Token;
                });
        });
    }

    /// <summary>
    /// The credential to use for a given tenant: federated when it is someone else's, the
    /// home identity when it is ours.
    /// </summary>
    public TokenCredential Resolve(string? tenantId) => For(tenantId) ?? homeCredential;
}
