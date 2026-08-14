using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 2026-07-20 體檢重設計的兩個確定性行為：due-date 到期判斷（ShouldRun）與
/// 窗口內三層皆無訊號時的閘門（RunAsync 早退路徑）。這裡用「有訊號時會嘗試呼叫 AI（因而在
/// 無法連線時失敗）」間接驗證閘門正確地沒有短路——這個寫法早於 IAiService 抽出
/// （docs/archive/FEEDBACK-12-PLAN.md §3.8-1），改用 <see cref="FakeAiService"/> 亦可，此處維持
/// 原寫法不重工，兩者驗證的斷言相同。
/// </summary>
public class WeeklyCheckupServiceTests
{
    // FakeReader／FakeReportSink 已搬到 TestDoubles\ReportingFakes.cs（FakeReportSink 與
    // RiskReportServiceTests 共用）。

    private static WeeklyCheckupService MakeService(FakeReader reader, out FakeReportSink sink)
    {
        sink = new FakeReportSink();
        // RetryDelaySeconds=1（預設 10 秒）：這裡只需要驗證「有沒有嘗試呼叫」，不需要等完整的
        // 正式重試延遲，避免測試套件被這幾個必然失敗的網路呼叫拖慢
        var aiService = new AIService(new AiSettings
        {
            BaseUrl = "http://localhost:1", RetryCount = 1, RetryDelaySeconds = 1, TimeoutSeconds = 1
        });
        return new WeeklyCheckupService(aiService, reader, sink);
    }

    // ── ShouldRun：due-date 到期判斷 ──────────────────────────────────

    [Fact]
    public void 尚無任何分析紀錄時不執行()
    {
        var service = MakeService(new FakeReader(new List<DailyAnalysisRecord>()), out _);

        Assert.False(service.ShouldRun(DateTime.Today, intervalDays: 7));
    }

    [Fact]
    public void 有紀錄但從未體檢過時立即執行以建立基準()
    {
        var reader = new FakeReader(new List<DailyAnalysisRecord> { new() { Date = DateTime.Today, RiskLevel = "低" } }, lastCheckup: null);
        var service = MakeService(reader, out _);

        Assert.True(service.ShouldRun(DateTime.Today, intervalDays: 7));
    }

    [Fact]
    public void 距上次體檢未達間隔天數時不執行()
    {
        var reader = new FakeReader(
            new List<DailyAnalysisRecord> { new() { Date = DateTime.Today, RiskLevel = "低" } },
            lastCheckup: DateTime.Today.AddDays(-3));
        var service = MakeService(reader, out _);

        Assert.False(service.ShouldRun(DateTime.Today, intervalDays: 7));
    }

    [Fact]
    public void 距上次體檢達間隔天數時執行()
    {
        var reader = new FakeReader(
            new List<DailyAnalysisRecord> { new() { Date = DateTime.Today, RiskLevel = "低" } },
            lastCheckup: DateTime.Today.AddDays(-7));
        var service = MakeService(reader, out _);

        Assert.True(service.ShouldRun(DateTime.Today, intervalDays: 7));
    }

    // ── RunAsync：確定性閘門 ──────────────────────────────────────────

    [Fact]
    public async Task 窗口內三層皆無訊號時不呼叫AI直接寫固定結論()
    {
        var window = Enumerable.Range(1, 7)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), RiskLevel = "低" })
            .ToList();
        var service = MakeService(new FakeReader(window), out var sink);

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7);

        Assert.True(outcome.Completed);
        Assert.False(outcome.HasFindings);
        Assert.Equal("本期無累積性異常，程式比對通過。", outcome.Conclusion);
        Assert.Null(outcome.ReportFile);
        Assert.False(sink.Called); // 沒有輸出報告檔——不消耗任何 I/O 或 AI 資源
    }

    [Fact]
    public async Task 窗口內有風險日時嘗試呼叫AI而非直接判定無訊號()
    {
        var window = new List<DailyAnalysisRecord> { new() { Date = DateTime.Today.AddDays(-1), RiskLevel = "高" } };
        var service = MakeService(new FakeReader(window), out _);

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7);

        // AIService 指向不可達位址，呼叫必定失敗——但重點是「有嘗試」而非被閘門短路成無訊號結論
        Assert.False(outcome.Completed);
        Assert.DoesNotContain("本期無累積性異常", outcome.Conclusion);
    }

    [Fact]
    public async Task 窗口內有關聯訊號時也視為有訊號()
    {
        var window = new List<DailyAnalysisRecord>
        {
            new() { Date = DateTime.Today.AddDays(-1), RiskLevel = "低", CorrelationAlerts = new List<string> { "測試關聯訊號" } }
        };
        var service = MakeService(new FakeReader(window), out _);

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7);

        Assert.False(outcome.Completed); // 同上：進入 AI 呼叫分支後因無法連線而失敗，證明閘門判定為「有訊號」
    }

    // ── RunAsync：useAi 短路（docs/archive/FEEDBACK-7-PLAN.md，AI 未設定時的行為）───────

    [Fact]
    public async Task 有訊號且AI未設定_不嘗試呼叫且未完成待補跑()
    {
        var window = new List<DailyAnalysisRecord> { new() { Date = DateTime.Today.AddDays(-1), RiskLevel = "高" } };
        var service = MakeService(new FakeReader(window), out var sink);

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7, useAi: false);

        Assert.False(outcome.Completed);
        Assert.False(outcome.HasFindings);
        Assert.Contains("AI 未設定", outcome.Conclusion);
        Assert.Null(outcome.ReportFile);
        Assert.False(sink.Called); // 不嘗試呼叫 AI，也不產生報告檔
    }

    [Fact]
    public async Task 無訊號且AI未設定_不受影響照常完成()
    {
        var window = Enumerable.Range(1, 7)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), RiskLevel = "低" })
            .ToList();
        var service = MakeService(new FakeReader(window), out var sink);

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7, useAi: false);

        Assert.True(outcome.Completed);
        Assert.False(outcome.HasFindings);
        Assert.Equal("本期無累積性異常，程式比對通過。", outcome.Conclusion);
        Assert.False(sink.Called);
    }

    // ── BuildPrompt：AiAnalyzed 守衛（回饋十四輪 B2）───────────────────────

    /// <summary>
    /// AiPending／統計模式的樣板 Summary（如「（統計已完成，AI 分析排隊中）」）不是真正的 AI
    /// 判讀，不該被當成「當日結論」引用進體檢 prompt——否則模型會把程式寫的佔位字串當成
    /// 先前已有的敘事脈絡來延續。比照 AnalysisPromptBuilder 對每日 prompt 的同一道守衛
    /// （<c>h.AiAnalyzed &amp;&amp; h.Summary.Length &gt; 0</c>）。
    /// </summary>
    [Fact]
    public async Task 非AI判讀的樣板摘要不會被引用進體檢prompt()
    {
        var window = new List<DailyAnalysisRecord>
        {
            new()
            {
                Date = DateTime.Today.AddDays(-2), RiskLevel = "高", AiAnalyzed = false,
                Summary = "（統計已完成，AI 分析排隊中）"
            },
            new()
            {
                Date = DateTime.Today.AddDays(-1), RiskLevel = "高", AiAnalyzed = true,
                Summary = "偵測到真正的AI判讀內容"
            }
        };
        var ai = new FakeAiService { NextContent = """{"conclusion":"測試結論"}""" };
        var service = new WeeklyCheckupService(ai, new FakeReader(window), new FakeReportSink());

        var outcome = await service.RunAsync(DateTime.Today, intervalDays: 7);

        Assert.True(outcome.Completed);
        var prompt = Assert.Single(ai.Prompts);
        Assert.DoesNotContain("AI 分析排隊中", prompt);
        Assert.Contains("偵測到真正的AI判讀內容", prompt);
    }
}
