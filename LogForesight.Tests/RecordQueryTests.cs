using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="IAnalysisRecordQuery"/> 的合約測試基底（docs/WEB-SPEC.md §12）。
/// JSONL 與未來的 SQL 實作跑同一組案例——尤其是 HostNames 空集合的語意，
/// 那是授權正確性的關鍵，兩個後端不容許有任何差異。
/// </summary>
public abstract class AnalysisRecordQueryContractTests : IDisposable
{
    protected abstract IAnalysisRecordStore CreateStore();
    protected abstract IAnalysisRecordQuery Query { get; }

    public virtual void Dispose() { }

    protected static DailyAnalysisRecord Record(
        string host, DateTime date, string risk = "低",
        params LogIssueSignature[] issues) => new()
    {
        Host = host,
        Date = date,
        RiskLevel = risk,
        Headline = $"{host} {date:MM-dd}",
        TopIssues = issues.ToList()
    };

    protected static LogIssueSignature Issue(
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
    public void HostNames為null_不限主機()
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
    public void HostNames為空集合_回傳空結果()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-B", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter { HostNames = Array.Empty<string>() });

        Assert.Empty(result);
    }

    [Fact]
    public void HostNames指定_只回該主機()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));
        store.Append(Record("HOST-B", DateTime.Today));

        var result = Query.Query(new RecordQueryFilter { HostNames = new[] { "HOST-A" } });

        Assert.Single(result);
        Assert.Equal("HOST-A", result[0].Host);
    }

    [Fact]
    public void HostNames比對不分大小寫()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today));

        Assert.Single(Query.Query(new RecordQueryFilter { HostNames = new[] { "host-a" } }));
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
            HostNames = new[] { "HOST-A" },
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
    public void GetOne_主機與日期為自然鍵()
    {
        var store = CreateStore();
        store.Append(Record("HOST-A", DateTime.Today, "高"));
        store.Append(Record("HOST-B", DateTime.Today, "低"));

        var result = Query.GetOne("HOST-A", DateTime.Today);

        Assert.NotNull(result);
        Assert.Equal("高", result!.RiskLevel);
    }

    [Fact]
    public void GetOne_查無資料回null()
    {
        CreateStore();
        Assert.Null(Query.GetOne("HOST-X", DateTime.Today));
    }
}

public class JsonlAnalysisRecordQueryTests : AnalysisRecordQueryContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-test-" + Guid.NewGuid().ToString("N"));
    private JsonlAnalysisRecordStore? _store;

    protected override IAnalysisRecordStore CreateStore()
    {
        _store = new JsonlAnalysisRecordStore(Path.Combine(_dir, "history.txt"));
        return _store;
    }

    protected override IAnalysisRecordQuery Query =>
        _store ?? (JsonlAnalysisRecordStore)CreateStore();

    public override void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
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
