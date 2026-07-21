using System.Diagnostics;

namespace LogForesight;

/// <summary>
/// 單筆 Event Log 事件的資料模型。
/// 原定義於 Service/EventLogService.cs，因 <see cref="LogAggregator"/>（Analysis 層）依賴它，
/// 抽 Core 時一併移入 Models——它是純資料、不做 I/O，本來就屬於資料模型層；
/// 讀取事件的服務（EventLogService）仍留在批次 exe。
/// </summary>
public class EventLogEntryData
{
    public DateTime TimeGenerated { get; set; }
    public EventLogEntryType EntryType { get; set; }
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long InstanceId { get; set; }

    /// <summary>傳統 Event ID（InstanceId 的低 16 位元），文件與規則表都以此為準</summary>
    public int EventId { get; set; }
}
