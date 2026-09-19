using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Sessions;

public sealed class StepUpService(IStateStore store, MfaEvidence mfa, TenantPolicyService policies, TimeProvider time)
{
    public async Task<string?> ConfirmAsync(Owner owner, string sessionId, string verificationId, string? token, CancellationToken ct)
    {
        var row = await store.ReadAsync(owner.TenantId, VerificationLedger.RecordId(owner, verificationId), ct);
        var receipt = row?.Value<VerificationReceipt>();
        if (row is null || receipt is null || receipt.SessionId != sessionId || receipt.Consumed
            || receipt.Result != "StepUpRequired" || !receipt.RequiresStepUp)
            return "No recoverable verification belongs to this session.";
        var policy = await policies.GetAsync(owner.TenantId, ct);
        if (policy.Refusal(receipt, time.GetUtcNow()) is { } refused) return refused;
        var why = await mfa.WhyNotMfaAsync(new CallerIdentity(owner.ObjectId, owner.TenantId, owner.Upn, false, false, false),
            token, ct, receipt.StartedAt);
        if (why is not null) return why;
        var updated = receipt with { Result = "Passed", RequiresStepUp = false, StepUpAt = time.GetUtcNow(),
            Reason = receipt.Reason + " Fresh Microsoft MFA was validated by the service." };
        var history = await store.ReadAsync(owner.TenantId, VerificationLedger.HistoryId(receipt), ct);
        return await store.CommitAsync(owner.TenantId, [
            StateWrite.Put(row.Id, updated, row.Version),
            StateWrite.Put(VerificationLedger.HistoryId(receipt), updated, history?.Version),
            StateWrite.Put($"outbox_{Guid.Parse(owner.ObjectId):N}_{verificationId}_stepup", updated),
        ], ct) ? null : "Verification changed. Please retry.";
    }
}
