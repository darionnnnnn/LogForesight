using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 76-2 輪批次 C-4：保留清除各段獨立執行，一段失敗不中斷其他段，結果回傳給呼叫端、
/// 並反映在執行紀錄的保留階段（不再永遠寫成功）。
/// </summary>
public class RetentionSegmentTests
{
    private sealed class NullConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }

    private sealed class EmptyCandidateSource : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = new Dictionary<long, DispatchCandidate>(), PoolMemberCount = 0, ActivePoolMemberCount = 0
        };
    }

    private sealed class NullProgress : IRunProgress
    {
        public void Report(string phase, int done, int total) { }
    }

    private static void DropTable(StorageBackend backend, string table)
    {
        using var ctx = backend.CreateContext();
        ctx.Database.ExecuteSqlRaw($"DROP TABLE {table}");
    }

    [Fact]
    public void 第一段擲例外_其餘段照常執行且回傳失敗段名()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-retention-seg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "r.db")}" }, dir);
            backend.RecordStore().Prune(30);   // 先讓 schema 建立
            // 後面的段要有可觀察的效果：過期的 PRTG 匯入診斷包應被「PRTG 鏡像資料」段清掉
            backend.Blob("prtg_import_diagnostic_old").Mutate(_ => ("{}", true));
            using (var db = backend.CreateContext())
                db.Blobs.Where(b => b.BlobKey == "prtg_import_diagnostic_old").ExecuteUpdate(u => u.SetProperty(b => b.UpdatedAt, DateTime.Now.AddDays(-400)));
            DropTable(backend, "lf_daily_records");   // 第一段（歷史紀錄）與第二段（紀錄詳情）都用這張表

            var result = RetentionPruner.Run(backend, new RetentionOptions { PrtgRetentionDays = 30 }, new NullConsole(), batchRunStore: null);

            Assert.False(result.Succeeded);
            Assert.Contains("歷史紀錄", result.FailedSegments);
            Assert.DoesNotContain("PRTG 鏡像資料", result.FailedSegments);
            Assert.DoesNotContain("稽核紀錄", result.FailedSegments);
            using var after = backend.CreateContext();
            Assert.False(after.Blobs.Any(b => b.BlobKey == "prtg_import_diagnostic_old"), "第一段失敗後，後面的段仍要執行");
            Assert.Null(RetentionPruner.LastRunDate(backend));   // 有段失敗就不記今日已執行
            var failure = RetentionPruner.LastFailure(backend);
            Assert.NotNull(failure);
            Assert.Contains("歷史紀錄", failure!.Segments);
            Assert.Contains("歷史紀錄", result.Note);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 全部成功_沒有失敗段名且不留失敗記錄()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-retention-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "r.db")}" }, dir);

            var result = RetentionPruner.Run(backend, new RetentionOptions(), new NullConsole(), batchRunStore: null);

            Assert.True(result.Succeeded, result.Note);
            Assert.Null(result.Note);
            Assert.Null(RetentionPruner.LastFailure(backend));
            Assert.Equal(DateTime.Today, RetentionPruner.LastRunDate(backend));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task 編排器_保留段有失敗_執行紀錄的保留階段不是成功()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-retention-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new AppSettings
            {
                Ai = new AiSettings { BaseUrl = "" },
                Storage = new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "t.db")};Pooling=False" }
            };
            var seed = new StorageBackend(settings.Storage, dir, 1);
            // 「PRTG 鏡像資料」段要擲例外：放一份過期的 PRTG 匯入診斷包，再用觸發器讓刪除它時中止
            // （拿掉表或欄位的作法會讓 Orchestrator 開啟儲存時就失敗；觸發器只影響這一段的刪除）
            seed.Blob("prtg_import_diagnostic_old").Mutate(_ => ("{}", true));
            using (var ctx = seed.CreateContext())
            {
                ctx.Blobs.Where(b => b.BlobKey == "prtg_import_diagnostic_old").ExecuteUpdate(u => u.SetProperty(b => b.UpdatedAt, DateTime.Now.AddDays(-400)));
                ctx.Database.ExecuteSqlRaw(
                    "CREATE TRIGGER fail_diag_delete BEFORE DELETE ON lf_blobs WHEN OLD.blob_key LIKE 'prtg_import_diagnostic_%' " +
                    "BEGIN SELECT RAISE(ABORT, 'retention test failure'); END");
            }

            var orchestrator = new AnalysisOrchestrator(new EmptyCandidateSource(), NullAiRequestGate.Instance);
            var result = await orchestrator.RunAsync(
                new RunRequest { Scope = RunScope.Full, IncludeLocal = false },
                settings, dir, new RetentionOptions(), new NullConsole(), CancellationToken.None,
                new NullProgress(), manualWorkGate: null, prtgBackfillTail: null);
            Assert.True(result.Success, result.FailureMessage);   // 保留清除失敗不讓整趟分析失敗

            var backend = new StorageBackend(settings.Storage, dir, 1);
            var store = new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
            var run = store.GetRun(result.BatchRunId!.Value)!;
            var retention = Assert.Single(run.Phases!, p => p.Key == RunTimingPhases.Retention);
            Assert.NotEqual(RunPhaseOutcomes.Success, retention.Outcome);
            Assert.Equal(RunPhaseOutcomes.Partial, retention.Outcome);
            Assert.Contains("PRTG 鏡像資料", retention.Note ?? string.Empty);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
