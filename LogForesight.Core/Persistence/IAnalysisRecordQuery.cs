namespace LogForesight;

/// <summary>
/// 查詢條件。全部欄位為選用（null／空 = 不限），組合起來就是 Web 的主篩選列
/// （主機／日期區間／風險層級／風險類型／Event ID）。
/// </summary>
public class RecordQueryFilter
{
    /// <summary>
    /// 限定主機識別（PK 為主、名稱為舊紀錄 fallback，比對規則見 <see cref="HostMatcher"/>）。
    /// **空集合 = 查不到任何資料**，不是「不限」——授權範圍為空的使用者必須得到空結果，
    /// 若把空集合當成不限，沒有任何授權的人反而看得到全部，那是最糟的失敗方向。
    /// null 才代表不限（僅供不需授權過濾的內部呼叫使用）。
    ///
    /// 一台主機可能對應多個識別（本身＋已併入它的墓碑列），由呼叫端以
    /// <see cref="HostIdentityResolver.Expand"/> 展開後傳入。
    /// </summary>
    public IReadOnlyCollection<HostKey>? Hosts { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    /// <summary>風險等級（高/中/低）</summary>
    public IReadOnlyCollection<string>? RiskLevels { get; set; }

    /// <summary>風險類型；紀錄中任一問題簽章屬於這些類別即命中</summary>
    public IReadOnlyCollection<IssueCategory>? Categories { get; set; }

    /// <summary>嚴重度；紀錄中任一問題簽章達到此嚴重度即命中（報表下鑽用）</summary>
    public IssueSeverity? MinSeverity { get; set; }

    /// <summary>指定 Event ID；紀錄中任一問題簽章的 EventId 相符即命中</summary>
    public int? EventId { get; set; }

    /// <summary>指定來源（搭配 EventId 做跨主機同簽章查詢）</summary>
    public string? Source { get; set; }
}

/// <summary>
/// 分析紀錄的查詢（Web 專用的讀取介面，↔ lf_daily_records 的查詢路徑）。
///
/// 與批次用的 <see cref="IAnalysisRecordReader"/> 刻意分開（ISP）：
/// 批次要的是「近 N 天」「這天有沒有紀錄」，Web 要的是多條件篩選與分頁，
/// 合成一個介面會讓兩邊都被迫依賴自己用不到的方法。
///
/// JSONL 後端讀 history.txt 後在記憶體篩選；SQL 後端轉成真正的查詢。
/// 語意（尤其是 <see cref="RecordQueryFilter.Hosts"/> 空集合的行為）由合約測試強制一致。
/// </summary>
public interface IAnalysisRecordQuery
{
    /// <summary>依條件查詢，依日期新到舊排序</summary>
    List<DailyAnalysisRecord> Query(RecordQueryFilter filter);

    /// <summary>
    /// 單筆紀錄（主機＋日期）；不存在回 null。
    /// 主機以識別集合表示——併入其他主機後同一台機器會有多個識別（本身＋墓碑），
    /// 依傳入順序擇一命中（呼叫端把存活主機排在最前，見 <see cref="HostIdentityResolver.Expand"/>）。
    /// </summary>
    DailyAnalysisRecord? GetOne(IReadOnlyCollection<HostKey> hosts, DateTime date);
}
