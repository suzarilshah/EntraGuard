using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using EntraGuard.MediaService.Configuration;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Tools;

/// <summary>
/// Thin Microsoft Graph client.
///
/// Raw HTTP rather than the Graph SDK: EntraGuard calls four endpoints, and the SDK would
/// add a large dependency while hiding the exact requests. For a security tool the wire
/// calls being legible in the source is worth more than the ergonomics.
/// </summary>
public sealed class GraphClient(
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    CrossTenantGraph crossTenant,
    ILogger<GraphClient> logger)
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>
    /// Named client resolved per request rather than a captured <see cref="HttpClient"/>.
    ///
    /// The remediation tools are singletons. Injecting a typed HttpClient into a singleton
    /// pins one message handler for the lifetime of the process, which defeats the
    /// factory's handler rotation and leaves the connection pool holding stale DNS. This
    /// service is long-lived by design, so that matters.
    /// </summary>
    public const string ClientName = "graph";

    public async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string path,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        return await SendAsync(request, cancellationToken);
    }

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    /// <summary>
    /// Read from a specific tenant's directory, federating in when it is not ours.
    /// </summary>
    /// <remarks>
    /// A Graph token is scoped to one directory. Calling with our own token and someone
    /// else's object ID does not read their tenant — it looks the ID up in ours, finds
    /// nothing, and returns an empty result that reads exactly like "this user has no
    /// history". That silent wrong answer is why this overload exists rather than callers
    /// passing a tenant id into the path.
    /// </remarks>
    public async Task<(HttpStatusCode Status, string Body)> GetForTenantAsync(
        string path,
        string? tenantId,
        CancellationToken cancellationToken = default) =>
        await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, path),
            crossTenant.Resolve(tenantId),
            cancellationToken);

    private Task<(HttpStatusCode, string)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        SendAsync(request, credential, cancellationToken);

    private async Task<(HttpStatusCode, string)> SendAsync(
        HttpRequestMessage request,
        TokenCredential tokenCredential,
        CancellationToken cancellationToken)
    {
        var token = await tokenCredential.GetTokenAsync(
            new TokenRequestContext([GraphScope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var httpClient = httpClientFactory.CreateClient(ClientName);
        var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        logger.LogDebug("Graph {Method} {Path} → {Status}",
            request.Method, request.RequestUri, (int)response.StatusCode);

        return (response.StatusCode, body);
    }
}

/// <summary>
/// Rung 1 — mark the user compromised in Entra ID Protection.
///
/// This is EntraGuard's headline claim and the one most likely to be unavailable: the
/// riskyUsers API requires Entra ID P2, which most demo and sponsorship tenants lack.
///
/// Positioning worth being precise about: Entra's "Report suspicious activity" is a HUMAN
/// reporting an MFA prompt they did not expect. <c>confirmCompromised</c> is the supported
/// programmatic equivalent. EntraGuard files that report autonomously, from evidence the
/// user does not have — because the user is, at that moment, being actively manipulated.
/// </summary>
public sealed class ElevateUserRiskTool(
    GraphClient graph,
    IOptions<EntraGuardOptions> options,
    ILogger<ElevateUserRiskTool> logger) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.ElevateUserRisk;

    public string Description =>
        "Mark the protected user as confirmed-compromised in Microsoft Entra ID Protection, " +
        "raising their risk state to High and triggering any risk-based Conditional Access policy. " +
        "Requires Entra ID P2.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(session.SubjectObjectId))
        {
            return RemediationResult.Unavailable(Action,
                "No Entra ID subject resolved for this call.");
        }

        if (options.Value.RiskTier == TenantRiskTier.Degraded)
        {
            return RemediationResult.Unavailable(Action,
                "Tenant lacks Entra ID P2 — identityProtection/riskyUsers/confirmCompromised is unavailable.");
        }

        var stopwatch = Stopwatch.StartNew();
        var (status, body) = await graph.PostAsync(
            "identityProtection/riskyUsers/confirmCompromised",
            new { userIds = new[] { session.SubjectObjectId } },
            cancellationToken);
        stopwatch.Stop();

        if (status == HttpStatusCode.NoContent)
        {
            logger.LogWarning("Elevated {Upn} to High risk in Entra ID Protection (session {SessionId}).",
                session.SubjectUpn, session.SessionId);
            return RemediationResult.Success(Action,
                $"User {session.SubjectUpn} confirmed compromised; Entra ID Protection risk state set to High.",
                (int)status, (int)stopwatch.ElapsedMilliseconds);
        }

        // A 403 here is the expected licensing outcome, not a bug — report it as such so
        // the portal can state the limitation instead of showing an unexplained failure.
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return RemediationResult.Unavailable(Action,
                $"Graph returned {(int)status}. Tenant is missing Entra ID P2, or the service principal " +
                "lacks IdentityRiskyUser.ReadWrite.All admin consent.", (int)status);
        }

        return RemediationResult.Failed(Action,
            $"Graph returned {(int)status}: {Truncate(body)}", (int)status);
    }

    internal static string Truncate(string value, int max = 300) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// Rung 2 — invalidate every refresh token for the user.
///
/// Available on any licensing tier, which makes it the backbone of the degraded path. If
/// the attacker already captured a token, this is what actually stops them using it.
/// </summary>
public sealed class RevokeSessionsTool(
    GraphClient graph,
    ILogger<RevokeSessionsTool> logger) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.RevokeSessions;

    public string Description =>
        "Invalidate all refresh tokens and browser sessions for the protected user, forcing " +
        "reauthentication everywhere. Works on any Entra ID licensing tier.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(session.SubjectObjectId))
        {
            return RemediationResult.Unavailable(Action, "No Entra ID subject resolved for this call.");
        }

        var stopwatch = Stopwatch.StartNew();
        var (status, body) = await graph.PostAsync(
            $"users/{session.SubjectObjectId}/revokeSignInSessions",
            new { },
            cancellationToken);
        stopwatch.Stop();

        if (status is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            logger.LogWarning("Revoked all sessions for {Upn} (session {SessionId}).",
                session.SubjectUpn, session.SessionId);
            return RemediationResult.Success(Action,
                $"All refresh tokens revoked for {session.SubjectUpn}; reauthentication required everywhere.",
                (int)status, (int)stopwatch.ElapsedMilliseconds);
        }

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return RemediationResult.Unavailable(Action,
                $"Graph returned {(int)status}. Service principal lacks User.RevokeSessions.All admin consent.",
                (int)status);
        }

        return RemediationResult.Failed(Action,
            $"Graph returned {(int)status}: {ElevateUserRiskTool.Truncate(body)}", (int)status);
    }
}

/// <summary>
/// Rung 3 — add the user to a group bound to a strict Conditional Access policy.
///
/// The degraded-tier substitute for risk elevation: it reaches the same enforcement point
/// (Conditional Access) without needing the Identity Protection risk signal that P2 gates.
/// </summary>
public sealed class QuarantineUserTool(
    GraphClient graph,
    IOptions<EntraGuardOptions> options,
    ILogger<QuarantineUserTool> logger) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.QuarantineUser;

    public string Description =>
        "Add the protected user to the EntraGuard quarantine group, which a Conditional Access " +
        "policy binds to phishing-resistant MFA or an outright block. The degraded-tier " +
        "substitute for Identity Protection risk elevation.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(session.SubjectObjectId))
        {
            return RemediationResult.Unavailable(Action, "No Entra ID subject resolved for this call.");
        }

        var groupId = options.Value.QuarantineGroupId;
        if (string.IsNullOrEmpty(groupId))
        {
            return RemediationResult.Unavailable(Action,
                "No quarantine group configured. Run scripts/02-entra-apps.sh to create it and bind a Conditional Access policy.");
        }

        var stopwatch = Stopwatch.StartNew();
        var (status, body) = await graph.PostAsync(
            $"groups/{groupId}/members/$ref",
            new { @odata_id = $"https://graph.microsoft.com/v1.0/directoryObjects/{session.SubjectObjectId}" },
            cancellationToken);
        stopwatch.Stop();

        if (status is HttpStatusCode.NoContent or HttpStatusCode.Created)
        {
            logger.LogWarning("Quarantined {Upn} via Conditional Access group (session {SessionId}).",
                session.SubjectUpn, session.SessionId);
            return RemediationResult.Success(Action,
                $"{session.SubjectUpn} added to the quarantine group; Conditional Access now enforces step-up.",
                (int)status, (int)stopwatch.ElapsedMilliseconds);
        }

        // Already a member is a success from the caller's perspective — the desired end
        // state holds, which is what matters during an incident.
        if (status == HttpStatusCode.BadRequest && body.Contains("already exist", StringComparison.OrdinalIgnoreCase))
        {
            return RemediationResult.Success(Action,
                $"{session.SubjectUpn} was already quarantined.", (int)status, (int)stopwatch.ElapsedMilliseconds);
        }

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return RemediationResult.Unavailable(Action,
                $"Graph returned {(int)status}. Service principal lacks GroupMember.ReadWrite.All admin consent.",
                (int)status);
        }

        return RemediationResult.Failed(Action,
            $"Graph returned {(int)status}: {ElevateUserRiskTool.Truncate(body)}", (int)status);
    }
}
