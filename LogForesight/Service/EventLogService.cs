using System.Diagnostics;
using NLog;

namespace LogForesight;

// EventLogEntryData 已移至 LogForesight.Core 的 Models/EventLogEntryData.cs（Analysis 層依賴它）。

/// <summary>單一來源（System/Application/Security）單次掃描的結果，含資料完整性中繼資料</summary>
public class EventLogScanResult
{
    public List<EventLogEntryData> Entries { get; init; } = new();

    /// <summary>
    /// false = 該來源保留的歷史不足以涵蓋整個請求區間（較舊的事件已被系統覆蓋）。
    /// 回補多天時若某天落在「未涵蓋」的範圍內，該日的統計會偏低甚至掛零——這不是真的沒事件，
    /// 是資料不完整，不該被當成正常的一天納入趨勢基準計算。
    /// </summary>
    public bool Complete { get; init; } = true;

    /// <summary>讀取失敗（如 Security 無系統管理員權限）時 true，Entries 為空清單</summary>
    public bool ReadFailed { get; init; }

    /// <summary>該來源目前實際保留、可回溯到的最早事件時間；無法判斷（讀取失敗或空日誌）時為 null</summary>
    public DateTime? EarliestAvailable { get; init; }
}

/// <summary>多來源平行掃描的彙總結果，保留每個來源各自的完整性中繼資料供回補流程判斷 DataIncomplete</summary>
public class MultiSourceScanResult
{
    public List<EventLogEntryData> Entries { get; init; } = new();
    public Dictionary<string, EventLogScanResult> BySource { get; init; } = new();

    /// <summary>Security 來源本次是否可讀（null = 未包含在本次掃描的來源清單中）</summary>
    public bool? SecurityAvailable => BySource.TryGetValue("Security", out var r) ? !r.ReadFailed : null;

    /// <summary>
    /// 判斷指定日期是否落在任一來源「歷史不足以涵蓋」的範圍內——用於標記 DailyAnalysisRecord.DataIncomplete，
    /// 避免趨勢基準把資料不完整的一天當成正常的一天計入平均。
    /// </summary>
    public bool IsDateIncomplete(DateTime date)
    {
        foreach (var result in BySource.Values)
        {
            if (!result.Complete && result.EarliestAvailable != null && date.Date <= result.EarliestAvailable.Value.Date)
            {
                return true;
            }
        }
        return false;
    }
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
        => ScanRange(startDate, endDateExclusive, logName).Entries;

    /// <summary>
    /// 與 <see cref="GetEventLogsRange"/> 相同的掃描邏輯，額外回傳資料完整性中繼資料
    /// （是否涵蓋完整區間、目前實際可回溯到的最早事件時間）。
    /// </summary>
    public EventLogScanResult ScanRange(DateTime startDate, DateTime endDateExclusive, string logName, int[]? securityExtraEventIds = null)
    {
        var logs = new List<EventLogEntryData>();

        try
        {
            Log.Debug("開始掃描 {LogName}：{Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd}", logName, startDate, endDateExclusive);
            using var eventLog = new EventLog(logName);

            // Entries 大致依時間排序（index 0 = 最舊、Count-1 = 最新），倒序遍歷、早於區間起點即停止，
            // 避免掃整個日誌。時鐘被回撥（時間同步、手動調整）時事件可能輕微亂序，多掃 1 小時緩衝才停，避免漏抓
            var scanFloor = startDate.AddHours(-1);
            int totalEntries = eventLog.Entries.Count;
            int i = totalEntries - 1;

            for (; i >= 0; i--)
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

                if (!ShouldInclude(logName, entry.EntryType, eventId, securityExtraEventIds))
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

            // 迴圈掃到 i<0（日誌最舊一筆仍未低於 scanFloor）代表該來源保留的歷史不足以涵蓋整個請求區間，
            // 不是「這段期間真的沒事件」，是「這段期間的事件已經被系統覆蓋掉了」
            bool complete = true;
            DateTime? earliestAvailable = totalEntries > 0 ? eventLog.Entries[0].TimeGenerated : null;
            if (i < 0 && totalEntries > 0 && earliestAvailable > scanFloor)
            {
                complete = false;
                Console.WriteLine($"  ⚠ {logName} 保留的歷史不足以涵蓋 {startDate:yyyy-MM-dd} 起的區間，" +
                                   $"最早可回溯到 {earliestAvailable:yyyy-MM-dd HH:mm}（更早的事件已被覆蓋，該區間統計可能偏低）");
                Log.Warn("{LogName} 保留歷史不足，最早可回溯到 {Earliest:yyyy-MM-dd HH:mm}，請求區間起點為 {Start:yyyy-MM-dd}",
                    logName, earliestAvailable, startDate);
            }

            // 只記筆數，不記事件內容——完整訊息本來就會存進歷史/報告，這裡的 log 只是流程追蹤
            Log.Info("{LogName} 掃描完成：{Count} 筆事件（{Start:yyyy-MM-dd}~{End:yyyy-MM-dd}），Complete={Complete}",
                logName, logs.Count, startDate, endDateExclusive, complete);

            return new EventLogScanResult { Entries = logs, Complete = complete, EarliestAvailable = earliestAvailable };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"讀取 {logName} Event Log 時發生錯誤（Security 需要以系統管理員執行）: {ex.Message}");
            Log.Warn(ex, "讀取 {LogName} Event Log 失敗", logName);
            return new EventLogScanResult { Entries = logs, Complete = false, ReadFailed = true };
        }
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
        => (await ScanRangeFromAllAsync(startDate, endDateExclusive, logNames)).Entries;

    /// <summary>
    /// 與 <see cref="GetEventLogsRangeFromAllAsync"/> 相同的平行掃描，額外回傳每個來源的完整性中繼資料，
    /// 供回補流程判斷哪些日期的統計不完整（DataIncomplete）、Security 是否可讀（SecurityLogAvailable）。
    /// </summary>
    public async Task<MultiSourceScanResult> ScanRangeFromAllAsync(
        DateTime startDate, DateTime endDateExclusive, string[]? logNames = null)
    {
        var names = logNames ?? DefaultLogNames;
        var tasks = names.Select(name => Task.Run(() => (name, result: ScanRange(startDate, endDateExclusive, name))));
        var results = await Task.WhenAll(tasks);

        return new MultiSourceScanResult
        {
            Entries = results.SelectMany(r => r.result.Entries).OrderBy(l => l.TimeGenerated).ToList(),
            BySource = results.ToDictionary(r => r.name, r => r.result)
        };
    }

    private static bool ShouldInclude(string logName, EventLogEntryType type, int eventId, int[]? securityExtraEventIds)
    {
        // classic API 讀新式 Critical 等級事件（如 Kernel-Power 41）時 EntryType 可能為 0
        //（EventLogEntryType 列舉沒有 Critical 值），這類事件最嚴重，絕不能被過濾掉
        if (type is EventLogEntryType.Error or EventLogEntryType.Warning || (int)type == 0)
        {
            return true;
        }

        if (logName.Equals("Security", StringComparison.OrdinalIgnoreCase))
        {
            if (type == EventLogEntryType.FailureAudit)
            {
                return true;
            }
            if (type == EventLogEntryType.SuccessAudit)
            {
                return KnownIssueCatalog.SecurityAuditWatchlist.Contains(eventId)
                       || (securityExtraEventIds != null && securityExtraEventIds.Contains(eventId));
            }
        }

        return false;
    }
}
