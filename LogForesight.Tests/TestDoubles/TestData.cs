using System.Diagnostics;
using LogForesight;

namespace LogForesight.Tests;

// ── 共用測試資料 builder ─────────────────────────────────────────────────────
// 這裡只收斂「逐字相同」的重複版本：多個測試檔各自寫了一份完全一樣的 helper。
// 大部份同名的 Issue(...)/Sig(...)/AddHost(...) helper 其實簽章或欄位不一樣
// （不同的預設值、不同的參數組合），那些刻意保留在各自的測試檔案裡，
// 不強行合併——過度抽象不是這次整理的目標，見 docs 的測試整備計畫。

internal static class TestData
{
    /// <summary>與 DayHandlingDerivationTests／HandlingServiceTests 原本逐字相同的版本合併</summary>
    public static LogIssueSignature Issue(string source, int eventId, IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = EventLogEntryType.Error,
        Severity = severity
    };

    /// <summary>
    /// 與 SuppressionTests／TrendAnalyzerTests 原本逐字相同的版本合併；CorrelationAnalyzerTests
    /// 原本多一個 keyDetails 參數，是純粹的擴充（未傳入時等同原本兩份省略 KeyDetails 的行為），
    /// 一併合併進來。
    /// </summary>
    public static LogIssueSignature Sig(string logName, string source, int eventId, int count, IssueSeverity severity,
        IssueCategory category = IssueCategory.Other, string? keyDetails = null)
        => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = EventLogEntryType.Error,
            Count = count,
            Severity = severity,
            Category = category,
            FirstSeen = "00:00",
            LastSeen = "23:59",
            KeyDetails = keyDetails
        };

    /// <summary>
    /// 與 HostDetailIssueSummaryTests／RecordQueryServiceIssueHistoryTests／
    /// RecordQueryServiceSearchTests／ReportServiceTests 原本逐字相同的版本合併。
    /// </summary>
    public static WebHost AddHost(IHostStore hosts, string name) => hosts.Upsert(new WebHost { HostName = name });
}
