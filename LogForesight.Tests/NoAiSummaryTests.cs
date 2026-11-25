using System.Text.RegularExpressions;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次 D（H8／O2）：沒有 AI 摘要時全站同一個出口——標題是當日最重的問題名稱與白話說明，不再是日期單獨一行、
/// 「（無 AI 摘要）」或「（統計模式紀錄，未呼叫 AI 分析）」；風險日詳情的標題與「狀況」不重複。
/// </summary>
public sealed class NoAiSummaryTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly RecordQueryServiceFacade _service;
    private readonly EfIssueAggregateQuery _aggregates;

    // 舊版統計模式寫進資料庫的佔位字（既有資料仍帶著它；測試資料用拼接避免在原始碼留下整句，見守門測試）
    private static readonly string LegacyPlaceholder = "（統計模式紀錄，" + "未呼叫 AI 分析）";

    public NoAiSummaryTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var visibility = new AlwaysVisibleService(_hosts);
        var severity = new FakeSystemSettingsService();
        var settings = new FakeSystemSettingsStore();
        var issueHandlings = new FakeIssueHandlingStore();
        var cases = new FakeIssueCaseStore();
        var repository = new RecordRepository(_recordStore, _hosts, visibility, severity);
        _aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        _service = new RecordQueryServiceFacade(
            repository, new NullReportReader(), _hosts, new FakeUserStore(), new FakeHostGroupStore(), visibility,
            new FakeHandlingStore(), issueHandlings, cases, new FakeNoiseMarkStore(), new FakeRuleStore(),
            FakeCurrentUser.WithCapabilities(), settings, _aggregates,
            new OccurrenceStatusResolver(_hosts, issueHandlings, cases, settings),
            new FakeIssueOwnerStore(), severity);
    }

    public void Dispose() { _fixture.Dispose(); GC.SuppressFinalize(this); }

    private static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity, int count,
        bool elevates = false, string? known = null, bool suppressed = false) => new()
        {
            LogName = "System", Source = source, EventId = eventId, Severity = severity, Count = count,
            ElevatesDayRisk = elevates, KnownIssue = known, Suppressed = suppressed, Category = IssueCategory.Other
        };

    private WebHost AddRecord(string name, DateTime date, string headline, string summary, params LogIssueSignature[] issues)
    {
        var host = TestData.AddHost(_hosts, name);
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = RiskLevels.High,
            Headline = headline, Summary = summary, TopIssues = issues.ToList()
        });
        return host;
    }

    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    // ── 判定：什麼算「沒有摘要」──────────────────────────────────

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("（統計模式紀錄，未呼叫 AI 分析）", true)]
    [InlineData("（統計已完成，AI 分析排隊中）", false)]   // 待補是另一種狀態（徽章表達），不在這裡改寫
    [InlineData("今日狀況正常，無需處理", false)]
    [InlineData("磁碟 I/O 錯誤，請檢查陣列", false)]
    [InlineData("（統計模式）", false)]                    // 只有一半特徵不算
    public void IsPlaceholder_空白與舊版佔位字才算沒有摘要(string? text, bool expected) =>
        Assert.Equal(expected, NoAiSummary.IsPlaceholder(text));

    // ── 標題：當日最重的問題名稱與白話說明 ───────────────────────

    [Fact]
    public void 標題是最重問題的名稱與白話說明()
    {
        var headline = NoAiSummary.HeadlineFor(new[]
        {
            Issue("app", 1, IssueSeverity.Low, count: 99),
            Issue("disk", 153, IssueSeverity.High, count: 3, known: "磁碟發生 I/O 錯誤"),
        });

        Assert.Equal("disk EventId 153：磁碟發生 I/O 錯誤", headline);
    }

    [Fact]
    public void 最重的判定_重大旗標優先於嚴重度_嚴重度優先於次數()
    {
        // 重大旗標 > 嚴重度
        Assert.StartsWith("elev EventId 2", NoAiSummary.HeadlineFor(new[]
        {
            Issue("high", 1, IssueSeverity.High, 50),
            Issue("elev", 2, IssueSeverity.Low, 1, elevates: true),
        }));
        // 嚴重度 > 次數
        Assert.StartsWith("high EventId 1", NoAiSummary.HeadlineFor(new[]
        {
            Issue("high", 1, IssueSeverity.High, 1),
            Issue("low", 2, IssueSeverity.Low, 500),
        }));
        // 同嚴重度時次數多者
        Assert.StartsWith("b EventId 2", NoAiSummary.HeadlineFor(new[]
        {
            Issue("a", 1, IssueSeverity.Medium, 3),
            Issue("b", 2, IssueSeverity.Medium, 4),
        }));
    }

    [Fact]
    public void 沒有已知說明時寫今日出現幾次()
    {
        Assert.Equal("svc EventId 7：今日出現 5 次",
            NoAiSummary.HeadlineFor(new[] { Issue("svc", 7, IssueSeverity.Medium, 5) }));
    }

    [Fact]
    public void 已抑制的問題不參與_全部被抑制時才使用()
    {
        Assert.StartsWith("shown EventId 2", NoAiSummary.HeadlineFor(new[]
        {
            Issue("muted", 1, IssueSeverity.High, 9, suppressed: true),
            Issue("shown", 2, IssueSeverity.Low, 1),
        }));
        Assert.StartsWith("muted EventId 1", NoAiSummary.HeadlineFor(new[]
        {
            Issue("muted", 1, IssueSeverity.High, 9, suppressed: true),
        }));
    }

    [Fact]
    public void 沒有任何問題時有固定標題_不是空白或日期()
    {
        var headline = NoAiSummary.HeadlineFor(Array.Empty<LogIssueSignature>());
        Assert.Equal(NoAiSummary.NoIssueHeadline, headline);
        Assert.False(NoAiSummary.IsPlaceholder(headline));
    }

    [Fact]
    public void 零問題但資料不完整_標題不寫未偵測到_有問題時不受影響()
    {
        var headline = NoAiSummary.HeadlineFor(Array.Empty<LogIssueSignature>(), coverageGap: true);

        Assert.Equal(NoAiSummary.NoIssueIncompleteHeadline, headline);
        Assert.Equal("資料不完整，尚未偵測到需要處理的問題", headline);
        Assert.DoesNotContain("今日未偵測到", headline);
        Assert.False(NoAiSummary.IsPlaceholder(headline));
        // 已存的是佔位字時，讀取端也依同一個判定改標題
        Assert.Equal(NoAiSummary.NoIssueIncompleteHeadline,
            NoAiSummary.ResolveHeadline("", Array.Empty<LogIssueSignature>(), coverageGap: true));
        // 有問題時標題仍是最重的問題，資料不完整不改它
        Assert.StartsWith("shown EventId 2", NoAiSummary.HeadlineFor(new[] { Issue("shown", 2, IssueSeverity.Low, 1) }, coverageGap: true));
    }

    [Fact]
    public void 標題過長截斷到上限()
    {
        var headline = NoAiSummary.HeadlineFor(new[]
        {
            Issue("svc", 7, IssueSeverity.High, 1, known: new string('長', 300))
        });
        Assert.True(headline.Length <= AiDegradation.MaxConclusionChars, headline.Length.ToString());
    }

    [Fact]
    public void 解析標題_有摘要原樣回傳_佔位字才改寫()
    {
        var issues = new[] { Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤") };
        Assert.Equal("AI 寫的標題", NoAiSummary.ResolveHeadline("AI 寫的標題", issues));
        Assert.Equal("disk EventId 153：磁碟錯誤", NoAiSummary.ResolveHeadline(LegacyPlaceholder, issues));
        Assert.Equal("disk EventId 153：磁碟錯誤", NoAiSummary.ResolveHeadline("", issues));
        Assert.Equal("", NoAiSummary.ResolveSummary(LegacyPlaceholder));
        Assert.Equal("今日無異常訊號", NoAiSummary.ResolveSummary("今日無異常訊號"));
    }

    // ── 三個畫面同一個出口（清單、依主機、詳情與時間軸、儀表板排行）──────

    [Fact]
    public void 清單列_舊紀錄的佔位字改顯示最重問題()
    {
        AddRecord("HOST-A", Day, LegacyPlaceholder, LegacyPlaceholder,
            Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤"));

        var item = Assert.Single(_service.Search(new RecordSearchRequest { Page = 1, PageSize = 50 }).Items);

        Assert.Equal("disk EventId 153：磁碟錯誤", item.Headline);
    }

    [Fact]
    public void 依主機視角_資料庫聚合也補上最重問題()
    {
        AddRecord("HOST-A", Day, LegacyPlaceholder, LegacyPlaceholder,
            Issue("low", 1, IssueSeverity.Low, 9), Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤"));
        AddRecord("HOST-B", Day, "", "", Array.Empty<LogIssueSignature>());
        AddRecord("HOST-C", Day, "AI 寫的標題", "AI 寫的說明", Issue("x", 1, IssueSeverity.High, 1));

        var rows = _aggregates.AggregateByHost(IssueExclusion.None, Day, Day, null)
            .ToDictionary(a => _hosts.GetAll().Single(h => h.HostId == a.HostId).HostName, a => a.LatestHeadline);

        Assert.Equal("disk EventId 153：磁碟錯誤", rows["HOST-A"]);
        Assert.Equal(NoAiSummary.NoIssueHeadline, rows["HOST-B"]);
        Assert.Equal("AI 寫的標題", rows["HOST-C"]);   // 邊界另一側：有 AI 標題不改
        // 依主機視角的 DTO 與聚合同一個值（不是日期單獨一行）
        var group = _service.SearchByHost(new RecordSearchRequest { Page = 1, PageSize = 50 }).Items;
        Assert.All(group, g => Assert.False(string.IsNullOrWhiteSpace(g.LatestHeadline)));
    }

    [Fact]
    public void 詳情_標題補最重問題_狀況不重複同一句()
    {
        var host = AddRecord("HOST-A", Day, LegacyPlaceholder, LegacyPlaceholder,
            Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤"));

        var detail = _service.GetDetail(host.HostId, Day);

        Assert.Equal("disk EventId 153：磁碟錯誤", detail.Headline);
        Assert.Equal("", detail.Summary);
        Assert.NotEqual(detail.Headline, detail.Summary);
    }

    [Fact]
    public void 詳情_有AI摘要時標題與狀況原樣不動()
    {
        var host = AddRecord("HOST-A", Day, "AI 標題", "AI 狀況說明", Issue("disk", 153, IssueSeverity.High, 3));

        var detail = _service.GetDetail(host.HostId, Day);

        Assert.Equal("AI 標題", detail.Headline);
        Assert.Equal("AI 狀況說明", detail.Summary);
    }

    [Fact]
    public void 主機詳情時間軸_補最重問題()
    {
        var host = AddRecord("HOST-A", Day, LegacyPlaceholder, LegacyPlaceholder,
            Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤"));

        var cell = _service.GetHostDetail(host.HostId, days: 7).Timeline.Single(t => t.HasRecord);

        Assert.Equal("disk EventId 153：磁碟錯誤", cell.Headline);
    }

    [Fact]
    public void 儀表板主機排行_記憶體聚合補最重問題()
    {
        var hostsByName = new Dictionary<string, WebHost>(StringComparer.OrdinalIgnoreCase);
        var record = new DailyAnalysisRecord
        {
            HostId = 1, Host = "HOST-A", Date = Day, RiskLevel = RiskLevels.High, Headline = LegacyPlaceholder,
            TopIssues = new List<LogIssueSignature> { Issue("disk", 153, IssueSeverity.High, 3, known: "磁碟錯誤") }
        };

        var host = Assert.Single(RecordStatsBuilder.BuildHostRanking(new List<DailyAnalysisRecord> { record }, hostsByName));

        Assert.Equal("disk EventId 153：磁碟錯誤", host.LatestHeadline);
    }

    // ── 寫入端：統計模式紀錄不再寫佔位字 ─────────────────────────

    [Fact]
    public async Task 統計模式紀錄寫入的標題是最重問題_摘要留空()
    {
        var history = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        var service = new LogAnalysisService(new EventLogService(), new FakeAiService(), history, new FakeSuppressionStore());
        var events = Enumerable.Range(0, 20).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(-(i % 20)), EntryType = LogEntryType.Error,
            LogName = "System", Source = "disk", EventId = 153, Message = $"磁碟發生 I/O 錯誤 #{i}"
        }).ToList();

        var record = await service.AnalyzeDayStatisticalAsync(Day, events, useAi: false);

        Assert.False(record.AiAnalyzed);
        Assert.False(NoAiSummary.IsPlaceholder(record.Headline));
        Assert.StartsWith("disk EventId 153", record.Headline);
        Assert.Equal("", record.Summary);
    }

    // ── 守門：三種舊寫法在正式碼零命中（含反例，確認守門會抓到）──────────

    private static readonly string[] OldTexts =
    {
        "（無 AI " + "摘要）",
        "（統計模式紀錄，" + "未呼叫 AI 分析）",
        "統計模式紀錄，" + "未呼叫"
    };

    /// <summary>「只剩日期」：把日期與標題用樣板字串直接黏成一個字串（標題空白時就只剩日期）。</summary>
    private static readonly Regex DateOnlyPattern = new(@"\$\{[^}]*[dD]ate[^}]*\}　\$\{[^}]*[hH]eadline[^}]*\}", RegexOptions.Compiled);

    internal static List<string> Violations(string text, string name)
    {
        var found = new List<string>();
        foreach (var old in OldTexts)
            if (text.Contains(old, StringComparison.Ordinal)) found.Add($"{name} 含舊寫法：{old}");
        if (DateOnlyPattern.IsMatch(text)) found.Add($"{name} 把日期與標題直接黏成一個字串（標題空白時只剩日期）");
        return found;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }

    private static IEnumerable<string> ProductionFiles(string root)
    {
        foreach (var project in new[] { "LogForesight.Core", "LogForesight.Web" })
        {
            var projectDir = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (ext is not (".cs" or ".js" or ".cshtml" or ".mjs")) continue;
                var relative = Path.GetRelativePath(projectDir, file);
                if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar)) continue;
                if (relative.Contains("node_modules")) continue;
                yield return file;
            }
        }
    }

    [Fact]
    public void 三種舊寫法在正式碼零命中()
    {
        var violations = ProductionFiles(FindRepoRoot())
            .SelectMany(f => Violations(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("text.textContent = record.headline || '" + "（無 AI " + "摘要）';")]
    [InlineData("record.Headline = \"" + "（統計模式紀錄，" + "未呼叫 AI 分析）\";")]
    [InlineData("render: h => latestStatusCell(h, `${h.latestDate}　${h.latestHeadline}`)")]
    public void 反例_守門會抓到舊寫法(string offending) =>
        Assert.NotEmpty(Violations(offending, "反例"));

    // ── SQL Server 翻譯：新增的批次查詢可翻成 SQL ─────────────────

    [Fact]
    public void 沒有摘要的問題子列查詢_SqlServer可翻譯為單一IN查詢()
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options;
        using var ctx = new LfDbContext(options);

        var sql = EfIssueAggregateQuery.NoSummaryIssueRows(ctx, new long[] { 1, 2, 3 }).ToQueryString();

        Assert.Contains("lf_top_issues", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("record_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?is)\bIN\b|OPENJSON", sql);
    }
}
