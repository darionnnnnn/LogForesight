using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordQuery"/> 的測試（docs/WEB-SPEC.md §12）。
/// 尤其是 <see cref="RecordQueryFilter.Hosts"/> 空集合的語意與「PK 優先、舊紀錄退回名稱」
/// 的比對規則，那是授權正確性的關鍵。
/// </summary>
public class AnalysisRecordQueryContractTests : IDisposable
{
    private readonly LogForesight.Sql.EfAnalysisRecordStore _store;
    private readonly EfSqliteFixture _fx = new();

    public AnalysisRecordQueryContractTests()
    {
        _store = new LogForesight.Sql.EfAnalysisRecordStore(_fx.NewContext, "test");
    }

    private IAnalysisRecordStore CreateStore() => _store;
    private IAnalysisRecordQuery Query => _store;

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    // 測試用的主機識別對應：Record 與 Key 共用同一份，測試才不必逐一傳 id 也能對得起來
    private static readonly Dictionary<string, long> HostIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HOST-A"] = 1,
        ["HOST-B"] = 2,
        ["HOST-X"] = 99
    };

    private static long IdOf(string hostName) => HostIds[hostName];

    private static HostKey Key(string hostName) =>
        new() { HostId = IdOf(hostName), HostName = hostName };

    /// <summary>現行紀錄：帶 HostId（關聯鍵）</summary>
    private static DailyAnalysisRecord Record(
        string host, DateTime date, string risk = "低",
        params LogIssueSignature[] issues) => new()
    {
        HostId = IdOf(host),
        Host = host,
        Date = date,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}",
        TopIssues = issues.ToList()
    };

    /// <summary>HostId 欄位問世前寫入的舊紀錄：只有名稱快照，沒有關聯鍵</summary>
    private static DailyAnalysisRecord LegacyRecord(string host, DateTime date, string risk = "低") => new()
    {
        HostId = 0,
        Host = host,
        Date = date,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}（舊紀錄）"
    };

    private static LogIssueSignature Issue(
        IssueCategory category = IssueCategory.Other,
        IssueSeverity severity = IssueSeverity.Low,
        int eventId = 1,
        string source = "test") => new()
    {
        Category = category,
        Severity = severity,
        EventId = eventId,
        Source = source,
        Count = 1
    };

    [Fact]
    public void Hosts為null_不限主機()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-B", DateTime.Today));

        Assert.Equal(2, Query.Query(new RecordQueryFilter()).Count);
    }

    /// <summary>
    /// **授權正確性的關鍵案例**：空集合代表「這個人沒有任何授權主機」，
    /// 必須回空結果。若實作把空集合當成「不限」，沒有任何授權的人反而看得到全部——
    /// 這是失敗方向最糟的一種錯誤。
    /// </summary>
    [Fact]
    public void Hosts為空集合_回傳空結果()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-B", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter { Hosts = Array.Empty<HostKey>() });

        Assert.Empty(result);
    }

    [Fact]
    public void Hosts指定_只回該主機()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-B", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter { Hosts = new[] { Key("HOST-A") } });

        Assert.Single(result);
        Assert.Equal("HOST-A", result[0].Host);
    }

    /// <summary>
    /// **PK 關聯的核心價值**：主機改名後，改名前寫入的紀錄仍歸戶正確。
    /// 名稱只是寫入當下的快照，比對走的是 HostId。
    /// </summary>
    [Fact]
    public void 主機改名_既有紀錄仍以HostId歸戶()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));

        var renamed = new HostKey { HostId = IdOf("HOST-A"), HostName = "改名後的主機" };

        Assert.Single(Query.Query(new RecordQueryFilter { Hosts = new[] { renamed } }));
    }

    /// <summary>
    /// 舊紀錄（HostId 未寫入）退回名稱比對——舊資料不遷移也查得到，
    /// 且名稱比對不分大小寫（沿用 HostId 問世前的既有語意）。
    /// </summary>
    [Fact]
    public void 舊紀錄無HostId_退回名稱比對且不分大小寫()
    {
        var store = CreateStore();
        store.Append(LegacyRecord("HOST-A", DateTime.Today));

        var lowerCaseKey = new HostKey { HostId = IdOf("HOST-A"), HostName = "host-a" };

        Assert.Single(Query.Query(new RecordQueryFilter { Hosts = new[] { lowerCaseKey } }));
    }

    /// <summary>
    /// **PK 優先的嚴格性**：紀錄有 HostId 就只認 HostId，不因名稱相同而放行。
    /// 若實作寫成「id 或名稱任一命中」，id 已經對不上的紀錄會從名稱溜回查詢範圍，
    /// 而查詢範圍正是授權範圍。
    /// </summary>
    [Fact]
    public void 紀錄有HostId_不因名稱相同而命中別台主機()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));

        var otherHostSameName = new HostKey { HostId = 12345, HostName = "HOST-A" };

        Assert.Empty(Query.Query(new RecordQueryFilter { Hosts = new[] { otherHostSameName } }));
    }

    /// <summary>
    /// 別名展開：一台主機併入另一台後有多個識別，任一命中即納入——
    /// 這正是「Merge 之後合併前的歷史不該消失」在儲存層的表現。
    /// </summary>
    [Fact]
    public void 多個識別_任一命中即納入()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1)));
        store.Append(Record("HOST-B", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter
        {
            Hosts = new[] { Key("HOST-B"), Key("HOST-A") }
        });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 日期區間過濾()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-10)));
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-5)));
        store.Append(Record("HOST-A", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter
        {
            From = DateTime.Today.AddDays(-6),
            To = DateTime.Today.AddDays(-1)
        });

        Assert.Single(result);
    }

    [Fact]
    public void 風險層級過濾()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1), "高"));
        store.Append(Record("HOST-A", DateTime.Today, "低"));

        var result = Query.Query(new RecordQueryFilter { RiskLevels = new[] { "高" } });

        Assert.Single(result);
        Assert.Equal("高", result[0].RiskLevel);
    }

    [Fact]
    public void 類別過濾_任一問題命中即算()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1), "中",
            Issue(IssueCategory.Storage), Issue(IssueCategory.Service)));
        store.Append(Record("HOST-A", DateTime.Today, "中", Issue(IssueCategory.Service)));

        var result = Query.Query(new RecordQueryFilter
        {
            Categories = new[] { IssueCategory.Storage }
        });

        Assert.Single(result);
    }

    [Fact]
    public void 嚴重度過濾_達門檻即算()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1), "高",
            Issue(severity: IssueSeverity.Critical)));
        store.Append(Record("HOST-A", DateTime.Today, "低", Issue(severity: IssueSeverity.Low)));

        var result = Query.Query(new RecordQueryFilter { MinSeverity = IssueSeverity.High });

        Assert.Single(result);
    }

    [Fact]
    public void EventId過濾()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1), "高", Issue(eventId: 153, source: "disk")));
        store.Append(Record("HOST-A", DateTime.Today, "低", Issue(eventId: 7031)));

        Assert.Single(Query.Query(new RecordQueryFilter { EventId = 153 }));
    }

    [Fact]
    public void 多條件_同時套用()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "高", Issue(IssueCategory.Storage)));
        store.Append(Record("HOST-B", DateTime.Today, "高", Issue(IssueCategory.Storage)));
        store.Append(Record("HOST-A", DateTime.Today, "低", Issue(IssueCategory.Service)));

        var result = Query.Query(new RecordQueryFilter
        {
            Hosts = new[] { Key("HOST-A") },
            RiskLevels = new[] { "高" },
            Categories = new[] { IssueCategory.Storage }
        });

        Assert.Single(result);
    }

    [Fact]
    public void 結果依日期新到舊()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-2)));
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1)));

        var result = Query.Query(new RecordQueryFilter());

        Assert.Equal(DateTime.Today, result[0].Date.Date);
        Assert.Equal(DateTime.Today.AddDays(-2), result[^1].Date.Date);
    }

    [Fact]
    public void GetOne_主機與日期為鍵()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "高"));
        store.Append(Record("HOST-B", DateTime.Today, "低"));

        var result = Query.GetOne(new[] { Key("HOST-A") }, DateTime.Today);

        Assert.NotNull(result);
        Assert.Equal("高", result!.RiskLevel);
    }

    [Fact]
    public void GetOne_查無資料回null()
    {
        CreateStore();
        Assert.Null(Query.GetOne(new[] { Key("HOST-X") }, DateTime.Today));
    }

    [Fact]
    public void GetOne_識別集合為空_回null()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));

        Assert.Null(Query.GetOne(Array.Empty<HostKey>(), DateTime.Today));
    }

    /// <summary>
    /// 合併當天兩個識別可能各有一筆紀錄。呼叫端把存活主機排在最前，
    /// 詳情頁呈現的就會是現行識別下的那筆，而不是墓碑那筆。
    /// </summary>
    [Fact]
    public void GetOne_多個識別_依傳入順序擇一()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "低"));
        store.Append(Record("HOST-B", DateTime.Today, "高"));

        var result = Query.GetOne(new[] { Key("HOST-B"), Key("HOST-A") }, DateTime.Today);

        Assert.Equal("高", result!.RiskLevel);
    }

    /// <summary>舊紀錄同樣要能以 GetOne 取得（詳情頁對舊資料不能開天窗）</summary>
    [Fact]
    public void GetOne_舊紀錄以名稱fallback取得()
    {
        var store = CreateStore();
        store.Append(LegacyRecord("HOST-A", DateTime.Today, "高"));

        var result = Query.GetOne(new[] { Key("HOST-A") }, DateTime.Today);

        Assert.Equal("高", result!.RiskLevel);
    }

    // ── QueryPage（docs/archive/HISTORY.md P1-2）─────────────────────────────

    private static DailyAnalysisRecord RecordWithCorrelation(string host, DateTime date, string risk, bool hasCorrelation) => new()
    {
        HostId = IdOf(host),
        Host = host,
        Date = date,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}",
        CorrelationAlerts = hasCorrelation ? new List<string> { "跨主機同時重啟" } : new List<string>()
    };

    [Fact]
    public void QueryPage_基本分頁_page與pageSize正確切分()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++)
            store.Append(Record("HOST-A", DateTime.Today.AddDays(-i)));

        var page1 = Query.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 2);
        var page2 = Query.QueryPage(new RecordQueryFilter(), page: 2, pageSize: 2);
        var page3 = Query.QueryPage(new RecordQueryFilter(), page: 3, pageSize: 2);

        Assert.Equal(5, page1.Total);
        Assert.Equal(5, page2.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Single(page3.Items);

        // 三頁合起來剛好是全部 5 天、彼此不重複
        var allDates = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(r => r.Date.Date).ToList();
        Assert.Equal(5, allDates.Distinct().Count());
    }

    [Fact]
    public void QueryPage_Total反映套用篩選條件後的總筆數()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "高"));
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-1), "低"));
        store.Append(Record("HOST-A", DateTime.Today.AddDays(-2), "高"));

        var result = Query.QueryPage(new RecordQueryFilter { RiskLevels = new[] { "高" } }, page: 1, pageSize: 50);

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Items.Count);
    }

    /// <summary>排序依「風險等級→有無關聯訊號→日期」三鍵新到舊——與清單頁排序同一套規則</summary>
    [Fact]
    public void QueryPage_排序依風險等級_關聯訊號_日期()
    {
        var store = CreateStore();
        // 刻意打亂寫入順序，逼排序邏輯真的生效而不是巧合地符合插入順序
        store.Append(RecordWithCorrelation("HOST-A", DateTime.Today.AddDays(-5), "低", hasCorrelation: false));
        store.Append(RecordWithCorrelation("HOST-A", DateTime.Today.AddDays(-1), "高", hasCorrelation: false));
        store.Append(RecordWithCorrelation("HOST-A", DateTime.Today.AddDays(-3), "高", hasCorrelation: true));
        store.Append(RecordWithCorrelation("HOST-A", DateTime.Today, "中", hasCorrelation: false));

        var result = Query.QueryPage(new RecordQueryFilter(), page: 1, pageSize: 50);

        // 高+有關聯 > 高+無關聯（日期較近者優先）> 中 > 低
        var order = result.Items.Select(r => r.Date.Date).ToList();
        Assert.Equal(new[]
        {
            DateTime.Today.AddDays(-3), // 高、有關聯訊號
            DateTime.Today.AddDays(-1), // 高、無關聯訊號
            DateTime.Today,             // 中
            DateTime.Today.AddDays(-5)  // 低
        }, order);
    }

    /// <summary>授權語意：空集合＝零結果，QueryPage 與 Query 必須一致，不能因為走分頁路徑就漏掉這條規則</summary>
    [Fact]
    public void QueryPage_Hosts為空集合_回傳空結果()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));

        var result = Query.QueryPage(new RecordQueryFilter { Hosts = Array.Empty<HostKey>() }, page: 1, pageSize: 50);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    /// <summary>
    /// 含 HostId=0 舊列時的退回路徑：結果集合（不只是筆數）必須與 Query() 的既有語意完全一致，
    /// 這是「正確性優先、退化的只有效能」這個設計承諾的驗收點。
    /// </summary>
    [Fact]
    public void QueryPage_含舊紀錄時_結果與Query語意一致()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "高"));
        store.Append(LegacyRecord("HOST-A", DateTime.Today.AddDays(-1), "中"));
        store.Append(Record("HOST-B", DateTime.Today.AddDays(-2), "低"));

        var filter = new RecordQueryFilter { Hosts = new[] { Key("HOST-A") } };
        var expected = Query.Query(filter);
        var paged = Query.QueryPage(filter, page: 1, pageSize: 50);

        Assert.Equal(expected.Count, paged.Total);
        Assert.Equal(
            expected.OrderBy(r => r.Date).Select(r => r.Headline),
            paged.Items.OrderBy(r => r.Date).Select(r => r.Headline));
    }

    /// <summary>跨頁完整遍歷 QueryPage 收集到的紀錄集合，需與一次性 Query() 的集合完全一致（不重複、不遺漏）</summary>
    [Fact]
    public void QueryPage_跨頁收集結果與Query一致()
    {
        var store = CreateStore();
        for (var i = 0; i < 7; i++)
            store.Append(Record("HOST-A", DateTime.Today.AddDays(-i), i % 2 == 0 ? "高" : "低"));

        var filter = new RecordQueryFilter();
        var expected = Query.Query(filter);

        var collected = new List<DailyAnalysisRecord>();
        for (var page = 1; ; page++)
        {
            var result = Query.QueryPage(filter, page, pageSize: 3);
            if (result.Items.Count == 0) break;
            collected.AddRange(result.Items);
            if (collected.Count >= result.Total) break;
        }

        Assert.Equal(expected.Count, collected.Count);
        Assert.Equal(
            expected.Select(r => r.Date.Date).OrderBy(d => d),
            collected.Select(r => r.Date.Date).OrderBy(d => d));
    }
}

/// <summary>
/// 報告全文讀取的路徑防護。報告參照來自歷史紀錄檔——那是資料而不是程式常數，
/// 若被竄改成 ..\..\Windows\System32\... 這類路徑，沒有防護就是任意檔案讀取。
/// </summary>
public class FileReportReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));

    public FileReportReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "export"));
        File.WriteAllText(Path.Combine(_dir, "export", "report.txt"), "報告內容");
    }

    [Fact]
    public void 讀取資料根目錄內的報告()
    {
        var reader = new FileReportReader(_dir);

        Assert.Equal("報告內容", reader.Read(Path.Combine(_dir, "export", "report.txt")));
    }

    [Fact]
    public void 相對路徑_以資料根目錄為基準()
    {
        var reader = new FileReportReader(_dir);

        Assert.Equal("報告內容", reader.Read(Path.Combine("export", "report.txt")));
    }

    [Fact]
    public void 路徑逃逸_拒絕讀取()
    {
        var reader = new FileReportReader(Path.Combine(_dir, "export"));

        // 試圖跳出 export 目錄讀取外部檔案
        var escaped = Path.Combine("..", "..", "..", "windows", "system32", "drivers", "etc", "hosts");

        Assert.Null(reader.Read(escaped));
    }

    /// <summary>
    /// 純字串前綴的陷阱：根目錄 C:\data 不該放行 C:\databad\x.txt——
    /// 前綴相符但不是子目錄。用「同名前綴的兄弟目錄」直接驗證這個邊界。
    /// </summary>
    [Fact]
    public void 同名前綴的兄弟目錄_拒絕讀取()
    {
        var siblingDir = _dir + "-evil";
        Directory.CreateDirectory(siblingDir);
        var outsideFile = Path.Combine(siblingDir, "secret.txt");
        File.WriteAllText(outsideFile, "不該讀得到");

        try
        {
            Assert.Null(new FileReportReader(_dir).Read(outsideFile));
        }
        finally
        {
            Directory.Delete(siblingDir, recursive: true);
        }
    }

    [Fact]
    public void 檔案不存在_回null不拋例外()
    {
        Assert.Null(new FileReportReader(_dir).Read(Path.Combine(_dir, "export", "missing.txt")));
    }

    [Fact]
    public void 空參照_回null()
    {
        Assert.Null(new FileReportReader(_dir).Read(""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
