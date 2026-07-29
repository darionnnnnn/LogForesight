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

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }
}
