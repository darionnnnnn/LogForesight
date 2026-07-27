using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class SystemSettingsDto
{
    /// <summary>未處理計算納入的嚴重度（Critical/High/Medium/Low）</summary>
    public List<string> UnhandledSeverities { get; set; } = new();

    public string AiBaseUrl { get; set; } = "";

    /// <summary>API 金鑰是否已設定；金鑰本身 write-only，絕不回傳明碼或密文</summary>
    public bool AiHasApiKey { get; set; }

    public int InitialHistoryDays { get; set; }

    public int RetentionDays { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }
}

public class UpdateSystemSettingsRequest
{
    [Required(ErrorMessage = "請至少勾選一個未處理等級")]
    [MinLength(1, ErrorMessage = "請至少勾選一個未處理等級")]
    public List<string> UnhandledSeverities { get; set; } = new();

    [Required]
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
}
