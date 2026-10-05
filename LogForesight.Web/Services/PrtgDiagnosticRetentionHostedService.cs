using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>Completed/explicitly abandoned diagnostic chunks expire in bounded, restartable batches.</summary>
public sealed class PrtgDiagnosticRetentionHostedService(StorageBackend backend, IPrtgTransferCapacityProvider capacity)
    : BackgroundService
{
    public static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan AbandonedRetention = TimeSpan.FromDays(1);
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal async Task<int> TickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completedBefore = now - CompletedRetention;
        var abandonedBefore = now - AbandonedRetention;
        var store = new EfPrtgTransferStore(backend, capacity);
        store.ExpireExportReadLeases(now);
        using var db = backend.CreateContext();
        var due = await db.PrtgTransferSessions.AsNoTracking()
            .Where(row => (row.State == PrtgTransferStates.Complete && row.CompletedAtUtc <= completedBefore) ||
                          (row.State == PrtgTransferStates.Abandoned && row.AbandonedAtUtc <= abandonedBefore))
            .OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.TransferId)
            .Select(row => new { row.TransferId, row.CleanupAfterOrdinal }).Take(16)
            .ToArrayAsync(cancellationToken);
        var removed = 0;
        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read only keys; CleanupChunkBatch never materializes the payload being deleted.
            try
            {
                var result = store.CleanupChunkBatch(row.TransferId, row.CleanupAfterOrdinal, 8,
                    completedBefore, abandonedBefore, now);
                removed += result.DeletedChunks;
            }
            catch (PrtgTransferStoreException ex) when (ex.Code is "cleanup_cursor_conflict" or "transfer_retention_not_elapsed")
            { /* A concurrent cleanup or a fresh state transition will be retried from durable metadata. */ }
            await Task.Yield();
        }
        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Warn(ex, "PRTG 診斷保留清理未完成，下輪從持久游標重試。"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
