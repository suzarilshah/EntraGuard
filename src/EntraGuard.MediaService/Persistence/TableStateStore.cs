using Azure;
using Azure.Data.Tables;

namespace EntraGuard.MediaService.Persistence;

public sealed class TableStateStore(TableServiceClient service) : IStateStore
{
    private readonly TableClient _table = service.GetTableClient("EntraGuardState");
    private readonly SemaphoreSlim _initialization = new(1);
    private bool _ready;

    private async Task ReadyAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _initialization.WaitAsync(ct);
        try
        {
            if (!_ready) { await _table.CreateIfNotExistsAsync(ct); _ready = true; }
        }
        finally { _initialization.Release(); }
    }

    public async Task<StateDocument?> ReadAsync(string tenant, string id, CancellationToken ct = default)
    {
        await ReadyAsync(ct);
        var entity = await _table.GetEntityIfExistsAsync<TableEntity>(Guid.Parse(tenant).ToString("N"), id, cancellationToken: ct);
        return entity.HasValue ? Convert(entity.Value!) : null;
    }

    public async Task<bool> CommitAsync(string tenant, IReadOnlyList<StateWrite> writes, CancellationToken ct = default)
    {
        if (writes.Count is < 1 or > 100 || writes.Select(w => w.Id).Distinct().Count() != writes.Count)
            throw new ArgumentException("A transaction needs 1–100 distinct rows.");
        await ReadyAsync(ct);
        var partition = Guid.Parse(tenant).ToString("N");
        var batch = writes.Select(write =>
        {
            if (write.Json?.Length > 30000) throw new ArgumentException("State document exceeds the storage limit.");
            var entity = new TableEntity(partition, write.Id);
            entity["WorkKind"] = write.Id.StartsWith("outbox_", StringComparison.Ordinal) ? "outbox"
                : write.Id.StartsWith("recovery_", StringComparison.Ordinal) ? "recovery" : "";
            if (write.Json is not null) entity["Json"] = write.Json;
            var type = write.Json is null ? TableTransactionActionType.Delete
                : write.ExpectedVersion is null ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace;
            if (type == TableTransactionActionType.Delete && write.ExpectedVersion is null)
                throw new ArgumentException("Deletion requires a version.");
            return new TableTransactionAction(type, entity,
                write.ExpectedVersion is null ? default : new ETag(write.ExpectedVersion));
        });
        try { await _table.SubmitTransactionAsync(batch, ct); return true; }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412) { return false; }
    }

    public async Task<StatePage> ListAsync(string tenant, string prefix, int limit, string? continuation = null, CancellationToken ct = default)
    {
        await ReadyAsync(ct);
        if (limit is < 1 or > 100 || !prefix.EndsWith('_')) throw new ArgumentException("Invalid page query.");
        var partition = Guid.Parse(tenant).ToString("N");
        var upper = prefix[..^1] + '`'; // '_' + 1; all rows for this exact owner/kind prefix.
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {upper}");
        await foreach (var page in _table.QueryAsync<TableEntity>(filter, limit, cancellationToken: ct).AsPages(continuation, limit))
            return new StatePage(page.Values.Select(Convert).ToArray(), page.ContinuationToken);
        return new StatePage([], null);
    }

    private static StateDocument Convert(TableEntity entity) => new(entity.RowKey, entity.GetString("Json")!, entity.ETag.ToString());

    public async IAsyncEnumerable<PendingWork> WorkAsync(string kind, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await ReadyAsync(ct);
        var filter = TableClient.CreateQueryFilter($"WorkKind eq {kind}");
        await foreach (var row in _table.QueryAsync<TableEntity>(filter, 100, cancellationToken: ct))
            yield return new PendingWork(Guid.Parse(row.PartitionKey).ToString("D"), Convert(row));
    }
}
