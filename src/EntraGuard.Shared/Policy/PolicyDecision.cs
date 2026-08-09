namespace EntraGuard.Shared.Policy;

/// <summary>
/// What the tenant's licensing actually permits. Resolved once by
/// <c>scripts/00-preflight.sh</c> rather than discovered mid-incident.
/// </summary>
public enum TenantRiskTier
{
    /// <summary>
    /// No Entra ID P2. <c>confirmCompromised</c> will 403, so the gate proposes
    /// Conditional Access quarantine instead of risk elevation.
    /// </summary>
    Degraded = 0,

    /// <summary>Entra ID P2 present. Identity Protection risk state is writable.</summary>
    Graph = 1,
}

/// <summary>
/// Everything the gate needs beyond the assessment itself. Passed explicitly rather than
/// read from ambient state so the gate stays a pure function and the tests stay honest.
/// </summary>
public sealed record PolicyContext
{
    /// <summary>What the tenant's licensing permits.</summary>
    public required TenantRiskTier RiskTier { get; init; }

    /// <summary>
    /// Actions already executed for this session.
    ///
    /// The Analyst re-scores roughly every three seconds. Without this, a 90-second scam
    /// call would revoke the user's sessions thirty times and inject thirty overlapping
    /// voice warnings on top of each other.
    /// </summary>
    public IReadOnlySet<RemediationAction> AlreadyExecuted { get; init; } =
        new HashSet<RemediationAction>();

    /// <summary>
    /// Whether the protected user has been resolved to an Entra ID object.
    ///
    /// Identity-plane actions are meaningless without a subject. An unauthenticated
    /// caller-to-caller call can still be transcribed, scored, and reported — but there
    /// is nobody to revoke.
    /// </summary>
    public bool HasIdentifiedSubject { get; init; }

    /// <summary>
    /// Operator kill switch. When false the gate proposes observation only.
    /// Every hackathon demo needs a way to show the pipeline without firing live actions.
    /// </summary>
    public bool AutonomousActionsEnabled { get; init; } = true;
}

/// <summary>Why a proposed action did not make the cut. Recorded for audit either way.</summary>
/// <param name="Action">The action that was withheld.</param>
/// <param name="Reason">Human-readable justification, surfaced in the portal and Sentinel.</param>
public sealed record BlockedAction(RemediationAction Action, string Reason);

/// <summary>
/// The gate's verdict: what to do, what was deliberately withheld, and why.
///
/// The blocked list is not diagnostic noise — it is the audit trail proving the system
/// considered a harsher action and declined it. "Why didn't it terminate the call?" and
/// "why did it lock out my user?" are both answerable from this record.
/// </summary>
public sealed record PolicyDecision
{
    /// <summary>Actions to execute now, in escalation order. Excludes anything already done.</summary>
    public required IReadOnlyList<RemediationAction> Actions { get; init; }

    /// <summary>Actions the risk level warranted but policy withheld.</summary>
    public IReadOnlyList<BlockedAction> Blocked { get; init; } = [];

    /// <summary>Risk score after compliance-stage weighting. See <c>PolicyGate</c>.</summary>
    public required double EffectiveRisk { get; init; }

    /// <summary>One-line summary of the reasoning, for the live portal feed.</summary>
    public required string Summary { get; init; }

    /// <summary>True when at least one irreversible action is being taken.</summary>
    public bool IsIntervening => Actions.Any(a => a.IsIrreversible());
}
