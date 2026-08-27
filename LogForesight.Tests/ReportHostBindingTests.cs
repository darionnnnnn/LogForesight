using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 報告寫入時的主機綁定。
///
/// 升級前三個寫入端都沒有把主機傳給 sink（風險報告走預設空字串、體檢報告走預設空字串、
/// 權限異動報告明寫空字串），報告因此全部落在同一個目錄、檔名只由「日期＋風險等級＋類別」
/// 組成——多台主機同日同風險同類別會互相覆蓋。這組測試釘住「主機一定要傳下去」，
/// 不讓那個形狀在任何一個寫入端悄悄回來。
/// </summary>
public class ReportHostBindingTests
{
    /// <summary>
    /// 風險等級與類別串要一起帶給 sink：報告清單與檢視畫面的標題列靠這兩個欄位，
    /// 不解全文就要顯示得出「這是哪一種、多嚴重」。
    /// </summary>
    [Fact]
    public async Task 風險報告寫入_帶上風險等級與類別串()
    {
        var sink = new FakeReportSink();
        var service = new RiskReportService(new FakeAiService(), sink);

        var record = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 8, 27),
            Host = "SRV01",
            HostId = 3,
            RiskLevel = RiskLevels.High,
            AiAnalyzed = true,
            Summary = "測試",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 7,
                    EntryType = System.Diagnostics.EventLogEntryType.Error,
                    Severity = IssueSeverity.High, Count = 3, Category = IssueCategory.Storage
                }
            }
        };

        await service.GenerateAsync(record, new List<EventLogEntryData>());

        Assert.Equal(ReportKind.DailyRisk, sink.LastKind);
        Assert.Equal(RiskLevels.High, sink.LastMeta?.RiskLevel);
        Assert.Equal(IssueCategoryNames.Zh(IssueCategory.Storage), sink.LastMeta?.Categories);
        Assert.Equal(3, sink.LastHost?.HostId);
    }

    /// <summary>
    /// host 參數省略時退回紀錄自身的主機——不是退回空值。呼叫端漏傳的代價應該是
    /// 「歸戶到這筆紀錄的主機」，而不是「所有主機共用一個空鍵」。
    /// </summary>
    [Fact]
    public async Task 未指定主機_退回紀錄自身的主機而不是空值()
    {
        var sink = new FakeReportSink();
        var service = new RiskReportService(new FakeAiService(), sink);

        var record = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 8, 27),
            Host = "SRV-FALLBACK",
            HostId = 9,
            RiskLevel = RiskLevels.Medium,
            AiAnalyzed = true,
            Summary = "測試",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 7,
                    EntryType = System.Diagnostics.EventLogEntryType.Error,
                    Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Storage
                }
            }
        };

        await service.GenerateAsync(record, new List<EventLogEntryData>());

        Assert.Equal(9, sink.LastHost?.HostId);
        Assert.Equal("SRV-FALLBACK", sink.LastHost?.HostName);
    }
}
