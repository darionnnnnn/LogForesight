using System.Globalization;

namespace LogForesight;

/// <summary>
/// <see cref="SentinelEvent"/>（<see cref="SentinelClient"/> 的原始投影）→ <see cref="EventLogEntryData"/>
/// （既有五層偵測/聚合/AI/報告吃的模型）的映射器（docs/archive/HISTORY.md 決策 B2）。
///
/// 這是整條 Sentinel 取數路徑唯一的轉換點：映射完成後，下游 <see cref="LogAggregator"/>／
/// <see cref="KnownIssueCatalog"/>／<see cref="TrendAnalyzer"/>／<see cref="CorrelationAnalyzer"/>／
/// AI 層／報告全部**零改動重用**——不需要為 Sentinel 路徑另外實作一套統計聚合。
/// </summary>
public static class SentinelEventMapper
{
    /// <summary>
    /// 映射單筆事件。時間或必要欄位缺席/無法解析時回傳 null，呼叫端應計入略過筆數
    /// （寧可少一筆也不要塞進假時間污染趨勢分析——同 <c>EventRecordMapper.Map</c> 對
    /// <c>TimeCreated is null</c> 的既有處理原則）。
    /// </summary>
    public static EventLogEntryData? Map(SentinelEvent evt)
    {
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

    /// <summary>批次映射，自動略過解析失敗的筆數；回傳映射結果與略過筆數，供呼叫端誠實申報
    /// （與既有 DataIncomplete／Truncated 的「資料不完整要說出來」原則一致）。</summary>
    public static (List<EventLogEntryData> Mapped, int SkippedCount) MapAll(IEnumerable<SentinelEvent> events)
    {
        var mapped = new List<EventLogEntryData>();
        var skipped = 0;

        foreach (var evt in events)
        {
            var entry = Map(evt);
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
