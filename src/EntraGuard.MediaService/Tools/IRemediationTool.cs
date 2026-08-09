using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;

namespace EntraGuard.MediaService.Tools;

/// <summary>What actually happened when a remediation was attempted.</summary>
public enum RemediationOutcome
{
    /// <summary>The action completed.</summary>
    Succeeded,

    /// <summary>The action was attempted and errored.</summary>
    Failed,

    /// <summary>
    /// The tenant or session cannot support this action — no Entra ID P2, no resolved
    /// subject, no live call. Distinct from <see cref="Failed"/> because it is a known
    /// limitation rather than a fault, and the portal reports it as such instead of
    /// showing a red error the operator cannot fix.
    /// </summary>
    Unavailable,

    /// <summary>The policy gate withheld the action.</summary>
    BlockedByPolicy,
}

/// <summary>
/// The result of one remediation attempt. Recorded verbatim to
/// <c>EntraGuard_Remediation_CL</c>, including failures and unavailability — an audit
/// trail that only records successes is not an audit trail.
/// </summary>
/// <param name="Action">Which action was attempted.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Human-readable detail, surfaced verbatim in the portal.</param>
/// <param name="GraphStatusCode">HTTP status from Microsoft Graph; 0 when Graph was not called.</param>
/// <param name="DurationMs">Wall-clock duration.</param>
public sealed record RemediationResult(
    RemediationAction Action,
    RemediationOutcome Outcome,
    string Reason,
    int GraphStatusCode = 0,
    int DurationMs = 0)
{
    public static RemediationResult Success(RemediationAction action, string reason, int status = 0, int ms = 0) =>
        new(action, RemediationOutcome.Succeeded, reason, status, ms);

    public static RemediationResult Unavailable(RemediationAction action, string reason, int status = 0) =>
        new(action, RemediationOutcome.Unavailable, reason, status);

    public static RemediationResult Failed(RemediationAction action, string reason, int status = 0) =>
        new(action, RemediationOutcome.Failed, reason, status);
}

/// <summary>
/// One remediation capability.
///
/// Modelled as a tool catalogue rather than a switch statement because the Actuator agent
/// selects from it by name — the same catalogue is exposed to Azure OpenAI as callable
/// tools, so the model reasons over the same list the code executes.
/// </summary>
public interface IRemediationTool
{
    /// <summary>The action this tool implements.</summary>
    RemediationAction Action { get; }

    /// <summary>Description shown to the Actuator model when it selects a tool.</summary>
    string Description { get; }

    /// <summary>Execute against a live call.</summary>
    Task<RemediationResult> ExecuteAsync(CallSession session, CancellationToken cancellationToken = default);
}
