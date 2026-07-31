using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
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

    /// <summary>讀取失敗（如 Security 無系統管理員權限、存取被拒）時 true，Entries 為空清單。偵測盲區。</summary>
    public bool ReadFailed { get; init; }

    /// <summary>
    /// 頻道在本機不存在（未安裝對應角色，如無 Defender 或未啟用 RDP）時 true。
    /// 與 <see cref="ReadFailed"/> 刻意分開：不存在屬預期、該偵測不適用，不算故障、不觸發 DataIncomplete。
    /// </summary>
    public bool ChannelMissing { get; init; }

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

    /// <summary>本次成功讀取的頻道名稱（既非讀取失敗、也非頻道不存在）。</summary>
    public List<string> ChannelsRead =>
        BySource.Where(kv => !kv.Value.ReadFailed && !kv.Value.ChannelMissing).Select(kv => kv.Key).ToList();

    /// <summary>本次存取被拒的頻道（存在但權限不足）——偵測盲區。</summary>
    public List<string> ChannelsDenied =>
        BySource.Where(kv => kv.Value.ReadFailed).Select(kv => kv.Key).ToList();

    /// <summary>本次在主機上不存在的頻道（未安裝對應角色）——該偵測不適用。</summary>
    public List<string> ChannelsMissing =>
        BySource.Where(kv => kv.Value.ChannelMissing).Select(kv => kv.Key).ToList();

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

    /// <summary>
    /// 用 classic <see cref="EventLog"/> API 讀取的傳統日誌——**這三個有遷移前就存在的 history.txt**，
    /// 必須維持 classic API 的識別鍵（尤其 <c>Source</c> 為註冊的短來源名，如 "DCOM"、"Wlclntfy"，
    /// 而 <see cref="EventRecord.ProviderName"/> 是完整 manifest 名 "Microsoft-Windows-DistributedCOM"，
    /// 兩者不同）。若改用 EventLogReader，聚合鍵 (LogName, Source, EventId, EntryType) 會全面漂移、
    /// 舊歷史趨勢比對斷裂。新式 Operational 頻道（Defender/RDP，classic API 讀不到）才用 EventLogReader，
    /// 那些頻道沒有遷移前的歷史，不存在漂移問題。這道分流是遷移期以兩套 API 實測比對發現漂移後的定案。
    /// </summary>
    private static readonly HashSet<string> ClassicApiLogs =
        new(StringComparer.OrdinalIgnoreCase) { "System", "Application", "Security" };

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
    ///
    /// 依頻道分流讀取 API（見 <see cref="ClassicApiLogs"/> 的說明）：傳統三個日誌走 classic
    /// <see cref="EventLog"/>（維持既有識別鍵、不斷舊歷史趨勢），Defender/RDP 等 Operational 頻道
    /// 走 <see cref="EventLogReader"/>（classic API 讀不到的新式頻道）。
    /// </summary>
    public EventLogScanResult ScanRange(DateTime startDate, DateTime endDateExclusive, string logName, int[]? securityExtraEventIds = null)
        => ClassicApiLogs.Contains(logName)
            ? ScanRangeClassic(startDate, endDateExclusive, logName, securityExtraEventIds)
            : ScanRangeModern(startDate, endDateExclusive, logName, securityExtraEventIds);

    /// <summary>
    /// 新式 Operational 頻道（Defender/RDP）的 EventLogReader 掃描。這些頻道沒有遷移前的歷史，
    /// <c>Source</c> 用 <see cref="EventRecord.ProviderName"/>（完整 manifest 名）不影響任何舊資料。
    /// </summary>
    private EventLogScanResult ScanRangeModern(DateTime startDate, DateTime endDateExclusive, string logName, int[]? securityExtraEventIds = null)
    {
        var logs = new List<EventLogEntryData>();
        var policy = ChannelCatalog.Resolve(logName);
        // Security 及其他稽核型頻道以 Keywords 判斷成功/失敗稽核（見 EventRecordMapper）。
        bool isAuditChannel = policy.Kind == ChannelInclusionKind.SecurityAudit;

        try
        {
            Log.Debug("開始掃描 {LogName}（EventLogReader）：{Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd}", logName, startDate, endDateExclusive);

            // 時鐘被回撥（時間同步、手動調整）時事件可能輕微亂序，多掃 1 小時緩衝才停，避免漏抓。
            var scanFloor = startDate.AddHours(-1);

            // XPath 伺服器端時間過濾（SystemTime 為 UTC）——只取回區間內的事件，不必載入整個日誌。
            // 保留 scanFloor 緩衝於查詢範圍，再於客戶端以本地時間做精確過濾（與 classic 的判斷語意一致）。
            // 格式用 "o"（round-trip）：文化不變，不像自訂格式字串的 ':'/'-' 會被替換成當前文化的分隔符號。
            string xpath = "*[System[TimeCreated[@SystemTime>='" +
                           $"{scanFloor.ToUniversalTime():o}' and @SystemTime<'" +
                           $"{endDateExclusive.ToUniversalTime():o}']]]";
            var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };

            using (var reader = new EventLogReader(query))
            {
                for (EventRecord? record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        if (record.TimeCreated is not { } time || time < startDate || time >= endDateExclusive)
                        {
                            continue;
                        }

                        var entryType = EventRecordMapper.MapEntryType(record.Level, record.Keywords, isAuditChannel);
                        if (!ShouldInclude(policy, entryType, record.Id, securityExtraEventIds))
                        {
                            continue;
                        }

                        var mapped = EventRecordMapper.Map(record, logName, isAuditChannel);
                        if (mapped != null)
                        {
                            logs.Add(mapped);
                        }
                    }
                }
            }

            // 該來源目前實際保留、可回溯到的最早事件時間。若它仍晚於 scanFloor，代表保留的歷史
            // 不足以涵蓋整個請求區間——不是「這段期間真的沒事件」，是「較舊的事件已被系統覆蓋掉了」。
            // （classic 版用「倒序掃到 index 0 仍未低於 scanFloor」判斷，等價於 earliest > scanFloor。）
            DateTime? earliestAvailable = ReadEarliestTime(logName);
            bool complete = !(earliestAvailable != null && earliestAvailable > scanFloor);
            if (!complete)
            {
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
        catch (EventLogNotFoundException ex)
        {
            // 頻道在本機不存在（未安裝 Defender、未啟用 RDP 角色等）——屬預期，不是故障、不觸發 DataIncomplete。
            // 只記 Log（不印 console），由呼叫端彙總印出「頻道不存在」清單，避免平行掃描時多行交錯。
            Log.Info(ex, "{LogName} 頻道不存在（主機未安裝對應角色）", logName);
            return new EventLogScanResult { Entries = logs, Complete = true, ChannelMissing = true };
        }
        catch (Exception ex)
        {
            // 其他例外（含權限不足 UnauthorizedAccessException）：存在但讀不到，屬偵測盲區。
            Console.WriteLine($"讀取 {logName} Event Log 時發生錯誤（Security／部分 Operational 頻道需要以系統管理員執行）: {ex.Message}");
            Log.Warn(ex, "讀取 {LogName} Event Log 失敗", logName);
            return new EventLogScanResult { Entries = logs, Complete = false, ReadFailed = true };
        }
    }

    /// <summary>
    /// 取該日誌目前實際保留、可回溯到的最早事件時間（正向讀第一筆即最舊一筆，惰性讀取只讀一筆）。
    /// 空日誌回傳 null。
    /// </summary>
    private static DateTime? ReadEarliestTime(string logName)
    {
        var query = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = false };
        using var reader = new EventLogReader(query);
        using EventRecord? first = reader.ReadEvent();
        return first?.TimeCreated;
    }

    /// <summary>
    /// 傳統三個日誌（System/Application/Security）的 classic <see cref="EventLog"/> 掃描——
    /// 與遷移前完全相同，維持既有識別鍵（尤其 <c>Source</c> 為註冊短來源名），不斷舊 history.txt 趨勢。
    /// 見 <see cref="ClassicApiLogs"/> 的說明。
    /// </summary>
    private EventLogScanResult ScanRangeClassic(DateTime startDate, DateTime endDateExclusive, string logName, int[]? securityExtraEventIds = null)
    {
        var logs = new List<EventLogEntryData>();
        var policy = ChannelCatalog.Resolve(logName);

        try
        {
            Log.Debug("開始掃描 {LogName}（classic EventLog）：{Start:yyyy-MM-dd} ~ {End:yyyy-MM-dd}", logName, startDate, endDateExclusive);
            using var eventLog = new EventLog(logName);

            // Entries 大致依時間排序（index 0 = 最舊、Count-1 = 最新），倒序遍歷、早於區間起點即停止，
            // 避免掃整個日誌。時鐘被回撥時事件可能輕微亂序，多掃 1 小時緩衝才停，避免漏抓
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

                if (!ShouldInclude(policy, entry.EntryType, eventId, securityExtraEventIds))
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

    private static bool ShouldInclude(ChannelPolicy policy, EventLogEntryType type, int eventId, int[]? securityExtraEventIds)
    {
        // Critical 等級事件 EntryType 為 0（EventLogEntryType 列舉沒有 Critical 值，見 EventRecordMapper），
        // 這類事件最嚴重，所有頻道一律收，絕不能被過濾掉
        if (type is EventLogEntryType.Error or EventLogEntryType.Warning || (int)type == 0)
        {
            return true;
        }

        switch (policy.Kind)
        {
            case ChannelInclusionKind.SecurityAudit:
                if (type == EventLogEntryType.FailureAudit)
                {
                    return true;
                }
                if (type == EventLogEntryType.SuccessAudit)
                {
                    return KnownIssueCatalog.SecurityAuditWatchlist.Contains(eventId)
                           || (securityExtraEventIds != null && securityExtraEventIds.Contains(eventId));
                }
                return false;

            case ChannelInclusionKind.OperationalWatchlist:
                // Defender/RDP 的關鍵訊號多是 Information 等級（防護關閉、RDP 登入），靠 watchlist 精挑，
                // 避免把整個頻道的正常運作紀錄都吸進來（securityExtraEventIds 保留給條件式撈取的擴充路徑）。
                return KnownIssueCatalog.IsWatched(policy.ProviderProbe, eventId)
                       || (securityExtraEventIds != null && securityExtraEventIds.Contains(eventId));

            default: // ErrorWarningOnly：Information 一律不收（System/Application 既有行為）
                return false;
        }
    }
}
