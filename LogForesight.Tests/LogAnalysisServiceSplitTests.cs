using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// LogAnalysisService 拆分統計段/AI 段（docs/FEEDBACK-12-PLAN.md §3.3）之後的回歸測試：
/// AnalyzeDayAsync 的組合呼叫（統計段緊接著 AI 段）產出的最終紀錄與報告內容要與拆分前
/// 完全一致。這裡專門釘住 <c>CompleteAiAsync</c> 內部組出的暫用 <see cref="DailyAnalysisRecord"/>
/// 有沒有把 <see cref="RiskReportService.GenerateAsync"/> 會讀的欄位都填齊——實作過程中曾經
/// 只填了 TopIssues/RiskLevel/AiAnalyzed 幾個欄位，報告會靜靜少一大塊內容（Headline／Summary／
/// TrendAssessment／Action／UncoveredChecks／DataIncomplete），但不會有任何例外或建置警告。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class LogAnalysisServiceSplitTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>命中 builtin-storage-disk-io（ElevatesDayRisk=true，Category=Storage≠Other）——
    /// 選 Storage 而非 Other 類別，報告產生時不會觸發 DeepDiveAsync 的第二個 AI 呼叫，
    /// 讓斷言只聚焦在「主分析的 AI 內容有沒有進報告」這一件事。</summary>
    private static List<EventLogEntryData> MakeHighRiskDiskEvents(int count = 20) =>
        Enumerable.Range(0, count).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(-(i % 20)),
            EntryType = EventLogEntryType.Error,
            LogName = "System",
            Source = "disk",
            EventId = 153,
            Message = $"磁碟發生 I/O 錯誤 #{i}"
        }).ToList();

    [Fact]
    public async Task 命中高風險規則時報告內容含AI產出的headline與summary()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService
        {
            NextContent = """{"risk_level":"高","headline":"磁碟即將故障","story":"偵測到大量磁碟I/O錯誤，硬碟可能即將故障。","trend_story":"","action":"今天就要處理"}"""
        };
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: reportService);

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-1), MakeHighRiskDiskEvents(), useAi: true);

        Assert.Equal("高", record.RiskLevel);
        Assert.True(record.AiAnalyzed);
        Assert.NotNull(record.ReportFile);
        Assert.NotNull(sink.LastContent);
        Assert.Contains("磁碟即將故障", sink.LastContent);       // AI headline 有沒有進報告
        Assert.Contains("偵測到大量磁碟I/O錯誤", sink.LastContent); // AI summary 有沒有進報告
        Assert.Contains("今天就要處理", sink.LastContent);       // AI action 有沒有進報告
    }

    /// <summary>統計模式（useAi=false）路徑不受拆分影響：規則本身判定的高風險一樣要出報告，
    /// 只是報告內容不含 AI 產出（此路徑走 BuildStatisticalRecordAsync 的「不需要 AI」分支，
    /// 不經過 CompleteAiAsync 的暫用記錄，是拆分後的另一條獨立路徑，需要各自驗證）。</summary>
    [Fact]
    public async Task 統計模式下高風險規則一樣輸出報告但不含AI內容()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService();
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: reportService);

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-1), MakeHighRiskDiskEvents(), useAi: false);

        Assert.Equal("高", record.RiskLevel);
        Assert.False(record.AiAnalyzed);
        Assert.Equal(0, ai.Calls);
        Assert.NotNull(record.ReportFile);
        Assert.NotNull(sink.LastContent);
        Assert.Contains("統計模式紀錄", sink.LastContent);
    }
}
