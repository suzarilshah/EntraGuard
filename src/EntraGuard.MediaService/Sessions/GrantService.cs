using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Sessions;

public sealed record GrantOutcome(bool Granted, string? Error = null, VerificationReceipt? Verification = null);

public sealed class GrantService(IStateStore store, VerificationLedger ledger, TimeProvider time, TenantPolicyService policies)
{
    public async Task<GrantOutcome> GrantAsync(Owner owner, string sessionId, string verificationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var sessionRow = await store.ReadAsync(owner.TenantId, owner.Row("session", sessionId), ct);
            var receiptRow = await store.ReadAsync(owner.TenantId, VerificationLedger.RecordId(owner, verificationId), ct);
            if (sessionRow is null || receiptRow is null) return new(false, "Verification not found.");
            var session = sessionRow.Value<RpSession>(); var receipt = receiptRow.Value<VerificationReceipt>();
            var policy = await policies.GetAsync(owner.TenantId, ct);
            if (session.Revoked || session.ExpiresAt <= time.GetUtcNow() || receipt.SessionId != sessionId
                || !owner.Owns(receipt.Owner.TenantId, receipt.Owner.ObjectId) || receipt.TransactionId is not null)
                return new(false, "Verification is not bound to this session.");
            if (receipt.Result != "Passed" || receipt.RequiresStepUp || receipt.CompletedAt is null
                || receipt.CompletedAt < time.GetUtcNow().AddMinutes(-policy.MaximumAgeMinutes)) return new(false, "A fresh successful verification is required.");
            if (policy.Refusal(receipt, time.GetUtcNow()) is { } refusal) return new(false, refusal);
            if (receipt.Consumed)
                return session.VerificationId == verificationId && session.VerifiedUntil > time.GetUtcNow()
                    ? new(true, Verification: receipt) : new(false, "Verification was already used.");
            var expires = receipt.CompletedAt.Value.AddMinutes(policy.MaximumAgeMinutes);
            if (expires > session.ExpiresAt) expires = session.ExpiresAt;
            if (await store.CommitAsync(owner.TenantId, [
                StateWrite.Put(sessionRow.Id, session with { VerificationId = verificationId, VerifiedUntil = expires }, sessionRow.Version),
                StateWrite.Put(receiptRow.Id, receipt with { Consumed = true }, receiptRow.Version),
            ], ct)) return new(true, Verification: receipt);
        }
        return new(false, "Session changed; retry.");
    }

    public async Task<GrantOutcome> CurrentAsync(Owner owner, string sessionId, CancellationToken ct)
    {
        var row = await store.ReadAsync(owner.TenantId, owner.Row("session", sessionId), ct);
        var session = row?.Value<RpSession>();
        if (session is null || session.Revoked || session.ExpiresAt <= time.GetUtcNow()
            || session.VerifiedUntil <= time.GetUtcNow() || session.VerifiedUntil is null || session.VerificationId is null)
            return new(false, "Verification required.");
        var receipt = await ledger.GetAsync(owner, session.VerificationId, ct);
        var policy = await policies.GetAsync(owner.TenantId, ct);
        return receipt is { Result: "Passed", RequiresStepUp: false, Consumed: true } && receipt.SessionId == sessionId
            && policy.Refusal(receipt, time.GetUtcNow()) is null
            ? new(true, Verification: receipt) : new(false, "Verification required.");
    }
}
