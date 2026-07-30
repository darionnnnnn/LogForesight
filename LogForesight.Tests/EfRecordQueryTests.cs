using LogForesight;
using LogForesight.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// EF 後端的查詢面（IAnalysisRecordQuery.Query/GetOne）——Web 的熱路徑。
/// 驗證 DB 端預篩（日期/主機/風險）＋共用 RecordFilterMatcher（類別/事件）的組合結果，
/// 以及 HostMatcher 的 PK 優先與「空集合＝零結果」授權語意。
/// </summary>
public class EfRecordQueryTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private EfAnalysisRecordStore Store() => new(_fx.NewContext, "test");

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

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

    // ── QueryPage 表頭排序（docs/WEB-SPEC.md §9.2）───────────────────────────

    [Fact]
    public void QueryPage_依日期升冪排序()
    {
        var store = Store();
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 10)));
        store.Append(Rec(1, "A", new DateTime(2026, 7, 15)));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "date", ascending: true);

        Assert.Equal(new[] { 10, 15, 20 }, page.Items.Select(r => r.Date.Day));
    }

    [Fact]
    public void QueryPage_依主機名降冪排序()
    {
        var store = Store();
        store.Append(Rec(1, "A", DateTime.Today));
        store.Append(Rec(2, "C", DateTime.Today));
        store.Append(Rec(3, "B", DateTime.Today));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "host", ascending: false);

        Assert.Equal(new[] { "C", "B", "A" }, page.Items.Select(r => r.Host));
    }

    [Fact]
    public void QueryPage_不合法SortKey_退回預設緊急程度排序()
    {
        var store = Store();
        store.Append(Rec(1, "低", DateTime.Today, "低"));
        store.Append(Rec(2, "高", DateTime.Today, "高"));

        var page = store.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 10, sortKey: "not-a-real-key");

        Assert.Equal("高", page.Items[0].Host);
    }

    /// <summary>含 HostId=0 舊列時 QueryPage 退回記憶體排序路徑（見類別內 hasLegacyHostRows 分支），
    /// 表頭排序在這條路徑也必須維持正確——這是唯二兩條實作路徑的另一條，不能只顧 SQL 全下推那條。</summary>
    [Fact]
    public void QueryPage_含舊列退回記憶體路徑_排序仍正確()
    {
        var store = Store();
        store.Append(Rec(0, "舊列-無HostId", DateTime.Today));   // 觸發 hasLegacyHostRows
        store.Append(Rec(1, "A", new DateTime(2026, 7, 20)));
        store.Append(Rec(2, "B", new DateTime(2026, 7, 10)));

        var page = store.QueryPage(new RecordQueryFilter { Hosts = new[]
        {
            new HostKey { HostId = 1, HostName = "A" }, new HostKey { HostId = 2, HostName = "B" }
        } }, page: 1, pageSize: 10, sortKey: "date", ascending: true);

        Assert.Equal(new[] { "B", "A" }, page.Items.Select(r => r.Host));
    }
}
