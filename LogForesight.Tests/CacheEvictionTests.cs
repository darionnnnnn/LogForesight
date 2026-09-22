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

    private static void RunScenario(Func<Func<DateTime>, CacheHarness> build)
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
        RunScenario(clock =>
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
        RunScenario(clock =>
        {
            var cache = new IssueRankingCache(clock);
            return new CacheHarness(
                i => cache.Set($"k{i}", new List<IssueRankingDto>()),
                i => cache.TryGet($"k{i}") != null);
        });
    }

    [Fact]
    public void ActionableSnapshotCache_滿了淘汰最久沒存取的一筆()
    {
        RunScenario(clock =>
        {
            var cache = new ActionableSnapshotCache(clock);
            return new CacheHarness(
                i => cache.Set($"k{i}", new List<ResolvedOccurrence>()),
                i => cache.TryGet($"k{i}") != null);
        });
    }

    [Fact]
    public void IssueOwnedHostIdsCache_滿了淘汰最久沒存取的一筆()
    {
        RunScenario(clock =>
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
        RunScenario(clock =>
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
}
