using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Configuration;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 59 輪 D3：排程輪詢先判斷窗口（優雅停止／觸發判斷）再寄信，寄信卡住不得拖延窗口 End 的停止；
/// 停機取消不得記成 Error。
/// </summary>
[Collection("NLogGlobalConfig")]
public class SchedulerTickOrderTests : IDisposable
{
    private readonly string _dir;
    private readonly StorageBackend _backend;

    public SchedulerTickOrderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-tick-order-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "t.db")}" }, _dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private (SchedulerHostedService Service, SchedulerRunState RunState, ScheduleOptionsStore Options) Create(
        bool enabled, ScheduleWindow[] windows,
        Func<DateTime, CancellationToken, Task> dailyMail, Func<DateTime, CancellationToken, Task>? workOrderMail = null)
    {
        var options = new ScheduleOptionsStore(_backend.Blob("schedule_options"));
        options.Update(o => { o.Enabled = enabled; o.Windows = windows.ToList(); });
        var runState = new SchedulerRunState();
        var service = new SchedulerHostedService(
            new WebAppSettings(), options, null!,
            new BatchRunStore(_backend.LogStore("batch_runs"), _backend.LogStore("batch_run_logs")),
            runState, null!, null!, null!,
            new NamedMutexGate(@"Local\LF-tick-order-" + Guid.NewGuid()),
            null!, new FakeLifetime(), new DataVersionStamp(),
            dailyMail, workOrderMail ?? ((_, _) => Task.CompletedTask))
        { StartupDelay = TimeSpan.FromMilliseconds(10) };
        return (service, runState, options);
    }

    /// <summary>一個保證不含「現在」的窗口（現在 +2h 到 +3h）</summary>
    private static ScheduleWindow[] WindowAwayFromNow()
    {
        var now = DateTime.Now;
        return new[] { new ScheduleWindow { Start = now.AddHours(2).ToString("HH:mm"), End = now.AddHours(3).ToString("HH:mm") } };
    }

    [Fact]
    public async Task Tick_寄信卡住時窗口外的排程執行仍先被優雅停止()
    {
        var (service, runState, _) = Create(true, WindowAwayFromNow(), (_, ct) => new TaskCompletionSource().Task);
        Assert.True(runState.TryBeginRun(BatchRunTriggers.Schedule, out var runCts));
        using var tickCts = new CancellationTokenSource();

        // 寄信卡住也不在輪詢路徑等待：Tick 短時間內結束，且窗口停止已發出
        await service.TickAsync(tickCts.Token).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(runCts.IsCancellationRequested, "窗口外的排程執行應已收到停止");
    }

    [Fact]
    public async Task Tick_上一次寄信未完成時下一輪跳過不重複啟動()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        var (service, _, _) = Create(false, WindowAwayFromNow(), (_, _) => { Interlocked.Increment(ref calls); return gate.Task; });

        await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls); // 卡住的那次還沒完成，第二輪不再啟動

        gate.SetResult();
        await Task.Delay(100); // 讓觀察用的包裝 Task 完成
        await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls); // 完成後下一輪才啟動新的一次
    }

    [Fact]
    public async Task Tick_寄信Task例外被觀察且不影響輪詢()
    {
        var calls = 0;
        var (service, _, _) = Create(false, WindowAwayFromNow(), (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException(new InvalidOperationException("寄信炸了"));
        });

        var target = new MemoryTarget("lf-tick-order-warn-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}|${exception:format=Message}" };
        var config = LogManager.Configuration ??= new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target, typeof(SchedulerHostedService).FullName);
        LogManager.ReconfigExistingLoggers();
        try
        {
            await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            // 例外已被包裝 Task 吞下並記 Log：Tick 不擲出，下一輪照樣能再啟動
            await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, calls);
            Assert.Contains(target.Logs, l => l.StartsWith("Warn|") && l.Contains("寄送發生例外") && l.Contains("寄信炸了"));
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.ReconfigExistingLoggers();
        }
    }

    [Theory]
    [InlineData(false)] // 排程停用 → 判斷段走 return
    [InlineData(true)]  // 手動執行佔住 gate → 判斷段走 return
    public async Task Tick_判斷段提前return時寄信仍各被呼叫一次(bool manualRunning)
    {
        var daily = 0;
        var workOrder = 0;
        var (service, runState, _) = Create(manualRunning, WindowAwayFromNow(),
            (_, _) => { Interlocked.Increment(ref daily); return Task.CompletedTask; },
            (_, _) => { Interlocked.Increment(ref workOrder); return Task.CompletedTask; });
        if (manualRunning) Assert.True(runState.TryBeginRun("manual", out _));

        await service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, daily);
        Assert.Equal(1, workOrder);
    }

    [Fact]
    public async Task ExecuteAsync_停機取消時正常結束且不記Error()
    {
        var entered = new TaskCompletionSource();
        var (service, _, _) = Create(false, WindowAwayFromNow(), async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // 寄信卡住，停機時以 OperationCanceledException 結束
        });

        var target = new MemoryTarget("lf-tick-order-capture-" + Guid.NewGuid().ToString("N")) { Layout = "${level}|${message}" };
        var oldConfig = LogManager.Configuration;
        var config = oldConfig ?? new LoggingConfiguration();
        config.AddTarget(target);
        config.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, target, typeof(SchedulerHostedService).FullName);
        LogManager.Configuration = config;
        try
        {
            await service.StartAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(target.Logs, l => l.Contains("排程輪詢發生未預期錯誤"));
            Assert.Empty(target.Logs);
        }
        finally
        {
            config.RemoveTarget(target.Name);
            config.LoggingRules.Where(r => r.Targets.Contains(target)).ToList().ForEach(r => config.LoggingRules.Remove(r));
            LogManager.Configuration = oldConfig == null ? null : config;
            LogManager.ReconfigExistingLoggers();
        }
    }
}
