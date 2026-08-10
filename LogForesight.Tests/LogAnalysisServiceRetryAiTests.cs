using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋十三輪 C（體檢批 0 #6，取代 §3.10 舊決策）：<see cref="LogAnalysisService.RetryAiAsync"/>
/// 孤兒補跑改為連深析報告一併補——從 <c>lf_risky_events</c> 風險事件暫存重建原始 log。
/// 直接測 RetryAiAsync 本身（而非整條 NetiqPipelineService 管線），因為要精確控制暫存內容
/// 是否存在，跑完整管線去湊「取消時機」與「保留期」的時序既慢又不穩定。
/// </summary>
public class LogAnalysisServiceRetryAiTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeRiskyEventStore _riskyEvents = new();
    private readonly FakeAiService _ai = new();
    private readonly FakeReportSink _reportSink = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static readonly DateTime Date = new(2026, 7, 31);

    private LogAnalysisService NewService(IRiskyEventStore? riskyEventStore) =>
        new(new EventLogService(), _ai, new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(_ai, _reportSink), riskyEventStore: riskyEventStore);

    /// <summary>命中規則、嚴重度 High 的簽章——足以讓當日風險等級判定為「高」（IsActionable）</summary>
    private static LogIssueSignature RuleHitIssue() => new()
    {
        LogName = "System", Source = "disk", EventId = 153,
        EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High,
        Category = IssueCategory.Storage, RuleId = "builtin-disk-error", Count = 3
    };

    private static DailyAnalysisRecord PendingRecord(List<LogIssueSignature> issues, string riskLevel = "高") => new()
    {
        Date = Date,
        HostId = 1,
        Host = "HOST-A",
        RiskLevel = riskLevel,
        AiPending = true,
        TopIssues = issues,
        TrendAlerts = new List<string>(),
        CorrelationAlerts = new List<string>(),
        UncoveredChecks = new List<string> { "既有申報項目" }
    };

    [Fact]
    public async Task 風險事件暫存有資料時補上報告並申報原始log來源()
    {
        _riskyEvents.Rows = new List<RiskyEvent>
        {
            new()
            {
                HostId = 1, Date = Date, LogName = "System", Source = "disk", EventId = 153,
                EntryType = EventLogEntryType.Error, EventTime = Date.AddHours(1),
                Message = "磁碟發生 I/O 錯誤", RuleId = "builtin-disk-error", CreatedAt = DateTime.Now
            }
        };
        _ai.NextContent = """{"risk_level":"高","headline":"補跑後標題","story":"補跑後摘要","trend_story":"","action":"a"}""";
        var service = NewService(_riskyEvents);

        var outcome = await service.RetryAiAsync(PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }), historyDays: 14);

        Assert.NotNull(outcome.ReportFile);
        Assert.NotNull(outcome.UncoveredChecksAddendum);
        Assert.Contains(outcome.UncoveredChecksAddendum!, c => c.Contains("風險事件暫存"));
        // 既有申報項目不受影響（追加而非取代，實際落地由 AttachAiResult 的合約測試釘住）
        Assert.DoesNotContain(outcome.UncoveredChecksAddendum!, c => c.Contains("既有申報項目"));
    }

    [Fact]
    public async Task 風險事件暫存查無資料時報告從缺並申報原因()
    {
        _riskyEvents.Rows = new List<RiskyEvent>();   // 逾期或當初無合格事件寫入
        _ai.NextContent = """{"risk_level":"高","headline":"h","story":"s","trend_story":"","action":"a"}""";
        var service = NewService(_riskyEvents);

        var outcome = await service.RetryAiAsync(PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }), historyDays: 14);

        Assert.Null(outcome.ReportFile);
        Assert.NotNull(outcome.UncoveredChecksAddendum);
        Assert.Contains(outcome.UncoveredChecksAddendum!, c => c.Contains("原始 log 佐證從缺"));
    }

    [Fact]
    public async Task 低風險日暫存查無資料時不申報從缺_低風險本來就不產報告()
    {
        _riskyEvents.Rows = new List<RiskyEvent>();
        var lowIssue = new LogIssueSignature
        {
            LogName = "System", Source = "noisy", EventId = 1, EntryType = EventLogEntryType.Information,
            Severity = IssueSeverity.Low, Category = IssueCategory.Other, Count = 1
        };
        var service = NewService(_riskyEvents);

        var outcome = await service.RetryAiAsync(PendingRecord(new List<LogIssueSignature> { lowIssue }, riskLevel: "低"), historyDays: 14);

        Assert.Null(outcome.ReportFile);
        Assert.Null(outcome.UncoveredChecksAddendum);   // 低風險日本來就不產報告，講從缺反而多餘
    }

    [Fact]
    public async Task 未接風險事件暫存時維持既有行為_報告從缺且不申報()
    {
        var service = NewService(riskyEventStore: null);

        var outcome = await service.RetryAiAsync(PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }), historyDays: 14);

        Assert.Null(outcome.ReportFile);
        Assert.Null(outcome.UncoveredChecksAddendum);
    }
}
