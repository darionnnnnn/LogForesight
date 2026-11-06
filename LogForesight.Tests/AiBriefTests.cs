using System.Reflection;
using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 66 輪 C：AI 契約新增「給主管的說明」（brief）。brief 是選填欄位——不進 validate、
/// 不合格只丟棄不重試不降級；欄位要一路流到落庫紀錄、報告與詳情 DTO。
/// </summary>
public class AiBriefPromptAndNormalizeTests
{
    [Fact]
    public void Prompt的JSON契約含brief鍵且排在headline之後()
    {
        var prompt = AnalysisPromptBuilder.BuildPrompt(
            DateTime.Today, new List<LogIssueSignature>(), errorCount: 1, warningCount: 0, auditCount: 0,
            history: new List<DailyAnalysisRecord>(), trendAlerts: new List<string>(),
            correlations: new List<CorrelationFinding>(),
            dataIncomplete: false, uncoveredChecks: new List<string>(), serverDescription: "測試主機");

        var headline = prompt.IndexOf("\"headline\":", StringComparison.Ordinal);
        var brief = prompt.IndexOf("\"brief\":", StringComparison.Ordinal);
        var story = prompt.IndexOf("\"story\":", StringComparison.Ordinal);
        Assert.True(headline >= 0 && brief > headline && story > brief, "brief 應在 headline 與 story 之間");
        Assert.Contains("給主管的說明", prompt);
    }

    [Fact]
    public void NormalizeBrief_超長且第120字處有句號_截到該句號()
    {
        var raw = new string('甲', 119) + "。" + new string('乙', 80);   // 200 字，第 120 字是「。」
        var result = LogAnalysisService.NormalizeBrief(raw, out var reason);
        Assert.Null(reason);
        Assert.Equal(120, result.Length);
        Assert.EndsWith("。", result);
    }

    [Fact]
    public void NormalizeBrief_含換行_壓成單一空白()
    {
        var result = LogAnalysisService.NormalizeBrief("第一句。\r\n\r\n  第二句。\n第三句。", out var reason);
        Assert.Null(reason);
        Assert.DoesNotContain("\n", result);
        Assert.DoesNotContain("\r", result);
        Assert.Equal("第一句。 第二句。 第三句。", result);
    }

    [Fact]
    public void NormalizeBrief_超長且只有半形句點_截到半形句點()
    {
        var raw = new string('a', 99) + ". " + new string('b', 100);
        var result = LogAnalysisService.NormalizeBrief(raw, out var reason);
        Assert.Null(reason);
        Assert.Equal(100, result.Length);
        Assert.EndsWith(".", result);
    }

    [Fact]
    public void NormalizeBrief_超長時版本號或小數的半形句點不算句尾()
    {
        // 「1.2.3」「3.5」裡的半形句點後面接數字，不是句尾；超長又沒有真正的句末就整段丟棄，不能截成「…1.」
        var raw = "代理程式版本 1.2.3 與 3.5 版不相容" + new string('字', 160);
        Assert.Equal(string.Empty, LogAnalysisService.NormalizeBrief(raw, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void NormalizeBrief_超長且無句末標點_丟棄()
    {
        var result = LogAnalysisService.NormalizeBrief(new string('甲', 200), out var reason);
        Assert.Equal(string.Empty, result);
        Assert.NotNull(reason);
    }

    [Fact]
    public void NormalizeBrief_超長時句末標點在150字之外_丟棄()
    {
        var raw = new string('甲', 160) + "。";
        Assert.Equal(string.Empty, LogAnalysisService.NormalizeBrief(raw, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("伺服器 10.216.19.43 磁碟異常。")]
    [InlineData("主機10.1.2.3磁碟異常。")]
    [InlineData("IP10.1.2.3。")]
    [InlineData("磁碟異常（Event ID 4625）。")]
    [InlineData("磁碟異常 eventid 7。")]
    [InlineData("磁碟異常 EventId 7。")]
    [InlineData("磁碟異常 事件ID 7。")]
    [InlineData("磁碟異常 事件 ID 7。")]
    [InlineData("磁碟異常，事件編號 7。")]
    public void NormalizeBrief_含IP或事件編號字樣_丟棄(string raw)
    {
        Assert.Equal(string.Empty, LogAnalysisService.NormalizeBrief(raw, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void NormalizeBrief_三段數字不是IP_保留()
    {
        Assert.Equal("版本 1.2.3 已更新。", LogAnalysisService.NormalizeBrief("版本 1.2.3 已更新。", out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void NormalizeBrief_禁用字樣只在被截掉的部分_保留截斷結果()
    {
        // 禁用字樣在被截掉的部分之內不算；在保留範圍內才算
        var kept = new string('甲', 20) + "。" + new string('乙', 140) + " 10.1.2.3";
        Assert.Equal(new string('甲', 20) + "。", LogAnalysisService.NormalizeBrief(kept, out var reasonKept));
        Assert.Null(reasonKept);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n ")]
    public void NormalizeBrief_空白_回空字串且不算丟棄(string? raw)
    {
        Assert.Equal(string.Empty, LogAnalysisService.NormalizeBrief(raw, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void NormalizeBrief_100字正常內容_原樣()
    {
        var raw = new string('甲', 99) + "。";
        var result = LogAnalysisService.NormalizeBrief("  " + raw + "  ", out var reason);
        Assert.Null(reason);
        Assert.Equal(raw, result);
    }
}

/// <summary>brief 經 <see cref="LogAnalysisService.RetryAiAsync"/> 流到 <see cref="AiOutcome"/>；不合格不拖垮判讀。</summary>
public class AiBriefRetryAiTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeAiService _ai = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private LogAnalysisService NewService() =>
        new(new EventLogService(), _ai, new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(_ai, new FakeReportSink()), riskyEventStore: null);

    private static DailyAnalysisRecord Pending() => new()
    {
        Date = new DateTime(2026, 7, 31), HostId = 1, Host = "HOST-A", RiskLevel = "中", AiPending = true,
        TopIssues = new List<LogIssueSignature>
        {
            new()
            {
                LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error,
                Severity = IssueSeverity.High, Category = IssueCategory.Storage, RuleId = "builtin-disk-error", Count = 3
            }
        },
        TrendAlerts = new List<string>(), CorrelationAlerts = new List<string>(), UncoveredChecks = new List<string>()
    };

    private static string Json(string briefPart) =>
        "{\"risk_level\":\"中\",\"headline\":\"標題\",\"story\":\"摘要\",\"trend_story\":\"\",\"action\":\"a\"" + briefPart + "}";

    [Fact]
    public async Task 合法brief_帶進outcome()
    {
        _ai.NextContent = Json(",\"brief\":\"一台主機磁碟出現錯誤，影響範圍有限，目前不需要主管決策。\"");
        var outcome = await NewService().RetryAiAsync(Pending(), historyDays: 14);
        Assert.Equal("一台主機磁碟出現錯誤，影響範圍有限，目前不需要主管決策。", outcome.Brief);
        Assert.True(outcome.AiAnalyzed);
        Assert.False(outcome.AiDegraded);
    }

    [Fact]
    public async Task 缺brief_仍成功且Brief空()
    {
        _ai.NextContent = Json("");
        var outcome = await NewService().RetryAiAsync(Pending(), historyDays: 14);
        Assert.Equal(string.Empty, outcome.Brief);
        Assert.True(outcome.AiAnalyzed);
        Assert.False(outcome.AiDegraded);
        Assert.Equal("標題", outcome.Headline);
    }

    [Fact]
    public async Task brief超長無標點_丟棄但不重試不降級()
    {
        _ai.NextContent = Json($",\"brief\":\"{new string('甲', 300)}\"");
        var outcome = await NewService().RetryAiAsync(Pending(), historyDays: 14);
        Assert.Equal(string.Empty, outcome.Brief);
        Assert.True(outcome.AiAnalyzed);
        Assert.False(outcome.AiDegraded);
        Assert.Equal(1, _ai.Calls);
    }

    [Fact]
    public async Task brief含IP_丟棄但整日判讀照常()
    {
        _ai.NextContent = Json(",\"brief\":\"主機 10.216.19.43 磁碟異常。\"");
        var outcome = await NewService().RetryAiAsync(Pending(), historyDays: 14);
        Assert.Equal(string.Empty, outcome.Brief);
        Assert.False(outcome.AiDegraded);
        Assert.Equal(1, _ai.Calls);
    }
}

/// <summary>brief 一路接到報告與歷史修訂（落庫與詳情 DTO 另見契約測試與 Web 測試）。</summary>
[Collection("KnownIssueCatalogState")]
public class AiBriefFlowTests
{
    [Theory]
    [InlineData("一台主機磁碟異常，需要主管支援採購備品。", true)]
    [InlineData("", false)]
    public async Task 報告白話總覽_Brief非空才印給主管的說明(string brief, bool expected)
    {
        var sink = new FakeReportSink();
        var ai = new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 }, NullAiRequestGate.Instance);
        var service = new RiskReportService(ai, sink);
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today, Host = "test-host", RiskLevel = "高", AiAnalyzed = true,
            Headline = "標題", Summary = "測試摘要", Brief = brief,
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error, Count = 47,
                    Category = IssueCategory.Storage, Severity = IssueSeverity.Critical,
                    KnownIssue = "磁碟 I/O 錯誤或壞軌前兆"
                }
            }
        };

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        if (expected) Assert.Contains($"給主管的說明：{brief}", draft.Content);
        else Assert.DoesNotContain("給主管的說明：", draft.Content);
    }

    [Fact]
    public async Task 報告白話總覽_AI結果失效後不印舊的給主管的說明()
    {
        var sink = new FakeReportSink();
        var ai = new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 }, NullAiRequestGate.Instance);
        var service = new RiskReportService(ai, sink);
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today, Host = "test-host", RiskLevel = "高", AiAnalyzed = false,
            Headline = "標題", Summary = "測試摘要", Brief = "舊的給主管說明",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error, Count = 47,
                    Category = IssueCategory.Storage, Severity = IssueSeverity.Critical,
                    KnownIssue = "磁碟 I/O 錯誤或壞軌前兆"
                }
            }
        };

        var draft = await service.PrepareAsync(record, new List<EventLogEntryData>());

        Assert.DoesNotContain("給主管的說明：", draft.Content);
    }

    [Fact]
    public void PRTG歷史修訂後Brief清空()
    {
        var record = new DailyAnalysisRecord
        {
            HostId = 101, Host = "TEST", Date = DateTime.Today.AddDays(-3), LogSource = AnalysisLogSource.Netiq,
            RiskLevel = RiskLevels.High, RiskBasis = CorrelationPatternIds.PrtgStorageCorroborated,
            Headline = "舊雙重確認", Brief = "舊的給主管說明", Summary = "舊高風險敘事",
            TopIssues = new List<LogIssueSignature>
                { new() { Source = "disk", EventId = 153, LogName = "System", Severity = IssueSeverity.High, ElevatesDayRisk = true } },
            CorrelationAlerts = new List<string> { "【儲存故障雙重確認】舊推論" },
            CorrelationAlertRefs = new List<CorrelationAlertRef>
                { new() { Text = "【儲存故障雙重確認】舊推論", PatternId = CorrelationPatternIds.PrtgStorageCorroborated } }
        };

        Assert.True(PrtgHistoricalRiskReview.Apply(record));
        Assert.Equal(string.Empty, record.Brief);
    }

    [Fact]
    public void 報告輸入指紋隨Brief改變()
    {
        var record = new DailyAnalysisRecord { Date = DateTime.Today, RiskLevel = "高", Headline = "h", Brief = "甲" };
        var before = HostDayWorkflowFingerprint.ForReportInput(record);
        record.Brief = "乙";
        Assert.NotEqual(before, HostDayWorkflowFingerprint.ForReportInput(record));
    }
}

/// <summary>敘事欄位同步守門：AiOutcome 與落庫紀錄同名的字串欄位，Attach 後一個都不能漏。</summary>
public class AiOutcomeNarrativeSyncTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>
    /// 明確排除、不當敘事欄位檢查的同名字串屬性：
    /// RiskLevel／RiskBasis——只升不降（MoreSevere），哨兵字串不是合法等級，語意由其他測試覆蓋；
    /// ReportFile——由報告草稿落庫的結果決定，不是 AI 敘事。
    /// </summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
        { nameof(AiOutcome.RiskLevel), nameof(AiOutcome.RiskBasis), nameof(AiOutcome.ReportFile) };

    [Fact]
    public void 同名字串敘事欄位Attach後全部寫進紀錄()
    {
        var shared = typeof(AiOutcome).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .Where(name => typeof(DailyAnalysisRecord).GetProperty(name) is { } rp && rp.PropertyType == typeof(string) && rp.CanWrite)
            .Where(name => !Excluded.Contains(name))
            .Distinct().ToList();

        // 守門自身：集合不得因改名悄悄縮水
        foreach (var must in new[] { "Headline", "Summary", "TrendAssessment", "Action", "Brief" })
            Assert.Contains(must, shared);

        var store = new EfAnalysisRecordStore(_fx.NewContext, "sqlite-in-memory");
        var date = DateTime.Today.AddDays(-2);
        store.Append(new DailyAnalysisRecord
        {
            HostId = 101, Host = "SRV-TEST", Date = date, RiskLevel = RiskLevels.High, AiPending = true, TopIssues = []
        });
        var parent = Assert.Single(store.ReadRecent(date, 1));
        var fingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);

        var outcome = new AiOutcome("h", "s", "t", "a", RiskLevels.High, null, true, 0, [], null, [],
            InputPrtgFingerprint: fingerprint);
        foreach (var name in shared)
        {
            var prop = typeof(AiOutcome).GetProperty(name)!;
            outcome = SetInit(outcome, prop, "S-" + name);
        }

        Assert.True(store.TryAttachAiResult(date, outcome, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), fingerprint));

        var attached = Assert.Single(store.ReadRecent(date, 1));
        foreach (var name in shared)
            Assert.Equal("S-" + name, (string?)typeof(DailyAnalysisRecord).GetProperty(name)!.GetValue(attached));
    }

    private static AiOutcome SetInit(AiOutcome outcome, PropertyInfo prop, string value)
    {
        // record 的 init 屬性可由反射呼叫 setter（編譯器只限制語法，執行期不阻擋）
        prop.SetValue(outcome, value);
        return outcome;
    }
}
