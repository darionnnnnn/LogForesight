using System.Diagnostics;
using NLog;

namespace LogForesight;

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

public class EventLogService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>預設分析的日誌來源。Security 需要系統管理員權限，讀不到時會略過並提示</summary>
    public static readonly string[] DefaultLogNames = { "System", "Application", "Security" };

    /// <summary>取得單一日期的 Event Log（區間掃描的特例）</summary>
    public List<EventLogEntryData> GetEventLogs(DateTime targetDate, string logName = "System")
        => GetEventLogsRange(targetDate.Date, targetDate.Date.AddDays(1), logName);

    /// <summary>
    /// 單次倒序掃描取回 [startDate, endDateExclusive) 區間內的事件。
    /// 回補多天時用這個：同一份日誌只掃一次，而不是每個日期各倒序掃一遍。
    /// System/Application 取 Error 與 Warning；
    /// Security 取 FailureAudit（如登入失敗），以及觀察清單內的 SuccessAudit 高價值事件
    /// （帳號建立、日誌被清除等入侵跡象——這類事件不是錯誤，但必須納入分析）。
    /// </summary>
    public List<EventLogEntryData> GetEventLogsRange(DateTime startDate, DateTime endDateExclusive, string logName)
    {
        var logs = new List<EventLogEntryData>();

        try
        {
            Log.Debug("開始掃描 {LogName}：{Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd}", logName, startDate, endDateExclusive);
            using var eventLog = new EventLog(logName);

            // Entries 大致依時間排序，倒序遍歷、早於區間起點即停止，避免掃整個日誌。
            // 時鐘被回撥（時間同步、手動調整）時事件可能輕微亂序，多掃 1 小時緩衝才停，避免漏抓
            var scanFloor = startDate.AddHours(-1);

            for (int i = eventLog.Entries.Count - 1; i >= 0; i--)
            {
                var entry = eventLog.Entries[i];

                if (entry.TimeGenerated < scanFloor)
                {
                    break;
                }

                if (entry.TimeGenerated < startDate || entry.TimeGenerated >= endDateExclusive)
                {
                    continue;
                }

                int eventId = (int)(entry.InstanceId & 0xFFFF);

                if (!ShouldInclude(logName, entry.EntryType, eventId))
                {
                    continue;
                }

                logs.Add(new EventLogEntryData
                {
                    TimeGenerated = entry.TimeGenerated,
                    EntryType = entry.EntryType,
                    LogName = logName,
                    Source = entry.Source,
                    Message = entry.Message,
                    InstanceId = entry.InstanceId,
                    EventId = eventId
                });
            }

            // 只記筆數，不記事件內容——完整訊息本來就會存進歷史/報告，這裡的 log 只是流程追蹤
            Log.Info("{LogName} 掃描完成：{Count} 筆事件（{Start:yyyy-MM-dd}~{End:yyyy-MM-dd}）",
                logName, logs.Count, startDate, endDateExclusive);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"讀取 {logName} Event Log 時發生錯誤（Security 需要以系統管理員執行）: {ex.Message}");
            Log.Warn(ex, "讀取 {LogName} Event Log 失敗", logName);
        }

        return logs;
    }

    /// <summary>從多個日誌來源取得指定日期的事件</summary>
    public List<EventLogEntryData> GetEventLogsFromAll(DateTime targetDate, string[]? logNames = null)
    {
        return (logNames ?? DefaultLogNames)
            .SelectMany(name => GetEventLogs(targetDate, name))
            .OrderBy(l => l.TimeGenerated)
            .ToList();
    }

    /// <summary>
    /// 平行掃描多個日誌來源，一次取回整個日期區間的事件。
    /// 三個日誌各自獨立，同時掃描；抓取全部前置完成後，AI 分析迴圈就不需要再等任何 I/O。
    /// </summary>
    public async Task<List<EventLogEntryData>> GetEventLogsRangeFromAllAsync(
        DateTime startDate, DateTime endDateExclusive, string[]? logNames = null)
    {
        var tasks = (logNames ?? DefaultLogNames)
            .Select(name => Task.Run(() => GetEventLogsRange(startDate, endDateExclusive, name)));

        var results = await Task.WhenAll(tasks);

        return results
            .SelectMany(r => r)
            .OrderBy(l => l.TimeGenerated)
            .ToList();
    }

    private static bool ShouldInclude(string logName, EventLogEntryType type, int eventId)
    {
        // classic API 讀新式 Critical 等級事件（如 Kernel-Power 41）時 EntryType 可能為 0
        //（EventLogEntryType 列舉沒有 Critical 值），這類事件最嚴重，絕不能被過濾掉
        if (type is EventLogEntryType.Error or EventLogEntryType.Warning || (int)type == 0)
        {
            return true;
        }

        if (logName.Equals("Security", StringComparison.OrdinalIgnoreCase))
        {
            return type == EventLogEntryType.FailureAudit
                || (type == EventLogEntryType.SuccessAudit && KnownIssueCatalog.SecurityAuditWatchlist.Contains(eventId));
        }

        return false;
    }
}
