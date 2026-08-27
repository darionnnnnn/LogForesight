using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Models;

namespace LogForesight.Web.Models.Dto;

public class SystemSettingsDto
{
    // ── 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）──────────────────────────────
    public string BrandName { get; set; } = "";
    public string BrandSubtitle { get; set; } = "";

    /// <summary>自訂圖示的 data URI，空＝沿用內建向量圖示</summary>
    public string BrandIconDataUri { get; set; } = "";

    /// <summary>未處理計算納入的嚴重度（Critical/High/Medium/Low）</summary>
    public List<string> UnhandledSeverities { get; set; } = new();

    /// <summary>層級顯示模式：DefaultHidden／SiteHidden（見 SystemSettings.SeverityDisplayMode）</summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>顯示中的日風險等級（高/中/低，docs/archive/FEEDBACK-3-PLAN.md #8）——與問題嚴重度是不同的兩套層級</summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new();

    public string AiProvider { get; set; } = LogForesight.Core.Configuration.AiProviders.Local;
    public string AiBaseUrl { get; set; } = "";
    public string AiModel { get; set; } = "local-model";
    public string AiAzureDeployment { get; set; } = "";
    public string AiAzureApiVersion { get; set; } = "2024-10-21";

    /// <summary>API 金鑰是否已設定；金鑰本身 write-only，絕不回傳明碼或密文</summary>
    public bool AiHasApiKey { get; set; }

    public int InitialHistoryDays { get; set; }

    public int RetentionDays { get; set; }

    /// <summary>執行歷程（批次執行紀錄／匯入紀錄）保留天數</summary>
    public int RunLogRetentionDays { get; set; }

    /// <summary>稽核紀錄保留天數</summary>
    public int AuditRetentionDays { get; set; }

    /// <summary>原始事件內容保留天數（日紀錄原始樣本與風險 log 暫存）</summary>
    public int RawEventRetentionDays { get; set; }

    /// <summary>報告全文保留天數（資料庫裡的風險報告、體檢報告、權限異動報告）</summary>
    public int ReportRetentionDays { get; set; }

    /// <summary>目前已存的報告份數（設定頁的空間告知）</summary>
    public int ReportCount { get; set; }

    /// <summary>目前報告全文佔用的概略空間（MB，設定頁的空間告知）</summary>
    public double ReportSizeMb { get; set; }

    /// <summary>是否啟用 DB 設定的 AD 驗證（docs/archive/HISTORY.md #9）</summary>
    public bool AdAuthEnabled { get; set; }

    public List<string> AdServers { get; set; } = new();

    public string AdSearchBase { get; set; } = "";

    public string AdSearchFilter { get; set; } = "";

    // ── AI 進階參數（§12：自 appsettings 的 Ai 區段遷入）─────────────────────────
    public int AiTimeoutSeconds { get; set; }
    public int AiRetryCount { get; set; }
    public int AiRetryDelaySeconds { get; set; }
    public int AiJsonRetryCount { get; set; }
    public int AiMaxTokens { get; set; }
    public int AiDeepDiveMaxTokens { get; set; }
    public double AiFrequencyPenalty { get; set; }
    public double AiPresencePenalty { get; set; }

    /// <summary>估費單價（每百萬 token）；0＝不估費</summary>
    public double AiInputPricePerMillion { get; set; }
    public double AiOutputPricePerMillion { get; set; }
    public string AiExtraRequestFieldsJson { get; set; } = "";

    /// <summary>
    /// AI 進階參數的出廠預設值（docs/archive/FEEDBACK-10-PLAN.md §3）：供設定頁「還原預設值」按鈕填回欄位。
    /// 值取自 <c>new SystemSettings()</c> 的屬性初始器——**單一事實來源仍在 Core 的模型**，
    /// 前端不硬編第二份，改了預設值兩邊自動一致。
    /// </summary>
    public AiAdvancedDefaultsDto AiAdvancedDefaults { get; set; } = new();

    // ── 分析參數（§12：自 appsettings 的 Permissions／Analysis 區段遷入）──────────
    public List<string> WatchedFolders { get; set; } = new();
    public string ServerDescription { get; set; } = "";
    public int CheckupIntervalDays { get; set; }
    public List<string> AnalysisChannels { get; set; } = new();

    // ── 權限異動欄位對應（自訂欄位名 → 語意角色）─────────────────────────────────
    public List<string> PermissionOperatorFields { get; set; } = new();
    public List<string> PermissionMemberFields { get; set; } = new();
    public List<string> PermissionGroupFields { get; set; } = new();
    public List<string> PermissionObjectFields { get; set; } = new();

    // ── 匯入限制（§12：自 appsettings 的 Import 區段遷入）────────────────────────
    public int ImportMaxFileSizeKb { get; set; }
    public int ImportMaxRows { get; set; }

    // ── 郵件通知（回饋十五輪批次D）───────────────────────────────────────────
    public bool MailEnabled { get; set; }
    public string SmtpServer { get; set; } = "";
    public int SmtpPort { get; set; }
    public bool SmtpUseTls { get; set; }
    public string SmtpAccount { get; set; } = "";

    /// <summary>SMTP 密碼是否已設定；密碼本身 write-only，絕不回傳明碼或密文（同 AiHasApiKey 慣例）</summary>
    public bool SmtpHasPassword { get; set; }

    public string MailFrom { get; set; } = "";
    public List<string> MailRecipients { get; set; } = new();
    public bool MailNotifyHostOwners { get; set; }
    public string MailMinRiskLevel { get; set; } = "";
    public bool MailOnRunCompleted { get; set; }
    public bool MailDailyEnabled { get; set; }
    public string MailDailyTime { get; set; } = "";
    public bool MailWeeklyEnabled { get; set; }
    public string MailWeeklyDayOfWeek { get; set; } = "";
    public string MailWeeklyTime { get; set; } = "";
    public bool MailUrgentEnabled { get; set; }
    public string MailSubjectTemplate { get; set; } = "";
    public string MailBodyIntro { get; set; } = "";

    /// <summary>每日／週報彙總期間內無達門檻風險日時是否略過寄送（回饋十七輪批次A-4）</summary>
    public bool MailDigestSkipEmpty { get; set; }

    /// <summary>因連續寄送失敗達門檻而暫停寄送的收件人（回饋十七輪批次B-1）：
    /// 讓管理者看得到「為什麼這個人一直沒收到信」，通常代表地址打錯。</summary>
    public List<string> SuspendedMailRecipients { get; set; } = new();

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者時為 null，前端退回只顯示帳號</summary>
    public string? UpdatedByDisplayName { get; set; }
}

/// <summary>
/// AI 進階參數的出廠預設值（docs/archive/FEEDBACK-10-PLAN.md §3）。欄位與 <see cref="SystemSettingsDto"/>
/// 的九個 Ai* 進階欄位一一對應，只讀不寫——「還原」是前端把值填回表單，仍需使用者按儲存才生效
/// （與整頁單一 form 的語意一致，不做偷偷落盤）。
/// </summary>
public class AiAdvancedDefaultsDto
{
    public int TimeoutSeconds { get; set; }
    public int RetryCount { get; set; }
    public int RetryDelaySeconds { get; set; }
    public int JsonRetryCount { get; set; }
    public int MaxTokens { get; set; }
    public int DeepDiveMaxTokens { get; set; }
    public double FrequencyPenalty { get; set; }
    public double PresencePenalty { get; set; }
    public string ExtraRequestFieldsJson { get; set; } = "";
}

public class UpdateSystemSettingsRequest
{
    // ── 外觀／品牌（docs/archive/FEEDBACK-10-PLAN.md §1）──────────────────────────────
    // 長度與圖示格式的實質驗證在 SystemSettingsService.Update（要回可讀的繁中訊息、
    // 且圖示要驗 base64 解碼後的真實大小，DataAnnotations 表達不了）。

    /// <summary>空白＝回退出廠名稱，不是驗證錯誤</summary>
    public string BrandName { get; set; } = "";

    /// <summary>空＝不顯示副標（刻意只留產品名是合法選擇）</summary>
    public string BrandSubtitle { get; set; } = "";

    /// <summary>PNG／JPG 的 data URI；空＝沿用內建向量圖示</summary>
    public string BrandIconDataUri { get; set; } = "";

    [Required(ErrorMessage = "請至少勾選一個未處理等級")]
    [MinLength(1, ErrorMessage = "請至少勾選一個未處理等級")]
    public List<string> UnhandledSeverities { get; set; } = new();

    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>顯示中的日風險等級（docs/archive/FEEDBACK-3-PLAN.md #8）；驗證要求必含「高」，
    /// 見 SystemSettingsService.Update</summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new();

    public string AiProvider { get; set; } = LogForesight.Core.Configuration.AiProviders.Local;

    /// <summary>空字串＝刻意停用 AI（設定頁明講「留空會停用」），所以不能標 [Required]——那會把空字串擋在驗證層</summary>
    [StringLength(500)]
    public string AiBaseUrl { get; set; } = "";

    [StringLength(200)]
    public string AiModel { get; set; } = "";

    [StringLength(200)]
    public string AiAzureDeployment { get; set; } = "";

    [StringLength(100)]
    public string AiAzureApiVersion { get; set; } = "";

    /// <summary>write-only；留空＝沿用既有金鑰，要清除請另外傳 ClearAiApiKey=true</summary>
    [StringLength(500)]
    public string? AiApiKey { get; set; }

    public bool ClearAiApiKey { get; set; }

    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "首次回補天數必須介於 90~3650 天")]
    public int InitialHistoryDays { get; set; }

    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "歷史資料保留天數必須介於 90~3650 天")]
    public int RetentionDays { get; set; }

    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "執行歷程保留天數必須介於 90~3650 天")]
    public int RunLogRetentionDays { get; set; }

    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "稽核紀錄保留天數必須介於 90~3650 天")]
    public int AuditRetentionDays { get; set; }

    /// <summary>原始事件內容保留天數（日紀錄原始樣本與風險 log 暫存）；上限交由
    /// SystemSettingsService.Update 對照 RetentionDays 驗證</summary>
    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "原始事件內容保留天數必須介於 90~3650 天")]
    public int RawEventRetentionDays { get; set; }

    /// <summary>報告全文保留天數；上限交由 SystemSettingsService.Update 對照 RetentionDays 驗證
    /// （報告全文存在資料庫，超過歷史資料保留天數之後在站上已無入口可點）</summary>
    [Range(SystemSettings.MinRetentionDays, 3650, ErrorMessage = "報告保留天數必須介於 90~3650 天")]
    public int ReportRetentionDays { get; set; }

    // ── AD 驗證（docs/archive/HISTORY.md #9）────────────────────────────────

    public bool AdAuthEnabled { get; set; }

    public List<string> AdServers { get; set; } = new();

    [StringLength(500)]
    public string AdSearchBase { get; set; } = "";

    [StringLength(500)]
    public string AdSearchFilter { get; set; } = "";

    // ── AI 進階參數（§12）：範圍取自原 appsettings 各欄位的實務上下限 ─────────────

    [Range(1, 3600, ErrorMessage = "AI 逾時秒數必須介於 1~3600 秒")]
    public int AiTimeoutSeconds { get; set; }

    [Range(1, 10, ErrorMessage = "網路層重試次數必須介於 1~10（Polly 要求至少 1）")]
    public int AiRetryCount { get; set; }

    [Range(0, 300, ErrorMessage = "重試等待秒數必須介於 0~300 秒")]
    public int AiRetryDelaySeconds { get; set; }

    [Range(0, 10, ErrorMessage = "JSON 重問次數必須介於 0~10")]
    public int AiJsonRetryCount { get; set; }

    [Range(0, 131072, ErrorMessage = "一般呼叫 token 上限必須介於 0~131072（0＝不設上限）")]
    public int AiMaxTokens { get; set; }

    [Range(0, 131072, ErrorMessage = "深入分析 token 上限必須介於 0~131072（0＝不設上限）")]
    public int AiDeepDiveMaxTokens { get; set; }

    [Range(0, 2, ErrorMessage = "頻率懲罰必須介於 0~2")]
    public double AiFrequencyPenalty { get; set; }

    [Range(0, 2, ErrorMessage = "存在懲罰必須介於 0~2")]
    public double AiPresencePenalty { get; set; }

    /// <summary>估費單價：每百萬 input token 的金額，0＝不估費（回饋二十七輪作業 B）</summary>
    [Range(0, 1000000, ErrorMessage = "單價必須介於 0~1000000")]
    public double AiInputPricePerMillion { get; set; }

    /// <summary>估費單價：每百萬 output token 的金額，0＝不估費</summary>
    [Range(0, 1000000, ErrorMessage = "單價必須介於 0~1000000")]
    public double AiOutputPricePerMillion { get; set; }

    /// <summary>JSON 物件文字；格式由 SystemSettingsService.Update 驗證（空字串＝不附加任何欄位）</summary>
    [StringLength(4000)]
    public string AiExtraRequestFieldsJson { get; set; } = "";

    // ── 分析參數（§12）───────────────────────────────────────────────────────

    public List<string> WatchedFolders { get; set; } = new();

    [StringLength(500)]
    public string ServerDescription { get; set; } = "";

    [Range(1, 365, ErrorMessage = "體檢間隔天數必須介於 1~365 天")]
    public int CheckupIntervalDays { get; set; }

    /// <summary>空清單＝使用預設六頻道；頻道名可解析性由 SystemSettingsService.Update 驗證</summary>
    public List<string> AnalysisChannels { get; set; } = new();

    // ── 權限異動欄位對應（自訂欄位名 → 語意角色）─────────────────────────────────
    public List<string> PermissionOperatorFields { get; set; } = new();
    public List<string> PermissionMemberFields { get; set; } = new();
    public List<string> PermissionGroupFields { get; set; } = new();
    public List<string> PermissionObjectFields { get; set; } = new();

    // ── 匯入限制（§12）───────────────────────────────────────────────────────

    [Range(1, 102400, ErrorMessage = "匯入檔案大小上限必須介於 1~102400 KB")]
    public int ImportMaxFileSizeKb { get; set; }

    [Range(1, 1000000, ErrorMessage = "匯入資料列上限必須介於 1~1000000 列")]
    public int ImportMaxRows { get; set; }

    // ── 郵件通知（回饋十五輪批次D）───────────────────────────────────────────
    // 實質驗證（收件人格式、時刻格式、SMTP 必填組合）在 SystemSettingsService.Update——
    // 與其他區段同一慣例，DataAnnotations 只擋得住型別層級的粗淺檢查。

    public bool MailEnabled { get; set; }

    [StringLength(255)]
    public string SmtpServer { get; set; } = "";

    [Range(1, 65535, ErrorMessage = "SMTP Port 必須介於 1~65535")]
    public int SmtpPort { get; set; } = 25;

    public bool SmtpUseTls { get; set; }

    [StringLength(255)]
    public string SmtpAccount { get; set; } = "";

    /// <summary>write-only；留空＝沿用既有密碼，要清除請另外傳 ClearSmtpPassword=true（同 AI 金鑰慣例）</summary>
    [StringLength(255)]
    public string? SmtpPassword { get; set; }

    public bool ClearSmtpPassword { get; set; }

    [StringLength(255)]
    public string MailFrom { get; set; } = "";

    public List<string> MailRecipients { get; set; } = new();

    public bool MailNotifyHostOwners { get; set; }

    public string MailMinRiskLevel { get; set; } = "高";

    public bool MailOnRunCompleted { get; set; }

    public bool MailDailyEnabled { get; set; }

    public string MailDailyTime { get; set; } = "08:00";

    public bool MailWeeklyEnabled { get; set; }

    public string MailWeeklyDayOfWeek { get; set; } = "Monday";

    public string MailWeeklyTime { get; set; } = "08:00";

    public bool MailUrgentEnabled { get; set; }

    [StringLength(255)]
    public string MailSubjectTemplate { get; set; } = "[{site}] {type}：{date} {summary}";

    [StringLength(2000)]
    public string MailBodyIntro { get; set; } = "";

    public bool MailDigestSkipEmpty { get; set; }
}

/// <summary>測試寄信（設定頁「測試寄信」鈕）：用表單目前值（可能還沒儲存）試寄一封，
/// 密碼留空時沿用已儲存的密文（與正式設定同一份 write-only 語意，測試不該逼使用者重打密碼）。</summary>
public class TestMailRequest
{
    [Required(ErrorMessage = "請輸入 SMTP 伺服器")]
    public string SmtpServer { get; set; } = "";

    [Range(1, 65535, ErrorMessage = "SMTP Port 必須介於 1~65535")]
    public int SmtpPort { get; set; } = 25;

    public bool SmtpUseTls { get; set; }

    public string SmtpAccount { get; set; } = "";

    /// <summary>留空＝沿用已儲存的密碼</summary>
    public string? SmtpPassword { get; set; }

    [Required(ErrorMessage = "請輸入寄件人")]
    public string MailFrom { get; set; } = "";

    [Required(ErrorMessage = "請輸入至少一位收件人")]
    [MinLength(1, ErrorMessage = "請輸入至少一位收件人")]
    public List<string> Recipients { get; set; } = new();

    public string SubjectTemplate { get; set; } = "";

    public string BodyIntro { get; set; } = "";
}

public class TestMailResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// AD 測試連線（docs/archive/HISTORY.md #9）：用管理者當場輸入的帳密，
/// 對表單目前填的伺服器清單試 bind（不需先儲存）。密碼不落盤、不進稽核 detail。
/// </summary>
public class TestAdConnectionRequest
{
    [Required(ErrorMessage = "請至少輸入一台 AD 伺服器")]
    [MinLength(1, ErrorMessage = "請至少輸入一台 AD 伺服器")]
    public List<string> Servers { get; set; } = new();

    public string? SearchBase { get; set; }

    public string? SearchFilter { get; set; }

    [Required(ErrorMessage = "請輸入測試用帳號")]
    public string Account { get; set; } = "";

    [Required(ErrorMessage = "請輸入測試用密碼")]
    public string Password { get; set; } = "";
}

public class TestAdConnectionResultDto
{
    public bool Success { get; set; }

    /// <summary>這裡是管理者對自己測試，可以顯示細節（與登入失敗一律「帳號或密碼錯誤」不同）</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 顯示層設定的公開子集（docs/archive/FEEDBACK-3-PLAN.md #8）：完整設定 API 需要 Maintain 能力，
/// 但「哪些日風險等級目前顯示」是任何已登入者的前端都要知道的資訊（用來決定 KPI 卡、
/// 趨勢線、篩選 chips 要不要出現），比照 HostsController 無 [Permission] 標註的先例——
/// 答案本身就是顯示範圍，不是需要額外授權才能問的問題。
/// </summary>
public class DisplaySettingsDto
{
    public List<string> VisibleDayRiskLevels { get; set; } = new();

    /// <summary>顯示中的問題嚴重度清單（回饋二十輪 B2，SiteHidden 模式下的可見嚴重度；未限制時為完整清單）</summary>
    public List<string> VisibleSeverities { get; set; } = new();
}
