namespace LogForesight.Core.Models;

/// <summary>
/// 全站系統設定（↔ webdata blob，key=system_settings）。**單一物件，非清單**——全站只有一份。
///
/// 涵蓋原本分散在批次 appsettings.json（AI 位址）與程式碼寫死常數（未處理等級門檻、
/// 補充／留存天數）的可調整項目，改由「系統管理 > 設定」頁維護。所有欄位的預設值
/// 沿用原本的寫死行為，既有部署升級後行為不變，直到管理者主動調整。
/// </summary>
public class SystemSettings
{
    // ── 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）────────────────────────────────
    // 消費端＝_Layout.cshtml 與 Login.cshtml 的伺服器端渲染（不走前端 fetch：側欄品牌是
    // 每頁第一眼，等 API 回來才換會閃動）。

    /// <summary>側欄與登入頁的主標題。空字串＝回退預設值</summary>
    public string BrandName { get; set; } = "LogForesight";

    /// <summary>
    /// 主標題下方的副標題。預設**不含「Windows」**——Linux 規則面已就緒（docs/LINUX-RULES.md），
    /// 寫死 Windows 名不符實。空字串＝不顯示副標題（使用者可以刻意只留主標題）。
    /// </summary>
    public string BrandSubtitle { get; set; } = "事件日誌預警";

    /// <summary>
    /// 自訂品牌圖示，`data:image/png;base64,...`／`data:image/jpeg;base64,...`。
    /// 空字串＝沿用內建的 speedometer2 向量圖示。**只收點陣圖不收 SVG**：SVG 可內嵌 script，
    /// 即使以 img 載入不會執行，也不值得為單一裝飾性功能開這個驗證面。
    /// 大小上限由 SystemSettingsService 驗證（本設定整包是一份 blob，塞大圖會拖慢每次設定讀寫）。
    /// </summary>
    public string BrandIconDataUri { get; set; } = "";

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
    ///
    /// **例外**（回饋十三輪新增項3）：儀表板／報表的「風險類型」卡片（<see cref="RecordStatsBuilder.BuildCategoryCards"/>）
    /// 不論此欄位為何值，一律只計入 <see cref="UnhandledSeverities"/> 涵蓋的嚴重度——那兩張卡片
    /// 沒有像風險日詳情頁一樣「預設隱藏但按鈕可展開」的手動入口，DefaultHidden 模式繼續全部顯示的話，
    /// 使用者在這裡取消勾選的層級（預設不含 Low）會在卡片上原封不動地出現，讀起來像沒生效。
    /// </summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>
    /// 日風險等級顯示設定（docs/archive/FEEDBACK-3-PLAN.md #8）：與 <see cref="UnhandledSeverities"/>／
    /// <see cref="SeverityDisplayMode"/> 是完全不同的兩套層級——那兩個管的是掛在單一問題上的
    /// 嚴重度，這裡管的是「主機×日」整天的批次判定結果（<see cref="RiskLevels.High"/> 等）。
    /// 未勾選的等級整筆從查詢/統計消失（套用點見 RecordRepository，與問題嚴重度可見性同一個
    /// 咽喉——docs/archive/HISTORY.md S1）；風險日詳情直連與主機時間軸豁免不過濾（見
    /// RecordRepository 類別註解）。**「高」強制勾選**（SystemSettingsService.Update 驗證）：
    /// 全部隱藏會讓儀表板永遠空白，違背產品目的。
    /// 預設不含「低」（回饋十三輪新增項2）：低風險日佔絕大多數且通常不需要人工介入，
    /// 預設就顯示會把畫面上真正需要關注的高／中風險日稀釋掉——這是新安裝與缺欄位的舊部署
    /// 才會套用的出廠值，已明確儲存過設定的部署不受影響。
    /// </summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new() { "高", "中" };

    /// <summary>AI 提供者（Local／OpenAi／AzureOpenAi）。預設為 Local（本機 OpenAI 相容端點）</summary>
    public string AiProvider { get; set; } = Configuration.AiProviders.Local;

    /// <summary>AI（llama.cpp／OpenAI 相容端點／Azure 端點）位址。空字串＝AI 加值層與批次 AI 分析停用</summary>
    public string AiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>模型名稱。OpenAI 官方必填；本機預設為 local-model</summary>
    public string AiModel { get; set; } = "local-model";

    /// <summary>Azure OpenAI 部署名稱（deployment）</summary>
    public string AiAzureDeployment { get; set; } = "";

    /// <summary>Azure OpenAI API 版本，預設 2024-10-21</summary>
    public string AiAzureApiVersion { get; set; } = "2024-10-21";

    // ── AI 進階參數（§12，回饋第九輪：自 appsettings 的 Ai 區段遷入）──────────────────
    // 各欄位預設值＝原 appsettings.json 的出廠值，舊部署升級後行為不變（零遷移）。
    // 套用點：RuntimeSettingsResolver.ApplySystemSettingsOverrides（排程/立即執行每次重建設定）。

    /// <summary>單次 AI 呼叫逾時秒數（本機 27B 級模型單次回應可能需數分鐘）</summary>
    public int AiTimeoutSeconds { get; set; } = 1200;

    /// <summary>網路層失敗重試次數（Polly：連線失敗/HTTP 錯誤/逾時/空回應）</summary>
    public int AiRetryCount { get; set; } = 3;

    /// <summary>第一次重試的等待秒數，之後指數遞增（10 → 20 → 40）</summary>
    public int AiRetryDelaySeconds { get; set; } = 10;

    /// <summary>AI 回覆未通過 JSON 格式/內容檢查時的額外重問次數（不計入網路層重試）</summary>
    public int AiJsonRetryCount { get; set; } = 2;

    /// <summary>一般呼叫（每日總覽、前置掃描）的 token 上限，0＝不設上限</summary>
    public int AiMaxTokens { get; set; } = 2048;

    /// <summary>深入分析呼叫的 token 上限（天生比終端摘要長，故與 <see cref="AiMaxTokens"/> 分開）</summary>
    public int AiDeepDiveMaxTokens { get; set; } = 8192;

    /// <summary>頻率懲罰：抑制「同一段文字反覆重複」的退化輸出</summary>
    public double AiFrequencyPenalty { get; set; } = 0.8;

    /// <summary>存在懲罰：與頻率懲罰互補，一起抑制退化重複</summary>
    public double AiPresencePenalty { get; set; } = 0.8;

    /// <summary>
    /// 原封不動合併進 AI 請求 JSON 的額外欄位（JSON 物件文字）。伺服器不認得的欄位通常會被忽略，
    /// 換 server/模型時請對照對方啟動設定重新確認（本專案實測的 KoboldCpp 認 <c>rep_pen</c> 與
    /// 聊天範本的 <c>enable_thinking</c> 布林開關）。空字串＝不附加任何欄位。
    /// 存文字而非結構：這是「原樣轉發」的逃生門，值域由對方 server 決定，不該被我們的型別限制住。
    /// </summary>
    public string AiExtraRequestFieldsJson { get; set; } =
        """{"chat_template_kwargs":{"enable_thinking":false},"rep_pen":1.3}""";

    // ── 分析參數（§12：自 appsettings 的 Permissions／Analysis 區段遷入）─────────────

    /// <summary>額外要監控權限異動的資料夾（支援環境變數如 %ProgramFiles%）。
    /// 執行檔自身目錄一律自動監控，不需列在此。</summary>
    public List<string> WatchedFolders { get; set; } = new();

    /// <summary>伺服器角色描述，會帶入 prompt 讓 AI 依環境判讀。留空＝略過</summary>
    public string ServerDescription { get; set; } = "";

    /// <summary>體檢間隔天數（due-date 輪巡）：距上次體檢達此天數即到期</summary>
    public int CheckupIntervalDays { get; set; } = 7;

    /// <summary>要掃描的 Event Log 頻道全名清單。空清單＝使用預設六頻道</summary>
    public List<string> AnalysisChannels { get; set; } = new();

    // ── 權限異動欄位對應（自訂欄位名 → 語意角色）─────────────────────────────────

    /// <summary>
    /// 操作者帳號自訂欄位名清單（對應主體區段帳戶名稱）。事件訊息使用非標準欄位名時才需要設定；留空＝只用內建的官方欄位名。
    /// </summary>
    public List<string> PermissionOperatorFields { get; set; } = new();

    /// <summary>
    /// 成員帳號自訂欄位名清單（對應成員區段帳戶名稱）。事件訊息使用非標準欄位名時才需要設定；留空＝只用內建的官方欄位名。
    /// </summary>
    public List<string> PermissionMemberFields { get; set; } = new();

    /// <summary>
    /// 群組名稱自訂欄位名清單。事件訊息使用非標準欄位名時才需要設定；留空＝只用內建的官方欄位名。
    /// </summary>
    public List<string> PermissionGroupFields { get; set; } = new();

    /// <summary>
    /// 物件名稱自訂欄位名清單。事件訊息使用非標準欄位名時才需要設定；留空＝只用內建的官方欄位名。
    /// </summary>
    public List<string> PermissionObjectFields { get; set; } = new();

    // ── 匯入限制（§12：自 appsettings 的 Import 區段遷入）───────────────────────────

    /// <summary>CSV 匯入單檔大小上限（KB）</summary>
    public int ImportMaxFileSizeKb { get; set; } = 2048;

    /// <summary>CSV 匯入單檔資料列上限</summary>
    public int ImportMaxRows { get; set; } = 5000;

    /// <summary>
    /// AI API 金鑰的密文（<see cref="CryptoHelper.Encrypt"/> 產生）。地端無驗證的端點留空即可；
    /// 需驗證的雲端／內部代理端點才需要設定，發送時以 <c>Authorization: Bearer</c> 帶入。
    /// </summary>
    public string AiApiKeyEnc { get; set; } = "";

    /// <summary>保留天數類設定的共同下限：任何一種資料都至少留 90 天，
    /// 低於此值的設定在寫入時會被擋下（讀取端不 clamp——已儲存的舊值照舊生效）。</summary>
    public const int MinRetentionDays = 90;

    /// <summary>InitialHistoryDays 的出廠預設</summary>
    public const int DefaultInitialHistoryDays = 120;

    /// <summary>首次執行（歷史資料庫全空）時回補歷史的天數</summary>
    public int InitialHistoryDays { get; set; } = DefaultInitialHistoryDays;

    /// <summary>RetentionDays 的出廠預設——設定不可得時的 fallback 也引用這裡
    /// （HostVisibilityResolver／VisibilityService／RetentionOptions），改預設值只改這一處。</summary>
    public const int DefaultRetentionDays = 120;

    /// <summary>歷史資料庫保留天數（需 &gt;= InitialHistoryDays）</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// 執行歷程保留天數（批次執行紀錄／診斷、匯入紀錄——docs/WEB-SPEC.md §11-6、
    /// docs/archive/HISTORY.md P0-3）。與業務資料的 <see cref="RetentionDays"/> 分開，
    /// 因為兩者性質不同：這是「這次跑了什麼」的執行歷程，不是分析結果本身。
    /// </summary>
    public int RunLogRetentionDays { get; set; } = DefaultRunLogRetentionDays;

    /// <summary>RunLogRetentionDays 的出廠預設</summary>
    public const int DefaultRunLogRetentionDays = 90;

    /// <summary>
    /// 稽核紀錄保留天數（操作稽核——docs/WEB-SPEC.md §11-6、docs/archive/HISTORY.md P0-3）。
    /// 稽核是合規／追責用途，保留期通常比業務資料更長，故獨立設定。
    /// </summary>
    public int AuditRetentionDays { get; set; } = DefaultAuditRetentionDays;

    /// <summary>AuditRetentionDays 的出廠預設</summary>
    public const int DefaultAuditRetentionDays = 730;

    /// <summary>
    /// 風險 log 暫存保留天數（docs/archive/WEB-SCHEDULER-PLAN.md §2.2.3，2026-07-31 定案）：規則命中或
    /// 趨勢異常的問題簽章原始事件，暫存供「詢問 AI」對話優先取用，不必每次都即時打 Sentinel。
    /// 與業務資料的 <see cref="RetentionDays"/> 是不同性質的保留期（暫存 vs 長期分析紀錄），
    /// 驗證要求 <c>MinRetentionDays &lt;= 值 &lt;= RetentionDays</c>——暫存活得比分析紀錄久沒有意義。
    /// </summary>
    public int RiskyEventRetentionDays { get; set; } = DefaultRiskyEventRetentionDays;

    /// <summary>RiskyEventRetentionDays 的出廠預設（與下限同值屬巧合，兩者語意不同，不要互相引用）</summary>
    public const int DefaultRiskyEventRetentionDays = 90;

    /// <summary>
    /// 詳情保留天數：風險日詳情頁的原始內容（樣本訊息等）保留多久。
    /// 必須小於或等於 <see cref="RetentionDays"/>。
    /// </summary>
    public int DetailRetentionDays { get; set; } = DefaultDetailRetentionDays;

    /// <summary>DetailRetentionDays 的出廠預設</summary>
    public const int DefaultDetailRetentionDays = 120;

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

    // ── 郵件通知（回饋十五輪批次D）───────────────────────────────────────────
    // 全案原本零 SMTP 基礎設施（十四輪暫緩批次D）。三路觸發共用同一份收件人/門檻設定：
    // 排程結束後摘要、每日/每週定時彙總、高風險即時（不受時間限制，見 MailNotificationService）。

    /// <summary>總開關。關閉時三路觸發全部略過，不需要額外檢查各自的 Enabled——單一咽喉點</summary>
    public bool MailEnabled { get; set; }

    public string SmtpServer { get; set; } = "";

    public int SmtpPort { get; set; } = 25;

    public bool SmtpUseTls { get; set; }

    public string SmtpAccount { get; set; } = "";

    /// <summary>SMTP 密碼的密文（<see cref="CryptoHelper.Encrypt"/> 產生），write-only，
    /// 語意與 <see cref="AiApiKeyEnc"/> 完全對稱——內部/測試 relay 常不需要密碼，留空即可</summary>
    public string SmtpPasswordEnc { get; set; } = "";

    public string MailFrom { get; set; } = "";

    /// <summary>全域收件人清單（必填才能寄出任何通知）</summary>
    public List<string> MailRecipients { get; set; } = new();

    /// <summary>額外把通知寄給主機負責人（WebIdentity.Email）——與全域收件人清單疊加，不是取代</summary>
    public bool MailNotifyHostOwners { get; set; }

    /// <summary>摘要納入門檻：RiskLevels.High（預設）或 RiskLevels.Medium，低於此等級不計入摘要</summary>
    public string MailMinRiskLevel { get; set; } = RiskLevels.High;

    /// <summary>排程/手動觸發的執行結束後寄一封本次執行摘要</summary>
    public bool MailOnRunCompleted { get; set; }

    public bool MailDailyEnabled { get; set; }

    /// <summary>HH:mm，24 小時制</summary>
    public string MailDailyTime { get; set; } = "08:00";

    public bool MailWeeklyEnabled { get; set; }

    /// <summary>DayOfWeek 的英文名（Monday…Sunday）</summary>
    public string MailWeeklyDayOfWeek { get; set; } = "Monday";

    public string MailWeeklyTime { get; set; } = "08:00";

    /// <summary>高風險日不受每日/每週時刻限制，判定當下立即寄送（每 host+date 僅一次）</summary>
    public bool MailUrgentEnabled { get; set; }

    /// <summary>可用變數：{site} {host} {date} {risk} {type} {summary}，見 MailNotificationService</summary>
    public string MailSubjectTemplate { get; set; } = "[{site}] {type}：{date} {summary}";

    /// <summary>信件開頭可自訂的一段純文字，附加在自動組出的摘要表格之前</summary>
    public string MailBodyIntro { get; set; } = "";

    /// <summary>每日／週報彙總期間內無達門檻風險日時是否略過寄送（回饋十七輪批次A-4）。
    /// 預設 false（照寄）——「期間內無事」同時是系統存活訊號，沉默無法區分「沒事」與
    /// 「排程本身掛了」；使用者可主動關閉以減少噪音。</summary>
    public bool MailDigestSkipEmpty { get; set; }

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
