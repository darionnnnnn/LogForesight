using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class RuleDto
{
    public string Id { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string SourcePattern { get; set; } = string.Empty;
    public List<int> EventIds { get; set; } = new();
    public bool MatchAllEventIds { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int CountThreshold { get; set; }

    public string PlainExplanation { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();

    /// <summary>true = 這條 builtin 規則被人改過（清單顯示「已修改」徽章）</summary>
    public bool IsModified { get; set; }
    public string? ModifiedByName { get; set; }
    public DateTime? ModifiedAt { get; set; }

    /// <summary>true = 內建種子有比目前版本更新的內容可回復（程式改版後出現）</summary>
    public bool SeedHasNewerVersion { get; set; }

    /// <summary>true = 有原廠種子可回復（builtin 且鏡像存在）</summary>
    public bool CanRestore { get; set; }

    /// <summary>true = 可刪除（僅 custom 規則）</summary>
    public bool CanDelete { get; set; }

    /// <summary>本機對此規則生效中的抑制設定</summary>
    public RuleSuppressionDto? Suppression { get; set; }
}

public class SaveRuleRequest
{
    [Required(ErrorMessage = "請輸入規則 Id")]
    [StringLength(100)]
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    [Required(ErrorMessage = "請輸入來源比對字串")]
    [StringLength(255)]
    public string SourcePattern { get; set; } = string.Empty;

    public List<int> EventIds { get; set; } = new();
    public bool MatchAllEventIds { get; set; }

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string Severity { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入規則說明")]
    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    [Range(1, 100000)]
    public int CountThreshold { get; set; } = 1;

    public string PlainExplanation { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

public class SetRuleEnabledRequest
{
    public bool Enabled { get; set; }
}

/// <summary>儲存前驗證的結果——不合格時逐條回報，不寫入任何資料</summary>
public class RuleValidationDto
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();

    /// <summary>不阻擋但需要注意的事項（如規則被前面的規則遮蔽，永遠不會命中）</summary>
    public List<string> Warnings { get; set; } = new();
}

public class RuleSuppressionDto
{
    public string RuleId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
}

public class AddSuppressionRequest
{
    [Required]
    public string Host { get; set; } = string.Empty;

    [Required(ErrorMessage = "請說明抑制原因")]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>null = 永久生效直到手動解除</summary>
    public int? Days { get; set; }
}

/// <summary>規則的回復預設預覽：目前內容 vs 原廠種子</summary>
public class RuleRestorePreviewDto
{
    public RuleDto Current { get; set; } = new();
    public RuleDto Seed { get; set; } = new();
    public List<RuleFieldDiffDto> Differences { get; set; } = new();
}

public class RuleFieldDiffDto
{
    public string Field { get; set; } = string.Empty;
    public string Current { get; set; } = string.Empty;
    public string Seed { get; set; } = string.Empty;
}
