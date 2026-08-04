using System.Diagnostics;

namespace LogForesight;

/// <summary>
/// 風險 log 暫存（↔ lf_risky_events）：規則命中或趨勢異常的問題簽章，保留一段時間內的原始事件，
/// 供「詢問 AI」對話優先取用，不必每次都即時打 Sentinel（docs/archive/WEB-SCHEDULER-PLAN.md §2）。
///
/// 入庫資格與上限由 <see cref="RiskyEventSelector"/> 決定；本模型只是資料形狀。
/// 與 <see cref="LogIssueSignature.SampleMessages"/>（3 則、200 字，隨每日分析紀錄長期保存）
/// 是兩份獨立資料：這裡量更大（每簽章最多 50 則、2000 字）但只保留設定的天數（預設 14 天）。
/// </summary>
public class RiskyEvent
{
    public long Id { get; set; }

    /// <summary>歸戶鍵，同 DailyAnalysisRecord.HostId 慣例</summary>
    public long HostId { get; set; }

    /// <summary>分析日（不是寫入時刻）</summary>
    public DateTime Date { get; set; }

    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public EventLogEntryType EntryType { get; set; }

    /// <summary>事件原始時間戳</summary>
    public DateTime EventTime { get; set; }

    /// <summary>原文，截 2000 字（見 RiskyEventSelector.MaxMessageChars）</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>命中規則的 Id，未命中規則（僅趨勢異常入庫）為 null</summary>
    public string? RuleId { get; set; }

    public DateTime CreatedAt { get; set; }
}
