using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace EntraGuard.MediaService.Auth;

public sealed record Owner(string TenantId, string ObjectId, string Upn)
{
    public string Prefix(string kind) => $"o_{Guid.Parse(ObjectId):N}_{kind}_";
    public string Row(string kind, string id) => Prefix(kind) + id;
    public bool Owns(string? tenant, string? subject) =>
        Guid.TryParse(tenant, out var t) && Guid.TryParse(subject, out var s)
        && t == Guid.Parse(TenantId) && s == Guid.Parse(ObjectId);
    public static Owner From(ClaimsPrincipal principal)
    {
        var caller = principal.Caller();
        if (caller is null || !Guid.TryParse(caller.TenantId, out var tenant) || !Guid.TryParse(caller.ObjectId, out var subject))
            throw new UnauthorizedAccessException("A validated tenant and user are required.");
        return new Owner(tenant.ToString("D"), subject.ToString("D"), caller.Upn);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
