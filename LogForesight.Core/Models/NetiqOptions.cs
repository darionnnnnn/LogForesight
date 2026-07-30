namespace LogForesight;

/// <summary>
/// NetIQ Sentinel 查詢的節流／逃生門設定（↔ webdata blob，key=netiq_options）。單一物件，非清單。
///
/// 與帳密事實來源（<see cref="ISentinelStore"/>）無關，是 <see cref="SentinelClient"/> 查詢行為的
/// 節流參數，由「系統管理 > NetIQ 維護」頁維護。取代原本寫死在批次 appsettings.json 的 NetIq 區段
/// （其 Servers 種子已改由 Web 直接在維護頁新增，appsettings 不再提供 NetIQ 相關設定）。
/// </summary>
public class NetiqOptions
{
    // SampleFetchMode（範例訊息 Q2 查詢範圍）已於 2026-07-29 退役：Phase 3 定案 msg 直接投影在
    // Q1 內（docs/NETIQ-API-PLAN.md §4），Q2 這類查詢不存在了，設定跟著失去所有行為消費端——
    // 「有設定無行為」是本專案紅線，故整欄移除。舊 blob 裡殘留的欄位值由 System.Text.Json
    // 反序列化時自動忽略，零遷移。

    /// <summary>每次 REST 呼叫（含建立/輪詢/翻頁）之間的節流間隔毫秒數。0 = 不節流。</summary>
    public int QueryDelayMs { get; set; } = 0;

    /// <summary>event-search job 的單頁筆數（對應 Sentinel API 的 <c>pgsize</c>）。</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>
    /// 單一 event-search job 最多回傳的事件筆數（對應 Sentinel API 的 <c>max-results</c>）。
    /// 異常爆量日（如遭大量暴力破解）不無限制拉取，超過時該批標記結果被截斷，
    /// 呼叫端比照 <c>DataIncomplete</c> 的基準排除邏輯處理。
    /// </summary>
    public int MaxResultsPerJob { get; set; } = 100_000;

    /// <summary>單次 REST 呼叫的逾時秒數。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>失敗重試次數（Polly：503／逾時／網路錯誤重試，4xx 不重試）。</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// true＝略過 Sentinel 憑證的驗證。Sentinel 常見以自簽憑證部署，這是顯式的逃生門
    /// （非靜默放行），啟用時每個 SentinelClient 建立時記一筆 WARN。預設 false（嚴格驗證）。
    /// </summary>
    public bool AllowInvalidCertificates { get; set; } = false;

    /// <summary>
    /// 每台主機每次執行最多回補幾天（docs/FEEDBACK-3-PLAN.md #1）。取代原本首次執行深度回補
    /// 14 天、非首次檢查 14 天內缺漏的雙軌設計——2000 台規模下對 Sentinel 做大量歷史日查詢
    /// 不現實，正式環境的預期行為是「只查前一天」。首次與非首次統一套用同一個值
    /// （<c>Math.Min(BackfillDays, trendWindowDays)</c>，見 NetiqPipelineService）。
    ///
    /// 預設 1 的取捨（顯式，非隱藏行為）：
    ///   - 缺漏日不自動自癒：排程漏跑的中間日子永遠不會補，主機時間軸的灰格與執行監控頁
    ///     仍會誠實顯示缺口；需要補歷史時，管理者可暫時調大這個值、跑一次、再調回來。
    ///   - 新主機的趨勢基準逐日累積：第一天沒有歷史可比對，TrendAnalyzer 對「無歷史」的
    ///     情況已有既定行為（與本機模式首次執行同款），不需要另外處理。
    /// </summary>
    public int BackfillDays { get; set; } = 1;

    /// <summary>
    /// 同時處理幾台 Sentinel（docs/FEEDBACK-3-PLAN.md #2）。各台 Sentinel 轄下主機互不重疊、
    /// 各自獨立的 <see cref="SentinelClient"/> 連線，跨台平行不破壞「同一台主機同一天內
    /// 依序處理」的趨勢比對前提（該限制只在單一主機內成立，一台主機只屬於一台 Sentinel）。
    /// 預設 2；設 1 等同完全依序處理（既有行為的逃生門）。上限 8——平行度越高，
    /// 單一 Sentinel 逾時／失敗互不拖累其他 Sentinel 的優勢越明顯，但同時發出的查詢
    /// 也越多，過高可能造成多台 Sentinel 同時被拖慢。
    /// </summary>
    public int MaxParallelServers { get; set; } = 2;

    /// <summary>
    /// 詢問 AI（實驗性）詢問當下是否向 Sentinel 即時查詢現場事件納入分析
    /// （docs/FEEDBACK-4-PLAN.md §5）。**預設 false**：對 Sentinel 的白天即時查詢負載
    /// 必須由管理者顯式開啟，不能因為 Web 加值功能就靜默對正式環境的 Sentinel 加壓。
    /// 只對 NetIQ 主機生效——本機直讀主機在 Web 端沒有即時讀取 Event Log 的路徑，
    /// 開關對它們沒有作用（前端不顯示任何取數跡象，不誤導）。
    /// </summary>
    public bool ChatLiveFetchEnabled { get; set; } = false;

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }
}
