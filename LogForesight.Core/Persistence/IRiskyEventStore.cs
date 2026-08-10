namespace LogForesight.Core.Persistence;

/// <summary>
/// 風險 log 暫存讀寫（↔ lf_risky_events，docs/archive/WEB-SCHEDULER-PLAN.md §2）。
/// 批次寫、Web（AI 對話）讀；資格與呈現量上限見 <see cref="RiskyEventSelector"/>。
/// </summary>
public interface IRiskyEventStore
{
    /// <summary>
    /// 覆寫「這台主機這一天」的暫存內容：先刪舊列再整批寫入。與
    /// <see cref="IAnalysisRecordReader.HasRecord"/> 的缺日回補機制配合——正常不會重寫，
    /// 但寫入本身天生冪等，重跑同一天不會殘留舊列。
    /// </summary>
    void ReplaceDay(long hostId, DateTime date, List<RiskyEvent> events);

    /// <summary>
    /// 依主機＋日期＋簽章（Source/EventId）查詢，依事件時間新到舊排序，取前 <paramref name="maxResults"/> 筆。
    /// 查無資格內的簽章或已超出保留天數時回空清單（呼叫端據此 fallback 即時查詢）。
    /// </summary>
    List<RiskyEvent> Query(long hostId, DateTime date, string source, int eventId, int maxResults);

    /// <summary>
    /// 依主機＋日期取整日暫存（回饋十三輪 C，體檢批 0 #6：孤兒補跑產報告）——不分簽章，一次拿回
    /// 這一天寫入的全部列（每主機日至多 <see cref="Analysis.RiskyEventSelector.MaxPerHostDay"/> 筆）。
    /// 與 <see cref="Query"/> 的差異：<c>Query</c> 是「問 AI」對話依單一簽章逐次取用的既有介面，
    /// 這裡是孤兒補跑要重建整份報告用的原始 log，需要一次拿到當天所有簽章的暫存內容。
    /// 查無資料（超出保留期或本來就沒寫入）回空清單。
    /// </summary>
    List<RiskyEvent> QueryDay(long hostId, DateTime date);

    /// <summary>清除超過保留天數的暫存（以 <see cref="RiskyEvent.Date"/> 為基準，非寫入時間），回傳清除筆數</summary>
    int Prune(int retentionDays);
}
