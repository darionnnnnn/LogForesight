using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogForesight.Web.Services;

/// <summary>Native streamed download: fixed memory reservation, consistent DB snapshot, fresh authorization per row.</summary>
public sealed class PrtgDiagnosticExportActionResult(StorageBackend backend,
    IPrtgTransferCapacityProvider capacity, Func<PrtgTransferBinding> captureBinding,
    DateTime fromDate, DateTime toDate, IAuditService audit) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        var token = deadline.Token;
        var store = new EfPrtgTransferStore(backend, capacity);
        PrtgDiagnosticReadLease? lease = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var binding = captureBinding();
            lease = store.AcquireExportReadLease(Guid.NewGuid(), Guid.NewGuid().ToString("N"), binding, DateTimeOffset.UtcNow);
            using var strategyContext = backend.CreateContext();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
            if (response.HasStarted) throw new InvalidDataException("已開始的下載不能重播；請重新下載。");
            await using var db = backend.CreateContext();
            db.Database.SetCommandTimeout(30);
            await db.Database.OpenConnectionAsync(token);
            if (db.Database.IsSqlServer())
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID()";
                var state = Convert.ToInt32(await command.ExecuteScalarAsync(token));
                if (state != 1) throw new PrtgTransferStoreException("export_snapshot_unavailable",
                    "SQL Server 尚未啟用 ALLOW_SNAPSHOT_ISOLATION；請依維運文件設定後重試診斷匯出。", 409);
            }
            else if (db.Database.IsSqlite())
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode";
                if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(token)), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new PrtgTransferStoreException("export_snapshot_unavailable",
                        "SQLite 診斷匯出需要 WAL，以避免長時間讀取阻塞正式寫入；請依維運文件設定後重試。", 409);
            }
            else throw new PrtgTransferStoreException("export_provider_unsupported", "此資料庫不支援一致串流匯出。", 409);

            await using var transaction = await db.Database.BeginTransactionAsync(
                db.Database.IsSqlServer() ? IsolationLevel.Snapshot : IsolationLevel.Serializable, token);
            var policyRow = await db.Blobs.AsNoTracking().Where(row => row.BlobKey == PrtgMonitoringPolicyStore.BlobKey)
                .Select(row => new { Prefix = row.Content.Substring(0, 4 * 1024 * 1024 + 1), Length = row.Content.Length, row.Version })
                .SingleOrDefaultAsync(token);
            if (policyRow is { Length: > 4 * 1024 * 1024 })
                throw new InvalidDataException("來源政策超過有界讀取上限。");
            var policy = policyRow == null ? new PrtgMonitoringPolicy() :
                JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRow.Prefix, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("來源政策格式無效。");
            if (captureBinding() != binding)
                throw new PrtgTransferStoreException("transfer_context_changed", "匯出來源或授權已變更，請重新開始下載。", 409);

            response.ContentType = "application/json; charset=utf-8";
            response.Headers.ContentDisposition = $"attachment; filename=prtg-export-{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}.json";
            response.Headers.CacheControl = "no-store";
            var rows = new PrtgDiagnosticExportSource().EnumerateRowsAsync(db, fromDate, toDate, policy, policyRow?.Version ?? 0, token);
            var result = await new PrtgDiagnosticExportWriter().WriteAsync(FencedRows(rows, binding, token),
                response.Body, new(DateTime.UtcNow, fromDate.Date, toDate.Date), token);
            token.ThrowIfCancellationRequested();
            if (captureBinding() != binding) throw new InvalidDataException("匯出授權或來源已變更。");
            await transaction.CommitAsync(token);
            audit.Record(AuditActions.PrtgDataExport,
                $"串流匯出診斷包 {result.TotalBytes} 位元組；正式監控資料未變更。",
                "prtg_data", $"{fromDate:yyyyMMdd}-{toDate:yyyyMMdd}",
                detail: new { result.Counts, result.TotalBytes, result.Sha256BeforeManifest });
            });
        }
        catch (OperationCanceledException)
        {
            if (response.HasStarted) context.HttpContext.Abort();
            else response.StatusCode = 499;
        }
        catch (Exception error)
        {
            context.HttpContext.RequestServices.GetService<ILogger<PrtgDiagnosticExportActionResult>>()?
                .LogWarning(error, "PRTG diagnostic export failed after response started: {ResponseStarted}", response.HasStarted);
            if (response.HasStarted) { context.HttpContext.Abort(); return; }
            response.StatusCode = error is PrtgTransferStoreException transfer ? transfer.HttpStatusCode : 422;
            await response.WriteAsJsonAsync(ApiResponse.Fail(
                error is PrtgTransferStoreException failure ? failure.Code : "export_failed",
                error is PrtgTransferStoreException ? error.Message : "診斷匯出未完成；來源資料超限或快照失效。"),
                context.HttpContext.RequestAborted);
        }
        finally
        {
            // Reader/transaction are disposed before returning this memory reservation.
            if (lease != null)
                try { store.ReleaseExportReadLease(lease, DateTimeOffset.UtcNow); }
                catch { /* Durable expiry retains the reservation until the reader's deadline has elapsed. */ }
        }
    }

    private async IAsyncEnumerable<PrtgDiagnosticExportItem> FencedRows(
        IAsyncEnumerable<PrtgDiagnosticExportItem> rows, PrtgTransferBinding initial,
        [EnumeratorCancellation] CancellationToken token)
    {
        var sinceFence = 0;
        string? property = null;
        await foreach (var row in rows.WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (property != row.PropertyName) { property = row.PropertyName; sinceFence = 0; }
            var pageSize = row.PropertyName == "Values" ? PrtgDiagnosticExportSource.ValuePageSize : PrtgDiagnosticExportSource.PageSize;
            if (sinceFence++ % pageSize == 0 && captureBinding() != initial)
                throw new PrtgTransferStoreException("transfer_context_changed", "匯出期間權限或來源已變更。", 409);
            yield return row;
        }
        if (captureBinding() != initial) throw new InvalidDataException("匯出期間權限或來源已變更。");
    }
}
