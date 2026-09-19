using System.Text.Json;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Sessions;

public sealed record TreasuryPayment(string Reference, string Beneficiary, long AmountMinor, string Currency,
    string Status, string Category, string Date, string Country, int Version = 1, string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null, bool DemoOnly = true)
{
    public string Digest(int policyVersion) => Owner.Hash(JsonSerializer.Serialize(new
        { Reference, Beneficiary, AmountMinor, Currency, Version, Date, Country, action = "approve-demo-payment", policyVersion }));
    public object Describe() => new { reference = Reference, beneficiary = Beneficiary, amount = AmountMinor / 100m,
        currency = Currency, status = Status, category = Category, date = Date, country = Country, version = Version,
        approvedBy = ApprovedBy, approvedAt = ApprovedAt, demoOnly = DemoOnly };
}
public sealed record ApprovalReceipt(string PaymentId, string VerificationId, string ActorId, DateTimeOffset ApprovedAt, string Digest, bool DemoOnly = true);
public sealed record ApprovalOutcome(bool Approved, string? Error = null, ApprovalReceipt? Receipt = null);

public sealed class PaymentService(IStateStore store, TenantPolicyService policies, TimeProvider time, IConfiguration config)
{
    private static readonly TreasuryPayment[] Samples = [
        new("PR-40192", "Northwind Logistics Ltd", 81240000, "GBP", "Awaiting approval", "Supplier payment", "2026-09-21", "United Kingdom"),
        new("PR-40191", "Fabrikam Industrial", 120400000, "GBP", "Awaiting approval", "Supplier payment", "2026-09-21", "Germany"),
        new("PR-40188", "Tailwind Freight", 46550000, "GBP", "Awaiting approval", "Logistics", "2026-09-22", "Netherlands"),
        new("PR-40184", "Contoso Payroll", 194022000, "GBP", "Released", "Payroll", "2026-09-18", "United Kingdom"),
        new("PR-40182", "Adventure Works", 32860000, "GBP", "Scheduled", "Supplier payment", "2026-09-23", "United States"),
        new("PR-40179", "Woodgrove Services", 17680000, "GBP", "Scheduled", "Professional services", "2026-09-24", "United Kingdom"),
        new("PR-40175", "Litware Systems", 28400000, "GBP", "Released", "Technology", "2026-09-17", "Singapore"),
        new("PR-40171", "Alpine Ski House", 9650000, "GBP", "Released", "Supplier payment", "2026-09-16", "Switzerland"),
    ];
    public bool Enabled => string.Equals(config["TREASURY_DEMO_LEDGER"], "true", StringComparison.OrdinalIgnoreCase);
    public static string Row(string id) => "payment_" + id;

    public async Task<IReadOnlyList<TreasuryPayment>> ListAsync(Owner owner, CancellationToken ct)
    {
        if (!Enabled) return [];
        // Each insert is conditional; repeated reads never reset a payment that was approved.
        foreach (var sample in Samples)
            if (await store.ReadAsync(owner.TenantId, Row(sample.Reference), ct) is null)
                await store.CommitAsync(owner.TenantId, [StateWrite.Put(Row(sample.Reference), sample)], ct);
        return (await store.ListAsync(owner.TenantId, "payment_", 100, ct: ct)).Items.Select(r => r.Value<TreasuryPayment>()).ToArray();
    }

    public async Task<TreasuryPayment?> GetAsync(Owner owner, string id, CancellationToken ct) =>
        Enabled ? (await store.ReadAsync(owner.TenantId, Row(id), ct))?.Value<TreasuryPayment>() : null;

    public async Task<ApprovalOutcome> ApproveAsync(Owner owner, string sessionId, string paymentId, string verificationId,
        string idempotencyKey, CancellationToken ct)
    {
        if (!Enabled || idempotencyKey.Length is < 8 or > 128) return new(false, "A valid idempotency key is required for the demo ledger.");
        var approvalId = owner.Row("approval", Owner.Hash(idempotencyKey));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var sessionRow = await store.ReadAsync(owner.TenantId, owner.Row("session", sessionId), ct);
            var session = sessionRow?.Value<RpSession>();
            if (session is null || session.Revoked || session.ExpiresAt <= time.GetUtcNow() || !session.CanApprove)
                return new(false, "A current payment-approver session is required.");
            var prior = await store.ReadAsync(owner.TenantId, approvalId, ct);
            if (prior is not null)
            {
                var priorReceipt = prior.Value<ApprovalReceipt>();
                return priorReceipt.PaymentId == paymentId && priorReceipt.VerificationId == verificationId
                    ? new(true, Receipt: priorReceipt) : new(false, "Idempotency key was used for another request.");
            }
            var paymentRow = await store.ReadAsync(owner.TenantId, Row(paymentId), ct);
            var verificationRow = await store.ReadAsync(owner.TenantId, VerificationLedger.RecordId(owner, verificationId), ct);
            if (paymentRow is null || verificationRow is null) return new(false, "Payment or verification not found.");
            var payment = paymentRow.Value<TreasuryPayment>(); var verification = verificationRow.Value<VerificationReceipt>();
            var policyRow = await store.ReadAsync(owner.TenantId, "tenant_policy", ct);
            var policy = policyRow?.Value<TenantPolicy>() ?? await policies.GetAsync(owner.TenantId, ct);
            var digest = payment.Digest(policy.Version);
            if (!payment.DemoOnly || payment.Status != "Awaiting approval") return new(false, "This payment cannot be approved.");
            if (!owner.Owns(verification.Owner.TenantId, verification.Owner.ObjectId) || verification.SessionId != sessionId
                || verification.TransactionId != paymentId || verification.TransactionDigest != digest
                || verification.Consumed || verification.Result != "Passed" || verification.RequiresStepUp)
                return new(false, "A fresh verification bound to these exact payment details is required.");
            if (policy.Refusal(verification, time.GetUtcNow()) is { } refusal) return new(false, refusal);
            var approval = new ApprovalReceipt(paymentId, verificationId, owner.ObjectId, time.GetUtcNow(), digest);
            var writes = new List<StateWrite> {
                StateWrite.Put(paymentRow.Id, payment with { Status = "Approved", Version = payment.Version + 1, ApprovedBy = owner.ObjectId, ApprovedAt = approval.ApprovedAt }, paymentRow.Version),
                StateWrite.Put(verificationRow.Id, verification with { Consumed = true }, verificationRow.Version),
                StateWrite.Put(approvalId, approval),
                // Include policy and session versions in the transaction: concurrent revocation or policy edits win safely.
                StateWrite.Put("tenant_policy", policy, policyRow?.Version),
                StateWrite.Put(sessionRow!.Id, session, sessionRow.Version),
            };
            var preferences = (await store.ReadAsync(owner.TenantId, PreferenceService.Row(owner), ct))?.Value<UserPreferences>() ?? new();
            if (preferences.ApprovalNotifications) writes.Add(PreferenceService.Notification(owner, "approval_" + verificationId,
                "Demo payment approved", $"{payment.Reference} was approved. No funds were moved.", approval.ApprovedAt));
            if (await store.CommitAsync(owner.TenantId, writes, ct)) return new(true, Receipt: approval);
        }
        return new(false, "Payment changed during approval. Refresh before retrying.");
    }
}
