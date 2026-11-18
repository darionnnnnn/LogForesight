using System.Data.Common;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests;

/// <summary>
/// 首見日（lf_issue_first_seen）寫入：一個主機日一道查詢＋合併 UPDATE＋批次 INSERT（第 78 輪批次 2a，2-1）。
/// 寫入結果與逐鍵 Any／UPDATE／INSERT 的舊作法完全相同；撞唯一鍵時逐筆降級、錯誤只影響各自的鍵。
/// </summary>
public sealed class FirstSeenBatchTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-firstseen-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public FirstSeenBatchTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _fx.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
    }

    private static readonly DateTime Today = new(2026, 10, 10);

    private static LogIssueSignature Issue(string source, int eventId) =>
        new() { LogName = "System", Source = source, EventId = eventId, Count = 1, Severity = IssueSeverity.Critical };

    private static DailyAnalysisRecord Rec(long hostId, DateTime date, params LogIssueSignature[] issues) => new()
    {
        Date = date, HostId = hostId, Host = "h" + hostId, RiskLevel = "高", Headline = "h", TopIssues = issues.ToList()
    };

    private static void Seed(Func<LfDbContext> factory, string source, int eventId, DateTime firstSeen)
    {
        using var ctx = factory();
        ctx.IssueFirstSeen.Add(new IssueFirstSeenRow
            { SourceKey = WorkOrderIssueKey.SourceKeyOf(source), EventId = eventId, SourceName = source, FirstSeen = firstSeen });
        ctx.SaveChanges();
    }

    private static Dictionary<(string, int), DateTime> Rows(Func<LfDbContext> factory)
    {
        using var ctx = factory();
        return ctx.IssueFirstSeen.AsNoTracking().ToList().ToDictionary(r => (r.SourceKey, r.EventId), r => r.FirstSeen);
    }

    [Fact]
    public void A1_全新_本日較早_本日較晚_混合五個問題_內容符合逐鍵語意()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        Seed(_fx.NewContext, "later", 2, Today.AddDays(3));    // 已存在且較晚 → 更新為本日
        Seed(_fx.NewContext, "later2", 3, Today.AddDays(1));   // 已存在且較晚 → 更新為本日
        Seed(_fx.NewContext, "earlier", 4, Today.AddDays(-5)); // 已存在且較早 → 不動
        Seed(_fx.NewContext, "same", 5, Today);                // 同日 → 不動

        store.Append(Rec(1, Today, Issue("fresh", 1), Issue("later", 2), Issue("later2", 3), Issue("earlier", 4), Issue("same", 5)));

        var rows = Rows(_fx.NewContext);
        Assert.Equal(5, rows.Count);   // 4 筆預先建立＋1 筆全新
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("fresh"), 1)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("later"), 2)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("later2"), 3)]);
        Assert.Equal(Today.AddDays(-5), rows[(WorkOrderIssueKey.SourceKeyOf("earlier"), 4)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("same"), 5)]);
    }

    [Fact]
    public void A1_同來源不同事件編號與重複問題各自獨立()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        Seed(_fx.NewContext, "src", 1, Today.AddDays(2));

        store.Append(Rec(1, Today, Issue("src", 1), Issue("src", 2), Issue("src", 2)));

        var rows = Rows(_fx.NewContext);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("src"), 1)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("src"), 2)]);
    }

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;

        private void Hit(DbCommand c)
        {
            if (c.CommandText.Contains("lf_issue_first_seen", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _count);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r)
        { Hit(c); return base.ReaderExecuting(c, e, r); }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r)
        { Hit(c); return base.NonQueryExecuting(c, e, r); }

        public override InterceptionResult<object> ScalarExecuting(DbCommand c, CommandEventData e, InterceptionResult<object> r)
        { Hit(c); return base.ScalarExecuting(c, e, r); }
    }

    private int CountFirstSeenCommands(int issueCount, bool preExistingLater)
    {
        var counter = new CountingInterceptor();
        using var fx = new EfSqliteFixture();
        var store = new EfAnalysisRecordStore(() => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(fx.NewContext().Database.GetDbConnection()).AddInterceptors(counter).Options), "sqlite-in-memory");
        var issues = Enumerable.Range(1, issueCount).Select(i => Issue("s" + i, i)).ToArray();
        if (preExistingLater) foreach (var i in Enumerable.Range(1, issueCount)) Seed(fx.NewContext, "s" + i, i, Today.AddDays(2));
        store.Append(Rec(1, Today, issues));
        return counter.Count;
    }

    [Fact]
    public void A2_往返數不隨問題數線性成長()
    {
        var newOne = CountFirstSeenCommands(1, preExistingLater: false);
        var newTen = CountFirstSeenCommands(10, preExistingLater: false);
        var oldOne = CountFirstSeenCommands(1, preExistingLater: true);
        var oldTen = CountFirstSeenCommands(10, preExistingLater: true);
        _output.WriteLine($"首見日指令數：全新 1 個={newOne}、全新 10 個={newTen}、既有較晚 1 個={oldOne}、既有較晚 10 個={oldTen}");

        Assert.True(newTen <= newOne + 1, $"全新問題 10 個 {newTen} 道、1 個 {newOne} 道");
        Assert.True(oldTen <= oldOne + 1, $"既有問題 10 個 {oldTen} 道、1 個 {oldOne} 道");
    }

    // ── 撞鍵降級（檔案型 SQLite：兩條連線各自獨立）──────────────────────────────

    private LfDbContext FileContext(string name, params IInterceptor[] interceptors)
    {
        var b = new DbContextOptionsBuilder<LfDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, name)}");
        if (interceptors.Length > 0) b.AddInterceptors(interceptors);
        return new LfDbContext(b.Options);
    }

    private Func<LfDbContext> FileDb(string name = "fs.db", params IInterceptor[] interceptors)
    {
        using (var ctx = FileContext(name)) ctx.Database.EnsureCreated();
        return () => FileContext(name, interceptors);
    }

    [Fact]
    public void A3_兩條執行緒同時第一次寫同一個新問題_都不擲例外且首見日為較早日()
    {
        var factory = FileDb();
        var storeA = new EfAnalysisRecordStore(factory, "sqlite-file");
        var storeB = new EfAnalysisRecordStore(factory, "sqlite-file");
        var early = Today.AddDays(-3);

        for (var round = 0; round < 30; round++)
        {
            var source = "race" + round;
            using var barrier = new Barrier(2);
            var tasks = new[]
            {
                Task.Run(() => { barrier.SignalAndWait(); storeA.Append(Rec(1, Today.AddDays(-round * 0), Issue(source, round + 1))); }),
                Task.Run(() => { barrier.SignalAndWait(); storeB.Append(Rec(2, early, Issue(source, round + 1))); })
            };
            Task.WaitAll(tasks);
        }

        var rows = Rows(factory);
        Assert.Equal(30, rows.Count);
        Assert.All(rows.Values, d => Assert.Equal(early, d));
    }

    /// <summary>
    /// 在「查已存在鍵」的讀取結束後、批次 INSERT 送出前，由另一條連線搶先寫入指定鍵，
    /// 製造確定的唯一鍵競態（只發動一次）。選讀取結束後才寫，是因為讀取中的游標會讓對方的寫入等鎖。
    /// </summary>
    private sealed class RaceInterceptor(Func<LfDbContext> rival, params (string Source, int EventId, DateTime Date)[] rivalRows) : DbCommandInterceptor
    {
        private int _fired;

        public override InterceptionResult DataReaderDisposing(DbCommand c, DataReaderDisposingEventData e, InterceptionResult r)
        {
            if (c.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                c.CommandText.Contains("lf_issue_first_seen", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _fired, 1) == 0)
            {
                foreach (var (source, eventId, date) in rivalRows) Seed(rival, source, eventId, date);
            }
            return base.DataReaderDisposing(c, e, r);
        }

        /// <summary>是否真的發動過（避免測試因攔截失敗而空轉）。</summary>
        public bool Fired => _fired == 1;
    }

    [Fact]
    public void A3_同批撞鍵_競爭者較晚_本日較早的更新_其餘鍵照常寫入()
    {
        var plain = FileDb("race1.db");
        var racer = new RaceInterceptor(plain, ("k2", 2, Today.AddDays(4)));
        var store = new EfAnalysisRecordStore(FileDb("race1.db", racer), "sqlite-file");

        store.Append(Rec(1, Today, Issue("k1", 1), Issue("k2", 2), Issue("k3", 3)));

        Assert.True(racer.Fired, "競態攔截沒有發動，測試空轉");
        var rows = Rows(plain);
        Assert.Equal(3, rows.Count);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("k1"), 1)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("k2"), 2)]);   // 撞鍵者被更新成較早日
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("k3"), 3)]);
    }

    [Fact]
    public void A3_同批撞鍵_競爭者較早_保留較早日且其餘鍵照常寫入()
    {
        var plain = FileDb("race2.db");
        var racer = new RaceInterceptor(plain, ("k2", 2, Today.AddDays(-4)));
        var store = new EfAnalysisRecordStore(FileDb("race2.db", racer), "sqlite-file");

        store.Append(Rec(1, Today, Issue("k1", 1), Issue("k2", 2), Issue("k3", 3)));

        Assert.True(racer.Fired, "競態攔截沒有發動，測試空轉");
        var rows = Rows(plain);
        Assert.Equal(3, rows.Count);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("k1"), 1)]);
        Assert.Equal(Today.AddDays(-4), rows[(WorkOrderIssueKey.SourceKeyOf("k2"), 2)]);
        Assert.Equal(Today, rows[(WorkOrderIssueKey.SourceKeyOf("k3"), 3)]);
    }
}
