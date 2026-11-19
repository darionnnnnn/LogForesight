using System.Collections.Frozen;
using LogForesight.Core.Persistence;
using LogForesight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 3b：排程選項開啟版本快取（3-6）、可見主機識別的鍵片段只組一次（3-7）。
/// 原則是不改運作邏輯：鍵字串逐字不變，只減少重複的讀取與字串組合。
/// </summary>
public class Batch3bScheduleOptionsCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sql = new();

    public Batch3bScheduleOptionsCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
        _sql.Clear();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .LogTo(line => { lock (_sql) _sql.Add(line); },
                new[] { Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted })
            .Options);

    private ScheduleOptionsStore NewStore() => new(new EfJsonBlobStore(NewContext, "schedule_options"));

    private int ContentReads()
    {
        lock (_sql) return _sql.Count(s => s.Contains("SELECT") && s.Contains("\"content\""));
    }

    [Fact]
    public void A1_另一個實例寫入後_下一次Get得到新值()
    {
        var reader = NewStore();
        var writer = NewStore();
        writer.Update(o => o.AiConcurrency = 2);
        Assert.Equal(2, reader.Get().AiConcurrency);

        writer.Update(o => o.AiConcurrency = 5);

        Assert.Equal(5, reader.Get().AiConcurrency);
    }

    [Fact]
    public void A1_連續兩次Get回傳不同物件實例()
    {
        var store = NewStore();
        store.Update(o => o.AiConcurrency = 2);
        store.Get();

        var first = store.Get();
        var second = store.Get();

        Assert.NotSame(first, second);
        first.AiConcurrency = 99;
        Assert.Equal(2, store.Get().AiConcurrency);
    }

    [Fact]
    public void A1_沒有寫入時第二次Get不整份讀取blob()
    {
        var store = NewStore();
        store.Update(o => o.AiConcurrency = 2);
        _sql.Clear();

        store.Get();
        store.Get();
        store.Get();

        Assert.Equal(1, ContentReads());
    }
}

public class Batch3bVisibleHostKeyTests
{
    // 改動前各 KeyOf 內嵌的寫法，逐字保留作比對基準
    private static string Legacy(IEnumerable<long> ids) => string.Join(",", ids.OrderBy(id => id));

    private static long[] Unsorted(int n) =>
        Enumerable.Range(1, n).Select(i => (long)((i * 7919L) % 100003 + 1)).Distinct().ToArray();

    public static IEnumerable<object[]> Inputs()
    {
        yield return new object[] { Array.Empty<long>() };
        yield return new object[] { new long[] { 3, 1, 2 } };
        yield return new object[] { new long[] { 42 } };
        yield return new object[] { Unsorted(3000) };
    }

    private static IReadOnlyCollection<long>[] Shapes(long[] ids) =>
        [ids.ToList(), ids, ids.ToHashSet(), new SortedSet<long>(ids)];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void A2_四個KeyOf輸出與改動前逐字相同(long[] ids)
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);
        var token = IssueExclusion.None.CacheToken;
        var hosts = Legacy(ids);

        foreach (var shape in Shapes(ids))
        {
            Assert.Equal($"report|p|{hosts}", SummaryCache.KeyOf("report", "p", shape));
            Assert.Equal($"v3|20260801|20260820|5|{token}|{hosts}",
                IssueRankingCache.KeyOf(3, from, to, shape, 5, token));
            Assert.Equal($"v3|20260801|20260820|中,高|*|{token}|{hosts}",
                ActionableSnapshotCache.KeyOf(3, from, to, shape, new[] { "高", "中" }, null, token));
            Assert.Equal($"*|{string.Join(",", new[] { "低", "高" }.OrderBy(x => x, StringComparer.Ordinal))}|{hosts}",
                NextUnhandledSequenceCache.KeyOf(shape, null, new[] { "低", "高" }));
        }
    }

    [Fact]
    public void A2_null主機集合維持星號()
    {
        var from = new DateTime(2026, 8, 1);
        var to = new DateTime(2026, 8, 20);
        var token = IssueExclusion.None.CacheToken;

        Assert.Equal($"v3|20260801|20260820|5|{token}|*", IssueRankingCache.KeyOf(3, from, to, null, 5, token));
        Assert.Equal($"v3|20260801|20260820|*|*|{token}|*", ActionableSnapshotCache.KeyOf(3, from, to, null, null, null, token));
    }

    // 以字串物件是否同一個判斷有沒有記憶（不用全域計數：其他測試類並行時計數會互相干擾）
    [Fact]
    public void A3_同一個FrozenSet只排序串接一次_四個快取共用同一份片段()
    {
        var set = Unsorted(3000).ToFrozenSet();

        var part = VisibleHostKeyPart.Of(set);
        Assert.Same(part, VisibleHostKeyPart.Of(set));
        Assert.Equal(Legacy(set), part);

        var a = SummaryCache.KeyOf("dashboard", "7", set);
        var b = IssueRankingCache.KeyOf(0, DateTime.Today, DateTime.Today, set, 1, "t");
        var c = NextUnhandledSequenceCache.KeyOf(set, null, null);
        Assert.Equal(a, SummaryCache.KeyOf("dashboard", "7", set));
        Assert.EndsWith(Legacy(set), b);
        Assert.EndsWith(Legacy(set), c);
    }

    [Fact]
    public void A4_HashSet雖實作IReadOnlySet仍不記憶_被修改後片段反映新內容()
    {
        var set = new HashSet<long> { 3, 1 };
        var first = VisibleHostKeyPart.Of(set);
        Assert.Equal("1,3", first);
        Assert.NotSame(first, VisibleHostKeyPart.Of(set));
        set.Add(2);
        Assert.Equal("1,2,3", VisibleHostKeyPart.Of(set));
    }

    [Fact]
    public void A3_內容不同的集合得到不同片段_內容相同物件不同得到相同片段()
    {
        var s1 = new long[] { 1, 2, 3 }.ToFrozenSet();
        var s2 = new long[] { 1, 2, 4 }.ToFrozenSet();
        var s3 = new long[] { 3, 2, 1 }.ToFrozenSet();

        Assert.NotEqual(VisibleHostKeyPart.Of(s1), VisibleHostKeyPart.Of(s2));
        Assert.Equal(VisibleHostKeyPart.Of(s1), VisibleHostKeyPart.Of(s3));
        Assert.Equal("1,2,3", VisibleHostKeyPart.Of(s1));
    }

    [Fact]
    public void A4_可變清單被修改後片段反映新內容()
    {
        var list = new List<long> { 3, 1 };

        Assert.Equal("1,3", VisibleHostKeyPart.Of(list));
        list.Add(2);
        Assert.Equal("1,2,3", VisibleHostKeyPart.Of(list));
        Assert.Equal("1,2,3", SummaryCache.KeyOf("x", "y", list).Split('|')[2]);
    }
}
