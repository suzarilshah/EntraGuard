using System.Security.Claims;
using System.Security.Cryptography;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Auth;

public sealed record RpSession(Owner Owner, string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string? VerificationId = null, DateTimeOffset? VerifiedUntil = null, bool Revoked = false, bool IsOperator = false, bool IsTenantAdmin = false, bool CanApprove = false);

public sealed class RpSessionService(IStateStore store, TimeProvider time, IConfiguration config)
{
    public const string Header = "X-Rp-Session";
    public const string Claim = "rp_session";

    public async Task<(string Token, RpSession Session)> CreateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = Owner.From(principal);
        var now = time.GetUtcNow();
        var exp = principal.FindFirst("exp")?.Value;
        if (!long.TryParse(exp, out var seconds)) throw new UnauthorizedAccessException("Token expiration is required.");
        var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (expires > now.AddHours(1)) expires = now.AddHours(1);
        if (expires <= now) throw new UnauthorizedAccessException("Token has expired.");
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var token = $"{Guid.Parse(owner.TenantId):N}.{Guid.Parse(owner.ObjectId):N}.{random}";
        var session = new RpSession(owner, Owner.Hash(token), now, expires, IsOperator: OperatorAccess.Allowed(principal, config),
            IsTenantAdmin: principal.HasClaim("roles", "EntraGuard.TenantAdmin"),
            CanApprove: principal.HasClaim("roles", "EntraGuard.PaymentApprover"));
        if (!await store.CommitAsync(owner.TenantId, [StateWrite.Put(owner.Row("session", session.Id), session)], ct))
            throw new InvalidOperationException("Could not create session.");
        return (token, session);
    }

    public async Task<RpSession?> ValidateAsync(string token, CancellationToken ct = default)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[2].Length != 64 || !Guid.TryParseExact(parts[0], "N", out var tenant)
            || !Guid.TryParseExact(parts[1], "N", out var subject)) return null;
        var owner = new Owner(tenant.ToString("D"), subject.ToString("D"), string.Empty);
        var document = await store.ReadAsync(owner.TenantId, owner.Row("session", Owner.Hash(token)), ct);
        if (document is null) return null;
        var session = document.Value<RpSession>();
        return !session.Revoked && session.ExpiresAt > time.GetUtcNow() && owner.Owns(session.Owner.TenantId, session.Owner.ObjectId)
            ? session : null;
    }

    public async Task<RpSession?> GetAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var owner = Owner.From(principal);
        var id = principal.FindFirst(Claim)?.Value;
        if (id is null) return null;
        var row = await store.ReadAsync(owner.TenantId, owner.Row("session", id), ct);
        var session = row?.Value<RpSession>();
        return session is { Revoked: false } && session.ExpiresAt > time.GetUtcNow() ? session : null;
    }

    public async Task RevokeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var owner = Owner.From(principal);
        var id = principal.FindFirst(Claim)?.Value;
        if (id is null) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var row = await store.ReadAsync(owner.TenantId, owner.Row("session", id), ct);
            if (row is null || row.Value<RpSession>().Revoked) return;
            if (await store.CommitAsync(owner.TenantId, [StateWrite.Put(row.Id,
                row.Value<RpSession>() with { Revoked = true, VerifiedUntil = null }, row.Version)], ct)) return;
        }
        throw new InvalidOperationException("Session changed; retry sign-out.");
    }

    public static ClaimsPrincipal Principal(RpSession session) => new(new ClaimsIdentity([
        new("oid", session.Owner.ObjectId), new("tid", session.Owner.TenantId),
        new("preferred_username", session.Owner.Upn), new(Claim, session.Id),
        new("exp", session.ExpiresAt.ToUnixTimeSeconds().ToString()),
        new("roles", session.IsOperator ? "EntraGuard.Operator" : "EntraGuard.User"),
        new("roles", session.IsTenantAdmin ? "EntraGuard.TenantAdmin" : "EntraGuard.User"),
        new("roles", session.CanApprove ? "EntraGuard.PaymentApprover" : "EntraGuard.User"),
    ], SessionAuthenticationHandler.SchemeName));
}
