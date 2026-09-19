using EntraGuard.MediaService.Sinks;

namespace EntraGuard.MediaService.Persistence;

public sealed class VerificationOutboxWorker(IStateStore store, VerificationLedger ledger, LogsIngestionSink sink,
    ILogger<VerificationOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await foreach (var work in store.WorkAsync("recovery", stoppingToken))
                    await ledger.RecoverAsync(work, stoppingToken);
                await foreach (var work in store.WorkAsync("outbox", stoppingToken))
                {
                    if (await sink.WriteVerificationAsync(work.Document.Value<VerificationReceipt>().ToTelemetry(), stoppingToken))
                        await store.CommitAsync(work.TenantId, [StateWrite.Delete(work.Document)], stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Durable verification delivery will retry."); }
        }
    }
}
