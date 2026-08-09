using Microsoft.AspNetCore.SignalR;

namespace EntraGuard.MediaService.Hubs;

/// <summary>
/// Live feed to the portal: transcript, risk trajectory, agent decisions, actions taken.
///
/// Server-to-client only. There are no client-callable methods, so a compromised or
/// malicious browser session cannot use this channel to influence a live call — the portal
/// observes, and every action originates from the policy gate.
///
/// In-process SignalR with no backplane is sufficient because the media service runs with
/// sticky sessions and a small replica count. A production deployment spanning replicas
/// would swap in Azure SignalR Service without touching the hub itself.
/// </summary>
public sealed class LiveHub : Hub
{
    /// <summary>Streaming transcript, including interim hypotheses.</summary>
    public const string TranscriptEvent = "Transcript";

    /// <summary>A new Analyst verdict.</summary>
    public const string AssessmentEvent = "Assessment";

    /// <summary>A policy decision and the actions it authorised.</summary>
    public const string DecisionEvent = "Decision";

    /// <summary>The outcome of one executed remediation.</summary>
    public const string RemediationEvent = "Remediation";

    /// <summary>A call started or ended.</summary>
    public const string SessionEvent = "Session";

    /// <summary>A step-up verification attempt was started or adjudicated.</summary>
    public const string VerificationEvent = "Verification";
}
