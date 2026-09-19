using System.Text.Json;

namespace EntraGuard.MediaService.Persistence;

public sealed record StateDocument(string Id, string Json, string Version)
{
    public T Value<T>() => JsonSerializer.Deserialize<T>(Json, StateJson.Options)
        ?? throw new InvalidDataException("Stored state was empty.");
}

public sealed record StateWrite(string Id, string? Json, string? ExpectedVersion = null)
{
    // Null version means INSERT, never unconditional replacement. Null JSON means delete.
    public static StateWrite Put<T>(string id, T value, string? version = null) =>
        new(id, JsonSerializer.Serialize(value, StateJson.Options), version);
    public static StateWrite Delete(StateDocument document) => new(document.Id, null, document.Version);
}

public sealed record StatePage(IReadOnlyList<StateDocument> Items, string? ContinuationToken);
public sealed record PendingWork(string TenantId, StateDocument Document);

public static class StateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Durable tenant partition. Conditional multi-row commits are atomic.</summary>
public interface IStateStore
{
    Task<StateDocument?> ReadAsync(string tenant, string id, CancellationToken ct = default);
    Task<bool> CommitAsync(string tenant, IReadOnlyList<StateWrite> writes, CancellationToken ct = default);
    Task<StatePage> ListAsync(string tenant, string prefix, int limit, string? continuation = null, CancellationToken ct = default);
    IAsyncEnumerable<PendingWork> WorkAsync(string kind, CancellationToken ct = default);
}
