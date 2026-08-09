using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Policy;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The policy gate is the safety rail between an autonomous agent and a user's account.
/// It is the one component where a subtle mistake locks a real person out of their job,
/// so it is tested exhaustively and it is kept pure — no I/O, no clock, no Azure.
/// </summary>
public class PolicyGateTests
{
    private static RiskAssessment Assess(
        double risk,
        double confidence = 0.9,
        ComplianceStage stage = ComplianceStage.Engaged,
        params ScamVector[] vectors) => new()
    {
        RiskScore = risk,
        Confidence = confidence,
        Stage = stage,
        Vectors = vectors,
        Rationale = "test",
    };

    private static PolicyContext Context(
        TenantRiskTier tier = TenantRiskTier.Graph,
        bool identified = true,
        bool autonomous = true,
        params RemediationAction[] alreadyDone) => new()
    {
        RiskTier = tier,
        HasIdentifiedSubject = identified,
        AutonomousActionsEnabled = autonomous,
        AlreadyExecuted = new HashSet<RemediationAction>(alreadyDone),
    };

    // ── Telemetry is unconditional ──────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void AlwaysLogsTelemetry_RegardlessOfRisk(double risk)
    {
        var decision = PolicyGate.Evaluate(Assess(risk), Context());

        decision.Actions.Should().Contain(RemediationAction.LogTelemetry,
            "every assessment must be auditable, including the ones that found nothing");
    }

    [Fact]
    public void BenignCall_TakesNoActionBeyondTelemetry()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 5, confidence: 0.95, stage: ComplianceStage.Unaware),
            Context());

        decision.Actions.Should().ContainSingle().Which.Should().Be(RemediationAction.LogTelemetry);
        decision.IsIntervening.Should().BeFalse();
    }

    // ── The confidence gate ─────────────────────────────────────────────────
    // A tentative verdict must never reach the identity plane, no matter how alarming.

    [Fact]
    public void LowConfidence_BlocksAllIrreversibleActions_EvenAtMaximumRisk()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 100, confidence: 0.5, stage: ComplianceStage.AboutToApprove),
            Context());

        decision.Actions.Should().NotContain(a => a.IsIrreversible());
        decision.IsIntervening.Should().BeFalse();
        decision.Blocked.Should().NotBeEmpty("withheld actions must be recorded, not silently dropped");
        decision.Blocked.Should().Contain(b => b.Reason.Contains("confidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LowConfidence_StillAllowsReversibleActions()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, confidence: 0.5, stage: ComplianceStage.AboutToApprove),
            Context());

        decision.Actions.Should().Contain(RemediationAction.InjectVoiceWarning,
            "warning a user is recoverable; staying silent on a probable scam is not");
        decision.Actions.Should().Contain(RemediationAction.NotifySoc);
    }

    [Theory]
    [InlineData(0.74, false)]
    [InlineData(0.75, true)]
    [InlineData(0.76, true)]
    public void ConfidenceThreshold_IsInclusiveAt75Percent(double confidence, bool expectIrreversible)
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, confidence: confidence, stage: ComplianceStage.Engaged),
            Context());

        decision.Actions.Any(a => a.IsIrreversible()).Should().Be(expectIrreversible);
    }

    // ── Risk escalation ladder ──────────────────────────────────────────────

    [Fact]
    public void ModerateRisk_NotifiesButDoesNotIntervene()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 45, stage: ComplianceStage.Unaware),
            Context());

        decision.Actions.Should().Contain(RemediationAction.NotifySoc);
        decision.Actions.Should().NotContain(RemediationAction.InjectVoiceWarning);
        decision.IsIntervening.Should().BeFalse();
    }

    [Fact]
    public void ElevatedRisk_WarnsTheUserInCall()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 65, stage: ComplianceStage.Unaware),
            Context());

        decision.Actions.Should().Contain(RemediationAction.InjectVoiceWarning);
        decision.IsIntervening.Should().BeFalse("a warning alone must not touch the identity plane");
    }

    [Fact]
    public void HighRisk_ElevatesRiskAndRevokesSessions()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, stage: ComplianceStage.Unaware,
                   vectors: ScamVector.MfaFatigueCoaching),
            Context());

        decision.Actions.Should().Contain(RemediationAction.ElevateUserRisk);
        decision.Actions.Should().Contain(RemediationAction.RevokeSessions);
        decision.Actions.Should().Contain(RemediationAction.RaiseSentinelIncident);
        decision.IsIntervening.Should().BeTrue();
    }

    // ── Compliance stage drives urgency ─────────────────────────────────────
    // The whole preventive claim rests on this: same evidence, more urgency, because
    // the window to act is closing.

    [Fact]
    public void AboutToApprove_EscalatesBeyondTheRawScore()
    {
        var unaware = PolicyGate.Evaluate(
            Assess(risk: 70, stage: ComplianceStage.Unaware), Context());
        var imminent = PolicyGate.Evaluate(
            Assess(risk: 70, stage: ComplianceStage.AboutToApprove), Context());

        imminent.EffectiveRisk.Should().BeGreaterThan(unaware.EffectiveRisk);
        imminent.Actions.Count.Should().BeGreaterThan(unaware.Actions.Count,
            "the same evidence warrants more force when approval is seconds away");
    }

    [Fact]
    public void TerminateCall_RequiresBothMaximumRiskAndImminentApproval()
    {
        var highRiskNotImminent = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.Unaware), Context());
        highRiskNotImminent.Actions.Should().NotContain(RemediationAction.TerminateCall,
            "hanging up on someone is not justified while there is still time to warn them");

        var imminentAndCertain = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.AboutToApprove), Context());
        imminentAndCertain.Actions.Should().Contain(RemediationAction.TerminateCall);
    }

    [Fact]
    public void AlreadyApproved_ContainsRatherThanTerminates()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.Approved), Context());

        decision.Actions.Should().Contain(RemediationAction.RevokeSessions,
            "the credential is already burned; containment is what is left");
        decision.Actions.Should().Contain(RemediationAction.ElevateUserRisk);
        decision.Actions.Should().NotContain(RemediationAction.TerminateCall,
            "terminating after compliance destroys evidence and prevents nothing");
    }

    // ── Degraded tenant tier ────────────────────────────────────────────────

    [Fact]
    public void DegradedTier_SubstitutesQuarantineForRiskElevation()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, stage: ComplianceStage.Engaged),
            Context(tier: TenantRiskTier.Degraded));

        decision.Actions.Should().NotContain(RemediationAction.ElevateUserRisk,
            "calling confirmCompromised without P2 would 403; propose what can actually run");
        decision.Actions.Should().Contain(RemediationAction.QuarantineUser);
        decision.Actions.Should().Contain(RemediationAction.RevokeSessions);
    }

    [Fact]
    public void DegradedTier_RecordsWhyRiskElevationWasUnavailable()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, stage: ComplianceStage.Engaged),
            Context(tier: TenantRiskTier.Degraded));

        decision.Blocked.Should().Contain(b =>
            b.Action == RemediationAction.ElevateUserRisk &&
            b.Reason.Contains("P2", StringComparison.OrdinalIgnoreCase),
            "the portal renders this reason verbatim instead of faking a successful elevation");
    }

    // ── Unidentified subject ────────────────────────────────────────────────

    [Fact]
    public void WithoutIdentifiedSubject_SkipsIdentityActionsButStillReports()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.AboutToApprove),
            Context(identified: false));

        decision.Actions.Should().NotContain(RemediationAction.ElevateUserRisk);
        decision.Actions.Should().NotContain(RemediationAction.RevokeSessions);
        decision.Actions.Should().NotContain(RemediationAction.QuarantineUser);

        decision.Actions.Should().Contain(RemediationAction.RaiseSentinelIncident,
            "an unattributable scam call is still worth an incident");
        decision.Actions.Should().Contain(RemediationAction.InjectVoiceWarning,
            "and the person on the line can still be warned");

        decision.Blocked.Should().Contain(b =>
            b.Reason.Contains("subject", StringComparison.OrdinalIgnoreCase));
    }

    // ── Idempotency ─────────────────────────────────────────────────────────
    // The Analyst re-scores every ~3s. Without this the gate would re-fire everything
    // on every pass for the whole call.

    [Fact]
    public void DoesNotRepeatActionsAlreadyExecuted()
    {
        var context = Context(
            alreadyDone: [RemediationAction.RevokeSessions, RemediationAction.InjectVoiceWarning]);

        var decision = PolicyGate.Evaluate(
            Assess(risk: 85, stage: ComplianceStage.Engaged), context);

        decision.Actions.Should().NotContain(RemediationAction.RevokeSessions);
        decision.Actions.Should().NotContain(RemediationAction.InjectVoiceWarning);
        decision.Actions.Should().Contain(RemediationAction.ElevateUserRisk,
            "actions not yet taken must still fire");
    }

    [Fact]
    public void RepeatedIdenticalAssessments_Converge_ToTelemetryOnly()
    {
        var executed = new HashSet<RemediationAction>();
        var assessment = Assess(risk: 95, stage: ComplianceStage.AboutToApprove);

        PolicyDecision decision;
        for (var pass = 0; pass < 5; pass++)
        {
            decision = PolicyGate.Evaluate(assessment, Context(alreadyDone: [.. executed]));
            foreach (var action in decision.Actions)
            {
                executed.Add(action);
            }
        }

        var finalPass = PolicyGate.Evaluate(assessment, Context(alreadyDone: [.. executed]));
        finalPass.Actions.Should().ContainSingle().Which.Should().Be(RemediationAction.LogTelemetry,
            "a stable scam verdict must stop re-firing remediation once it has all been done");
    }

    // ── Kill switch ─────────────────────────────────────────────────────────

    [Fact]
    public void AutonomyDisabled_ObservesOnly()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 100, confidence: 1.0, stage: ComplianceStage.AboutToApprove),
            Context(autonomous: false));

        decision.IsIntervening.Should().BeFalse();
        decision.Actions.Should().NotContain(RemediationAction.TerminateCall);
        decision.Actions.Should().Contain(RemediationAction.LogTelemetry);
        decision.Actions.Should().Contain(RemediationAction.NotifySoc,
            "shadow mode must still surface what it would have done");
        decision.Blocked.Should().Contain(b =>
            b.Reason.Contains("autonomous", StringComparison.OrdinalIgnoreCase));
    }

    // ── Ordering and structural invariants ──────────────────────────────────

    [Fact]
    public void ActionsAreReturnedInEscalationOrder()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.AboutToApprove), Context());

        decision.Actions.Should().BeInAscendingOrder(a => (int)a,
            "the actuator executes in order; a hang-up before the warning would silence it");
    }

    [Fact]
    public void ActionsAreDistinct()
    {
        var decision = PolicyGate.Evaluate(
            Assess(risk: 95, stage: ComplianceStage.AboutToApprove), Context());

        decision.Actions.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)]
    public void EffectiveRiskIsAlwaysClampedTo0_100(double rawRisk)
    {
        var decision = PolicyGate.Evaluate(
            Assess(rawRisk, stage: ComplianceStage.AboutToApprove), Context());

        decision.EffectiveRisk.Should().BeInRange(0, 100,
            "a malformed model response must not push the gate outside its calibrated range");
    }

    [Fact]
    public void SummaryIsAlwaysPopulated()
    {
        var decision = PolicyGate.Evaluate(Assess(risk: 50), Context());

        decision.Summary.Should().NotBeNullOrWhiteSpace();
    }
}
