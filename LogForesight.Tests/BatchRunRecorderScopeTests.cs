using NLog;
using NLog.Config;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 <see cref="BatchRunRecorder"/> 的 NLog 作用域隔離機制（ScopeContext），
/// 確保執行紀錄只收集屬於該趟執行非同步流程內的 Warn 以上事件，排除外部或並存的其他執行事件。
/// </summary>
public class BatchRunRecorderScopeTests
{
    public BatchRunRecorderScopeTests()
    {
        LogManager.Configuration ??= new LoggingConfiguration();
    }

    [Fact]
    public void Scope內的Warn會被記錄且Dispose後不再記錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger1");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Warn("這是一筆在 scope 內的測試警告");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Single(logs);
        Assert.Equal("這是一筆在 scope 內的測試警告", logs[0].Message);
        Assert.Equal("Warn", logs[0].Level);

        // Dispose 之後再記的 Warn 不得進入該趟
        logger.Warn("這是一筆在 Dispose 之後的測試警告");
        var logsAfter = store.GetLogs(runId);
        Assert.Single(logsAfter);
        var runAfter = store.GetRun(runId);
        Assert.Equal(1, runAfter!.WarnCount);
    }

    [Fact]
    public async Task Await之後仍在Scope內記錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger2");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            await Task.Yield();
            logger.Warn("await Yield 之後的測試警告");
            await Task.Delay(10);
            logger.Warn("await Delay 之後的測試警告");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(2, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.Message == "await Yield 之後的測試警告");
        Assert.Contains(logs, l => l.Message == "await Delay 之後的測試警告");
    }

    [Fact]
    public async Task ParallelForEachAsync子任務內仍在Scope內記錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger3");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            var items = Enumerable.Range(1, 5).ToList();
            await Parallel.ForEachAsync(items, async (item, ct) =>
            {
                await Task.Yield();
                logger.Warn($"平行子任務 {item} 的警告");
            });
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(5, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Equal(5, logs.Count);
        for (int i = 1; i <= 5; i++)
        {
            Assert.Contains(logs, l => l.Message == $"平行子任務 {i} 的警告");
        }
    }

    [Fact]
    public async Task Scope外的Warn不會被記錄()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger4");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Warn("Scope 內的正常警告");

            // 必須使用 ExecutionContext.SuppressFlow() 包住 Task.Run 模擬外部背景執行緒或前景請求
            Task outsideTask;
            using (ExecutionContext.SuppressFlow())
            {
                outsideTask = Task.Run(() =>
                {
                    logger.Warn("Scope 外的無關警告（如前景慢SQL或未帶scope的背景服務）");
                });
            }
            await outsideTask;

            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Single(logs);
        Assert.Equal("Scope 內的正常警告", logs[0].Message);
        Assert.DoesNotContain(logs, l => l.Message.Contains("Scope 外的無關警告"));
    }

    [Fact]
    public async Task 兩個Recorder各認各的()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger5");

        var readyA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task taskA;
        Task taskB;
        long runIdA = 0;
        long runIdB = 0;

        using (ExecutionContext.SuppressFlow())
        {
            taskA = Task.Run(async () =>
            {
                using var recorderA = new BatchRunRecorder(store, "hostA", Array.Empty<string>());
                runIdA = recorderA.RunId;
                readyA.SetResult();
                await startSignal.Task;
                logger.Warn("A 的警告訊息");
                recorderA.Finish(0);
            });
        }

        using (ExecutionContext.SuppressFlow())
        {
            taskB = Task.Run(async () =>
            {
                using var recorderB = new BatchRunRecorder(store, "hostB", Array.Empty<string>());
                runIdB = recorderB.RunId;
                readyB.SetResult();
                await startSignal.Task;
                recorderB.Finish(0);
            });
        }

        await Task.WhenAll(readyA.Task, readyB.Task);
        startSignal.SetResult();
        await Task.WhenAll(taskA, taskB);

        var runA = store.GetRun(runIdA);
        var runB = store.GetRun(runIdB);
        Assert.NotNull(runA);
        Assert.NotNull(runB);

        Assert.Equal(1, runA!.WarnCount);
        Assert.Equal(0, runB!.WarnCount);

        var logsA = store.GetLogs(runIdA);
        var logsB = store.GetLogs(runIdB);

        Assert.Single(logsA);
        Assert.Equal("A 的警告訊息", logsA[0].Message);
        Assert.Empty(logsB);
    }

    [Fact]
    public async Task 在被Await的輔助Async方法中建構Recorder_返回後Scope不會向上傳播()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("ScopeTestLogger6");

        // 模擬錯誤用法：在被 await 的輔助 async 方法中建構 recorder
        static async Task<BatchRunRecorder> HelperConstructAsync(BatchRunStore s)
        {
            await Task.Yield();
            return new BatchRunRecorder(s, "test-host", Array.Empty<string>());
        }

        long runId;
        using (var recorder = await HelperConstructAsync(store))
        {
            runId = recorder.RunId;
            // 此時已返回呼叫端方法本體，由於 AsyncLocal 不會向上傳播，這裡已不在 scope 內
            logger.Warn("輔助方法返回後的警告");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        // 釘住此限制：未在涵蓋整趟執行的本體推入 scope 時，Warn 將無法被收錄
        Assert.Equal(0, run!.WarnCount);
        var logs = store.GetLogs(runId);
        Assert.Empty(logs);
    }
}
