namespace EntraGuard.Shared.Policy;

/// <summary>
/// Actions EntraGuard can take against a live call. Ordered by escalating impact —
/// the policy gate emits a prefix of this ladder, never a gap in the middle.
/// </summary>
public enum RemediationAction
{
    /// <summary>Write the verdict to Log Analytics. Always taken, on every assessment.</summary>
    LogTelemetry = 0,

    /// <summary>Raise a Microsoft Sentinel incident for analyst triage.</summary>
    RaiseSentinelIncident = 1,

    /// <summary>Push a real-time alert to the EntraGuard portal / SOC feed.</summary>
    NotifySoc = 2,

    /// <summary>
    /// Synthesise speech and stream it back into the live call, warning the user.
    ///
    /// The only action that reaches the victim inside the attack window, and the reason
    /// bidirectional audio streaming is required rather than receive-only.
    /// </summary>
    InjectVoiceWarning = 3,

    /// <summary>Revoke all refresh tokens via Graph <c>revokeSignInSessions</c>.</summary>
    RevokeSessions = 4,

    /// <summary>
    /// Add the user to the quarantine group bound to a strict Conditional Access policy.
    /// The degraded-tier substitute for risk elevation.
    /// </summary>
    QuarantineUser = 5,

    /// <summary>
    /// Mark the user compromised in Entra ID Protection via
    /// <c>identityProtection/riskyUsers/confirmCompromised</c>. Requires Entra ID P2.
    /// </summary>
    ElevateUserRisk = 6,

    /// <summary>Hang up the call. The most disruptive action, and the hardest to justify after the fact.</summary>
    TerminateCall = 7,
}

/// <summary>Extension helpers describing the safety properties of each action.</summary>
public static class RemediationActionExtensions
{
    /// <summary>
    /// True when the action cannot be cleanly undone and is user-visible if wrong.
    ///
    /// Irreversible actions are gated on confidence; reversible ones are not. Warning a
    /// user unnecessarily costs a moment of confusion. Terminating a legitimate call or
    /// locking out a real user during a real emergency costs far more, and EntraGuard's
    /// false positives land on people who have done nothing wrong.
    /// </summary>
    public static bool IsIrreversible(this RemediationAction action) => action switch
    {
        RemediationAction.RevokeSessions => true,
        RemediationAction.QuarantineUser => true,
        RemediationAction.ElevateUserRisk => true,
        RemediationAction.TerminateCall => true,
        _ => false,
    };

    /// <summary>Which rung of the remediation ladder this action sits on, for audit records.</summary>
    public static int LadderRung(this RemediationAction action) => action switch
    {
        RemediationAction.ElevateUserRisk => 1,
        RemediationAction.RevokeSessions => 2,
        RemediationAction.QuarantineUser => 3,
        _ => 4,
    };
}
