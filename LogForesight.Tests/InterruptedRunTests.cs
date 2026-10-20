using System.Text.Json;
using LogForesight.Core.Configuration;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 任務 55-A1：中斷執行的偵測、關閉、反序列化相容性與排程互斥測試。
/// </summary>
public class InterruptedRunTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public InterruptedRunTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .Options);

    private EfJsonLogStore Store(string key) => new(NewContext, key);

    private BatchRunStore NewBatchRunStore() => new(Store("batch_runs"), Store("batch_run_logs"));

    [Fact]
    public void CloseUnfinishedRuns_關閉未結束執行_更新狀態與診斷紀錄()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(new BatchRun
        {
            HostName = "SRV-01",
            StartedAt = DateTime.Now.AddHours(-1)
        });

        var now = DateTime.Now;
        var closedCount = store.CloseUnfinishedRuns(now, 7, startedBefore: now);

        Assert.Equal(1, closedCount);

        var recent = store.GetRecentRuns(7, null);
        var run = recent.Single(r => r.RunId == runId);
        Assert.True(run.Interrupted);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(BatchRun.ExitCodeInterrupted, run.ExitCode);

        var logs = store.GetLogs(runId);
        Assert.Contains(logs, l =>
            l.Level == "Info" &&
            l.Logger == "Milestone" &&
            l.Message == "站台重啟時發現此執行未正常結束，標記為異常中斷");
    }

    [Fact]
    public void BatchRunStatusCompute_中斷執行_回傳stuck()
    {
        var run = new BatchRun
        {
            StartedAt = DateTime.Now.AddHours(-1),
            FinishedAt = DateTime.Now,
            Interrupted = true,
            ExitCode = BatchRun.ExitCodeInterrupted
        };

        var status = BatchRunStatus.Compute(run, DateTime.Now, TimeSpan.FromHours(2));

        Assert.Equal(BatchRunStatus.Stuck, status);
        Assert.Equal("stuck", status);
    }

    [Fact]
    public void CloseUnfinishedRuns_第二次呼叫_回傳0()
    {
        var store = NewBatchRunStore();
        store.StartRun(new BatchRun
        {
            HostName = "SRV-01",
            StartedAt = DateTime.Now.AddHours(-1)
        });

        var now = DateTime.Now;
        var first = store.CloseUnfinishedRuns(now, 7, startedBefore: now);
        Assert.Equal(1, first);

        var second = store.CloseUnfinishedRuns(now, 7, startedBefore: now);
        Assert.Equal(0, second);
    }

    [Fact]
    public void CloseUnfinishedRuns_本行程啟動後才開始的執行_不關閉()
    {
        // AI 排程與啟動程序同在開站 10 秒後開跑：它的紀錄 FinishedAt 也是空的，但它活著
        var store = NewBatchRunStore();
        var processStartedAt = DateTime.Now.AddMinutes(-5);
        var oldRunId = store.StartRun(new BatchRun { HostName = "SRV-01", StartedAt = processStartedAt.AddHours(-2) });
        var liveRunId = store.StartRun(new BatchRun { HostName = "SRV-01", StartedAt = processStartedAt.AddSeconds(10), JobType = BatchRun.JobTypeAi });

        var closed = store.CloseUnfinishedRuns(DateTime.Now, 7, startedBefore: processStartedAt);

        Assert.Equal(1, closed);
        var runs = store.GetRecentRuns(7, null);
        Assert.True(runs.Single(r => r.RunId == oldRunId).Interrupted);
        var live = runs.Single(r => r.RunId == liveRunId);
        Assert.False(live.Interrupted);
        Assert.Null(live.FinishedAt);
    }

    [Fact]
    public void 舊格式JSON反序列化_缺Interrupted欄位_預設為false()
    {
        var json = """{"RunId":100,"HostName":"SRV-01","StartedAt":"2026-10-20T10:00:00"}""";
        var run = JsonSerializer.Deserialize<BatchRun>(json, LfJsonOptions.Compact);

        Assert.NotNull(run);
        Assert.False(run.Interrupted);
    }

    [Fact]
    public async Task SchedulerHostedService_CloseInterruptedRuns_持有鎖時回null且不關閉_釋放後再呼叫回1()
    {
        var store = NewBatchRunStore();
        store.StartRun(new BatchRun
        {
            HostName = "SRV-01",
            StartedAt = DateTime.Now.AddHours(-1)
        });

        var mutexName = @"Local\LF-test-" + Guid.NewGuid();
        var gate = new NamedMutexGate(mutexName);

        using var holdingSignal = new ManualResetEventSlim(false);
        using var releaseSignal = new ManualResetEventSlim(false);

        var holderTask = Task.Run(() =>
        {
            using var mutex = new Mutex(true, mutexName, out var acquired);
            Assert.True(acquired);
            holdingSignal.Set();
            releaseSignal.Wait();
            mutex.ReleaseMutex();
        });

        holdingSignal.Wait();
        try
        {
            var lockedResult = SchedulerHostedService.CloseInterruptedRuns(gate, store, DateTime.Now, DateTime.Now);
            Assert.Null(lockedResult);

            var runs = store.GetRecentRuns(7, null);
            var run = runs.Single();
            Assert.Null(run.FinishedAt);
            Assert.False(run.Interrupted);
        }
        finally
        {
            releaseSignal.Set();
            await holderTask;
        }

        var unlockedResult = SchedulerHostedService.CloseInterruptedRuns(gate, store, DateTime.Now, DateTime.Now);
        Assert.Equal(1, unlockedResult);

        var runsAfter = store.GetRecentRuns(7, null);
        var closedRun = runsAfter.Single();
        Assert.NotNull(closedRun.FinishedAt);
        Assert.True(closedRun.Interrupted);
        Assert.Equal(BatchRun.ExitCodeInterrupted, closedRun.ExitCode);
    }
}
