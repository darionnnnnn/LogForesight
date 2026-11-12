using System.Runtime.CompilerServices;
using NLog;
using NLog.Config;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 測試組件載入時就建好 NLog 全域設定，在任何測試類別建構之前。
/// 否則多個測試類別的建構子同時以 <c>LogManager.Configuration ??= new ...</c> 初始化時，
/// 兩邊都讀到 null、後設定的那份會取代先設定的那份——已經掛在先前那份上的 recorder target 跟著消失，
/// 該趟執行紀錄靜默變成沒有任何警告，Scope 相關斷言偶發失敗。
/// </summary>
internal static class NLogTestConfigurationInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        LogManager.Configuration ??= new LoggingConfiguration();
        // PRTG 分頁等待重試（15 秒、60 秒）在整合測試連到不可達位址時會讓案例慢數分鐘；測試組件一律零等待
        LogForesight.Core.Service.PrtgTablePager.DefaultWait = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        // 全程序共用的配額池：真時鐘下全量並行的 PRTG 測試彼此排隊到逾時；配額規則照常生效，只是時間加速
        LogForesight.Core.Service.PrtgRequestBudget.Shared =
            new LogForesight.Core.Service.PrtgRequestBudget(new LogForesight.Tests.TestDoubles.AcceleratedPrtgClock());
    }
}

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

    [Fact]
    public void 慢SQL的Warn寫入log但不計入WarnCount()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("LogForesight.Core.Persistence.Sql.SqlPerformanceMonitor");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Warn("[SQL][慢] 耗時 2500ms > 門檻 1000ms: SELECT * FROM lf_records");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(0, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Single(logs);
        Assert.Equal("SqlPerformanceMonitor", logs[0].Logger);
        Assert.Equal("Warn", logs[0].Level);
        Assert.Contains("[SQL][慢]", logs[0].Message);
    }

    [Fact]
    public void AI診斷logger的Warn寫入log但不計入WarnCount()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        // 完整名稱帶命名空間時，短名稱取最後一段也要對得上；實際診斷 logger 沒有句點
        var logger = LogManager.GetLogger(AiJsonDiagnostics.LoggerName);

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Warn("{Diagnostics}", "{\"label\":\"daily-20261101-h1\"}");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(0, run!.WarnCount);

        var log = Assert.Single(store.GetLogs(runId));
        Assert.Equal(AiJsonDiagnostics.LoggerName, log.Logger);
        Assert.Equal("Warn", log.Level);
        Assert.Equal("{\"label\":\"daily-20261101-h1\"}", log.Message);
    }

    [Fact]
    public void AI診斷logger與慢SQL各自免計_其他logger同訊息仍計數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            LogManager.GetLogger(AiJsonDiagnostics.LoggerName).Warn("同一段訊息");
            LogManager.GetLogger("LogForesight.Core.Persistence.Sql.SqlPerformanceMonitor").Warn("同一段訊息");
            LogManager.GetLogger("LogForesight.Core.Service.SomeOtherService").Warn("同一段訊息");
            LogManager.GetLogger(AiJsonDiagnostics.LoggerName).Error("診斷 logger 的 Error 照常計數");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.WarnCount);
        Assert.Equal(1, run.ErrorCount);
        Assert.Equal(4, store.GetLogs(runId).Count);
    }

    [Theory]
    [InlineData("SqlPerformanceMonitor", true)]
    [InlineData("AiJsonDiagnostics", true)]
    [InlineData("AIService", false)]
    [InlineData("aijsondiagnostics", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 免計WarnCount的logger判定_只認兩個名稱(string? name, bool expected) =>
        Assert.Equal(expected, BatchRunRecorder.IsWarnCountExemptLogger(name));

    [Fact]
    public void 其他來源的Warn照常計數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("LogForesight.Core.Service.SomeOtherService");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Warn("其他來源的警告訊息");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.WarnCount);

        var logs = store.GetLogs(runId);
        Assert.Single(logs);
        Assert.Equal("Warn", logs[0].Level);
    }

    [Fact]
    public void SqlPerformanceMonitor的Error照常計數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var logger = LogManager.GetLogger("LogForesight.Core.Persistence.Sql.SqlPerformanceMonitor");

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            logger.Error("資料庫連線失敗未預期例外");
            recorder.Finish(0);
        }

        var run = store.GetRun(runId);
        Assert.NotNull(run);
        Assert.Equal(0, run!.WarnCount);
        Assert.Equal(1, run!.ErrorCount);

        var logs = store.GetLogs(runId);
        Assert.Single(logs);
        Assert.Equal("SqlPerformanceMonitor", logs[0].Logger);
        Assert.Equal("Error", logs[0].Level);
    }

    private sealed class FixedContentHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]}",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task ChatJsonAsync最終失敗是Warn_進執行紀錄_ErrorCount不增()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var ai = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", Model = "m", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 1
        }, new FixedContentHandler("這不是 JSON"), NullAiRequestGate.Instance);

        long runId;
        AiJsonResult<AiAnalysisResult> result;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            result = await ai.ChatJsonAsync<AiAnalysisResult>("請分析", label: "daily-20261101-h1");
            recorder.Finish(0);
        }

        Assert.False(result.Success);
        var run = store.GetRun(runId)!;
        Assert.Equal(0, run.ErrorCount);
        var logs = store.GetLogs(runId);
        var final = Assert.Single(logs, l => l.Message.Contains("ChatJsonAsync 最終失敗"));
        Assert.Equal("Warn", final.Level);
        Assert.DoesNotContain(logs, l => l.Level is "Error" or "Fatal");
        // 每次嘗試的解析失敗 2 筆（AIServiceAttempt，第 70 輪 C-2 起不計）＋最終失敗 1 筆（照計）；診斷行不計
        Assert.Equal(1, run.WarnCount);
        Assert.Equal(2, logs.Count(l => l.Logger == AIService.AttemptLoggerName && l.Level == "Warn"));
    }

    [Fact]
    public async Task 略過憑證驗證的兩則提醒是Info_趟次不會因此變成有警告()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            // 不注入 handler：由 client 自己建 handler，各寫出一則「略過憑證驗證」的提醒
            using var prtg = new PrtgClient("https://prtg.example.com", "token", 30, ignoreSslErrors: true, handler: null,
                PrtgAuthModes.Token, "", "", "");
            var sentinelSettings = new NetiqOptions { AllowInvalidCertificates = true };
            await using var sentinel = new SentinelClient(
                new SentinelServer { Name = "SENTINEL-A", BaseUrl = "https://sentinel.local:8443", Username = "u", Password = "p" },
                sentinelSettings);
            recorder.Finish(0);
        }

        var run = store.GetRun(runId)!;
        Assert.Equal(0, run.WarnCount);
        Assert.Equal(0, run.ErrorCount);
        Assert.Empty(store.GetLogs(runId));
        Assert.Equal(BatchRunStatus.Success, BatchRunStatus.Compute(run, DateTime.Now, TimeSpan.FromHours(6)));
    }
}
