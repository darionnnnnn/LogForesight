using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class SystemSettingsDto
{
    /// <summary>未處理計算納入的嚴重度（Critical/High/Medium/Low）</summary>
    public List<string> UnhandledSeverities { get; set; } = new();

    /// <summary>層級顯示模式：DefaultHidden／SiteHidden（見 SystemSettings.SeverityDisplayMode）</summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>顯示中的日風險等級（高/中/低，docs/archive/FEEDBACK-3-PLAN.md #8）——與問題嚴重度是不同的兩套層級</summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new();

    public string AiBaseUrl { get; set; } = "";

    /// <summary>API 金鑰是否已設定；金鑰本身 write-only，絕不回傳明碼或密文</summary>
    public bool AiHasApiKey { get; set; }

    public int InitialHistoryDays { get; set; }

    public int RetentionDays { get; set; }

    /// <summary>執行歷程（批次執行紀錄／匯入紀錄）保留天數</summary>
    public int RunLogRetentionDays { get; set; }

    /// <summary>稽核紀錄保留天數</summary>
    public int AuditRetentionDays { get; set; }

    /// <summary>風險 log 暫存保留天數（docs/archive/WEB-SCHEDULER-PLAN.md §2）</summary>
    public int RiskyEventRetentionDays { get; set; }

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
    public string AiExtraRequestFieldsJson { get; set; } = "";

    // ── 分析參數（§12：自 appsettings 的 Permissions／Analysis 區段遷入）──────────
    public List<string> WatchedFolders { get; set; } = new();
    public string ServerDescription { get; set; } = "";
    public int CheckupIntervalDays { get; set; }
    public List<string> AnalysisChannels { get; set; } = new();

    // ── 匯入限制（§12：自 appsettings 的 Import 區段遷入）────────────────────────
    public int ImportMaxFileSizeKb { get; set; }
    public int ImportMaxRows { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者時為 null，前端退回只顯示帳號</summary>
    public string? UpdatedByDisplayName { get; set; }
}

public class UpdateSystemSettingsRequest
{
    [Required(ErrorMessage = "請至少勾選一個未處理等級")]
    [MinLength(1, ErrorMessage = "請至少勾選一個未處理等級")]
    public List<string> UnhandledSeverities { get; set; } = new();

    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>顯示中的日風險等級（docs/archive/FEEDBACK-3-PLAN.md #8）；驗證要求必含「高」，
    /// 見 SystemSettingsService.Update</summary>
    public List<string> VisibleDayRiskLevels { get; set; } = new();

    /// <summary>空字串＝刻意停用 AI（設定頁明講「留空會停用」），所以不能標 [Required]——那會把空字串擋在驗證層</summary>
    [StringLength(500)]
    public string AiBaseUrl { get; set; } = "";

    /// <summary>write-only；留空＝沿用既有金鑰，要清除請另外傳 ClearAiApiKey=true</summary>
    [StringLength(500)]
    public string? AiApiKey { get; set; }

    public bool ClearAiApiKey { get; set; }

    [Range(1, 3650, ErrorMessage = "首次回補天數必須介於 1~3650 天")]
    public int InitialHistoryDays { get; set; }

    [Range(1, 3650, ErrorMessage = "歷史資料保留天數必須介於 1~3650 天")]
    public int RetentionDays { get; set; }

    [Range(7, 3650, ErrorMessage = "執行歷程保留天數必須介於 7~3650 天")]
    public int RunLogRetentionDays { get; set; }

    [Range(90, 3650, ErrorMessage = "稽核紀錄保留天數必須介於 90~3650 天")]
    public int AuditRetentionDays { get; set; }

    /// <summary>風險 log 暫存保留天數（docs/archive/WEB-SCHEDULER-PLAN.md §2）；上限交由
    /// SystemSettingsService.Update 對照 RetentionDays 驗證（暫存不可活得比分析紀錄久）</summary>
    [Range(1, 3650, ErrorMessage = "風險 log 暫存保留天數必須介於 1~3650 天")]
    public int RiskyEventRetentionDays { get; set; }

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

    // ── 匯入限制（§12）───────────────────────────────────────────────────────

    [Range(1, 102400, ErrorMessage = "匯入檔案大小上限必須介於 1~102400 KB")]
    public int ImportMaxFileSizeKb { get; set; }

    [Range(1, 1000000, ErrorMessage = "匯入資料列上限必須介於 1~1000000 列")]
    public int ImportMaxRows { get; set; }
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
}
