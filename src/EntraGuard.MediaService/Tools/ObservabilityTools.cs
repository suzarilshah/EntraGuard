using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Tools;

/// <summary>
/// Writes the verdict to Log Analytics. Runs on every assessment, at every risk level.
/// </summary>
public sealed class LogTelemetryTool(LogsIngestionSink sink) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.LogTelemetry;

    public string Description =>
        "Record the current assessment in the EntraGuard_CallAnalysis_CL table for Sentinel " +
        "correlation and post-incident review.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await sink.WriteAssessmentAsync(session, session.CurrentAssessment, cancellationToken);
        stopwatch.Stop();

        return RemediationResult.Success(Action,
            "Assessment written to EntraGuard_CallAnalysis_CL.", 0, (int)stopwatch.ElapsedMilliseconds);
    }
}

/// <summary>
/// Pushes the current state to the portal's live feed over SignalR.
/// </summary>
public sealed class NotifySocTool(IHubContext<LiveHub> hub) : IRemediationTool
{
    public RemediationAction Action => RemediationAction.NotifySoc;

    public string Description =>
        "Push a real-time alert to the EntraGuard portal so a SOC analyst sees this call, its " +
        "transcript, and its risk trajectory as it happens.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        await hub.Clients.All.SendAsync("SocAlert", new
        {
            sessionId = session.SessionId,
            subjectUpn = session.SubjectUpn,
            riskScore = session.CurrentAssessment.RiskScore,
            confidence = session.CurrentAssessment.Confidence,
            stage = session.CurrentAssessment.Stage.ToString(),
            vectors = session.CurrentAssessment.Vectors.Select(v => v.ToString()),
            rationale = session.CurrentAssessment.Rationale,
            at = DateTimeOffset.UtcNow,
        }, cancellationToken);

        return RemediationResult.Success(Action, "SOC notified over the live portal feed.");
    }
}

/// <summary>
/// Creates a Microsoft Sentinel incident directly.
///
/// The deployed analytics rule also produces incidents from EntraGuard_CallAnalysis_CL,
/// but on a five-minute schedule — far too slow to appear during a live interception.
/// Creating the incident directly closes the loop while the call is still up; the
/// scheduled rule remains as the durable, query-driven path that would carry the load in
/// a real deployment.
/// </summary>
public sealed class RaiseSentinelIncidentTool(
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IConfiguration configuration,
    ILogger<RaiseSentinelIncidentTool> logger) : IRemediationTool
{
    public const string ClientName = "sentinel";

    public RemediationAction Action => RemediationAction.RaiseSentinelIncident;

    public string Description =>
        "Open a Microsoft Sentinel incident for this call so it enters the SOC queue with its " +
        "evidence, risk trajectory, and affected account attached.";

    public async Task<RemediationResult> ExecuteAsync(
        CallSession session,
        CancellationToken cancellationToken = default)
    {
        var workspaceResourceId = configuration["LAW_RESOURCE_ID"];
        if (string.IsNullOrEmpty(workspaceResourceId))
        {
            return RemediationResult.Unavailable(Action,
                "No Log Analytics workspace configured for Sentinel incident creation.");
        }

        var stopwatch = Stopwatch.StartNew();
        var assessment = session.CurrentAssessment;
        var incidentId = Guid.NewGuid().ToString();
        var url =
            $"https://management.azure.com{workspaceResourceId}/providers/Microsoft.SecurityInsights" +
            $"/incidents/{incidentId}?api-version=2024-03-01";

        var payload = new
        {
            properties = new
            {
                title = $"EntraGuard: voice social engineering against {session.SubjectUpn ?? "an unidentified user"}",
                description = $"""
                    EntraGuard intercepted a live authentication call and assessed it as a social-engineering attempt.

                    Risk score:       {assessment.RiskScore:F0}/100
                    Confidence:       {assessment.Confidence:P0}
                    Compliance stage: {assessment.Stage}
                    Vectors:          {string.Join(", ", assessment.Vectors)}
                    Session:          {session.SessionId}
                    ACS correlation:  {session.AcsCorrelationId}

                    Analyst rationale:
                    {assessment.Rationale}

                    Supporting evidence:
                    {string.Join("\n", assessment.Evidence.Select(e => $"  [{e.Speaker}] \"{e.Quote}\""))}

                    Correlate with Entra ID sign-in and MFA activity for this user in the surrounding window.
                    """,
                severity = assessment.RiskScore >= 90 ? "High" : "Medium",
                status = "New",
                classification = (string?)null,
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(payload),
        };

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        try
        {
            using var httpClient = httpClientFactory.CreateClient(ClientName);
            var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                logger.LogWarning("Raised Sentinel incident {IncidentId} for session {SessionId}.",
                    incidentId, session.SessionId);
                return RemediationResult.Success(Action,
                    $"Sentinel incident {incidentId} created.",
                    (int)response.StatusCode, (int)stopwatch.ElapsedMilliseconds);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return RemediationResult.Unavailable(Action,
                    $"Sentinel returned {(int)response.StatusCode}. The managed identity needs the " +
                    "Microsoft Sentinel Contributor role on the workspace. The scheduled analytics " +
                    "rule will still raise this incident within five minutes.",
                    (int)response.StatusCode);
            }

            return RemediationResult.Failed(Action,
                $"Sentinel returned {(int)response.StatusCode}: {ElevateUserRiskTool.Truncate(body)}",
                (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sentinel incident creation failed for {SessionId}.", session.SessionId);
            return RemediationResult.Failed(Action, $"Incident creation failed: {ex.Message}");
        }
    }
}
