using EntraGuard.Shared.Detection;

namespace EntraGuard.Shared.Policy;

/// <summary>
/// The deterministic safety rail between the Analyst agent and a user's account.
///
/// EntraGuard is agentic: a language model reads a live conversation and proposes
/// remediation. That is the interesting part, and it is also the dangerous part — a model
/// that misreads an angry-but-legitimate help-desk call can revoke a real person's
/// sessions in the middle of their workday.
///
/// So the model never acts directly. It produces a <see cref="RiskAssessment"/>; this
/// gate decides what may actually happen. The gate is pure, exhaustively tested, and
/// small enough to read in one sitting. Autonomy lives upstream; authority lives here.
///
/// Every rule below encodes one judgement about asymmetric cost. Changing a threshold
/// changes who gets locked out of their account by mistake.
/// </summary>
public static class PolicyGate
{
    /// <summary>
    /// Minimum Analyst confidence before any irreversible action is permitted.
    ///
    /// Reversible actions (warn, notify, raise an incident) are deliberately NOT gated on
    /// this. Warning someone unnecessarily costs a moment of confusion; staying silent
    /// through a real scam costs the account.
    /// </summary>
    public const double IrreversibleActionConfidenceThreshold = 0.75;

    /// <summary>Effective risk at which the SOC is notified.</summary>
    public const double NotifyThreshold = 40;

    /// <summary>Effective risk at which the user is warned inside the live call.</summary>
    public const double WarnThreshold = 60;

    /// <summary>Effective risk at which identity-plane containment begins.</summary>
    public const double ContainThreshold = 80;

    /// <summary>Effective risk at which hanging up becomes permissible — and only then.</summary>
    public const double TerminateThreshold = 90;

    /// <summary>
    /// Urgency weighting applied to the raw score based on how close the victim is to
    /// complying.
    ///
    /// This is what makes EntraGuard preventive rather than forensic. Identical evidence
    /// justifies more force when approval is seconds away, because the window to act is
    /// closing. <see cref="ComplianceStage.Approved"/> is weighted lower than
    /// <see cref="ComplianceStage.AboutToApprove"/> on purpose: once the victim has
    /// complied there is nothing left to prevent, only to contain, and the most
    /// disruptive action available no longer buys anything.
    /// </summary>
    private static double UrgencyWeight(ComplianceStage stage) => stage switch
    {
        ComplianceStage.Unaware => 0,
        ComplianceStage.Engaged => 5,
        ComplianceStage.AboutToApprove => 15,
        ComplianceStage.Approved => 10,
        _ => 0,
    };

    /// <summary>Actions permitted when the operator has disabled autonomous action.</summary>
    private static readonly RemediationAction[] ObserveOnlyActions =
    [
        RemediationAction.LogTelemetry,
        RemediationAction.NotifySoc,
        RemediationAction.RaiseSentinelIncident,
    ];

    /// <summary>Actions that need a resolved Entra ID subject to mean anything.</summary>
    private static readonly RemediationAction[] IdentityPlaneActions =
    [
        RemediationAction.RevokeSessions,
        RemediationAction.QuarantineUser,
        RemediationAction.ElevateUserRisk,
    ];

    /// <summary>
    /// Decide what to do about one Analyst verdict.
    /// </summary>
    /// <param name="assessment">The Analyst agent's verdict for the current transcript window.</param>
    /// <param name="context">Tenant capability, session history, and operator switches.</param>
    /// <returns>Actions to execute now, plus an audit trail of everything withheld.</returns>
    public static PolicyDecision Evaluate(RiskAssessment assessment, PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(context);

        // A malformed model response must not be able to push the gate outside its
        // calibrated range — clamping here means every threshold below is meaningful.
        var effectiveRisk = Math.Clamp(
            assessment.RiskScore + UrgencyWeight(assessment.Stage), 0, 100);

        var blocked = new List<BlockedAction>();
        var proposed = Propose(effectiveRisk, assessment.Stage, blocked);

        var permitted = ApplyTierCapability(proposed, context, blocked)
            .Where(action => PassesAutonomyGate(action, context, blocked))
            .Where(action => PassesConfidenceGate(action, assessment, blocked))
            .Where(action => PassesSubjectGate(action, context, blocked))
            // Telemetry is per-assessment, not once-per-session: every verdict is logged,
            // including the ones that change nothing. Everything else fires at most once.
            .Where(action => action == RemediationAction.LogTelemetry
                             || !context.AlreadyExecuted.Contains(action))
            .Distinct()
            .OrderBy(action => (int)action)
            .ToList();

        return new PolicyDecision
        {
            Actions = permitted,
            Blocked = blocked,
            EffectiveRisk = effectiveRisk,
            Summary = Summarise(effectiveRisk, assessment, permitted, blocked),
        };
    }

    /// <summary>
    /// Build the set of actions the risk level warrants, before any safety filtering.
    /// </summary>
    private static List<RemediationAction> Propose(
        double effectiveRisk,
        ComplianceStage stage,
        List<BlockedAction> blocked)
    {
        // Unconditional: an assessment that produced no action is still evidence, and a
        // gap in the telemetry is indistinguishable from the system being down.
        var proposed = new List<RemediationAction> { RemediationAction.LogTelemetry };

        if (effectiveRisk >= NotifyThreshold)
        {
            proposed.Add(RemediationAction.NotifySoc);
        }

        if (effectiveRisk >= WarnThreshold)
        {
            proposed.Add(RemediationAction.InjectVoiceWarning);
        }

        if (effectiveRisk >= ContainThreshold)
        {
            proposed.Add(RemediationAction.RaiseSentinelIncident);
            proposed.Add(RemediationAction.RevokeSessions);
            proposed.Add(RemediationAction.ElevateUserRisk);
        }

        // Hanging up requires BOTH near-certainty and an imminent approval. High risk
        // alone is not enough: while there is still time to warn someone, warning them is
        // strictly better than cutting them off, and a wrongly terminated call is the most
        // visible failure this system can produce.
        if (effectiveRisk >= TerminateThreshold && stage == ComplianceStage.AboutToApprove)
        {
            proposed.Add(RemediationAction.TerminateCall);
        }
        else if (effectiveRisk >= TerminateThreshold)
        {
            blocked.Add(new BlockedAction(
                RemediationAction.TerminateCall,
                stage == ComplianceStage.Approved
                    ? "Victim has already complied — terminating the call prevents nothing and destroys evidence. Containing instead."
                    : $"Risk is high but the victim is not about to approve (stage: {stage}). There is still time to warn rather than disconnect."));
        }

        return proposed;
    }

    /// <summary>
    /// Shadow mode. Lets the whole pipeline run and report without touching the call or
    /// the identity plane — how you demo the system, and how you'd pilot it in a real
    /// tenant before trusting it with live remediation.
    /// </summary>
    private static bool PassesAutonomyGate(
        RemediationAction action,
        PolicyContext context,
        List<BlockedAction> blocked)
    {
        if (context.AutonomousActionsEnabled || ObserveOnlyActions.Contains(action))
        {
            return true;
        }

        blocked.Add(new BlockedAction(
            action,
            "Autonomous action is disabled for this session (shadow mode). Action was warranted and recorded, but not executed."));
        return false;
    }

    /// <summary>
    /// The core safety rule: a tentative verdict never reaches the identity plane, no
    /// matter how alarming the score attached to it.
    /// </summary>
    private static bool PassesConfidenceGate(
        RemediationAction action,
        RiskAssessment assessment,
        List<BlockedAction> blocked)
    {
        if (!action.IsIrreversible() || assessment.Confidence >= IrreversibleActionConfidenceThreshold)
        {
            return true;
        }

        blocked.Add(new BlockedAction(
            action,
            $"Analyst confidence {assessment.Confidence:P0} is below the {IrreversibleActionConfidenceThreshold:P0} threshold required for irreversible action."));
        return false;
    }

    /// <summary>
    /// Identity remediation needs somebody to remediate. An unauthenticated call can still
    /// be transcribed, scored, reported, and interrupted — there is simply no account to act on.
    /// </summary>
    private static bool PassesSubjectGate(
        RemediationAction action,
        PolicyContext context,
        List<BlockedAction> blocked)
    {
        if (context.HasIdentifiedSubject || !IdentityPlaneActions.Contains(action))
        {
            return true;
        }

        blocked.Add(new BlockedAction(
            action,
            "No Entra ID subject resolved for this call — there is no account to act on. Reporting only."));
        return false;
    }

    /// <summary>
    /// Apply the tenant's real capability.
    ///
    /// Without Entra ID P2, <c>confirmCompromised</c> returns 403. Proposing it anyway
    /// would produce a demo that appears to elevate risk and silently does nothing, so the
    /// gate substitutes Conditional Access quarantine and records exactly why. The portal
    /// renders that reason verbatim.
    /// </summary>
    private static IEnumerable<RemediationAction> ApplyTierCapability(
        IEnumerable<RemediationAction> actions,
        PolicyContext context,
        List<BlockedAction> blocked)
    {
        foreach (var action in actions)
        {
            if (action == RemediationAction.ElevateUserRisk && context.RiskTier == TenantRiskTier.Degraded)
            {
                blocked.Add(new BlockedAction(
                    RemediationAction.ElevateUserRisk,
                    "Tenant lacks Entra ID P2 — identityProtection/riskyUsers/confirmCompromised is unavailable. Substituting Conditional Access quarantine."));
                yield return RemediationAction.QuarantineUser;
                continue;
            }

            yield return action;
        }
    }

    private static string Summarise(
        double effectiveRisk,
        RiskAssessment assessment,
        IReadOnlyList<RemediationAction> actions,
        IReadOnlyList<BlockedAction> blocked)
    {
        var band = effectiveRisk switch
        {
            >= TerminateThreshold => "critical",
            >= ContainThreshold => "high",
            >= WarnThreshold => "elevated",
            >= NotifyThreshold => "moderate",
            _ => "low",
        };

        var vectors = assessment.Vectors.Count > 0
            ? $" ({string.Join(", ", assessment.Vectors)})"
            : string.Empty;

        var taken = actions.Count == 1 && actions[0] == RemediationAction.LogTelemetry
            ? "observing"
            : string.Join(", ", actions.Where(a => a != RemediationAction.LogTelemetry));

        var withheld = blocked.Count > 0 ? $"; {blocked.Count} action(s) withheld" : string.Empty;

        return $"Risk {effectiveRisk:F0}/100 ({band}, confidence {assessment.Confidence:P0}) at stage {assessment.Stage}{vectors} → {taken}{withheld}.";
    }
}
