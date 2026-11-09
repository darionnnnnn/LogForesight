using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 五個跨請求快取的逐筆淘汰：上限 256，滿了先清過期、再清最後存取時間最舊的一筆，不整批清空。
/// 共同情境：寫入 256 個不同鍵 → 讀一次第 1 個鍵（更新存取時間）→ 再寫第 257 個鍵 →
/// 第 1 個鍵仍命中、第 2 個鍵已被淘汰、第 257 個鍵命中。
/// 時鐘每次操作前進 1 毫秒（全程遠小於 TTL），讓存取時間有先後。
/// </summary>
public class CacheEvictionTests
{
    private const int Capacity = 256;

    /// <summary>以「寫入」與「是否命中」兩個動作描述一個快取，讓五個快取跑同一套情境。</summary>
    private sealed record CacheHarness(Action<int> Put, Func<int, bool> Hits);

    private static void AssertLruEvictionScenario(Func<Func<DateTime>, CacheHarness> build)
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);
        DateTime Clock() => now = now.AddMilliseconds(1);
        var cache = build(Clock);

        for (var i = 1; i <= Capacity; i++) cache.Put(i);
        Assert.True(cache.Hits(1));        // 讀一次第 1 個鍵，更新它的存取時間

        cache.Put(Capacity + 1);

        Assert.True(cache.Hits(1));
        Assert.False(cache.Hits(2));
        Assert.True(cache.Hits(Capacity + 1));
    }

    [Fact]
    public void SummaryCache_滿了淘汰最久沒存取的一筆()
    {
        AssertLruEvictionScenario(clock =>
        {
            var cache = new SummaryCache(new DataVersionStamp(), clock);
            return new CacheHarness(
                i => cache.GetOrAdd($"k{i}", () => new object()),
                i =>
                {
                    var hit = true;
                    cache.GetOrAdd($"k{i}", () => { hit = false; return new object(); });
                    return hit;
                });
        });
    }

    [Fact]
    public void IssueRankingCache_滿了淘汰最久沒存取的一筆()
    {
        AssertLruEvictionScenario(clock =>
        {
            var cache = new IssueRankingCache(new DataVersionStamp(), clock);
            return new CacheHarness(
                i => cache.Set($"k{i}", new List<IssueRankingDto>()),
                i => cache.TryGet($"k{i}") != null);
        });
    }

    [Fact]
    public void ActionableSnapshotCache_滿了淘汰最久沒存取的一筆()
    {
        AssertLruEvictionScenario(clock =>
        {
            var cache = new ActionableSnapshotCache(new DataVersionStamp(), clock);
            return new CacheHarness(
                i => cache.Set($"k{i}", new List<ResolvedOccurrence>()),
                i => cache.TryGet($"k{i}") != null);
        });
    }

    [Fact]
    public void IssueOwnedHostIdsCache_滿了淘汰最久沒存取的一筆()
    {
        AssertLruEvictionScenario(clock =>
        {
            var cache = new IssueOwnedHostIdsCache(new DataVersionStamp(), clock);
            return new CacheHarness(
                i => cache.GetOrAdd(i, 30, () => new HashSet<long>()),
                i =>
                {
                    var hit = true;
                    cache.GetOrAdd(i, 30, () => { hit = false; return new HashSet<long>(); });
                    return hit;
                });
        });
    }

    [Fact]
    public void NextUnhandledSequenceCache_滿了淘汰最久沒存取的一筆()
    {
        AssertLruEvictionScenario(clock =>
        {
            var cache = new NextUnhandledSequenceCache(new DataVersionStamp(), clock);
            return new CacheHarness(
                i => cache.GetOrAdd($"k{i}", () => new List<(long HostId, string Date)>()),
                i =>
                {
                    var hit = true;
                    cache.GetOrAdd($"k{i}", () => { hit = false; return new List<(long HostId, string Date)>(); });
                    return hit;
                });
        });
    }

    // ───────── 版本清除／元素總數上限／命中不複製（第 67 輪 D）─────────

    private static List<IssueRankingDto> Dtos(int n) =>
        Enumerable.Range(0, n).Select(i => new IssueRankingDto { Source = "s", EventId = i }).ToList();

    private static List<ResolvedOccurrence> Occs(int n) => Enumerable.Range(0, n).Select(_ => (ResolvedOccurrence)null!).ToList();

    private static Func<DateTime> TickingClock()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);
        return () => now = now.AddMilliseconds(1);
    }

    [Fact]
    public void IssueRankingCache_版本推進後寫新鍵_舊版整批清除()
    {
        var stamp = new DataVersionStamp();
        var cache = new IssueRankingCache(stamp, TickingClock());
        for (var i = 1; i <= 3; i++) cache.Set($"old{i}", Dtos(1));
        stamp.Bump();
        cache.Set("new", Dtos(1));

        var stats = cache.GetStatistics();
        Assert.Equal(1, stats.Entries);
        Assert.Equal(3, stats.Evictions);
        for (var i = 1; i <= 3; i++) Assert.Null(cache.TryGet($"old{i}"));
        Assert.NotNull(cache.TryGet("new"));
    }

    [Fact]
    public void ActionableSnapshotCache_版本推進後寫新鍵_舊版整批清除()
    {
        var stamp = new DataVersionStamp();
        var cache = new ActionableSnapshotCache(stamp, TickingClock());
        for (var i = 1; i <= 3; i++) cache.Set($"old{i}", Occs(1));
        stamp.Bump();
        cache.Set("new", Occs(1));

        var stats = cache.GetStatistics();
        Assert.Equal(1, stats.Entries);
        Assert.Equal(3, stats.Evictions);
        for (var i = 1; i <= 3; i++) Assert.Null(cache.TryGet($"old{i}"));
        Assert.NotNull(cache.TryGet("new"));
    }

    [Fact]
    public void IssueRankingCache_元素總數超限_淘汰最久沒存取的條目()
    {
        var cache = new IssueRankingCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        cache.Set("a", Dtos(3));
        cache.Set("b", Dtos(3));
        cache.Set("c", Dtos(3));
        Assert.NotNull(cache.TryGet("a"));   // a 變新，b 成為最久沒存取
        cache.Set("d", Dtos(3));

        var stats = cache.GetStatistics();
        Assert.Equal(3, stats.Entries);
        Assert.True(stats.Elements <= 10);
        Assert.Null(cache.TryGet("b"));
        Assert.NotNull(cache.TryGet("a"));
        Assert.NotNull(cache.TryGet("c"));
        Assert.NotNull(cache.TryGet("d"));
    }

    [Fact]
    public void ActionableSnapshotCache_元素總數超限_淘汰最久沒存取的條目()
    {
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        cache.Set("a", Occs(3));
        cache.Set("b", Occs(3));
        cache.Set("c", Occs(3));
        Assert.NotNull(cache.TryGet("a"));
        cache.Set("d", Occs(3));

        var stats = cache.GetStatistics();
        Assert.Equal(3, stats.Entries);
        Assert.True(stats.Elements <= 10);
        Assert.Null(cache.TryGet("b"));
        Assert.NotNull(cache.TryGet("a"));
        Assert.NotNull(cache.TryGet("c"));
        Assert.NotNull(cache.TryGet("d"));
    }

    [Fact]
    public void IssueRankingCache_單筆超過上限_不進快取並計入拒收()
    {
        var cache = new IssueRankingCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        cache.Set("big", Dtos(11));

        Assert.Null(cache.TryGet("big"));
        var stats = cache.GetStatistics();
        Assert.Equal(1, stats.OversizeRejected);
        Assert.Equal(0, stats.Entries);
    }

    [Fact]
    public void ActionableSnapshotCache_單筆超過上限_不進快取並計入拒收()
    {
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        cache.Set("big", Occs(11));

        Assert.Null(cache.TryGet("big"));
        var stats = cache.GetStatistics();
        Assert.Equal(1, stats.OversizeRejected);
        Assert.Equal(0, stats.Entries);
    }

    [Fact]
    public void 計算期間版本已前進_結果不寫入快取()
    {
        var stamp = new DataVersionStamp();
        var ranking = new IssueRankingCache(stamp, TickingClock());
        var actionable = new ActionableSnapshotCache(stamp, TickingClock());
        var keyVersion = stamp.Current;
        stamp.Bump();
        ranking.Set("k", Dtos(2), keyVersion);
        actionable.Set("k", Occs(2), keyVersion);
        Assert.Null(ranking.TryGet("k"));
        Assert.Null(actionable.TryGet("k"));
        Assert.Equal(0, ranking.GetStatistics().Entries);

        ranking.Set("k2", Dtos(2), stamp.Current);
        Assert.NotNull(ranking.TryGet("k2"));
    }

    [Fact]
    public void 單筆超過上限時同鍵舊條目一併移除()
    {
        var ranking = new IssueRankingCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        ranking.Set("k", Dtos(3));
        ranking.Set("k", Dtos(11));
        Assert.Null(ranking.TryGet("k"));
        Assert.Equal(0, ranking.GetStatistics().Elements);

        var actionable = new ActionableSnapshotCache(new DataVersionStamp(), TickingClock(), maxElements: 10);
        actionable.Set("k", Occs(3));
        actionable.Set("k", Occs(11));
        Assert.Null(actionable.TryGet("k"));
        Assert.Equal(0, actionable.GetStatistics().Elements);
    }

    [Fact]
    public void IssueRankingCache_命中不複製_同鍵回同一個參考()
    {
        var cache = new IssueRankingCache(new DataVersionStamp(), TickingClock());
        cache.Set("k", Dtos(2));
        Assert.Same(cache.TryGet("k"), cache.TryGet("k"));
    }

    [Fact]
    public void ActionableSnapshotCache_命中不複製_同鍵回同一個參考()
    {
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), TickingClock());
        cache.Set("k", Occs(2));
        Assert.Same(cache.TryGet("k"), cache.TryGet("k"));
    }

    [Fact]
    public void 統計的元素總數_隨寫入覆寫與移除同步()
    {
        var stamp = new DataVersionStamp();
        var r = new IssueRankingCache(stamp, TickingClock());
        r.Set("a", Dtos(2));
        r.Set("b", Dtos(5));
        Assert.Equal(7, r.GetStatistics().Elements);
        r.Set("a", Dtos(1));   // 覆寫同鍵：舊的 2 要扣掉
        Assert.Equal(6, r.GetStatistics().Elements);
        stamp.Bump();
        r.Set("c", Dtos(4));   // 舊版整批清除
        Assert.Equal(4, r.GetStatistics().Elements);

        var a = new ActionableSnapshotCache(new DataVersionStamp(), TickingClock());
        a.Set("a", Occs(2));
        a.Set("b", Occs(5));
        Assert.Equal(7, a.GetStatistics().Elements);
        a.Set("a", Occs(1));
        Assert.Equal(6, a.GetStatistics().Elements);
    }
}
