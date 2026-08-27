using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>機房級基準線的純函式計算（回饋十九輪批次G1）。</summary>
public class IssueBaselineCalculatorTests
{
    private static IssueDailyHostCount Day(string source, int eventId, DateTime date, int hostCount) => new()
    {
        Source = source, EventId = eventId, Date = date, HostCount = hostCount
    };

    [Fact]
    public void 出現天數為零_視為無基準()
    {
        var result = IssueBaselineCalculator.Compute(new List<IssueDailyHostCount>());

        Assert.Empty(result);
    }

    [Fact]
    public void 出現天數不足三天_無基準()
    {
        var d0 = new DateTime(2026, 8, 1);
        var days = new List<IssueDailyHostCount> { Day("disk", 153, d0, 2), Day("disk", 153, d0.AddDays(1), 3) };

        var result = IssueBaselineCalculator.Compute(days);

        var baseline = result[("DISK", 153)];
        Assert.Equal(2, baseline.OccurrenceDays);
        Assert.Null(baseline.MedianHostCount);
        Assert.Null(baseline.LatestHostCount);
        Assert.Null(baseline.DeviationMultiplier);
    }

    [Fact]
    public void 恰好三天_奇數筆中位數取中間值()
    {
        var d0 = new DateTime(2026, 8, 1);
        var days = new List<IssueDailyHostCount>
        {
            Day("disk", 153, d0, 1), Day("disk", 153, d0.AddDays(1), 5), Day("disk", 153, d0.AddDays(2), 3)
        };

        var baseline = IssueBaselineCalculator.Compute(days)[("DISK", 153)];

        Assert.Equal(3, baseline.OccurrenceDays);
        Assert.Equal(3, baseline.MedianHostCount);       // 排序後 1,3,5 取中間
        Assert.Equal(3, baseline.LatestHostCount);        // 最近日＝d0+2，台數 3
        Assert.Equal(1.0, baseline.DeviationMultiplier);
    }

    [Fact]
    public void 偶數筆_中位數取中間兩筆平均()
    {
        var d0 = new DateTime(2026, 8, 1);
        var days = new List<IssueDailyHostCount>
        {
            Day("disk", 153, d0, 1), Day("disk", 153, d0.AddDays(1), 2),
            Day("disk", 153, d0.AddDays(2), 4), Day("disk", 153, d0.AddDays(3), 10)
        };

        var baseline = IssueBaselineCalculator.Compute(days)[("DISK", 153)];

        Assert.Equal(3.0, baseline.MedianHostCount);      // (2+4)/2
        Assert.Equal(10, baseline.LatestHostCount);
    }

    /// <summary>Source 鍵正規化為大寫——查詢端的 wanted-normalization 慣例，大小寫不同的呼叫端要查得到</summary>
    [Fact]
    public void Source鍵正規化為大寫()
    {
        var d0 = new DateTime(2026, 8, 1);
        var days = new List<IssueDailyHostCount>
        {
            Day("Disk", 153, d0, 1), Day("Disk", 153, d0.AddDays(1), 1), Day("Disk", 153, d0.AddDays(2), 1)
        };

        var result = IssueBaselineCalculator.Compute(days);

        Assert.True(result.ContainsKey(("DISK", 153)));
    }

    /// <summary>不同問題各自獨立分組計算，不會互相污染</summary>
    [Fact]
    public void 多個問題各自獨立計算()
    {
        var d0 = new DateTime(2026, 8, 1);
        var days = new List<IssueDailyHostCount>
        {
            Day("disk", 153, d0, 1), Day("disk", 153, d0.AddDays(1), 1), Day("disk", 153, d0.AddDays(2), 1),
            Day("DCOM", 10016, d0, 9)
        };

        var result = IssueBaselineCalculator.Compute(days);

        Assert.Equal(3, result[("DISK", 153)].OccurrenceDays);
        Assert.Equal(1, result[("DCOM", 10016)].OccurrenceDays);
        Assert.Null(result[("DCOM", 10016)].MedianHostCount);   // 只有一天，無基準
    }

    [Fact]
    public void Window以查詢期間終點往前推30天含終點當天()
    {
        var to = new DateTime(2026, 8, 10);

        var (from, resolvedTo) = IssueBaselineCalculator.Window(to);

        Assert.Equal(new DateTime(2026, 7, 12), from);   // 30 天含頭尾：8/10 往前 29 天
        Assert.Equal(to, resolvedTo);
    }
}
