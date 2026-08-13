using Azure.Monitor.Ingestion;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Tools;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Verification;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Sinks;

/// <summary>
/// Publishes EntraGuard telemetry into Log Analytics for Microsoft Sentinel.
///
/// Uses the Logs Ingestion API (DCE + DCR) rather than the HTTP Data Collector API, which
/// is retired on 2026-09-14. Authentication is the managed identity holding Monitoring
/// Metrics Publisher scoped to the DCR — so this service can write these two streams and
/// nothing else in the workspace.
/// </summary>
public sealed class LogsIngestionSink(
    LogsIngestionClient client,
    IOptions<EntraGuardOptions> options,
    ILogger<LogsIngestionSink> logger)
{
    private readonly EntraGuardOptions _options = options.Value;

    /// <summary>
    /// Record one Analyst verdict.
    ///
    /// Every assessment is written, including benign ones. A gap in this table would be
    /// indistinguishable from the service being down, and the benign rows are what make
    /// the false-positive rate measurable after the fact.
    /// </summary>
    public async Task WriteAssessmentAsync(
        CallSession session,
        RiskAssessment assessment,
        CancellationToken cancellationToken = default)
    {
        var row = new
        {
            TimeGenerated = assessment.AssessedAt.UtcDateTime,
            SessionId = session.SessionId,
            CallConnectionId = session.CallConnectionId ?? string.Empty,
            AcsCorrelationId = session.AcsCorrelationId ?? string.Empty,
            RiskScore = assessment.RiskScore,
            Confidence = assessment.Confidence,
            ComplianceStage = assessment.Stage.ToString(),
            Vectors = string.Join(",", assessment.Vectors),
            Evidence = assessment.Evidence
                .Select(e => new { e.Quote, Speaker = e.Speaker.ToString(), e.OffsetMs })
                .ToArray(),
            Rationale = assessment.Rationale,
            SubjectUpn = session.SubjectUpn ?? string.Empty,
            SubjectObjectId = session.SubjectObjectId ?? string.Empty,
            CallerIdentity = session.CallerIdentity ?? string.Empty,
            // Truncated: the full transcript lives in blob storage. Log Analytics is for
            // the structured verdict, not for bulk conversation content — both because of
            // ingestion cost and because a SIEM is the wrong place to accumulate raw PII.
            TranscriptWindow = Truncate(
                session.TranscriptWindow(_options.AnalysisWindow, session.ElapsedMs(DateTimeOffset.UtcNow)), 4000),
            AnalysisLatencyMs = assessment.AnalysisLatencyMs,
            ModelDeployment = _options.OpenAiDeployment,
        };

        await UploadAsync(_options.CallAnalysisStream, [row], cancellationToken);
    }

    /// <summary>
    /// Record one remediation attempt and its real outcome.
    ///
    /// Failures and unavailability are written exactly like successes. "We tried to elevate
    /// this user's risk and the tenant licensing refused" is the record that makes the
    /// degraded path auditable instead of invisible.
    /// </summary>
    public async Task WriteRemediationAsync(
        CallSession session,
        RemediationResult result,
        string decidedBy,
        CancellationToken cancellationToken = default)
    {
        var row = new
        {
            TimeGenerated = DateTime.UtcNow,
            SessionId = session.SessionId,
            ActionName = result.Action.ToString(),
            LadderRung = result.Action.LadderRung(),
            Outcome = result.Outcome.ToString(),
            Reason = result.Reason,
            GraphStatusCode = result.GraphStatusCode,
            SubjectUpn = session.SubjectUpn ?? string.Empty,
            SubjectObjectId = session.SubjectObjectId ?? string.Empty,
            RiskScore = session.CurrentAssessment.RiskScore,
            DecidedBy = decidedBy,
            DurationMs = result.DurationMs,
        };

        await UploadAsync(_options.RemediationStream, [row], cancellationToken);
    }

    /// <summary>
    /// Record a step-up verification attempt.
    ///
    /// Written for every outcome, including refusals. A BlockedCoercion row is the most
    /// important record this system produces: it says a user presented the correct
    /// credential and was denied anyway, which is exactly the event a SOC needs to see and
    /// exactly the one an attacker would want absent.
    /// </summary>
    public async Task WriteVerificationAsync(
        VerificationSession verification,
        CancellationToken cancellationToken = default)
    {
        var row = new
        {
            TimeGenerated = DateTime.UtcNow,
            VerificationId = verification.VerificationId,
            SubjectUpn = verification.SubjectUpn,
            SubjectObjectId = verification.SubjectObjectId ?? string.Empty,
            ApplicationName = verification.ApplicationName,
            Result = verification.Result.ToString(),
            Reason = verification.Reason,
            GrantsAccess = verification.GrantsAccess,
            Attempts = verification.Attempts,
            PeakRiskDuringCall = verification.PeakRiskDuringCall,
            CallConnectionId = verification.CallConnectionId ?? string.Empty,
            MonitorSessionId = verification.MonitorSessionId ?? string.Empty,
            DurationMs = verification.DurationMs,
            // Written even in observe mode — this column IS the calibration dataset.
            // Thresholds get set from the distribution of real scores against real
            // outcomes, not from a number published against studio recordings.
            VoiceScore = verification.VoiceScore ?? 0,
            VoiceOutcome = verification.VoiceOutcome,
            LivenessOutcome = verification.LivenessOutcome,
            LivenessLatencyMs = verification.LivenessLatencyMs ?? 0,
            SpoofScore = verification.SpoofScore ?? 0,
            VoiceDetail = verification.VoiceDetail,
            RiskScore = verification.RiskScore,
            RiskBand = verification.RiskBand,
        };

        await UploadAsync(_options.VerificationStream, [row], cancellationToken);
    }

    /// <summary>
    /// Record the creation, replacement, failure, or deletion of a voiceprint.
    ///
    /// Separate from the verification stream because these are not authentication attempts —
    /// they are the consent record. A voiceprint is special-category data under GDPR Article
    /// 9, and the two events a regulator asks for first are when consent was given and when
    /// it was withdrawn. Neither reached the SIEM at all before this: enrolment and deletion
    /// wrote ILogger lines, which are not an audit trail and do not survive a container
    /// restart.
    ///
    /// Deliberately records no audio, no template and no embedding — only that an event
    /// happened, to whom, under which version of the consent text.
    /// </summary>
    public async Task WriteBiometricEventAsync(
        string eventType,
        string subjectUpn,
        string subjectObjectId,
        string subjectTenantId,
        string consentVersion = "",
        DateTimeOffset? consentAt = null,
        int phraseCount = 0,
        double selfConsistency = 0,
        string reason = "",
        bool usedMfa = false,
        CancellationToken cancellationToken = default)
    {
        var row = new
        {
            TimeGenerated = DateTime.UtcNow,
            EventType = eventType,
            SubjectUpn = subjectUpn,
            SubjectObjectId = subjectObjectId,
            SubjectTenantId = subjectTenantId,
            ConsentVersion = consentVersion,
            ConsentAt = (consentAt ?? DateTimeOffset.UtcNow).UtcDateTime,
            PhraseCount = phraseCount,
            SelfConsistency = selfConsistency,
            Reason = reason,
            UsedMfa = usedMfa,
        };

        await UploadAsync(_options.BiometricStream, [row], cancellationToken);
    }

    /// <summary>
    /// Publish rows, deliberately detached from the caller's cancellation token.
    /// </summary>
    /// <param name="cancellationToken">
    /// Accepted for signature compatibility and NOT honoured — see below.
    /// </param>
    /// <remarks>
    /// The audit record of how a call ended must survive the end of that call.
    ///
    /// Callers on this path hold a token scoped to the call itself, so by the time the
    /// verdict is written that token is already cancelled and its source disposed. The
    /// upload then failed with <c>ObjectDisposedException</c> — and only ever on the
    /// success path, because a call refused at ACS completes before its lifetime is torn
    /// down. The observable symptom was the worst kind: every FAILED verification appeared
    /// in Sentinel and every PASSED one silently did not, so the audit trail looked healthy
    /// while under-reporting exactly the outcome that grants access.
    ///
    /// A bounded independent timeout replaces it. Ten seconds is generous for one row and
    /// still bounds a hung request, and nothing upstream waits on this.
    /// </remarks>
    private async Task UploadAsync(string stream, object[] rows, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (string.IsNullOrEmpty(_options.DcrImmutableId))
        {
            logger.LogDebug("Logs ingestion is not configured; skipping upload to {Stream}.", stream);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await client.UploadAsync(
                _options.DcrImmutableId, stream, rows, cancellationToken: timeout.Token);
        }
        catch (Exception ex)
        {
            // Telemetry must never break the call. A dropped row costs a gap in Sentinel;
            // a thrown exception on this path would abort an in-flight interception.
            logger.LogError(ex, "Failed to publish to {Stream}.", stream);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
