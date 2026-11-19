using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 計畫階段「哪些主機要查某一天」（第 78 輪批次 2a，2-4）：
/// 預先建好的日期索引，每個日期的主機清單與順序必須和逐日 <c>Where(Contains)</c> 相同。
/// </summary>
public class NetiqPlanByDateTests
{
    private sealed record Plan(string Host, List<DateTime> Missing);

    private static readonly DateTime D0 = new(2026, 10, 10);

    private static void AssertSameAsWhereContains(List<Plan> plans)
    {
        var index = NetiqPipelineService.PlansByMissingDate(plans, p => p.Missing);
        var allDates = plans.SelectMany(p => p.Missing).Distinct().OrderBy(d => d).ToList();

        Assert.Equal(allDates, index.Keys.OrderBy(d => d).ToList());   // 沒有多餘或遺漏的日期
        foreach (var date in allDates)
            Assert.Equal(plans.Where(p => p.Missing.Contains(date)).Select(p => p.Host).ToList(),
                index[date].Select(p => p.Host).ToList());
    }

    [Fact]
    public void 各主機缺不同天_每日清單與順序相同()
    {
        AssertSameAsWhereContains(new List<Plan>
        {
            new("a", new() { D0, D0.AddDays(-1) }),
            new("b", new() { D0.AddDays(-1) }),
            new("c", new() { D0, D0.AddDays(-2) }),
            new("d", new() { D0.AddDays(-2), D0.AddDays(-1), D0 })
        });
    }

    [Fact]
    public void 回望七天_每台缺的天數不同()
    {
        var plans = Enumerable.Range(0, 12)
            .Select(i => new Plan($"h{i:00}", Enumerable.Range(1, 7).Where(d => (d + i) % 3 != 0).Select(d => D0.AddDays(-d)).ToList()))
            .ToList();
        AssertSameAsWhereContains(plans);
    }

    [Fact]
    public void 缺日清單內重複日期只進該日清單一次()
    {
        var plans = new List<Plan> { new("a", new() { D0, D0 }), new("b", new() { D0 }) };
        var index = NetiqPipelineService.PlansByMissingDate(plans, p => p.Missing);

        Assert.Equal(new[] { "a", "b" }, index[D0].Select(p => p.Host).ToArray());
        AssertSameAsWhereContains(plans);
    }

    [Fact]
    public void 沒有計畫或沒有缺日時索引為空()
    {
        Assert.Empty(NetiqPipelineService.PlansByMissingDate(new List<Plan>(), p => p.Missing));
        Assert.Empty(NetiqPipelineService.PlansByMissingDate(new List<Plan> { new("a", new()) }, p => p.Missing));
    }
}
