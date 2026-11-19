using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>交易式單日取代（<see cref="IAnalysisRecordStore.ReplaceDay"/>）：刪舊日與寫新日同進退。檔案型 SQLite，避免共用記憶體連線。</summary>
public sealed class AnalysisRecordStoreReplaceDayTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 1);
    private const long HostId = 7001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-replaceday-" + Guid.NewGuid().ToString("N"));
    private readonly FailingInsertInterceptor _interceptor = new();
    private readonly Func<LfDbContext> _factory;
    private readonly HostKey _owner = new() { HostId = HostId, HostName = "REPLACE-HOST" };

    public AnalysisRecordStoreReplaceDayTests()
    {
        Directory.CreateDirectory(_dir);
        var cs = $"Data Source={Path.Combine(_dir, "replace.db")}";
        _factory = () => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(cs).AddInterceptors(_interceptor).Options);
        using var ctx = _factory();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private EfAnalysisRecordStore Store() => new(_factory, "sqlite-test", _owner);

    private static DailyAnalysisRecord Record(string headline, DateTime? date = null, int issues = 1) => new()
    {
        HostId = HostId, Host = "REPLACE-HOST", Date = date ?? Day, RiskLevel = "低", Headline = headline,
        TopIssues = Enumerable.Range(0, issues).Select(i => new LogIssueSignature
        {
            LogName = "System", Source = "fixture", EventId = 100 + i, EventKey = $"fixture:{headline}:{i}"
        }).ToList()
    };

    private (string Headline, int Issues)[] ReadDay(DateTime day)
    {
        using var ctx = _factory();
        return ctx.DailyRecords.AsNoTracking().Where(r => r.HostId == HostId && r.RecordDate == day)
            .Select(r => new { r.Headline, Issues = ctx.TopIssues.Count(t => t.RecordId == r.RecordId) })
            .AsEnumerable().Select(x => (x.Headline ?? "", x.Issues)).ToArray();
    }

    [Fact]
    public void 單日取代_正常時舊列與舊子列被新紀錄取代()
    {
        var store = Store();
        store.Append(Record("舊", issues: 2));
        store.Append(Record("另一天", Day.AddDays(1)));

        var replacement = Record("新", issues: 3);
        store.ReplaceDay(replacement);

        var rows = ReadDay(Day);
        Assert.Single(rows);
        Assert.Equal(("新", 3), rows[0]);
        Assert.True(replacement.RecordId > 0);
        Assert.Single(ReadDay(Day.AddDays(1)));
        using var ctx = _factory();
        Assert.Equal(3 + 1, ctx.TopIssues.Count()); // 新日 3 筆＋另一天 1 筆；舊日 2 筆已清掉
    }

    [Fact]
    public void 單日取代_該日沒有舊列時等同新增()
    {
        Store().ReplaceDay(Record("首次"));

        Assert.Equal([("首次", 1)], ReadDay(Day));
    }

    [Fact]
    public void 單日取代_寫入擲例外時舊資料仍在()
    {
        var store = Store();
        store.Append(Record("舊", issues: 2));
        _interceptor.FailOnTopIssueInsert = true;

        Assert.ThrowsAny<Exception>(() => store.ReplaceDay(Record("新", issues: 1)));

        _interceptor.FailOnTopIssueInsert = false;
        Assert.True(_interceptor.Triggered);
        Assert.Equal([("舊", 2)], ReadDay(Day));
    }

    [Fact]
    public void 單日取代_寫入失敗後可重新取代成功()
    {
        var store = Store();
        store.Append(Record("舊"));
        _interceptor.FailOnTopIssueInsert = true;
        Assert.ThrowsAny<Exception>(() => store.ReplaceDay(Record("新")));
        _interceptor.FailOnTopIssueInsert = false;

        store.ReplaceDay(Record("新"));

        Assert.Equal([("新", 1)], ReadDay(Day));
    }

    [Fact]
    public void 單日取代_不動其他主機同一天的紀錄()
    {
        var other = new DailyAnalysisRecord { HostId = 7002, Host = "OTHER", Date = Day, RiskLevel = "低", Headline = "別台" };
        new EfAnalysisRecordStore(_factory, "sqlite-test", new HostKey { HostId = 7002, HostName = "OTHER" }).Append(other);
        var store = Store();
        store.Append(Record("舊"));

        store.ReplaceDay(Record("新"));

        using var ctx = _factory();
        Assert.Equal("別台", ctx.DailyRecords.AsNoTracking().Single(r => r.HostId == 7002).Headline);
        Assert.Equal([("新", 1)], ReadDay(Day));
    }

    private sealed class FailingInsertInterceptor : SaveChangesInterceptor
    {
        public bool FailOnTopIssueInsert { get; set; }
        public bool Triggered { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (FailOnTopIssueInsert && eventData.Context!.ChangeTracker.Entries<TopIssueRow>().Any(e => e.State == EntityState.Added))
            {
                Triggered = true;
                throw new InvalidOperationException("模擬問題子列寫入失敗");
            }
            return base.SavingChanges(eventData, result);
        }
    }
}
