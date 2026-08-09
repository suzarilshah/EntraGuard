using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Sessions;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Read APIs the portal calls, plus health probes.
///
/// The portal's live view is driven by SignalR; these endpoints exist so a page load has
/// something to render before the first event arrives, and so the state is inspectable
/// with curl when a demo misbehaves.
/// </summary>
public static class SessionApiEndpoints
{
    public static void MapSessionApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions/live", (LiveCallRegistry registry) =>
            Results.Ok(registry.Active.Select(Describe)))
            .WithName("LiveSessions");

        app.MapGet("/api/sessions/{sessionId}", (string sessionId, LiveCallRegistry registry) =>
        {
            var call = registry.Get(sessionId);
            return call is null ? Results.NotFound() : Results.Ok(DescribeDetailed(call));
        })
        .WithName("SessionDetail");

        // Lets the portal state the operating mode honestly rather than assuming the
        // happy path — this is what drives the "risk elevation unavailable" banner.
        app.MapGet("/api/config", (IOptions<EntraGuardOptions> options) => Results.Ok(new
        {
            riskTier = options.Value.RiskTier.ToString(),
            autonomousActionsEnabled = options.Value.AutonomousActionsEnabled,
            analysisIntervalMs = (int)options.Value.AnalysisInterval.TotalMilliseconds,
            analysisWindowMs = (int)options.Value.AnalysisWindow.TotalMilliseconds,
            model = options.Value.OpenAiDeployment,
        }))
        .WithName("RuntimeConfig");

        // Liveness must not depend on Azure: a Speech outage should not cause Container
        // Apps to restart a replica that is holding live calls.
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
            .ExcludeFromDescription();

        app.MapGet("/health/ready", (IOptions<EntraGuardOptions> options) =>
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(options.Value.AcsEndpoint)) missing.Add("ACS_ENDPOINT");
            if (string.IsNullOrEmpty(options.Value.PublicBaseUrl)) missing.Add("PUBLIC_BASE_URL");
            if (string.IsNullOrEmpty(options.Value.OpenAiEndpoint)) missing.Add("AOAI_ENDPOINT");
            if (string.IsNullOrEmpty(options.Value.SpeechEndpoint)) missing.Add("SPEECH_ENDPOINT");

            return missing.Count == 0
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready", missing }, statusCode: 503);
        })
        .ExcludeFromDescription();
    }

    private static object Describe(LiveCall call)
    {
        var session = call.Session;
        return new
        {
            sessionId = session.SessionId,
            startedAt = session.StartedAt,
            subjectUpn = session.SubjectUpn,
            callerIdentity = session.CallerIdentity,
            riskScore = session.CurrentAssessment.RiskScore,
            peakRisk = session.PeakRisk,
            confidence = session.CurrentAssessment.Confidence,
            stage = session.CurrentAssessment.Stage.ToString(),
            vectors = session.CurrentAssessment.Vectors.Select(v => v.ToString()),
            isActive = session.IsActive,
            actionsTaken = session.ExecutedActions.Select(a => a.ToString()),
        };
    }

    private static object DescribeDetailed(LiveCall call)
    {
        var session = call.Session;
        return new
        {
            sessionId = session.SessionId,
            startedAt = session.StartedAt,
            endedAt = session.EndedAt,
            subjectUpn = session.SubjectUpn,
            subjectObjectId = session.SubjectObjectId,
            callerIdentity = session.CallerIdentity,
            acsCorrelationId = session.AcsCorrelationId,
            isActive = session.IsActive,
            peakRisk = session.PeakRisk,
            assessment = new
            {
                riskScore = session.CurrentAssessment.RiskScore,
                confidence = session.CurrentAssessment.Confidence,
                stage = session.CurrentAssessment.Stage.ToString(),
                vectors = session.CurrentAssessment.Vectors.Select(v => v.ToString()),
                evidence = session.CurrentAssessment.Evidence.Select(e => new
                {
                    e.Quote,
                    speaker = e.Speaker.ToString(),
                }),
                session.CurrentAssessment.Rationale,
            },
            transcript = session.FinalUtterances.Select(u => new
            {
                speaker = u.Speaker.ToString(),
                u.Text,
                u.OffsetMs,
            }),
            actionsTaken = session.ExecutedActions.Select(a => a.ToString()),
        };
    }
}
