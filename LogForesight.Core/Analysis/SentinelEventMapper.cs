using System.Globalization;
using System.Text.RegularExpressions;

namespace LogForesight.Core.Analysis;

/// <summary>
/// <see cref="SentinelEvent"/>（<see cref="SentinelClient"/> 的原始投影）→ <see cref="EventLogEntryData"/>
/// （既有五層偵測/聚合/AI/報告吃的模型）的映射器（docs/archive/HISTORY.md 決策 B2）。
///
/// 這是整條 Sentinel 取數路徑唯一的轉換點：映射完成後，下游 <see cref="LogAggregator"/>／
/// <see cref="KnownIssueCatalog"/>／<see cref="TrendAnalyzer"/>／<see cref="CorrelationAnalyzer"/>／
/// AI 層／報告全部**零改動重用**——不需要為 Sentinel 路徑另外實作一套統計聚合。
/// </summary>
internal static class SentinelEventMapper
{
    /// <summary>
    /// msg 前綴解析（Source 三段 fallback 鏈第三順位，docs/FEEDBACK-12-PLAN.md §4.4）：
    /// syslog 訊息常以 <c>program:</c> 或 <c>program[pid]:</c> 開頭（如 <c>kernel: sdh: sdh1</c>、
    /// <c>dbus[987]: [system] Successfully…</c>）。刻意要求緊接在行首、程式名後立即接
    /// （選填的）<c>[數字]</c> 再接冒號空白——像 <c>pam_unix(sshd:session): session opened…</c>
    /// 這種訊息本體內夾帶的偽冒號不會誤判成前綴（`(` 不在字元類別內，比對在那裡就會失敗），
    /// 「error: Received disconnect…」這類 sshd 自己訊息格式帶的字首才會被誤吃，但這條路本來
    /// 就是 sp／obssvcname 都缺席時的最後防線，不是主要取值來源（輪 B 實證受監控主機的
    /// 事件 sp 幾乎恆在）。
    /// </summary>
    private static readonly Regex LinuxMessagePrefixRegex =
        new(@"^(?<program>[A-Za-z0-9_.-]+)(?:\[\d+\])?:\s", RegexOptions.Compiled);

    /// <summary>
    /// 映射單筆事件。時間或必要欄位缺席/無法解析時回傳 null，呼叫端應計入略過筆數
    /// （寧可少一筆也不要塞進假時間污染趨勢分析——同 <c>EventRecordMapper.Map</c> 對
    /// <c>TimeCreated is null</c> 的既有處理原則）。
    /// </summary>
    /// <param name="os">依 <see cref="WebHost.OsLinux"/>／<c>OsWindows</c>
    /// 決定走哪條映射路徑；預設 windows（既有單一呼叫端在補上 Linux 分支前維持原行為）。</param>
    public static EventLogEntryData? Map(SentinelEvent evt, string os = WebHost.OsWindows)
    {
        if (os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase))
        {
            return MapLinux(evt);
        }

        var fields = evt.Fields;

        if (!fields.TryGetValue(SentinelFieldMap.Timestamp, out var rawTimestamp) ||
            !DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return null;
        }

        var logName = fields.GetValueOrDefault(SentinelFieldMap.LogName, string.Empty);
        var severity = ParseInt(fields, SentinelFieldMap.Severity);
        var xdasOutcome = fields.ContainsKey(SentinelFieldMap.XdasOutcome)
            ? ParseInt(fields, SentinelFieldMap.XdasOutcome)
            : (int?)null;
        var eventId = ParseInt(fields, SentinelFieldMap.EventId);

        var message = fields.GetValueOrDefault(SentinelFieldMap.Message);
        if (string.IsNullOrEmpty(message))
        {
            // msg 偶爾缺席（如純狀態通知類事件）時退回 evt（人看的事件名，繁中）——
            // 兩者皆缺才是真的沒有可顯示內容
            message = fields.GetValueOrDefault(SentinelFieldMap.EventName, string.Empty);
        }

        return new EventLogEntryData
        {
            // dt 是 UTC；轉本機時區（部署環境固定為 Asia/Taipei，estz 欄位佐證）與既有
            // EventRecordMapper 產出的 TimeGenerated（EventLogReader 回傳的本機時間）同一語意——
            // Program.cs 的日切界判定（TimeGenerated.Date vs DateTime.Today）依賴這個前提。
            TimeGenerated = timestamp.ToLocalTime().DateTime,
            EntryType = SentinelFieldMap.MapEntryType(logName, severity, xdasOutcome),
            LogName = logName,
            Source = fields.GetValueOrDefault(SentinelFieldMap.Source, string.Empty),
            Message = message,
            EventId = eventId,
            // InstanceId 只在 classic API 相容情境使用（history.txt 舊格式），Sentinel 路徑沒有
            // Qualifiers 概念，直接等於 EventId（同 EventRecordMapper 對此欄位的「僅相容性保留」定位）
            InstanceId = eventId
        };
    }

    /// <summary>
    /// Linux 事件映射（docs/FEEDBACK-12-PLAN.md §4.4，第二次 probe 修訂）：<c>Source</c> 走三段
    /// fallback 鏈——<see cref="SentinelFieldMap.LinuxProgram"/>（`sp`，受監控主機的主要路徑）→
    /// <see cref="SentinelFieldMap.LinuxObsSvcName"/>（`obssvcname`，CEF collector 路徑的備援）→
    /// <see cref="LinuxMessagePrefixRegex"/>（msg 前綴解析，最後防線）。三段皆解不出時回傳 null，
    /// 交給 <see cref="MapAll"/> 既有的略過計數機制申報（不新開一條計數路徑，見規劃「既有的解析
    /// 失敗計數」）——避免「program 靜默變空、全部聚成 Other」的降級不被看見。
    /// <c>EventId</c> 恆 0（Linux 事件沒有這個概念）、<c>LogName</c> 固定
    /// <see cref="LogAggregator.LinuxLogName"/>（"Linux"，下游 <see cref="LogAggregator"/> 依此
    /// 判定走 Linux 分路）。
    /// </summary>
    private static EventLogEntryData? MapLinux(SentinelEvent evt)
    {
        var fields = evt.Fields;

        if (!fields.TryGetValue(SentinelFieldMap.Timestamp, out var rawTimestamp) ||
            !DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return null;
        }

        var message = fields.GetValueOrDefault(SentinelFieldMap.Message, string.Empty);

        var source = fields.GetValueOrDefault(SentinelFieldMap.LinuxProgram, string.Empty);
        if (source.Length == 0)
        {
            source = fields.GetValueOrDefault(SentinelFieldMap.LinuxObsSvcName, string.Empty);
        }
        if (source.Length == 0 && message.Length > 0)
        {
            var match = LinuxMessagePrefixRegex.Match(message);
            if (match.Success)
            {
                source = match.Groups["program"].Value;
            }
        }
        if (source.Length == 0)
        {
            return null; // 三段皆解不出——計入 MapAll 的略過計數，不產出 Source 恆空的假資料
        }

        var severity = ParseInt(fields, SentinelFieldMap.Severity);

        return new EventLogEntryData
        {
            TimeGenerated = timestamp.ToLocalTime().DateTime,
            EntryType = SentinelFieldMap.MapEntryTypeLinux(severity),
            LogName = LogAggregator.LinuxLogName,
            Source = source,
            Message = message,
            EventId = 0,
            InstanceId = 0
        };
    }

    /// <summary>批次映射，自動略過解析失敗的筆數；回傳映射結果與略過筆數，供呼叫端誠實申報
    /// （與既有 DataIncomplete／Truncated 的「資料不完整要說出來」原則一致）。</summary>
    public static (List<EventLogEntryData> Mapped, int SkippedCount) MapAll(
        IEnumerable<SentinelEvent> events, string os = WebHost.OsWindows)
    {
        var mapped = new List<EventLogEntryData>();
        var skipped = 0;

        foreach (var evt in events)
        {
            var entry = Map(evt, os);
            if (entry != null)
            {
                mapped.Add(entry);
            }
            else
            {
                skipped++;
            }
        }

        return (mapped, skipped);
    }

    /// <summary>數字欄位的容錯讀取：缺席或無法解析一律回 0，不擲例外——探測環境的欄位形狀
    /// 仍可能有出入，解析失敗不該讓整批事件連帶報廢（同 SentinelClient.ReadInt 的既有慣例）。</summary>
    private static int ParseInt(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
