using System.Text.RegularExpressions;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 背景服務改寫資料後的快取失效時機（CLAUDE.md：背景服務不走 HTTP 管線，快取版本戳要自己推進）。
/// 回填是分批寫入、每批一個交易，中途擲例外時前面的批次已落地，因此 Bump 必須在 finally，
/// 成功或失敗都推進一次；清理類的 Bump 則要排在清理之後，否則清理刪列後快取不會失效。
/// 行為測試用替身讓實際工作擲例外；無法替身的服務改掃原始碼結構。
/// </summary>
public class BackgroundServiceDataVersionTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string ReadService(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "LogForesight.Web", "Services", fileName));

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

    // ---------- 行為測試：回填擲例外時仍要 Bump 一次 ----------

    [Fact]
    public async Task WorkOrderBackfill_回填擲例外時仍推進資料版本()
    {
        // 替身：CountPending 第一步就擲例外，等同回填中途失敗
        var store = new EfWorkOrderStore(() => throw new InvalidOperationException("測試用：交辦單回填擲例外"));
        var backfiller = new WorkOrderBackfiller(store);
        var dataVersion = new DataVersionStamp();
        var gate = new BackgroundWorkGate(new SchedulerRunState(), Poll);
        var service = new WorkOrderBackfillHostedService(backfiller, dataVersion, gate);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // 服務內建 15 秒啟動延遲，等待時間取寬鬆值
            var bumped = await WaitUntilAsync(() => dataVersion.Current >= 1, TimeSpan.FromSeconds(40));
            Assert.True(bumped, "回填擲例外後資料版本未推進（快取不會失效）");
            Assert.Equal(1, dataVersion.Current);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DailyRecordBackfill_回填擲例外時仍推進資料版本()
    {
        // 替身：CountPending 第一步就擲例外，等同回填中途失敗
        var backfiller = new DailyRecordBackfiller(() => throw new InvalidOperationException("測試用：每日紀錄回填擲例外"));
        var dataVersion = new DataVersionStamp();
        var gate = new BackgroundWorkGate(new SchedulerRunState(), Poll);
        var service = new DailyRecordBackfillHostedService(backfiller, dataVersion, gate);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        try
        {
            // 服務內建 20 秒啟動延遲，等待時間取寬鬆值
            var bumped = await WaitUntilAsync(() => dataVersion.Current >= 1, TimeSpan.FromSeconds(45));
            Assert.True(bumped, "回填擲例外後資料版本未推進（快取不會失效）");
            Assert.Equal(1, dataVersion.Current);
        }
        finally
        {
            cts.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ---------- 原始碼結構：無法替身的服務 ----------

    [Fact]
    public void TopIssueBackfill_Bump位於finally()
    {
        var src = ReadService("TopIssueBackfillHostedService.cs");
        Assert.Single(Regex.Matches(src, @"_dataVersion\.Bump\(\)"));
        Assert.Matches(@"finally\s*\{(?:\s|//[^\r\n]*)*_dataVersion\.Bump\(\);", src);
    }

    [Fact]
    public void IssueFirstSeenSeed_Bump位於finally()
    {
        var src = ReadService("IssueFirstSeenSeedHostedService.cs");
        Assert.Single(Regex.Matches(src, @"_dataVersion\.Bump\(\)"));
        Assert.Matches(@"finally\s*\{(?:\s|//[^\r\n]*)*_dataVersion\.Bump\(\);", src);
    }

    [Fact]
    public void PermissionChangeMigration_Bump在舊彙總列清理之後且只一次()
    {
        var src = ReadService("PermissionChangeMigrationHostedService.cs");
        var bumps = Regex.Matches(src, @"_dataVersion\.Bump\(\)");
        Assert.Single(bumps);

        var cleanupCall = src.IndexOf("CleanupLegacySummaryRows();", StringComparison.Ordinal);
        Assert.True(cleanupCall >= 0, "找不到 CleanupLegacySummaryRows() 呼叫");
        Assert.True(bumps[0].Index > cleanupCall,
            "Bump 位於清理之前：清理刪列後快取不會失效");
    }
}
