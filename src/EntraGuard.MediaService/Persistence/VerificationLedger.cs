using EntraGuard.MediaService.Auth;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Persistence;

/// <summary>Allow-listed receipt. No match code, viewer token, answer, or transcript is persisted here.</summary>
public sealed record VerificationReceipt(
    string VerificationId, Owner Owner, string SessionId, string ApplicationName, DateTimeOffset StartedAt,
    DateTimeOffset Deadline, string EndpointKind, string Result = "Pending", string Reason = "Call requested.",
    DateTimeOffset? CompletedAt = null, string AssuranceLevel = "None", IReadOnlyList<string>? AssuranceBasis = null,
    IReadOnlyList<string>? AssuranceGaps = null, string VoiceOutcome = "NotAssessed", double? VoiceScore = null,
    bool RequiresStepUp = false, bool AnalystAssessed = false, double PeakRiskDuringCall = 0, int Attempts = 0,
    bool MediaStreamConnected = false, long AudioFrames = 0, int DtmfReceived = 0, bool Consumed = false,
    string? TransactionId = null, string? TransactionDigest = null, int PolicyVersion = 1,
    IReadOnlyList<QuestionEvidence>? Questions = null, double RiskScore = 0, string RiskBand = "Low",
    IReadOnlyList<string>? RiskContributors = null, IReadOnlyList<FollowUpOutcome>? FollowUps = null,
    string Register = "Warm", string VoiceDetail = "", string? CallConnectionId = null, string? MonitorSessionId = null,
    DateTimeOffset? StepUpAt = null)
{
    public bool IsComplete => Result != "Pending";
    public object Describe() => new
    {
        verificationId = VerificationId, upn = Owner.Upn, applicationName = ApplicationName,
        result = Result, reason = Reason, isComplete = IsComplete, grantsAccess = false,
        startedAt = StartedAt, completedAt = CompletedAt, endpointKind = EndpointKind,
        assuranceLevel = AssuranceLevel, assuranceBasis = AssuranceBasis ?? [], assuranceGaps = AssuranceGaps ?? [],
        voiceOutcome = VoiceOutcome, voiceScore = VoiceScore, requiresStepUp = RequiresStepUp,
        analystAssessed = AnalystAssessed, peakRiskDuringCall = PeakRiskDuringCall, attempts = Attempts,
        policyVersion = PolicyVersion, transactionId = TransactionId,
        questions = Questions ?? [], stepUpAt = StepUpAt,
    };
    public VerificationSession ToTelemetry() => new()
    {
        VerificationId = VerificationId, SubjectUpn = Owner.Upn, SubjectTenantId = Owner.TenantId,
        SubjectObjectId = Owner.ObjectId, StartedAt = StartedAt, CompletedAt = StepUpAt ?? CompletedAt,
        ApplicationName = ApplicationName, CalleeAcsId = "", MatchCode = "", EndpointKind = EndpointKind,
        Result = Enum.Parse<VerificationResult>(Result), Reason = Reason, AssuranceLevel = AssuranceLevel,
        AssuranceBasis = AssuranceBasis ?? [], AssuranceGaps = AssuranceGaps ?? [], VoiceOutcome = VoiceOutcome,
        VoiceScore = VoiceScore, PeakRiskDuringCall = PeakRiskDuringCall, Attempts = Attempts,
        RequiresStepUp = RequiresStepUp,
        RiskScore = RiskScore, RiskBand = RiskBand, RiskContributors = RiskContributors ?? [], FollowUps = FollowUps ?? [],
        Register = Register, VoiceDetail = VoiceDetail, CallConnectionId = CallConnectionId, MonitorSessionId = MonitorSessionId,
    };
}

public sealed class VerificationLedger(IStateStore store, TimeProvider time)
{
    public static string HistoryId(VerificationReceipt receipt) => receipt.Owner.Row("history",
        $"{DateTimeOffset.MaxValue.UtcTicks - receipt.StartedAt.UtcTicks:D19}_{receipt.VerificationId}");
    public static string RecordId(Owner owner, string id) => owner.Row("verification", id);
    private static string RecoveryId(VerificationReceipt r) => $"recovery_{Guid.Parse(r.Owner.ObjectId):N}_{r.VerificationId}";

    public async Task BeginAsync(VerificationSession v, CancellationToken ct)
    {
        var owner = new Owner(v.SubjectTenantId!, v.SubjectObjectId!, v.SubjectUpn);
        var session = await store.ReadAsync(owner.TenantId, owner.Row("session", v.RpSessionId!), ct)
            ?? throw new UnauthorizedAccessException("The requesting session no longer exists.");
        var state = session.Value<RpSession>();
        if (state.Revoked || state.ExpiresAt <= time.GetUtcNow()) throw new UnauthorizedAccessException("Session has expired.");
        var receipt = new VerificationReceipt(v.VerificationId, owner, v.RpSessionId!, v.ApplicationName,
            v.StartedAt, time.GetUtcNow().AddMinutes(15), v.EndpointKind, PolicyVersion: v.PolicyVersion,
            TransactionId: v.TransactionId, TransactionDigest: v.TransactionDigest);
        if (!await store.CommitAsync(owner.TenantId, [
            StateWrite.Put(RecordId(owner, v.VerificationId), receipt), StateWrite.Put(HistoryId(receipt), receipt),
            StateWrite.Put(RecoveryId(receipt), receipt),
            StateWrite.Put(session.Id, v.TransactionId is null ? state with { VerifiedUntil = null, VerificationId = null } : state, session.Version),
        ], ct)) throw new InvalidOperationException("Session changed; retry verification.");
    }

    public async Task<VerificationReceipt?> GetAsync(Owner owner, string id, CancellationToken ct = default) =>
        (await store.ReadAsync(owner.TenantId, RecordId(owner, id), ct))?.Value<VerificationReceipt>();

    public async Task<bool> CompleteAsync(VerificationSession v, CancellationToken ct)
    {
        if (v.RpSessionId is null || v.SubjectTenantId is null || v.SubjectObjectId is null) return false;
        var owner = new Owner(v.SubjectTenantId, v.SubjectObjectId, v.SubjectUpn);
        var existing = await GetAsync(owner, v.VerificationId, ct);
        if (existing is null) throw new InvalidOperationException("Verification was not durably started.");
        return await FinishAsync(existing with
        {
            Result = v.Result.ToString(), Reason = v.Reason, CompletedAt = v.CompletedAt ?? time.GetUtcNow(),
            AssuranceLevel = v.AssuranceLevel, AssuranceBasis = v.AssuranceBasis, AssuranceGaps = v.AssuranceGaps,
            VoiceOutcome = v.VoiceOutcome, VoiceScore = v.VoiceScore, RequiresStepUp = v.RequiresStepUp,
            AnalystAssessed = v.AnalystAssessed, PeakRiskDuringCall = v.PeakRiskDuringCall, Attempts = v.Attempts,
            MediaStreamConnected = v.MediaStreamConnected, AudioFrames = v.AudioFramesReceived, DtmfReceived = v.DtmfReceived,
            Questions = v.QuestionEvidence, RiskScore = v.RiskScore, RiskBand = v.RiskBand, RiskContributors = v.RiskContributors,
            FollowUps = v.FollowUps.Select(f => f with { Question = "" }).ToArray(), Register = v.Register,
            VoiceDetail = v.VoiceDetail, CallConnectionId = v.CallConnectionId, MonitorSessionId = v.MonitorSessionId,
        }, ct);
    }

    public async Task<bool> FinishAsync(VerificationReceipt receipt, CancellationToken ct)
    {
        var record = await store.ReadAsync(receipt.Owner.TenantId, RecordId(receipt.Owner, receipt.VerificationId), ct);
        if (record is null || record.Value<VerificationReceipt>().IsComplete) return false;
        var history = await store.ReadAsync(receipt.Owner.TenantId, HistoryId(receipt), ct);
        var recovery = await store.ReadAsync(receipt.Owner.TenantId, RecoveryId(receipt), ct);
        var writes = new List<StateWrite>
        {
            StateWrite.Put(record.Id, receipt, record.Version), StateWrite.Put(HistoryId(receipt), receipt, history?.Version),
            StateWrite.Put($"outbox_{Guid.Parse(receipt.Owner.ObjectId):N}_{receipt.VerificationId}", receipt),
        };
        if (recovery is not null) writes.Add(StateWrite.Delete(recovery));
        var preferences = (await store.ReadAsync(receipt.Owner.TenantId, Sessions.PreferenceService.Row(receipt.Owner), ct))?.Value<Sessions.UserPreferences>() ?? new();
        if (preferences.VerificationNotifications) writes.Add(Sessions.PreferenceService.Notification(receipt.Owner, receipt.VerificationId,
            "Verification completed", $"{receipt.ApplicationName}: {receipt.Result}. {receipt.Reason}", receipt.CompletedAt ?? time.GetUtcNow()));
        return await store.CommitAsync(receipt.Owner.TenantId, writes, ct);
    }

    public Task<StatePage> HistoryAsync(Owner owner, int limit, string? cursor, CancellationToken ct) =>
        store.ListAsync(owner.TenantId, owner.Prefix("history"), Math.Clamp(limit, 1, 100), cursor, ct);

    public async Task RecoverAsync(PendingWork work, CancellationToken ct)
    {
        var receipt = work.Document.Value<VerificationReceipt>();
        if (receipt.Deadline > time.GetUtcNow()) return;
        await FinishAsync(receipt with { Result = "CallFailed", Reason = "The call did not produce a durable result before its deadline. It may have been interrupted.", CompletedAt = time.GetUtcNow() }, ct);
    }
}
