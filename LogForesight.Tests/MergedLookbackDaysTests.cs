using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 立即執行的兩個天數欄位合併為一個（回饋三十四輪 C）。
/// 「回望天數」找**沒有分析紀錄**的日子來補，「重新分析回望天數」找**已有紀錄**的日子依模式重跑——
/// 兩者是同一條時間軸上互斥的兩半，沒有任何模式需要不同的天數。
/// 這裡驗證同一個 N 餵給兩個尋找器時，聯集完整覆蓋窗口且不重疊。
/// </summary>
public sealed class MergedLookbackDaysTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeIssueHandlingStore _handlings = new();
    private const string HostName = "SRV-DC01";

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>在窗口內的某幾天寫入紀錄，其餘天留白</summary>
    private EfAnalysisRecordStore StoreWithRecordsOn(params int[] daysAgo)
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, HostName);
        foreach (var offset in daysAgo)
        {
            store.Append(new DailyAnalysisRecord
            {
                Date = DateTime.Today.AddDays(-offset),
                HostId = 1,
                Host = HostName,
                RiskLevel = "低",
                Headline = $"第 {offset} 天"
            });
        }
        return store;
    }

    [Theory]
    [InlineData(RerunMode.All)]
    [InlineData(RerunMode.Unhandled)]
    [InlineData(RerunMode.UnhandledAndAssigned)]
    public void 同一個回望天數下缺漏日與重跑日聯集覆蓋整個窗口且不重疊(RerunMode mode)
    {
        const int lookback = 7;
        var store = StoreWithRecordsOn(2, 4, 6);

        var missing = MissingDateFinder.Find(store, lookback);
        var rerun = RerunDateFinder.Find(store, _handlings, HostName, lookback, mode);

        // 互斥：一天要嘛有紀錄要嘛沒有，不可能同時落在兩邊
        Assert.Empty(missing.Intersect(rerun));

        // 完整覆蓋：窗口內每一天都被其中一邊挑到
        var window = Enumerable.Range(1, lookback).Select(o => DateTime.Today.AddDays(-o)).ToList();
        Assert.Equal(window.OrderBy(d => d), missing.Union(rerun).OrderBy(d => d));

        // 有紀錄的那幾天落在重跑側（此三種模式下都沒有處理紀錄，全部符合）
        Assert.Equal(new[] { 6, 4, 2 }.Select(o => DateTime.Today.AddDays(-o)).OrderBy(d => d), rerun.OrderBy(d => d));
    }

    [Fact]
    public void 只補跑模式下不重跑任何已有紀錄的日子()
    {
        var store = StoreWithRecordsOn(2, 4, 6);

        var rerun = RerunDateFinder.Find(store, _handlings, HostName, 7, RerunMode.None);

        Assert.Empty(rerun);
    }

    [Fact]
    public void 回望天數變大時兩側同步變大()
    {
        var store = StoreWithRecordsOn(2, 9);

        var missingShort = MissingDateFinder.Find(store, 7);
        var rerunShort = RerunDateFinder.Find(store, _handlings, HostName, 7, RerunMode.All);
        var missingLong = MissingDateFinder.Find(store, 14);
        var rerunLong = RerunDateFinder.Find(store, _handlings, HostName, 14, RerunMode.All);

        // 第 9 天在 7 天窗口外、14 天窗口內：兩側都必須跟著同一個天數走
        Assert.Single(rerunShort);
        Assert.Equal(2, rerunLong.Count);
        Assert.Equal(6, missingShort.Count);
        Assert.Equal(12, missingLong.Count);
    }
}
