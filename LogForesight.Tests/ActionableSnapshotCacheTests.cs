using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// ActionableSnapshotCache（回饋三十六輪批次B）：TTL 語意、鍵組成與副本回傳。
/// 機制比照 IssueRankingCache，測試也比照其重點。
/// </summary>
public class ActionableSnapshotCacheTests
{
    private static List<ResolvedOccurrence> EmptySnapshot() => new();

    [Fact]
    public void TTL內命中_逾期後失效()
    {
        var now = new DateTime(2026, 9, 1, 10, 0, 0);
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), () => now);
        var key = ActionableSnapshotCache.KeyOf(0, 
            new DateTime(2026, 8, 25), new DateTime(2026, 8, 31), new long[] { 1, 2 }, null, null, IssueExclusion.None.CacheToken);

        cache.Set(key, EmptySnapshot());
        Assert.NotNull(cache.TryGet(key));

        now = now.AddSeconds(ActionableSnapshotCache.TtlSeconds - 1);
        Assert.NotNull(cache.TryGet(key));

        now = now.AddSeconds(2);
        Assert.Null(cache.TryGet(key));
    }

    [Fact]
    public void 鍵含主機集合與篩選條件_順序不影響同集合()
    {
        var from = new DateTime(2026, 8, 25);
        var to = new DateTime(2026, 8, 31);

        // 同一集合不同順序 → 同一鍵
        Assert.Equal(
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 2, 1 }, new[] { "高", "中" }, null, IssueExclusion.None.CacheToken),
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 1, 2 }, new[] { "中", "高" }, null, IssueExclusion.None.CacheToken));

        // 不同主機集合／不同風險等級 → 不同鍵（不得串味）
        Assert.NotEqual(
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 1 }, null, null, IssueExclusion.None.CacheToken),
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 1, 2 }, null, null, IssueExclusion.None.CacheToken));
        Assert.NotEqual(
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 1 }, new[] { "高" }, null, IssueExclusion.None.CacheToken),
            ActionableSnapshotCache.KeyOf(0, from, to, new long[] { 1 }, new[] { "高", "中" }, null, IssueExclusion.None.CacheToken));
    }

    [Fact]
    public void 命中回傳副本_呼叫端修改不影響快取()
    {
        var cache = new ActionableSnapshotCache(new DataVersionStamp(), () => new DateTime(2026, 9, 1));
        var key = ActionableSnapshotCache.KeyOf(0, new DateTime(2026, 8, 25), new DateTime(2026, 8, 31), null, null, null, IssueExclusion.None.CacheToken);

        cache.Set(key, EmptySnapshot());
        var first = cache.TryGet(key)!;
        first.Add(null!);

        var second = cache.TryGet(key)!;
        Assert.Empty(second);
    }

    [Fact]
    public void 可行動快照快取_版本推進後不命中_未推進則命中()
    {
        var stamp = new DataVersionStamp();
        var cache = new ActionableSnapshotCache(stamp, () => new DateTime(2026, 9, 1));
        var calls = 0;
        List<ResolvedOccurrence> GetOrAdd()
        {
            var key = ActionableSnapshotCache.KeyOf(cache.Version, new DateTime(2026, 8, 25), new DateTime(2026, 8, 31), null, null, null, IssueExclusion.None.CacheToken);
            var hit = cache.TryGet(key);
            if (hit != null) return hit;
            calls++;
            var v = EmptySnapshot();
            cache.Set(key, v);
            return v;
        }

        GetOrAdd();
        GetOrAdd();
        Assert.Equal(1, calls);

        stamp.Bump();
        GetOrAdd();
        Assert.Equal(2, calls);
    }
}
