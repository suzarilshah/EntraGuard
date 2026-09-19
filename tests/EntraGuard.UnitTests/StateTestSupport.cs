using System.Security.Claims;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.UnitTests;

internal sealed class MemoryStateStore : IStateStore
{
    public async IAsyncEnumerable<PendingWork> WorkAsync(string kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        List<PendingWork> work;
        lock (_rows) work = _rows.Where(r => r.Key.Id.StartsWith(kind + "_", StringComparison.Ordinal)).Select(r => new PendingWork(r.Key.Tenant, r.Value)).ToList();
        foreach (var item in work) { ct.ThrowIfCancellationRequested(); yield return item; }
        await Task.CompletedTask;
    }
    private readonly Dictionary<(string Tenant, string Id), StateDocument> _rows = [];
    private int _version;
    public Task<StateDocument?> ReadAsync(string tenant, string id, CancellationToken ct = default)
    { lock (_rows) return Task.FromResult(_rows.GetValueOrDefault((tenant, id))); }
    public Task<bool> CommitAsync(string tenant, IReadOnlyList<StateWrite> writes, CancellationToken ct = default)
    {
        lock (_rows)
        {
            if (writes.Any(w => _rows.GetValueOrDefault((tenant, w.Id))?.Version != w.ExpectedVersion)) return Task.FromResult(false);
            foreach (var w in writes)
                if (w.Json is null) _rows.Remove((tenant, w.Id));
                else _rows[(tenant, w.Id)] = new StateDocument(w.Id, w.Json, (++_version).ToString());
            return Task.FromResult(true);
        }
    }
    public Task<StatePage> ListAsync(string tenant, string prefix, int limit, string? continuation = null, CancellationToken ct = default)
    {
        lock (_rows)
        {
            var values = _rows.Where(r => r.Key.Tenant == tenant && r.Key.Id.StartsWith(prefix, StringComparison.Ordinal)
                && (continuation is null || string.CompareOrdinal(r.Key.Id, continuation) > 0))
                .Select(r => r.Value).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
            var page = values.Take(limit).ToArray();
            return Task.FromResult(new StatePage(page, values.Count > limit ? page[^1].Id : null));
        }
    }
}

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class TestIdentity
{
    public const string Tenant = "11111111-1111-4111-8111-111111111111";
    public const string Subject = "22222222-2222-4222-8222-222222222222";
    public const string Other = "33333333-3333-4333-8333-333333333333";
    public static ClaimsPrincipal Principal(TimeProvider time, string subject = Subject, string tenant = Tenant) => new(new ClaimsIdentity([
        new("tid", tenant), new("oid", subject), new("preferred_username", "test@example.test"),
        new("exp", time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds().ToString()), new("scp", "VoiceProfile.Manage"),
    ], "test"));
}
