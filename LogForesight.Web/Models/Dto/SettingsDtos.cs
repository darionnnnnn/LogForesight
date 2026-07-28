using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class SystemSettingsDto
{
    /// <summary>未處理計算納入的嚴重度（Critical/High/Medium/Low）</summary>
    public List<string> UnhandledSeverities { get; set; } = new();

    /// <summary>層級顯示模式：DefaultHidden／SiteHidden（見 SystemSettings.SeverityDisplayMode）</summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    public string AiBaseUrl { get; set; } = "";

    /// <summary>API 金鑰是否已設定；金鑰本身 write-only，絕不回傳明碼或密文</summary>
    public bool AiHasApiKey { get; set; }

    public int InitialHistoryDays { get; set; }

    public int RetentionDays { get; set; }

    /// <summary>執行歷程（批次執行紀錄／匯入紀錄）保留天數</summary>
    public int RunLogRetentionDays { get; set; }

    /// <summary>稽核紀錄保留天數</summary>
    public int AuditRetentionDays { get; set; }

    /// <summary>是否啟用 DB 設定的 AD 驗證（docs/HISTORY.md #9）</summary>
    public bool AdAuthEnabled { get; set; }

    public List<string> AdServers { get; set; } = new();

    public string AdSearchBase { get; set; } = "";

    public string AdSearchFilter { get; set; } = "";

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }
}

public class UpdateSystemSettingsRequest
{
    [Required(ErrorMessage = "請至少勾選一個未處理等級")]
    [MinLength(1, ErrorMessage = "請至少勾選一個未處理等級")]
    public List<string> UnhandledSeverities { get; set; } = new();

    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

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

    // ── AD 驗證（docs/HISTORY.md #9）────────────────────────────────

    public bool AdAuthEnabled { get; set; }

    public List<string> AdServers { get; set; } = new();

    [StringLength(500)]
    public string AdSearchBase { get; set; } = "";

    [StringLength(500)]
    public string AdSearchFilter { get; set; } = "";
}

/// <summary>
/// AD 測試連線（docs/HISTORY.md #9）：用管理者當場輸入的帳密，
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
