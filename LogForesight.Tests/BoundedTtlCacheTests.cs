using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 跨請求快取的唯一實作 <see cref="BoundedTtlCache{T}"/>（H1）：以最小的子類別直接驗演算法，
/// 不經六個具體快取（它們的行為由各自的既有測試守）。邊界都測兩側。
/// </summary>
public sealed class BoundedTtlCacheTests
{
    private sealed class TestCache : BoundedTtlCache<List<int>>
    {
        public TestCache(long maxElements, DataVersionStamp? stamp = null, Func<DateTime>? now = null, int maxEntries = 256)
            : base("TestCache", 30, maxEntries, maxElements, l => l.Count, stamp == null ? null : () => stamp.Current, now)
        {
        }

        public List<int> Get(string key, Func<List<int>> compute) => GetOrCompute(key, CurrentVersion, compute);
        public List<int>? Peek(string key) => Lookup(key, version: null, countStats: false);
    }

    [Fact]
    public async Task 二十個並發請求同鍵未命中_只計算一次且拿到同一份()
    {
        var cache = new TestCache(maxElements: 1000);
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        List<int> Compute()
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return [1, 2, 3];
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Factory.StartNew(() => cache.Get("k", Compute), TaskCreationOptions.LongRunning))
            .ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(200);   // 讓其餘請求都走到等待點；判定看呼叫次數，不看耗時
        release.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task 結果超過元素上限不入快取_二十個並發仍只計算一次()
    {
        var cache = new TestCache(maxElements: 3);
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        List<int> Compute()
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return [1, 2, 3, 4];
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Factory.StartNew(() => cache.Get("k", Compute), TaskCreationOptions.LongRunning))
            .ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        release.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.Equal(0, cache.GetStatistics().Entries);
        Assert.Equal(1, cache.GetStatistics().OversizeRejected);
    }

    [Fact]
    public void 單筆剛好等於元素上限入快取_多一個就拒收()
    {
        var cache = new TestCache(maxElements: 3);
        cache.Get("fit", () => [1, 2, 3]);
        Assert.NotNull(cache.Peek("fit"));
        Assert.Equal(0, cache.GetStatistics().OversizeRejected);

        cache.Get("big", () => [1, 2, 3, 4]);
        Assert.Null(cache.Peek("big"));
        Assert.Equal(1, cache.GetStatistics().OversizeRejected);
    }

    [Fact]
    public void 存活時間邊界_差一毫秒仍命中_剛好到期未命中()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        var cache = new TestCache(maxElements: 1000, now: () => now);
        cache.Get("k", () => [1]);

        now = now.AddSeconds(30).AddMilliseconds(-1);
        Assert.NotNull(cache.Peek("k"));

        now = now.AddMilliseconds(1);
        Assert.Null(cache.Peek("k"));
    }

    [Fact]
    public void 條目數上限邊界_滿一格前不逐出_滿了逐出最久沒存取的一筆()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        var cache = new TestCache(maxElements: 1000, now: () => now = now.AddMilliseconds(1), maxEntries: 3);
        for (var i = 1; i <= 3; i++) cache.Get($"k{i}", () => [i]);
        Assert.Equal(0, cache.GetStatistics().Evictions);
        Assert.Equal(3, cache.GetStatistics().Entries);

        cache.Get("k4", () => [4]);

        Assert.Equal(1, cache.GetStatistics().Evictions);
        Assert.Null(cache.Peek("k1"));
        Assert.NotNull(cache.Peek("k4"));
    }

    [Fact]
    public void 版本戳推進後_舊版條目不再命中且寫新鍵時整批釋放()
    {
        var stamp = new DataVersionStamp();
        var cache = new TestCache(maxElements: 1000, stamp);
        cache.Get("a", () => [1]);
        cache.Get("b", () => [2]);
        stamp.Bump();

        var calls = 0;
        cache.Get("a", () => { calls++; return [3]; });
        Assert.Equal(1, calls);   // 同鍵但版本已推進：不命中舊版條目，重算

        cache.Get("c", () => [4]);   // 寫新鍵：舊版（b）整批釋放
        Assert.Null(cache.Peek("b"));
        Assert.Equal(2, cache.GetStatistics().Entries);   // 新版的 a 與 c
        Assert.Equal(1, cache.GetStatistics().Evictions);
    }

    [Fact]
    public void 計算期間版本已前進_結果不寫入但呼叫端照常拿到結果()
    {
        var stamp = new DataVersionStamp();
        var cache = new TestCache(maxElements: 1000, stamp);

        var result = cache.Get("k", () => { stamp.Bump(); return [7]; });

        Assert.Equal([7], result);
        Assert.Equal(0, cache.GetStatistics().Entries);
    }

    [Fact]
    public void 計算擲例外_不寫入快取_下一次重新計算()
    {
        var cache = new TestCache(maxElements: 1000);
        Assert.Throws<InvalidOperationException>(() => cache.Get("k", () => throw new InvalidOperationException("boom")));

        Assert.Equal([1], cache.Get("k", () => [1]));
    }
}
