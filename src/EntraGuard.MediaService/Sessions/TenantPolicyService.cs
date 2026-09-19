using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.Shared.Verification;

namespace EntraGuard.MediaService.Sessions;

public sealed record TenantPolicy(int Version = 1, string MinimumAssurance = "Low", int MaximumAgeMinutes = 10,
    bool RequireAnalyst = false, string[]? AllowedChannels = null)
{
    public string[] Channels => AllowedChannels ?? ["teams", "browser", "phone"];
    public string? Validate() => Version < 1 || MaximumAgeMinutes is < 1 or > 60
        || !Enum.TryParse<AssuranceLevel>(MinimumAssurance, out var minimum) || !Enum.IsDefined(minimum) || minimum == AssuranceLevel.None
        || Channels.Length is < 1 or > 3 || Channels.Distinct().Count() != Channels.Length
        || Channels.Any(c => c is not ("teams" or "phone" or "browser")) ? "Invalid verification policy." : null;

    public string? Refusal(VerificationReceipt receipt, DateTimeOffset now)
    {
        if (receipt.PolicyVersion != Version) return "Verification policy changed; verify again.";
        if (!Channels.Contains(receipt.EndpointKind)) return "This verification channel is not allowed.";
        if (receipt.CompletedAt is null || receipt.CompletedAt <= now.AddMinutes(-MaximumAgeMinutes) || receipt.CompletedAt > now.AddMinutes(2)) return "Verification has expired.";
        if (RequireAnalyst && !receipt.AnalystAssessed) return "An Analyst assessment is required by this tenant.";
        if (!Enum.TryParse<AssuranceLevel>(receipt.AssuranceLevel, out var actual)
            || actual < Enum.Parse<AssuranceLevel>(MinimumAssurance)) return $"This operation requires {MinimumAssurance} assurance.";
        return null;
    }
}

public sealed class TenantPolicyService(IStateStore store, TimeProvider time)
{
    public async Task<TenantPolicy> GetAsync(string tenant, CancellationToken ct = default) =>
        (await store.ReadAsync(tenant, "tenant_policy", ct))?.Value<TenantPolicy>() ?? new();

    public async Task<bool> UpdateAsync(Owner actor, TenantPolicy desired, int expectedVersion, CancellationToken ct)
    {
        var row = await store.ReadAsync(actor.TenantId, "tenant_policy", ct);
        var previous = row?.Value<TenantPolicy>() ?? new();
        if (previous.Version != expectedVersion || desired.Validate() is not null) return false;
        var next = desired with { Version = previous.Version + 1 };
        return await store.CommitAsync(actor.TenantId, [StateWrite.Put("tenant_policy", next, row?.Version),
            StateWrite.Put($"policy_audit_{next.Version:D10}", new { actor.ObjectId, at = time.GetUtcNow(), previous, next })], ct);
    }
}
