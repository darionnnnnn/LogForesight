using Microsoft.EntityFrameworkCore;
using NLog;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 常駐 NLog target 的分派、寫入解耦與不增減全域設定（取代原本逐趟附掛卸除的規則洩漏測試）。
/// 順序一律由 TaskCompletionSource／事件控制，不靠睡眠或壓力。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class BatchRunRecorderCaptureTests : IDisposable
{
    /// <summary>檔案型 SQLite：每次操作各開連線（與正式環境相同），背景寫入執行緒才能與測試並行存取</summary>
    private sealed class FileLogDb : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("lf-runlog-").FullName;
        private readonly Microsoft.EntityFrameworkCore.DbContextOptions<LfDbContext> _options;

        public FileLogDb()
        {
            _options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LfDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_dir, "t.db")};Pooling=False").Options;
            using var ctx = NewContext();
            ctx.Database.EnsureCreated();
        }

        public LfDbContext NewContext() => new(_options);
        public EfJsonLogStore LogStore(string key) => new(NewContext, key);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    public BatchRunRecorderCaptureTests() => RunLogQueue.WriteInline = false;

    public void Dispose() => RunLogQueue.WriteInline = true;

    private static BatchRunStore NewStore(FileLogDb fixture) =>
        new(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

    private static Task RunInOwnFlow(Func<Task> body)
    {
        // 各趟執行有自己的非同步流程（正式環境即如此），不共用 ScopeContext
        using (ExecutionContext.SuppressFlow()) return Task.Run(body);
    }

    [Fact]
    public async Task 取數與AI兩趟並行_各自的警告只進各自的執行紀錄()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestDispatch");
        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aWarned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bWarned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long idA = 0, idB = 0;

        var a = RunInOwnFlow(async () =>
        {
            using var rec = new BatchRunRecorder(store, "fetch", Array.Empty<string>(), jobType: null);
            idA = rec.RunId;
            aReady.SetResult();
            await bReady.Task;           // 兩個 recorder 同時存活
            logger.Warn("取數的警告");
            aWarned.SetResult();
            await bWarned.Task;          // 對方也記完才結束，交錯到底
            rec.Finish(0);
        });
        var b = RunInOwnFlow(async () =>
        {
            using var rec = new BatchRunRecorder(store, "ai", Array.Empty<string>(), jobType: BatchRun.JobTypeAi);
            idB = rec.RunId;
            bReady.SetResult();
            await aReady.Task;
            await aWarned.Task;
            logger.Warn("AI 的警告");
            logger.Error("AI 的錯誤");
            bWarned.SetResult();
            rec.Finish(0);
        });
        await Task.WhenAll(a, b);

        Assert.Equal(new[] { "取數的警告" }, store.GetLogs(idA).Select(l => l.Message));
        Assert.Equal(new[] { "AI 的警告", "AI 的錯誤" }, store.GetLogs(idB).Select(l => l.Message));
        Assert.Equal(1, store.GetRun(idA)!.WarnCount);
        Assert.Equal(1, store.GetRun(idB)!.WarnCount);
        Assert.Equal(1, store.GetRun(idB)!.ErrorCount);
    }

    [Fact]
    public async Task 範圍內開出的背景工作與平行工作繼承識別_警告進對的那一趟()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestInherit");
        long idA = 0, idB = 0;
        var aInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = RunInOwnFlow(async () =>
        {
            using var rec = new BatchRunRecorder(store, "a", Array.Empty<string>());
            idA = rec.RunId;
            aInside.SetResult();
            await Task.Run(() => logger.Warn("A Task.Run"));
            await Parallel.ForEachAsync(new[] { 1, 2 }, async (i, _) =>
            {
                await Task.Yield();
                logger.Warn($"A 平行 {i}");
            });
            await bDone.Task;
            rec.Finish(0);
        });
        var b = RunInOwnFlow(async () =>
        {
            await aInside.Task;
            using var rec = new BatchRunRecorder(store, "b", Array.Empty<string>());
            idB = rec.RunId;
            await Task.Run(() => logger.Warn("B Task.Run"));
            await Parallel.ForEachAsync(new[] { 1 }, async (i, _) =>
            {
                await Task.Yield();
                logger.Warn($"B 平行 {i}");
            });
            rec.Finish(0);
            bDone.SetResult();
        });
        await Task.WhenAll(a, b);

        Assert.Equal(new[] { "A Task.Run", "A 平行 1", "A 平行 2" }.OrderBy(m => m, StringComparer.Ordinal),
            store.GetLogs(idA).Select(l => l.Message).OrderBy(m => m, StringComparer.Ordinal));
        Assert.Equal(new[] { "B Task.Run", "B 平行 1" }.OrderBy(m => m, StringComparer.Ordinal),
            store.GetLogs(idB).Select(l => l.Message).OrderBy(m => m, StringComparer.Ordinal));
        Assert.Equal(3, store.GetRun(idA)!.WarnCount);
        Assert.Equal(2, store.GetRun(idB)!.WarnCount);
    }

    [Fact]
    public async Task 沒有識別或識別為空字串的事件不進任何一趟()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestNoScope");
        long runId;
        using (var rec = new BatchRunRecorder(store, "x", Array.Empty<string>()))
        {
            runId = rec.RunId;
            // 範圍外：不繼承 recorder 的識別
            await RunInOwnFlow(() => { logger.Warn("沒有識別"); return Task.CompletedTask; });
            // 範圍內被覆寫成空字串
            using (ScopeContext.PushProperty("lf_run_scope", string.Empty)) logger.Warn("空字串識別");
            // 未登記的識別（recorder 已結束或別的行程）
            using (ScopeContext.PushProperty("lf_run_scope", "never-registered")) logger.Warn("未登記識別");
            logger.Warn("範圍內的警告");
            rec.Finish(0);
        }

        Assert.Equal(new[] { "範圍內的警告" }, store.GetLogs(runId).Select(l => l.Message));
        Assert.Equal(1, store.GetRun(runId)!.WarnCount);
    }

    [Fact]
    public void 連續一百趟執行後全域設定的規則數與target數不變()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestHundred");
        RunLogCapture.EnsureInstalled();
        var config = LogManager.Configuration!;
        var rules = config.LoggingRules.Count;
        var targets = config.AllTargets.Count;
        var sinks = RunLogCapture.ActiveSinkCount;

        for (var i = 0; i < 100; i++)
        {
            using var rec = new BatchRunRecorder(store, "h", Array.Empty<string>());
            logger.Warn("第 {0} 趟", i);
            rec.Finish(0);
        }

        Assert.Same(config, LogManager.Configuration);
        Assert.Equal(rules, config.LoggingRules.Count);
        Assert.Equal(targets, config.AllTargets.Count);
        Assert.Equal(sinks, RunLogCapture.ActiveSinkCount);
        Assert.True(config.LoggingRules.Count(r => r.Targets.Any(t => t.Name == RunLogCapture.TargetName)) == 1,
            string.Join("; ", config.LoggingRules.Select(r => r.LoggerNamePattern + ":" + string.Join(",", r.Targets.Select(t => t.Name)))));
    }

    [Fact]
    public void EnsureInstalled重複呼叫是冪等的()
    {
        RunLogCapture.EnsureInstalled();
        var config = LogManager.Configuration!;
        var rules = config.LoggingRules.Count;
        RunLogCapture.EnsureInstalled();
        RunLogCapture.EnsureInstalled();
        Assert.Equal(rules, config.LoggingRules.Count);
    }

    [Fact]
    public void 結束前排空_Finish回傳時所有已記錄的警告都已落庫()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestDrain");
        long runId;
        using (var rec = new BatchRunRecorder(store, "h", Array.Empty<string>()))
        {
            runId = rec.RunId;
            for (var i = 0; i < 300; i++) logger.Warn("警告 {0}", i);
            rec.Milestone("里程碑夾在中間");
            logger.Warn("最後一筆");
            rec.Finish(0);
            // Finish 一回傳就要讀得到，不等任何背景時機
            var logs = store.GetLogs(runId);
            Assert.Equal(302, logs.Count);
            Assert.Equal("最後一筆", logs[^1].Message);
            Assert.Equal(302, store.GetRun(runId)!.WarnCount + 1);
        }
    }

    [Fact]
    public void 診斷行保持記錄順序_里程碑與擷取事件共用同一條佇列()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestOrder");
        long runId;
        using (var rec = new BatchRunRecorder(store, "h", Array.Empty<string>()))
        {
            runId = rec.RunId;
            rec.Milestone("一");
            logger.Warn("二");
            rec.Message("三");
            logger.Warn("四");
            rec.Finish(0);
        }
        Assert.Equal(new[] { "一", "二", "三", "四" }, store.GetLogs(runId).Select(l => l.Message));
    }

    [Fact]
    public void 結束後才記的警告不進紀錄也不擲例外()
    {
        using var fixture = new FileLogDb();
        var store = NewStore(fixture);
        var logger = LogManager.GetLogger("CaptureTestLate");
        using var rec = new BatchRunRecorder(store, "h", Array.Empty<string>());
        rec.Finish(0);
        logger.Warn("結束後");
        Assert.Empty(store.GetLogs(rec.RunId));
    }

    [Fact]
    public void 寫庫一直失敗時不會無限遞迴_直接寫入路徑上的失敗警告不再被擷取()
    {
        RunLogQueue.WriteInline = true; // 直接寫入路徑：失敗警告帶著本趟識別，沒有防護會再被擷取而遞迴
        var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("CaptureTestRecursion");
        using var rec = new BatchRunRecorder(store, "h", Array.Empty<string>());
        fixture.Dispose(); // 之後每次寫庫都失敗

        logger.Warn("寫不進去的警告");
        rec.Milestone("也寫不進去");
        rec.Finish(0); // 走到這裡沒有 StackOverflow 就是通過
    }
}
