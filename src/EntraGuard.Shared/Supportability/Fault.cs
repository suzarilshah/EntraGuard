namespace EntraGuard.Shared.Supportability;

/// <summary>How much a fault matters to the person on the call.</summary>
public enum FaultSeverity
{
    /// <summary>Worth recording. Nothing about the outcome changed.</summary>
    Info,

    /// <summary>
    /// The system did its job with a weaker mechanism than intended.
    ///
    /// The most important value here, and the one this whole type exists for. Degradation is
    /// invisible by construction: the call still completes, the user still gets an answer,
    /// and nothing in the response distinguishes the strong path from the weak one. Every
    /// expensive failure in this project has been a degradation nobody could see.
    /// </summary>
    Degraded,

    /// <summary>A capability is unavailable. Something a user expected did not happen.</summary>
    Broken,
}

/// <summary>Which part of the system is reporting.</summary>
public enum FaultComponent
{
    Telemetry,
    Voice,
    Ingestion,
    Graph,
    Acs,
    Realtime,
    Storage,
    Auth,
}

/// <summary>
/// One thing that went wrong, described so that whoever reads it next does not have to
/// reconstruct the reasoning.
///
/// <para>
/// The fields are deliberately opinionated. A log line saying "sign-in telemetry unavailable
/// (403)" is true and nearly useless: it does not say that the call therefore fell back to a
/// stored security question, that the user heard only "your first pet", that the likely cause
/// is missing tenant consent, or what to do about it. Every one of those was reconstructed by
/// hand, from container logs, on a live system, more than once.
/// </para>
///
/// <para>
/// So a fault carries four separate things, and none is optional: what failed, what the USER
/// experienced as a result, what probably caused it, and what to do. If a caller cannot fill
/// in the last three, the condition is probably not worth recording as a fault.
/// </para>
/// </summary>
/// <param name="Component">Which subsystem noticed.</param>
/// <param name="Code">
/// A stable identifier, dotted and lower-case: <c>telemetry.signins_unavailable</c>. Stable
/// because it is what you group by when asking "is this happening a lot?", and free text
/// cannot be grouped.
/// </param>
/// <param name="Severity">See <see cref="FaultSeverity"/>.</param>
/// <param name="WhatFailed">The mechanical fact, in one sentence.</param>
/// <param name="UserImpact">
/// What the person on the call actually experienced. Written from their side, not ours —
/// "the caller was asked only their stored security question", not "the challenge builder
/// returned an empty list".
/// </param>
/// <param name="ProbableCause">The most likely explanation, stated as a hypothesis.</param>
/// <param name="Remediation">The next action, concrete enough to carry out.</param>
/// <param name="CorrelationId">Verification or session id, so this joins to the audit trail.</param>
/// <param name="SubjectUpn">Who it happened to, when that is known.</param>
/// <param name="Detail">Raw evidence: status codes, counts, exception messages.</param>
public sealed record Fault(
    FaultComponent Component,
    string Code,
    FaultSeverity Severity,
    string WhatFailed,
    string UserImpact,
    string ProbableCause,
    string Remediation,
    string? CorrelationId = null,
    string? SubjectUpn = null,
    string? Detail = null)
{
    /// <summary>
    /// Sign-in telemetry could not be read, so the call fell back to a stored question.
    /// </summary>
    /// <remarks>
    /// The exact failure that made a user believe features had been deleted: they heard one
    /// question about a pet and concluded the location and device questions were gone. The
    /// mechanism worked as written; nothing anywhere said it had downgraded.
    /// </remarks>
    public static Fault TelemetryUnavailable(
        string? tenantId, string? objectId, string detail, bool hadStoredQuestion) => new(
        FaultComponent.Telemetry,
        "telemetry.signins_unavailable",
        FaultSeverity.Degraded,
        "Entra sign-in logs could not be read, so no live telemetry questions were built.",
        hadStoredQuestion
            ? "The caller was asked only their stored security question — a secret an attacker "
            + "can research — instead of facts from their own sign-in activity minutes earlier."
            : "No identity questions could be asked at all. The call rested on the number match alone.",
        "The user's tenant has not granted admin consent for AuditLog.Read.All, or has no "
        + "Entra ID P1 — /v1.0/auditLogs/signIns is a premium endpoint. A 200 with zero usable "
        + "rows instead means every sign-in was filtered out.",
        "Check /api/verify/telemetry-probe/{tenantId}/{objectId}. It reports the Graph status, "
        + "how many sign-ins came back, how many survived filtering, and which apps they came from.",
        CorrelationId: objectId,
        Detail: $"tenant={tenantId} {detail}");

    /// <summary>No voice comparison happened on a call where one was expected.</summary>
    public static Fault VoiceNotAssessed(
        string verificationId, string? upn, string reason) => new(
        FaultComponent.Voice,
        "voice.not_assessed",
        FaultSeverity.Degraded,
        "The speaker was not compared against an enrolled voiceprint.",
        "The verification proved possession of the phone and knowledge of the answers, but "
        + "nothing checked WHO was speaking — the one factor a thief of the handset cannot satisfy.",
        "Most often the user has simply not enrolled. Otherwise: under three seconds of usable "
        + "speech was captured, the scorer was unreachable, or the call ended before scoring ran.",
        "Read the VoiceDetail column on this verification — it distinguishes those causes. "
        + "GET /api/voice-profile/selftest proves whether the scorer itself is answering.",
        CorrelationId: verificationId,
        SubjectUpn: upn,
        Detail: reason);

    /// <summary>An audit row did not reach Sentinel.</summary>
    /// <remarks>
    /// Silence here is uniquely dangerous: the audit trail looks healthy precisely because
    /// the rows that would have shown otherwise are the ones missing. This project has
    /// already had a version where only FAILED verifications were reaching Sentinel and every
    /// PASSED one silently did not.
    /// </remarks>
    public static Fault IngestionFailed(string stream, string detail) => new(
        FaultComponent.Ingestion,
        "ingestion.upload_failed",
        FaultSeverity.Broken,
        $"A row destined for {stream} was not accepted by the Logs Ingestion API.",
        "Nothing the user can see. The audit trail is now incomplete, and it will look "
        + "healthy while it is — the missing rows are invisible by definition.",
        "The data collection rule may not declare a column the service is sending (undeclared "
        + "columns are dropped silently), the managed identity may have lost Monitoring "
        + "Metrics Publisher on the DCR, or the endpoint may be transiently unavailable.",
        "Compare the stream declaration in infra/modules/observability.bicep against the row "
        + "shape in LogsIngestionSink. They must match exactly; a mismatch does not error.",
        Detail: detail);

    /// <summary>An external dependency is failing repeatedly and has been shed.</summary>
    public static Fault DependencyOpen(FaultComponent component, string name, string detail) => new(
        component,
        "dependency.circuit_open",
        FaultSeverity.Degraded,
        $"{name} failed repeatedly and calls to it are being shed rather than retried.",
        "Whatever that dependency contributes is missing. The call continues without it "
        + "rather than hanging while a dead service is dialled again on every attempt.",
        "The dependency is down, unreachable from this container, or rejecting our credentials.",
        $"Check the component directly. Calls resume automatically once a probe succeeds; "
        + "no restart is needed and none should be performed.",
        Detail: detail);
}
