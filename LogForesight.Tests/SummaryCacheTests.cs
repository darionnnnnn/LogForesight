using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 儀表板／報表整包快取（回饋三十五輪批次F）的契約。
///
/// 最重要的一條是**授權維度**：快取鍵含可見主機集合，兩個看得到不同主機的使用者
/// 用同一組參數查詢時絕不能共用同一筆快取——那是把 A 的資料端給 B。
/// </summary>
public class SummaryCacheTests
{
    private static SummaryCache Cache(DataVersionStamp stamp, Func<DateTime> now) => new(stamp, now);

    [Fact]
    public void 同鍵同版本_TTL內命中不重算()
    {
        var now = new DateTime(2026, 8, 28, 10, 0, 0);
        var cache = Cache(new DataVersionStamp(), () => now);
        var calls = 0;

        var key = SummaryCache.KeyOf("dashboard", "7", new long[] { 1, 2 });
        var a = cache.GetOrAdd(key, () => { calls++; return new List<string> { "x" }; });
        var b = cache.GetOrAdd(key, () => { calls++; return new List<string> { "x" }; });

        Assert.Equal(1, calls);
        Assert.Same(a, b);
    }

    [Fact]
    public void 版本戳推進後_不再命中舊結果()
    {
        var now = new DateTime(2026, 8, 28, 10, 0, 0);
        var stamp = new DataVersionStamp();
        var cache = Cache(stamp, () => now);
        var calls = 0;
        var key = SummaryCache.KeyOf("dashboard", "7", new long[] { 1 });

        cache.GetOrAdd(key, () => { calls++; return new List<string>(); });
        stamp.Bump();
        cache.GetOrAdd(key, () => { calls++; return new List<string>(); });

        Assert.Equal(2, calls);
    }

    [Fact]
    public void 超過TTL_重算()
    {
        var now = new DateTime(2026, 8, 28, 10, 0, 0);
        var cache = Cache(new DataVersionStamp(), () => now);
        var calls = 0;
        var key = SummaryCache.KeyOf("report", "20260801|20260828||", new long[] { 1 });

        cache.GetOrAdd(key, () => { calls++; return new List<string>(); });
        now = now.AddSeconds(SummaryCache.TtlSeconds + 1);
        cache.GetOrAdd(key, () => { calls++; return new List<string>(); });

        Assert.Equal(2, calls);
    }

    /// <summary>
    /// 越權紅線：可見主機集合不同＝不同鍵。漏掉這個維度就是把別人的資料端出去。
    /// </summary>
    [Fact]
    public void 可見主機集合不同_不得共用快取()
    {
        var now = new DateTime(2026, 8, 28, 10, 0, 0);
        var cache = Cache(new DataVersionStamp(), () => now);

        var userA = cache.GetOrAdd(
            SummaryCache.KeyOf("dashboard", "7", new long[] { 1, 2 }), () => new List<string> { "A 的資料" });
        var userB = cache.GetOrAdd(
            SummaryCache.KeyOf("dashboard", "7", new long[] { 3 }), () => new List<string> { "B 的資料" });

        Assert.Equal("A 的資料", userA[0]);
        Assert.Equal("B 的資料", userB[0]);
    }

    /// <summary>主機集合順序不同但內容相同＝同一鍵（排序後組鍵）。</summary>
    [Fact]
    public void 可見主機順序不同_視為同一鍵()
    {
        Assert.Equal(
            SummaryCache.KeyOf("dashboard", "7", new long[] { 2, 1, 3 }),
            SummaryCache.KeyOf("dashboard", "7", new long[] { 1, 3, 2 }));
    }

    /// <summary>查詢條件不同（天數／期間／處理範圍）＝不同鍵。</summary>
    [Fact]
    public void 查詢條件不同_不得共用快取()
    {
        Assert.NotEqual(
            SummaryCache.KeyOf("dashboard", "7", new long[] { 1 }),
            SummaryCache.KeyOf("dashboard", "30", new long[] { 1 }));
        Assert.NotEqual(
            SummaryCache.KeyOf("dashboard", "7", new long[] { 1 }),
            SummaryCache.KeyOf("report", "7", new long[] { 1 }));
    }
}
