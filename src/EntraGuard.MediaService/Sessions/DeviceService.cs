using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Sessions;

public sealed record RegisteredDevice(string Kind, string AcsUserId, string SessionId, DateTimeOffset RegisteredAt,
    DateTimeOffset? LastSeen = null, bool Revoked = false, string? Name = null);

public sealed class DeviceService(IStateStore store, TimeProvider time)
{
    public static bool ValidKind(string kind) => kind is "browser" or "phone";
    public async Task<RegisteredDevice?> GetAsync(Owner owner, string kind, CancellationToken ct = default) =>
        (await store.ReadAsync(owner.TenantId, owner.Row("device", kind), ct))?.Value<RegisteredDevice>();

    public async Task<RegisteredDevice> RegisterAsync(Owner owner, string kind, string sessionId,
        Func<Task<string>> createIdentity, CancellationToken ct)
    {
        if (!ValidKind(kind)) throw new ArgumentException("Unsupported device kind.");
        var id = owner.Row("device", kind);
        var row = await store.ReadAsync(owner.TenantId, id, ct);
        var previous = row?.Value<RegisteredDevice>();
        var acsId = previous is { Revoked: false } ? previous.AcsUserId : await createIdentity();
        var device = new RegisteredDevice(kind, acsId, sessionId, time.GetUtcNow(), Name: previous?.Name);
        if (!await store.CommitAsync(owner.TenantId, [StateWrite.Put(id, device, row?.Version)], ct))
            throw new InvalidOperationException("Device registration changed; reconnect this device.");
        return device;
    }

    public async Task<bool> HeartbeatAsync(Owner owner, string kind, string acsId, string sessionId, CancellationToken ct)
    {
        if (!ValidKind(kind)) return false;
        var row = await store.ReadAsync(owner.TenantId, owner.Row("device", kind), ct);
        var device = row?.Value<RegisteredDevice>();
        if (row is null || device is null || device.Revoked || device.SessionId != sessionId || device.AcsUserId != acsId) return false;
        return await store.CommitAsync(owner.TenantId, [StateWrite.Put(row.Id, device with { LastSeen = time.GetUtcNow() }, row.Version)], ct);
    }

    public async Task<IReadOnlyList<RegisteredDevice>> ListAsync(Owner owner, CancellationToken ct = default)
    {
        var page = await store.ListAsync(owner.TenantId, owner.Prefix("device"), 10, ct: ct);
        return page.Items.Select(d => d.Value<RegisteredDevice>()).ToArray();
    }
    public bool Reachable(RegisteredDevice device) => !device.Revoked && device.LastSeen > time.GetUtcNow().AddSeconds(-30);

    public async Task<bool> RevokeAsync(Owner owner, string kind, CancellationToken ct)
    {
        if (!ValidKind(kind)) return false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var row = await store.ReadAsync(owner.TenantId, owner.Row("device", kind), ct);
            if (row is null) return true;
            var device = row.Value<RegisteredDevice>();
            var session = await store.ReadAsync(owner.TenantId, owner.Row("session", device.SessionId), ct);
            var writes = new List<StateWrite> { StateWrite.Put(row.Id, device with { Revoked = true, LastSeen = null }, row.Version) };
            if (session is not null) writes.Add(StateWrite.Put(session.Id, session.Value<RpSession>() with { Revoked = true, VerifiedUntil = null }, session.Version));
            if (await store.CommitAsync(owner.TenantId, writes, ct)) return true;
        }
        return false;
    }
}
