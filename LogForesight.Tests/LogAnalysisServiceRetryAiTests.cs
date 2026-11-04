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
        EntryType = LogEntryType.Error, Severity = IssueSeverity.High,
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
    public async Task 低風險紀錄補跑時不呼叫AI_回傳Skipped且不標AI已分析()
    {
        var service = NewService(_riskyEvents);
        var pending = PendingRecord(new List<LogIssueSignature>(), riskLevel: "低");
        pending.RiskBasis = "無異常訊號";

        var outcome = await service.RetryAiAsync(pending, historyDays: 14);

        Assert.Equal(0, _ai.Calls);
        Assert.True(outcome.Skipped);
        Assert.False(outcome.AiAnalyzed);
        Assert.Equal("今日狀況正常，無需處理", outcome.Headline);
        Assert.Equal("今日無異常訊號，規則/趨勢/慢速趨勢/關聯四層檢查全數通過。", outcome.Summary);
        Assert.Equal("低", outcome.RiskLevel);
        Assert.Equal("無異常訊號", outcome.RiskBasis);
        Assert.Null(outcome.ReportFile);
        Assert.Null(outcome.ReportDraft);
        Assert.Empty(outcome.DeepDives);
    }

    [Fact]
    public async Task 第一次JSON不合格第二次傳輸失敗時不走原文保留_維持AI未完成()
    {
        // D4b 回歸：傳輸失敗不得帶出前一次的不合格原文，否則被降級成「原文保留」並標成 AI 已完成、不再補跑
        var handler = new SequenceHandler();
        var realAi = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 2
        }, handler, NullAiRequestGate.Instance);
        var service = new LogAnalysisService(new EventLogService(), realAi,
            new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(realAi, _reportSink), riskyEventStore: null);

        var outcome = await service.RetryAiAsync(
            PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);

        Assert.Equal(2, handler.Requests);
        Assert.False(outcome.AiAnalyzed);
        Assert.False(outcome.Skipped);
        Assert.False(outcome.AiDegraded);   // 完全失敗不是降級：標題同用規則層結論，待補語意不變
        Assert.Equal("disk EventId 153 今日出現 3 次（AI 服務未回應，AI 待補）", outcome.Headline);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Requests == 1)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"這不是 JSON\"}}]}",
                        System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }

    [Fact]
    public async Task 低風險但判定依據為ai_raise時照常呼叫主分析()
    {
        _ai.NextContent = """{"risk_level":"低","headline":"補跑標題","story":"補跑摘要","trend_story":"","action":"a"}""";
        var service = NewService(riskyEventStore: null);
        var pending = PendingRecord(new List<LogIssueSignature>(), riskLevel: "低");
        pending.RiskBasis = "ai_raise";

        var outcome = await service.RetryAiAsync(pending, historyDays: 14);

        Assert.True(_ai.Calls >= 1);
        Assert.False(outcome.Skipped);
        Assert.True(outcome.AiAnalyzed);
        Assert.Equal("補跑標題", outcome.Headline);
    }

    [Fact]
    public async Task 中風險紀錄補跑時照常呼叫AI()
    {
        _ai.NextContent = """{"risk_level":"中","headline":"補跑標題","story":"補跑摘要","trend_story":"","action":"a"}""";
        var service = NewService(riskyEventStore: null);

        var outcome = await service.RetryAiAsync(
            PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);

        Assert.True(_ai.Calls >= 1);
        Assert.False(outcome.Skipped);
        Assert.True(outcome.AiAnalyzed);
    }

    [Fact]
    public async Task AI回非JSON原文含高字時風險維持規則風險_不標ai_raise()
    {
        _ai.NextContent = "整體看來高度可疑，其中有中斷情形，但這不是 JSON";
        var service = NewService(riskyEventStore: null);
        var pending = PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中");
        pending.RiskBasis = "規則命中";

        var outcome = await service.RetryAiAsync(pending, historyDays: 14);

        // 降級標題改為規則層結論（不含次數後綴，後綴由寫入端依次數補），並標記 AiDegraded
        Assert.Equal("disk EventId 153 今日出現 3 次", outcome.Headline);
        Assert.True(outcome.AiDegraded);
        Assert.Equal("中", outcome.RiskLevel);
        Assert.NotEqual("ai_raise", outcome.RiskBasis);
    }

    [Fact]
    public async Task 降級為原文保留_標題摘要風險與AiAnalyzed維持既有語意()
    {
        _ai.NextContent = "這是一段不合格的回覆";
        var service = NewService(riskyEventStore: null);

        var outcome = await service.RetryAiAsync(
            PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);

        Assert.Equal("disk EventId 153 今日出現 3 次", outcome.Headline);
        Assert.NotEqual(LogAnalysisService.AiFormatAnomalyHeadline, outcome.Headline);
        Assert.True(outcome.AiDegraded);
        // 常數保留給歷史資料（匯出仍用它辨認存量案例）
        Assert.Equal("AI 回覆格式異常，以下為原始內容", LogAnalysisService.AiFormatAnomalyHeadline);
        Assert.Equal("（AI 回覆經 1 次嘗試仍未通過 JSON 檢查，保留原文供參考）這是一段不合格的回覆", outcome.Summary);
        Assert.Equal("中", outcome.RiskLevel);
        Assert.True(outcome.AiAnalyzed);
        Assert.False(outcome.Skipped);
    }

    [Theory]
    [InlineData(7L, "daily-20260731-h7")]   // 服務帶主機 id：用服務的
    [InlineData(0L, "daily-20260731-h1")]   // 服務沒帶：退回紀錄自己的 HostId
    public async Task 主分析label帶主機識別(long serviceHostId, string expectedLabel)
    {
        _ai.NextContent = """{"risk_level":"中","headline":"h","story":"s","trend_story":"","action":"a"}""";
        var service = new LogAnalysisService(new EventLogService(), _ai, new EfAnalysisRecordStore(_fx.NewContext, "test"),
            new FakeSuppressionStore(), reportService: new RiskReportService(_ai, _reportSink),
            hostId: serviceHostId, riskyEventStore: null);

        await service.RetryAiAsync(
            PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);

        Assert.Contains(expectedLabel, _ai.Labels);
    }

    [Fact]
    public async Task 降級的Warn帶失敗原因_且診斷行用含主機識別的label_經真實AIService()
    {
        var handler = new AlwaysContentHandler("這不是 JSON");
        var realAi = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 0
        }, handler, NullAiRequestGate.Instance);
        var service = new LogAnalysisService(new EventLogService(), realAi,
            new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(realAi, _reportSink), hostId: 5, riskyEventStore: null);
        var store = new BatchRunStore(_fx.LogStore("runs"), _fx.LogStore("run_logs"));

        long runId;
        AiOutcome outcome;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            outcome = await service.RetryAiAsync(
                PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);
            recorder.Finish(0);
        }

        Assert.Equal("disk EventId 153 今日出現 3 次", outcome.Headline);
        Assert.True(outcome.AiDegraded);
        var logs = store.GetLogs(runId);
        var degraded = Assert.Single(logs, l => l.Logger == nameof(LogAnalysisService) && l.Message.Contains("降級為原文保留"));
        Assert.Contains("1 次嘗試仍未通過 JSON 檢查）：", degraded.Message);
        Assert.Contains("AI 回覆不是合法 JSON 或格式不符契約", degraded.Message);
        var diagnostics = AiJsonDiagnostics.TryRead(Assert.Single(logs, l => l.Logger == AiJsonDiagnostics.LoggerName).Message);
        Assert.Equal("daily-20260731-h5", diagnostics!.Label);
        Assert.Equal(AiJsonDiagnostics.CategoryNoJson, Assert.Single(diagnostics.AttemptDetails).Category);
    }

    [Fact]
    public async Task AI服務不通時主分析完全失敗仍是Error_且格式異常的最終失敗是Warn_經真實AIService()
    {
        var down = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 0
        }, new StatusHandler(System.Net.HttpStatusCode.InternalServerError), NullAiRequestGate.Instance);
        var service = new LogAnalysisService(new EventLogService(), down, new EfAnalysisRecordStore(_fx.NewContext, "test"),
            new FakeSuppressionStore(), reportService: new RiskReportService(down, _reportSink), hostId: 5, riskyEventStore: null);
        var store = new BatchRunStore(_fx.LogStore("runs"), _fx.LogStore("run_logs"));

        long runId;
        AiOutcome outcome;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            outcome = await service.RetryAiAsync(
                PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);
            recorder.Finish(0);
        }

        Assert.Equal("disk EventId 153 今日出現 3 次（AI 服務未回應，AI 待補）", outcome.Headline);
        var logs = store.GetLogs(runId);
        var failed = Assert.Single(logs, l => l.Logger == nameof(LogAnalysisService) && l.Message.Contains("主分析完全失敗"));
        Assert.Equal("Error", failed.Level);
        Assert.Equal(1, store.GetRun(runId)!.ErrorCount);
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    [Fact]
    public async Task 回覆含孤立代理字元的信封_主分析拿到結果而非例外()
    {
        // 信封裡的 \ud800 是孤立代理字元跳脫；不論在哪一層被擋下，呼叫端都只會拿到降級結果，不會是例外
        var handler = new RawEnvelopeHandler("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"壞\\ud800字 {x}\"},\"finish_reason\":\"stop\"}]}");
        var realAi = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 0, RetryDelaySeconds = 1, TimeoutSeconds = 10, JsonRetryCount = 1
        }, handler, NullAiRequestGate.Instance);
        var service = new LogAnalysisService(new EventLogService(), realAi, new EfAnalysisRecordStore(_fx.NewContext, "test"),
            new FakeSuppressionStore(), reportService: new RiskReportService(realAi, _reportSink), hostId: 5, riskyEventStore: null);

        var outcome = await service.RetryAiAsync(
            PendingRecord(new List<LogIssueSignature> { RuleHitIssue() }, riskLevel: "中"), historyDays: 14);

        Assert.NotNull(outcome);
        Assert.False(string.IsNullOrEmpty(outcome.Headline));
    }

    private sealed class RawEnvelopeHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class AlwaysContentHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]}",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task JSON的risk_level為高風險且規則風險低時拉高到高並標ai_raise()
    {
        _ai.NextContent = """{"risk_level":"高風險","headline":"h","story":"s","trend_story":"","action":"a"}""";
        var service = NewService(riskyEventStore: null);
        var pending = PendingRecord(new List<LogIssueSignature>(), riskLevel: "低");
        pending.RiskBasis = "ai_raise";   // 低風險要呼叫主分析的唯一入口

        var outcome = await service.RetryAiAsync(pending, historyDays: 14);

        Assert.Equal("高", outcome.RiskLevel);
        Assert.Equal("ai_raise", outcome.RiskBasis);
    }

    [Fact]
    public async Task 風險事件暫存有資料時補上報告並申報原始log來源()
    {
        _riskyEvents.Rows = new List<RiskyEvent>
        {
            new()
            {
                HostId = 1, Date = Date, LogName = "System", Source = "disk", EventId = 153,
                EntryType = LogEntryType.Error, EventTime = Date.AddHours(1),
                Message = "磁碟發生 I/O 錯誤", RuleId = "builtin-disk-error", CreatedAt = DateTime.Now
            }
        };
        _ai.NextContent = """{"risk_level":"高","headline":"補跑後標題","story":"補跑後摘要","trend_story":"","action":"a"}""";
        var service = NewService(_riskyEvents);

        var pending = PendingRecord(new List<LogIssueSignature> { RuleHitIssue() });
        var outcome = await service.RetryAiAsync(pending, historyDays: 14);

        Assert.Null(outcome.ReportFile); // report persistence belongs to guarded parent attachment
        var draft = Assert.IsType<PreparedRiskReport>(outcome.ReportDraft);
        Assert.NotNull(outcome.UncoveredChecksAddendum);
        Assert.Contains(outcome.UncoveredChecksAddendum!, c => c.Contains("風險事件暫存"));
        // 既有申報項目不受影響（追加而非取代，實際落地由 AttachAiResult 的合約測試釘住）
        Assert.DoesNotContain(outcome.UncoveredChecksAddendum!, c => c.Contains("既有申報項目"));
        var finalParent = System.Text.Json.JsonSerializer.Deserialize<DailyAnalysisRecord>(
            System.Text.Json.JsonSerializer.Serialize(pending))!;
        finalParent.Headline = outcome.Headline;
        finalParent.Summary = outcome.Summary;
        finalParent.TrendAssessment = outcome.TrendAssessment;
        finalParent.Action = outcome.Action;
        finalParent.AiPending = false;
        finalParent.AiAnalyzed = outcome.AiAnalyzed;
        finalParent.RiskLevel = outcome.RiskLevel;
        finalParent.RiskBasis = outcome.RiskBasis;
        finalParent.ScreenedTailCount = outcome.ScreenedTailCount;
        finalParent.ScreeningNotes = outcome.ScreeningNotes;
        finalParent.UncoveredChecks.AddRange(outcome.UncoveredChecksAddendum!);
        Assert.Equal(HostDayWorkflowFingerprint.ForReportInput(finalParent), draft.DecisionInputFingerprint);
        Assert.Contains("本次報告資料範圍", draft.Content);
        Assert.Contains("有界風險事件暫存", draft.Content);
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
            LogName = "System", Source = "noisy", EventId = 1, EntryType = LogEntryType.Information,
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
