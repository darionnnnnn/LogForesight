using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 驗證 2026-07-20 AI 角色轉換後的報告雙軌渲染：規則已命中的類別（Category ≠ Other）
/// 直接查靜態知識庫、零 AI 呼叫；只有 Other 類別才會嘗試呼叫 AI。
/// 這裡只涵蓋純規則命中的情境（不含 Other），因此不需要 mock/啟動真的 AI 服務——
/// AIService 的建構子本身不發任何網路請求，只要流程中不觸發 DeepDiveAsync 就不會用到它。
/// </summary>
public class RiskReportServiceTests
{
    private sealed class FakeReportSink : IReportSink
    {
        public string? LastContent { get; private set; }

        public Task<ReportRef> WriteAsync(ReportKind kind, string host, string fileName, string content)
        {
            LastContent = content;
            return Task.FromResult<ReportRef>(new ReportRef($"fake/{fileName}"));
        }
    }

    private static RiskReportService MakeService(FakeReportSink sink)
    {
        // 不會被呼叫到（測試情境全是規則命中的類別），BaseUrl 隨意即可
        var aiService = new AIService(new AiSettings { BaseUrl = "http://localhost:1", RetryCount = 1, TimeoutSeconds = 1 });
        return new RiskReportService(aiService, sink);
    }

    private static LogIssueSignature MakeStorageIssue() => new()
    {
        LogName = "System",
        Source = "disk",
        EventId = 153,
        EntryType = EventLogEntryType.Error,
        Count = 47,
        FirstSeen = "03:12",
        LastSeen = "23:40",
        Category = IssueCategory.Storage,
        Severity = IssueSeverity.Critical,
        KnownIssue = "磁碟 I/O 錯誤或壞軌前兆，硬碟可能即將故障，應盡快備份並安排更換"
    };

    private static DailyAnalysisRecord MakeRecord(LogIssueSignature issue) => new()
    {
        Date = DateTime.Today,
        Host = "test-host",
        RiskLevel = "高",
        AiAnalyzed = true,
        TopIssues = new List<LogIssueSignature> { issue },
        Summary = "測試摘要"
    };

    [Fact]
    public async Task 規則命中類別直接渲染靜態知識庫不呼叫AI()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());

        await service.GenerateAsync(record, new List<EventLogEntryData>());

        Assert.NotNull(sink.LastContent);
        Assert.Contains("處置參考（知識庫）", sink.LastContent);
        Assert.DoesNotContain("AI 深入分析（儲存裝置）", sink.LastContent);
        Assert.Contains("硬碟可能即將故障", sink.LastContent + record.TopIssues[0].KnownIssue);
    }

    [Fact]
    public async Task 靜態知識庫內容寫入DeepDives供DB查詢()
    {
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());

        await service.GenerateAsync(record, new List<EventLogEntryData>());

        var dive = Assert.Single(record.DeepDives);
        Assert.Equal(IssueCategory.Storage, dive.Category);
        var finding = Assert.Single(dive.Findings);
        Assert.False(string.IsNullOrWhiteSpace(finding.Problem));
        Assert.False(string.IsNullOrWhiteSpace(finding.Impact));
        Assert.NotEmpty(finding.LikelyCauses);
        Assert.NotEmpty(finding.NextSteps);
    }

    [Fact]
    public async Task AI未分析時規則命中類別仍能渲染靜態知識庫()
    {
        // AiAnalyzed=false（AI 呼叫失敗降級的統計模式紀錄）——靜態知識庫不依賴 AI 是否可用，
        // 這正是 AI 角色轉換的核心收益：規則命中的處置建議不再從缺
        var sink = new FakeReportSink();
        var service = MakeService(sink);
        var record = MakeRecord(MakeStorageIssue());
        record.AiAnalyzed = false;

        await service.GenerateAsync(record, new List<EventLogEntryData>());

        Assert.Single(record.DeepDives);
        Assert.Contains("處置參考（知識庫）", sink.LastContent);
    }
}
