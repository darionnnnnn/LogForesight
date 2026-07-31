using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace LogForesight;

/// <summary>
/// 把新式 <see cref="EventRecord"/>（EventLogReader）映射成 <see cref="EventLogEntryData"/>，
/// **供新式 Operational 頻道（Defender/RDP）使用**——傳統三個日誌仍走 classic <see cref="System.Diagnostics.EventLog"/>
/// 以維持既有識別鍵（見 EventLogService.ClassicApiLogs）。純函數、無 I/O 副作用（<see cref="Map"/>
/// 讀取 record 屬性但不觸碰外部狀態），拆出來是為了讓 Level/Keywords → EntryType 的對應能被單元測試
/// 直接鎖住——這個對應決定 Operational 頻道事件在聚合鍵 (LogName, Source, EventId, EntryType) 與
/// 錯誤/警告/稽核計數中的分類，且保留 classic 的「Critical → 0」慣例讓兩條讀取路徑的計數邏輯一致。
/// </summary>
internal static class EventRecordMapper
{
    // 標準稽核 Keywords（winnt.h）：成功/失敗稽核。Security 事件的 Level 常為 0/4，
    // 不能用 Level 判斷成功或失敗，必須看 Keywords。
    private const long AuditSuccessKeyword = 0x20000000000000L;
    private const long AuditFailureKeyword = 0x10000000000000L;

    /// <summary>
    /// 映射單筆事件；<see cref="EventRecord.TimeCreated"/> 為 null 的極罕見事件回傳 null，呼叫端略過。
    /// </summary>
    public static EventLogEntryData? Map(EventRecord record, string logName, bool isAuditChannel)
    {
        if (record.TimeCreated is not { } time)
        {
            return null;
        }

        int eventId = record.Id;

        return new EventLogEntryData
        {
            TimeGenerated = time,
            EntryType = MapEntryType(record.Level, record.Keywords, isAuditChannel),
            LogName = logName,
            Source = record.ProviderName ?? string.Empty,
            Message = ReadMessage(record),
            // 重現 classic InstanceId 語意（Qualifiers 在高位、Event ID 在低 16 位）。
            // 此欄位不進任何識別鍵與簽章序列化，僅為相容性保留。
            InstanceId = ((long)(record.Qualifiers ?? 0) << 16) | (ushort)eventId,
            EventId = eventId
        };
    }

    /// <summary>
    /// Level/Keywords → legacy <see cref="EventLogEntryType"/>。
    /// 刻意保留 classic API 的「Critical → 0」慣例：EventLogEntryType 列舉沒有 Critical 值，
    /// classic 讀新式 Critical 事件（如 Kernel-Power 41）時 EntryType 就是 0，
    /// EventLogService.ShouldInclude、LogAnalysisService 的錯誤計數與顯示、history.txt 序列化都依賴它。
    /// </summary>
    public static EventLogEntryType MapEntryType(byte? level, long? keywords, bool isAuditChannel)
    {
        if (isAuditChannel && keywords is { } kw)
        {
            if ((kw & AuditFailureKeyword) != 0)
            {
                return EventLogEntryType.FailureAudit;
            }
            if ((kw & AuditSuccessKeyword) != 0)
            {
                return EventLogEntryType.SuccessAudit;
            }
        }

        return level switch
        {
            1 => (EventLogEntryType)0,          // Critical：沿用 classic 的無名稱值 0
            2 => EventLogEntryType.Error,
            3 => EventLogEntryType.Warning,
            _ => EventLogEntryType.Information   // 4(Info)/5(Verbose)/0/null
        };
    }

    /// <summary>
    /// 取事件訊息文字。讀取端缺 publisher metadata 時 <see cref="EventRecord.FormatDescription"/>
    /// 會回傳 null 或丟例外，退回把 Properties 值串起來——帳號/IP 等關鍵欄位仍保得住，
    /// 只是少了樣板文字（本機讀本機通常有 metadata，此 fallback 是防禦性設計）。
    /// </summary>
    private static string ReadMessage(EventRecord record)
    {
        try
        {
            var description = record.FormatDescription();
            if (!string.IsNullOrEmpty(description))
            {
                return description;
            }
        }
        catch (EventLogException)
        {
        }

        try
        {
            return string.Join("; ", record.Properties.Select(p => p.Value?.ToString() ?? string.Empty));
        }
        catch (EventLogException)
        {
            return string.Empty;
        }
    }
}
