using System.Text.Json.Serialization;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Replays a scripted conversation through the real pipeline.
///
/// Everything downstream of audio is genuine: the same Analyst agent, the same Azure
/// OpenAI deployment, the same policy gate, the same Actuator and the same Sentinel
/// writes. Only ACS and Speech are bypassed — the transcript is supplied rather than
/// recognised.
///
/// This exists because the alternative way to exercise the system is to place a real
/// phone call, which needs two ACS identities, working audio, and a quiet room. That is
/// the right final test, but it is a terrible inner loop and a fragile thing to depend on
/// in front of an audience. This endpoint makes the detection logic testable in seconds,
/// deterministically, from a browser button.
///
/// It is deliberately honest about what it is: sessions created here are flagged
/// <c>IsSimulated</c> and the portal labels them, so a replayed call can never be mistaken
/// for an intercepted one.
/// </summary>
public static class SimulationEndpoint
{
    public sealed record SimulatedLine
    {
        /// <summary>"caller" or "user".</summary>
        [JsonPropertyName("speaker")]
        public string Speaker { get; init; } = "caller";

        [JsonPropertyName("text")]
        public string Text { get; init; } = string.Empty;
    }

    public sealed record SimulationRequest
    {
        [JsonPropertyName("scenario")]
        public string Scenario { get; init; } = "helpdesk-fraud";

        [JsonPropertyName("subjectUpn")]
        public string? SubjectUpn { get; init; }

        [JsonPropertyName("subjectObjectId")]
        public string? SubjectObjectId { get; init; }

        /// <summary>Optional custom script. When absent a built-in scenario is used.</summary>
        [JsonPropertyName("lines")]
        public List<SimulatedLine>? Lines { get; init; }

        /// <summary>
        /// Milliseconds between lines. Zero runs as fast as the model allows, which is what
        /// you want for a test; a demo wants it paced so the risk gauge visibly climbs.
        /// </summary>
        [JsonPropertyName("paceMs")]
        public int PaceMs { get; init; } = 1200;
    }

    public static void MapSimulation(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/simulate/scenarios", () => Results.Ok(
            Scenarios.All.Select(s => new
            {
                id = s.Key,
                name = s.Value.Name,
                description = s.Value.Description,
                expected = s.Value.Expectation,
                lines = s.Value.Lines.Count,
            })))
            .WithName("SimulationScenarios");

        app.MapPost("/api/simulate", async (
            SimulationRequest request,
            LiveCallRegistry registry,
            AnalystAgent analyst,
            ActuatorAgent actuator,
            IOptions<EntraGuardOptions> options,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Simulation");

            var lines = request.Lines is { Count: > 0 }
                ? request.Lines
                : Scenarios.All.TryGetValue(request.Scenario, out var scenario)
                    ? scenario.Lines
                    : null;

            if (lines is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown scenario '{request.Scenario}'.",
                    available = Scenarios.All.Keys,
                });
            }

            var sessionId = $"sim-{Guid.NewGuid():N}"[..16];
            var call = registry.Create(sessionId);
            call.Session.SubjectUpn = request.SubjectUpn ?? "demo.user@contoso.com";
            call.Session.SubjectObjectId = request.SubjectObjectId;
            call.Session.CallerIdentity = "8:acs:simulated-caller";
            call.Session.IsSimulated = true;

            logger.LogInformation("Starting simulated call {SessionId} ({Scenario}, {Lines} lines).",
                sessionId, request.Scenario, lines.Count);

            await hub.Clients.All.SendAsync(LiveHub.SessionEvent, new
            {
                sessionId,
                state = "answered",
                simulated = true,
                scenario = request.Scenario,
                at = DateTimeOffset.UtcNow,
            }, cancellationToken);

            // Run detached: the caller gets the session ID immediately and watches the live
            // feed, exactly as they would for a real call.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReplayAsync(call, lines, request.PaceMs, analyst, actuator,
                        options.Value, hub, logger, call.Lifetime.Token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Simulated call {SessionId} failed.", sessionId);
                }
                finally
                {
                    await hub.Clients.All.SendAsync(LiveHub.SessionEvent, new
                    {
                        sessionId, state = "disconnected", simulated = true, at = DateTimeOffset.UtcNow,
                    }, CancellationToken.None);

                    // A real call is retired by the ACS CallDisconnected callback. A replay
                    // has no such callback, so without this the session stays IsActive
                    // forever and the overview reports phantom "calls in progress" — which
                    // is worse than a cosmetic bug, because an operator cannot tell a stale
                    // entry from an attack actually underway.
                    await registry.RemoveAsync(sessionId);
                }
            }, CancellationToken.None);

            return Results.Accepted($"/api/sessions/{sessionId}", new
            {
                sessionId,
                scenario = request.Scenario,
                lines = lines.Count,
                watch = "/live",
            });
        })
        .WithName("SimulateCall");
    }

    private static async Task ReplayAsync(
        LiveCall call,
        IReadOnlyList<SimulatedLine> lines,
        int paceMs,
        AnalystAgent analyst,
        ActuatorAgent actuator,
        EntraGuardOptions options,
        IHubContext<LiveHub> hub,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var session = call.Session;
        var offsetMs = 0L;
        var sinceLastAnalysis = 0;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var speaker = line.Speaker.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? SpeakerRole.ProtectedUser
                : SpeakerRole.Caller;

            // Advance the clock at roughly speaking pace so the timeline and the risk
            // trajectory have realistic spacing rather than collapsing to a single instant.
            var spokenMs = Math.Max(1200, (line.Text.Split(' ').Length / 2.7) * 1000);
            offsetMs += (long)spokenMs;

            session.AddUtterance(new Utterance(
                speaker, line.Text, offsetMs, DateTimeOffset.UtcNow, IsFinal: true));

            await hub.Clients.All.SendAsync(LiveHub.TranscriptEvent, new
            {
                sessionId = session.SessionId,
                speaker = speaker.ToString(),
                text = line.Text,
                offsetMs,
                isFinal = true,
                simulated = true,
            }, cancellationToken);

            if (paceMs > 0)
            {
                await Task.Delay(paceMs, cancellationToken);
            }

            // Score on the same cadence the live pipeline uses, expressed in transcript
            // time rather than wall-clock so a fast replay still produces the same number
            // of assessments a real call would.
            sinceLastAnalysis += (int)spokenMs;
            if (sinceLastAnalysis < options.AnalysisInterval.TotalMilliseconds)
            {
                continue;
            }
            sinceLastAnalysis = 0;

            await ScoreAndActAsync(call, analyst, actuator, options, hub, cancellationToken);
        }

        // Always score the finished conversation, so a short script still produces a verdict.
        await ScoreAndActAsync(call, analyst, actuator, options, hub, cancellationToken);

        logger.LogInformation("Simulated call {SessionId} complete. Peak risk {Peak}.",
            session.SessionId, session.PeakRisk);
    }

    private static async Task ScoreAndActAsync(
        LiveCall call,
        AnalystAgent analyst,
        ActuatorAgent actuator,
        EntraGuardOptions options,
        IHubContext<LiveHub> hub,
        CancellationToken cancellationToken)
    {
        var assessment = await analyst.AssessAsync(call.Session, cancellationToken);
        if (assessment is null)
        {
            return;
        }

        call.Session.CurrentAssessment = assessment;

        await hub.Clients.All.SendAsync(LiveHub.AssessmentEvent, new
        {
            sessionId = call.Session.SessionId,
            riskScore = assessment.RiskScore,
            confidence = assessment.Confidence,
            stage = assessment.Stage.ToString(),
            vectors = assessment.Vectors.Select(v => v.ToString()),
            evidence = assessment.Evidence.Select(e => new { e.Quote, speaker = e.Speaker.ToString() }),
            assessment.Rationale,
            assessment.AnalysisLatencyMs,
            simulated = true,
            at = assessment.AssessedAt,
        }, cancellationToken);

        var decision = PolicyGate.Evaluate(assessment, new PolicyContext
        {
            RiskTier = options.RiskTier,
            AlreadyExecuted = call.Session.ExecutedActions,
            HasIdentifiedSubject = !string.IsNullOrEmpty(call.Session.SubjectObjectId),
            AutonomousActionsEnabled = options.AutonomousActionsEnabled,
        });

        call.Session.PeakRisk = Math.Max(call.Session.PeakRisk, decision.EffectiveRisk);

        await actuator.ExecuteAsync(call.Session, decision, cancellationToken);
    }
}
