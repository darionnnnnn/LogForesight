using Microsoft.Extensions.Hosting;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 一次性背景服務外殼的行為：經閘門的四支在取數排程執行中時不開工、結束後才開工；
/// 六支在實際工作擲例外時，ExecuteAsync 的 Task 都不能以例外結束（否則 BackgroundService 預設會停掉整站）。
/// 實際工作用替身（呼叫次數＋擲例外）取代，只驗外殼，不驗各回填器本身。
/// </summary>
public class OneShotHostedServiceShellTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(30);

    private static BackgroundService Create(string name, BackgroundWorkGate gate, DataVersionStamp dataVersion, Action work)
    {
        switch (name)
        {
            case "ReportFileMigration":
                return new ReportFileMigrationHostedService(_ => { work(); throw new InvalidOperationException("測試用：報告檔遷移擲例外"); }, gate)
                { InitialDelay = TimeSpan.Zero };
            case "TopIssueBackfill":
                return new TopIssueBackfillHostedService(() => false, _ => { work(); throw new InvalidOperationException("測試用：問題聚合回填擲例外"); }, dataVersion, gate)
                { InitialDelay = TimeSpan.Zero, RetryInterval = TimeSpan.Zero };
            case "WorkOrderBackfill":
                return new WorkOrderBackfillHostedService(
                    new WorkOrderBackfiller(new EfWorkOrderStore(() => { work(); throw new InvalidOperationException("測試用：交辦單回填擲例外"); })),
                    dataVersion, gate)
                { InitialDelay = TimeSpan.Zero };
            case "DailyRecordBackfill":
                return new DailyRecordBackfillHostedService(
                    new DailyRecordBackfiller(() => { work(); throw new InvalidOperationException("測試用：每日紀錄回填擲例外"); }),
                    dataVersion, gate)
                { InitialDelay = TimeSpan.Zero };
            case "HandlingMigration":
                return new HandlingMigrationHostedService(_ => { work(); throw new InvalidOperationException("測試用：處理狀態遷移擲例外"); }, dataVersion)
                { InitialDelay = TimeSpan.Zero };
            case "PermissionChangeMigration":
                return new PermissionChangeMigrationHostedService(_ => { work(); throw new InvalidOperationException("測試用：權限異動遷移擲例外"); }, dataVersion)
                { InitialDelay = TimeSpan.Zero };
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, null);
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(Poll);
        }
        return condition();
    }

    public static IEnumerable<object[]> GatedServices() => new[]
    {
        new object[] { "ReportFileMigration" },
        new object[] { "TopIssueBackfill" },
        new object[] { "WorkOrderBackfill" },
        new object[] { "DailyRecordBackfill" },
    };

    public static IEnumerable<object[]> AllServices() => GatedServices().Concat(new[]
    {
        new object[] { "HandlingMigration" },
        new object[] { "PermissionChangeMigration" },
    });

    [Theory]
    [MemberData(nameof(GatedServices))]
    public async Task 經閘門的服務_取數排程執行中不開工_結束後才開工(string name)
    {
        var runState = new SchedulerRunState();
        var gate = new BackgroundWorkGate(runState, Poll);
        var calls = 0;
        var service = Create(name, gate, new DataVersionStamp(), () => Interlocked.Increment(ref calls));

        Assert.True(runState.TryBeginRun("test", out _));
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // 排程執行中：給服務足夠時間越過啟動延遲與數個輪詢週期，工作仍不能開始
            await Task.Delay(400);
            Assert.Equal(0, Volatile.Read(ref calls));

            runState.EndRun();

            var started = await WaitUntilAsync(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(10));
            Assert.True(started, "排程結束後工作沒有開始");
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task 工作擲例外時_ExecuteAsync不以例外結束(string name)
    {
        var gate = new BackgroundWorkGate(new SchedulerRunState(), Poll);
        var calls = 0;
        var service = Create(name, gate, new DataVersionStamp(), () => Interlocked.Increment(ref calls));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            var task = service.ExecuteTask;
            Assert.NotNull(task);
            var finished = await Task.WhenAny(task!, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(task, finished);
            Assert.Equal(TaskStatus.RanToCompletion, task!.Status);
            Assert.True(Volatile.Read(ref calls) >= 1, "替身工作沒有被呼叫，測試沒有走到擲例外的路徑");
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }
}
