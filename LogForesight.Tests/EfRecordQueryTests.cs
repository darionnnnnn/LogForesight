using LogForesight;
using LogForesight.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// EF 後端的查詢面（IAnalysisRecordQuery.Query/GetOne）——Web 的熱路徑。
/// 驗證 DB 端預篩（日期/主機/風險）＋共用 RecordFilterMatcher（類別/事件）的組合結果，
/// 以及 HostMatcher 的 PK 優先與「空集合＝零結果」授權語意。
/// </summary>
public class EfRecordQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public EfRecordQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options);

    private EfAnalysisRecordStore Store() => new(NewContext, "test");

    public void Dispose() { _connection.Dispose(); GC.SuppressFinalize(this); }

    private static DailyAnalysisRecord Rec(long hostId, string host, DateTime date, string risk = "低",
        params LogIssueSignature[] issues) => new()
    {
        HostId = hostId, Host = host, Date = date, RiskLevel = risk, TopIssues = issues.ToList()
    };

    private static LogIssueSignature Issue(string source, int eventId, IssueCategory cat = IssueCategory.Other) =>
        new() { LogName = "System", Source = source, EventId = eventId, Category = cat, Count = 1 };

    [Fact]
    public void Query_日期區間篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", new DateTime(2026, 7, 10)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));

        var result = store.Query(new RecordQueryFilter
        {
            From = new DateTime(2026, 7, 15), To = new DateTime(2026, 7, 25)
        });

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 7, 20), result[0].Date.Date);
    }

    [Fact]
    public void Query_主機以PK優先比對()
    {
        var store = Store();
        store.Append(Rec(2, "舊名稱", DateTime.Today));   // 有 HostId → 只認 id
        store.Append(Rec(0, "SRV-B", DateTime.Today));    // HostId=0 → 認名稱

        // 找 HostId=2（名稱給錯的也該以 id 命中）＋名稱 SRV-B
        var result = store.Query(new RecordQueryFilter
        {
            Hosts = new[] { new HostKey { HostId = 2, HostName = "隨便" }, new HostKey { HostId = 0, HostName = "SRV-B" } }
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Query_空主機集合_零結果()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));

        // 空集合＝授權範圍為空 → 什麼都不該回（授權正確性關鍵）
        var result = store.Query(new RecordQueryFilter { Hosts = System.Array.Empty<HostKey>() });

        Assert.Empty(result);
    }

    [Fact]
    public void Query_類別與事件篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today, "高", Issue("disk", 153, IssueCategory.Storage)));
        store.Append(Rec(2, "B", DateTime.Today, "高", Issue("svc", 7031, IssueCategory.Service)));

        var byCat = store.Query(new RecordQueryFilter { Categories = new[] { IssueCategory.Storage } });
        Assert.Single(byCat);
        Assert.Equal("A", byCat[0].Host);

        var byEvent = store.Query(new RecordQueryFilter { EventId = 7031 });
        Assert.Single(byEvent);
        Assert.Equal("B", byEvent[0].Host);
    }

    [Fact]
    public void Query_風險層級篩選()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today, "高"));
        store.Append(Rec(2, "B", DateTime.Today, "低"));

        var result = store.Query(new RecordQueryFilter { RiskLevels = new[] { "高" } });
        Assert.Single(result);
        Assert.Equal("A", result[0].Host);
    }

    [Fact]
    public void GetOne_依主機順序擇一()
    {
        var store = Store();
        var date = DateTime.Today;
        store.Append(Rec(2, "存活", date, "高"));
        store.Append(Rec(3, "墓碑", date, "中"));

        // 傳入順序：存活主機（id=2）排前面 → 取到它
        var result = store.GetOne(new[] { new HostKey { HostId = 2, HostName = "存活" }, new HostKey { HostId = 3, HostName = "墓碑" } }, date);

        Assert.NotNull(result);
        Assert.Equal("存活", result!.Host);
    }

    [Fact]
    public void GetOne_空集合回null()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));
        Assert.Null(store.GetOne(System.Array.Empty<HostKey>(), DateTime.Today));
    }
}
