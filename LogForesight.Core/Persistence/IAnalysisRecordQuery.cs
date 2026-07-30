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
/// 語意（尤其是 <see cref="RecordQueryFilter.Hosts"/> 空集合的行為）由合約測試強制一致，
/// Sqlite 與 SqlServer 兩個 provider 跑同一組案例。
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

    /// <summary>
    /// 分頁查詢（docs/HISTORY.md P1-2），依「風險等級→有無關聯訊號→日期」新到舊排序——
    /// 與 <c>RecordQueryService.Search</c> 清單頁的排序同一套規則。
    ///
    /// 與 <see cref="Query"/> 不同：<see cref="Query"/> 撈回全部符合條件的紀錄（批次與不分頁呼叫端用），
    /// 這個方法在條件允許時直接在 SQL 端排序＋分頁，不必先把全部符合條件的紀錄撈進記憶體——
    /// 這是 2000 台規模下清單頁效能的關鍵。若篩選條件裡含有無法下推的殘餘（見
    /// <see cref="EfAnalysisRecordStore"/> 的實作說明），會退回「SQL 過濾＋整批撈回→記憶體排序與分頁」，
    /// 正確性優先，退化的只有效能。
    ///
    /// sortKey（選填，表格表頭排序，docs/WEB-SPEC.md §9.2）：date | host | risk。
    /// null／不合法值＝維持預設的「風險→關聯→日期」緊急程度排序（既有行為不變）。
    /// </summary>
    PagedResult<DailyAnalysisRecord> QueryPage(RecordQueryFilter filter, int page, int pageSize, string? sortKey = null, bool ascending = false);

    /// <summary>
    /// 窗口內「哪個主機（HostId）、哪一天有分析紀錄」的輕量投影（執行監控頁 NetIQ 主機專用，
    /// docs/WEB-SPEC.md §9.10）。NetIQ 主機沒有個別的 <c>lf_batch_runs</c> 紀錄（管線只用主批次
    /// 那一筆彙總計數，見 <c>NetiqPipelineService</c>），「當日是否有分析紀錄」是唯一可用的執行代理指標。
    /// 只投影 HostId／RecordDate，不反序列化整份 ContentJson——避免逐格查詢在兩千台規模下退化成
    /// 全量載入。HostId=0（舊列／無主機識別）不列入，NetIQ 紀錄必有真實 HostId。
    /// </summary>
    HashSet<(long HostId, DateTime Date)> ListHostDates(DateTime from, DateTime to);
}
