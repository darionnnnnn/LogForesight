namespace LogForesight;

/// <summary>
/// 全站系統設定（↔ webdata blob，key=system_settings）。**單一物件，非清單**——全站只有一份。
///
/// 涵蓋原本分散在批次 appsettings.json（AI 位址）與程式碼寫死常數（未處理等級門檻、
/// 補充／留存天數）的可調整項目，改由「系統管理 > 設定」頁維護。所有欄位的預設值
/// 沿用原本的寫死行為，既有部署升級後行為不變，直到管理者主動調整。
/// </summary>
public class SystemSettings
{
    /// <summary>
    /// 未處理計算納入哪些嚴重度（<see cref="IssueSeverity"/> 名稱：High/Medium/Low——
    /// docs/archive/HISTORY.md #1，B1 三級化後 Critical 不再是可選層級，
    /// 舊部署存的 "Critical" 由 <see cref="ParseUnhandledSeverities"/> 讀取時正規化為 "High"）。
    /// 未列在此清單的嚴重度，若問題未被明確標記，視同「不處理（預設）」，不計入未處理／待辦統計
    /// （一般化原本寫死「Low 一律預設不處理」的規則）。
    /// </summary>
    public List<string> UnhandledSeverities { get; set; } = new() { "High", "Medium" };

    /// <summary>
    /// 層級顯示模式，決定 <see cref="UnhandledSeverities"/> 以外的嚴重度（問題嚴重度，
    /// 非日風險等級）在全站如何呈現：<c>DefaultHidden</c>（預設隱藏但可手動開啟，純顯示層行為）／
    /// <c>SiteHidden</c>（全站查詢層直接排除——詳情頁、AI 對話、儀表板、報表、問題查詢皆同一套過濾，
    /// 見 docs/archive/HISTORY.md S1、RecordRepository）。
    /// 舊值 <c>Locked</c>／<c>GlobalFilter</c>（docs/archive/HISTORY.md #5 簡化前的三模式設計）
    /// 讀取時正規化為 <c>SiteHidden</c>，見 SystemSettingsService.NormalizeDisplayMode。
    /// 不影響風險等級判定與報告全文——那是批次時已算定的證據層，不受顯示設定影響。
    /// </summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>
    /// 日風險等級顯示設定（docs/archive/FEEDBACK-3-PLAN.md #8）：與 <see cref="UnhandledSeverities"/>／
    /// <see cref="SeverityDisplayMode"/> 是完全不同的兩套層級——那兩個管的是掛在單一問題上的
    /// 嚴重度，這裡管的是「主機×日」整天的批次判定結果（<see cref="RiskLevels.High"/> 等）。
    /// 未勾選的等級整筆從查詢/統計消失（套用點見 RecordRepository，與問題嚴重度可見性同一個
    /// 咽喉——docs/archive/HISTORY.md S1）；風險日詳情直連與主機時間軸豁免不過濾（見
    /// RecordRepository 類別註解）。**「高」強制勾選**（SystemSettingsService.Update 驗證）：
    /// 全部隱藏會讓儀表板永遠空白，違背產品目的。舊部署缺這個欄位時預設全顯示——
    /// 風險日判定本身（批次算定的證據層）不受影響，只是顯示範圍，行為不變直到有人主動改設定。
    /// </summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new() { "高", "中", "低" };

    /// <summary>AI（llama.cpp／OpenAI 相容端點）位址。空字串＝AI 加值層與批次 AI 分析停用</summary>
    public string AiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// AI API 金鑰的密文（<see cref="CryptoHelper.Encrypt"/> 產生）。地端無驗證的端點留空即可；
    /// 需驗證的雲端／內部代理端點才需要設定，發送時以 <c>Authorization: Bearer</c> 帶入。
    /// </summary>
    public string AiApiKeyEnc { get; set; } = "";

    /// <summary>首次執行（歷史資料庫全空）時回補歷史的天數</summary>
    public int InitialHistoryDays { get; set; } = 120;

    /// <summary>歷史資料庫保留天數（需 &gt;= InitialHistoryDays）</summary>
    public int RetentionDays { get; set; } = 120;

    /// <summary>
    /// 執行歷程保留天數（批次執行紀錄／診斷、匯入紀錄——docs/WEB-SPEC.md §11-6、
    /// docs/archive/HISTORY.md P0-3）。與業務資料的 <see cref="RetentionDays"/> 分開，
    /// 因為兩者性質不同：這是「這次跑了什麼」的執行歷程，不是分析結果本身。
    /// </summary>
    public int RunLogRetentionDays { get; set; } = 90;

    /// <summary>
    /// 稽核紀錄保留天數（操作稽核——docs/WEB-SPEC.md §11-6、docs/archive/HISTORY.md P0-3）。
    /// 稽核是合規／追責用途，保留期通常比業務資料更長，故獨立設定。
    /// </summary>
    public int AuditRetentionDays { get; set; } = 730;

    /// <summary>
    /// 風險 log 暫存保留天數（docs/archive/WEB-SCHEDULER-PLAN.md §2.2.3，2026-07-31 定案）：規則命中或
    /// 趨勢異常的問題簽章原始事件，暫存供「詢問 AI」對話優先取用，不必每次都即時打 Sentinel。
    /// 與業務資料的 <see cref="RetentionDays"/> 是不同性質的保留期（暫存 vs 長期分析紀錄），
    /// 驗證要求 <c>1 &lt;= 值 &lt;= RetentionDays</c>——暫存活得比分析紀錄久沒有意義。
    /// </summary>
    public int RiskyEventRetentionDays { get; set; } = 14;

    /// <summary>
    /// 是否啟用 DB 設定的 AD 驗證（docs/archive/HISTORY.md #9）。開啟後不論 appsettings 的
    /// Auth:Provider 是 Stub 或 Ldap，一律改用 <see cref="AdServers"/> 等 DB 設定連線驗證——
    /// 這正是「測試模式（Stub）開啟後也走 AD 驗證」的開關，見 DynamicAuthenticationProvider。
    /// 不儲存任何 AD 服務帳號密碼：bind 一律用登入者自己的帳密，沒有新機密要保管。
    /// </summary>
    public bool AdAuthEnabled { get; set; }

    /// <summary>AD 伺服器清單（IP 或 LDAP URL），依序嘗試第一個可連線者</summary>
    public List<string> AdServers { get; set; } = new();

    /// <summary>查詢基準 DN，選填（如 DC=corp,DC=com）；留空使用目錄預設根目錄</summary>
    public string AdSearchBase { get; set; } = "";

    /// <summary>查詢使用者的過濾器樣板，{0} 代入登入帳號</summary>
    public string AdSearchFilter { get; set; } = "(sAMAccountName={0})";

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }

    /// <summary>
    /// <see cref="UnhandledSeverities"/> 解析成 <see cref="IssueSeverity"/> 集合，供
    /// <c>DayHandlingDerivation.Derive</c> 與問題明細的預設不處理判定共用。無法解析的字串（設定損毀）
    /// 靜默略過，不讓整個未處理計算因為一個壞字串而掛掉。
    /// 舊資料相容（docs/archive/HISTORY.md #1，B1 三級化）：既有設定中殘留的 "Critical"
    /// 正規化為 "High" 再解析——三級化後問題嚴重度不會再產生 Critical，讀取時静默轉換一次，
    /// 既有部署不需要手動改設定。
    /// </summary>
    public HashSet<IssueSeverity> ParseUnhandledSeverities() =>
        UnhandledSeverities
            .Select(s => s == "Critical" ? "High" : s)
            .Select(s => Enum.TryParse<IssueSeverity>(s, ignoreCase: true, out var severity) ? severity : (IssueSeverity?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToHashSet();
}
