using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 跨請求快取的記憶體行為（第 77 輪 M-5、M-6）：
/// 同鍵並發只算一次（結果大到不入快取時也一樣）；過期與舊版本條目在讀取時釋放，不等到快取滿。
/// 都以計數斷言，不靠耗時或堆大小。
/// </summary>
public sealed class CacheMemoryBoundTests
{
    private const int Threads = 8;

    /// <summary>8 條執行緒同時要同一個鍵；計算函式在柵欄放行前不回傳，確保它們真的同時在等。</summary>
    private static (int Calls, T[] Results) Concurrently<T>(Func<Func<T>, T> get, Func<T> produce) where T : class
    {
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var results = new T[Threads];
        var compute = () =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return produce();
        };
        var tasks = Enumerable.Range(0, Threads)
            .Select(i => Task.Factory.StartNew(() => results[i] = get(compute), TaskCreationOptions.LongRunning))
            .ToArray();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "計算函式沒有被呼叫");
        Thread.Sleep(200);   // 讓其餘執行緒都走到等待點；不影響判定（判定看呼叫次數）
        release.Set();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(20));
        return (calls, results);
    }

    [Fact]
    public void 單一合併元件_同鍵並發只算一次且拿到同一份()
    {
        var flight = new SingleFlight();
        var (calls, results) = Concurrently<object>(compute => flight.Run("k", compute), () => new object());

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(0, flight.InFlightCount);
    }

    [Fact]
    public void 單一合併元件_不同鍵各算各的()
    {
        var flight = new SingleFlight();
        var a = flight.Run("a", () => new object());
        var b = flight.Run("b", () => new object());
        Assert.NotSame(a, b);
    }

    [Fact]
    public void 單一合併元件_計算失敗時等待者都收到例外_下一次重新計算()
    {
        var flight = new SingleFlight();
        var (calls, _) = Concurrently<object>(compute =>
        {
            try { return flight.Run<object>("k", () => { compute(); throw new InvalidOperationException("boom"); }); }
            catch (InvalidOperationException) { return "failed"; }
        }, () => new object());

        Assert.Equal(1, calls);
        Assert.Equal(0, flight.InFlightCount);
        Assert.Equal("ok", flight.Run("k", () => "ok"));
    }

    [Fact]
    public void 儀表板快取_同鍵並發只算一次()
    {
        var cache = new SummaryCache(new DataVersionStamp());
        var (calls, results) = Concurrently<object>(compute => cache.GetOrAdd("dash|x", compute), () => new object());

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public void 問題排行快取_結果超過元素上限不入快取_並發仍只算一次()
    {
        var stamp = new DataVersionStamp();
        var cache = new IssueRankingCache(stamp, maxElements: 3);
        var big = Enumerable.Range(0, 5).Select(_ => new IssueRankingDto()).ToList();

        var (calls, results) = Concurrently<IReadOnlyList<IssueRankingDto>>(
            compute => cache.GetOrAdd("k", stamp.Current, () => compute().ToList()), () => big);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(0, cache.GetStatistics().Entries);
        Assert.Equal(1, cache.GetStatistics().OversizeRejected);
    }

    [Fact]
    public void 可行動快照快取_同鍵並發只算一次()
    {
        var stamp = new DataVersionStamp();
        var cache = new ActionableSnapshotCache(stamp);
        var (calls, _) = Concurrently<IReadOnlyList<ResolvedOccurrence>>(
            compute => cache.GetOrAdd("k", stamp.Current, () => compute().ToList()), () => new List<ResolvedOccurrence>());

        Assert.Equal(1, calls);
    }

    [Fact]
    public void 下一筆未處理快取與問題負責主機快取_同鍵並發只算一次()
    {
        var stamp = new DataVersionStamp();
        var next = new NextUnhandledSequenceCache(stamp);
        var (nextCalls, _) = Concurrently<List<(long HostId, string Date)>>(
            compute => next.GetOrAdd("k", compute), () => new List<(long, string)> { (1, "2026-11-01") });
        Assert.Equal(1, nextCalls);

        var owned = new IssueOwnedHostIdsCache(stamp);
        var (ownedCalls, _) = Concurrently<HashSet<long>>(
            compute => new HashSet<long>(owned.GetOrAdd(7, 180, compute)), () => new HashSet<long> { 1 });
        Assert.Equal(1, ownedCalls);
    }

    [Fact]
    public void 儀表板快取_過期條目在讀取時釋放_不必等快取滿()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0);
        var cache = new SummaryCache(new DataVersionStamp(), () => now);
        for (var i = 0; i < 100; i++) cache.GetOrAdd($"k{i}", () => new object());
        Assert.Equal(100, cache.GetStatistics().Entries);

        now = now.AddSeconds(SummaryCache.TtlSeconds * 10);
        cache.GetOrAdd("k0", () => new object());   // 一次讀取：掃掉其餘 99 筆過期條目

        Assert.Equal(1, cache.GetStatistics().Entries);
    }

    [Fact]
    public void 儀表板快取_版本推進後舊版本條目在讀取時釋放()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0);
        var stamp = new DataVersionStamp();
        var cache = new SummaryCache(stamp, () => now);
        var start = now;
        cache.GetOrAdd("warm", () => new object());        // 第一次讀取掃描一次，下一次掃描在一個 TTL 之後
        now = start.AddSeconds(SummaryCache.TtlSeconds / 2.0);
        for (var i = 0; i < 10; i++) cache.GetOrAdd($"k{i}", () => new object());

        stamp.Bump();
        now = start.AddSeconds(SummaryCache.TtlSeconds);   // 進入下一個掃描週期；這 10 筆才存半個 TTL，只因版本舊而失效
        cache.GetOrAdd("other", () => new object());

        Assert.Equal(1, cache.GetStatistics().Entries);
    }

    [Fact]
    public void 問題排行快取_過期條目在讀取時釋放()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0);
        var stamp = new DataVersionStamp();
        var cache = new IssueRankingCache(stamp, () => now);
        for (var i = 0; i < 50; i++) cache.Set($"k{i}", new List<IssueRankingDto> { new() }, stamp.Current);

        now = now.AddSeconds(IssueRankingCache.TtlSeconds * 10);
        Assert.Null(cache.TryGet("missing"));

        Assert.Equal(0, cache.GetStatistics().Entries);
        Assert.Equal(0, cache.GetStatistics().Elements);
    }

    [Fact]
    public void 下一筆未處理快取_過期條目在讀取時釋放()
    {
        var now = new DateTime(2026, 11, 18, 9, 0, 0);
        var cache = new NextUnhandledSequenceCache(new DataVersionStamp(), () => now);
        for (var i = 0; i < 30; i++) cache.GetOrAdd($"k{i}", () => new List<(long, string)> { (i, "d") });

        now = now.AddSeconds(NextUnhandledSequenceCache.TtlSeconds * 10);
        cache.GetOrAdd("k0", () => new List<(long, string)>());

        Assert.Equal(1, cache.GetStatistics().Entries);
    }

    /// <summary>六個跨請求快取的未命中路徑都經 SingleFlight：新增快取或改寫時漏接會在這裡紅。</summary>
    [Theory]
    [InlineData("SummaryCache.cs")]
    [InlineData("NextUnhandledSequenceCache.cs")]
    [InlineData("IssueOwnedHostIdsCache.cs")]
    [InlineData("PrtgDeviceIndexCache.cs")]
    [InlineData("IssueRankingCache.cs")]
    [InlineData("ActionableSnapshotCache.cs")]
    public void 跨請求快取都經同鍵合併(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "LogForesight.Web", "Services", file));
        Assert.Contains("_flight.Run", src);
    }
}
