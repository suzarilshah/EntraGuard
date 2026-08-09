using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sinks;
using EntraGuard.MediaService.Tools;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Executes the actions the policy gate authorised.
///
/// The division of labour is the whole safety argument:
///   · <see cref="AnalystAgent"/> reasons about the conversation — model-driven, fallible.
///   · <see cref="PolicyGate"/> decides what may happen — deterministic, tested, auditable.
///   · This class carries it out — mechanical, ordered, and it records what really happened.
///
/// The Actuator never re-litigates the decision. If it could override the gate, the gate
/// would not be a guarantee, and "an LLM decided to lock out this user" would be a true
/// statement about the system.
/// </summary>
public sealed class ActuatorAgent(
    IEnumerable<IRemediationTool> tools,
    LogsIngestionSink sink,
    IHubContext<LiveHub> hub,
    ILogger<ActuatorAgent> logger)
{
    private readonly Dictionary<RemediationAction, IRemediationTool> _tools =
        tools.ToDictionary(tool => tool.Action);

    /// <summary>
    /// Execute a decision.
    ///
    /// Actions run sequentially in escalation order, not in parallel: a hang-up racing the
    /// warning that is meant to precede it would silence the warning.
    /// </summary>
    public async Task<IReadOnlyList<RemediationResult>> ExecuteAsync(
        CallSession session,
        PolicyDecision decision,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RemediationResult>();

        await hub.Clients.All.SendAsync(LiveHub.DecisionEvent, new
        {
            sessionId = session.SessionId,
            effectiveRisk = decision.EffectiveRisk,
            summary = decision.Summary,
            actions = decision.Actions.Select(a => a.ToString()),
            blocked = decision.Blocked.Select(b => new { action = b.Action.ToString(), b.Reason }),
            intervening = decision.IsIntervening,
            at = DateTimeOffset.UtcNow,
        }, cancellationToken);

        // Withheld actions are recorded too. An audit trail that only shows what happened,
        // and never what was considered and declined, cannot answer "why didn't it act?".
        foreach (var blocked in decision.Blocked)
        {
            await sink.WriteRemediationAsync(
                session,
                new RemediationResult(blocked.Action, RemediationOutcome.BlockedByPolicy, blocked.Reason),
                decidedBy: "PolicyGate",
                cancellationToken);
        }

        foreach (var action in decision.Actions)
        {
            if (!_tools.TryGetValue(action, out var tool))
            {
                logger.LogError("No tool registered for {Action}.", action);
                continue;
            }

            // Telemetry is per-assessment; everything else fires at most once per session.
            // TryMarkExecuted settles the race when two assessments land close together.
            if (action != RemediationAction.LogTelemetry && !session.TryMarkExecuted(action))
            {
                continue;
            }

            RemediationResult result;
            try
            {
                result = await tool.ExecuteAsync(session, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Remediation {Action} threw for {SessionId}.", action, session.SessionId);
                result = RemediationResult.Failed(action, $"Unhandled error: {ex.Message}");
            }

            results.Add(result);

            if (action != RemediationAction.LogTelemetry)
            {
                await sink.WriteRemediationAsync(session, result, decidedBy: "Actuator", cancellationToken);

                await hub.Clients.All.SendAsync(LiveHub.RemediationEvent, new
                {
                    sessionId = session.SessionId,
                    action = result.Action.ToString(),
                    outcome = result.Outcome.ToString(),
                    result.Reason,
                    ladderRung = result.Action.LadderRung(),
                    result.DurationMs,
                    at = DateTimeOffset.UtcNow,
                }, cancellationToken);
            }

            // An action that could not run must not block the rest of the ladder. This is
            // exactly the degraded path: risk elevation reports Unavailable on a tenant
            // without P2, and session revocation still runs immediately after.
            if (result.Outcome == RemediationOutcome.Unavailable)
            {
                logger.LogInformation("{Action} unavailable for {SessionId}: {Reason}",
                    action, session.SessionId, result.Reason);
                session.TryMarkExecuted(action);
            }
        }

        return results;
    }
}
