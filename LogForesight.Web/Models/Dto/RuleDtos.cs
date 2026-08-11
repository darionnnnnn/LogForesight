using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class RuleDto
{
    public string Id { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>'windows'（預設）| 'linux'——決定下面用哪組比對欄位（docs/LINUX-RULES.md）</summary>
    public string Platform { get; set; } = "windows";

    /// <summary>
    /// 比對順序（回饋十五輪 B-1）：同平台規則在儲存清單中的序位（1-based，含停用規則）——
    /// FindRule／FindLinuxRule 依清單順序取第一個命中的規則，這是唯讀顯示值，本頁不支援調整
    /// （新規則一律加在最後，見 RuleAdminService 的 Save 邏輯）。
    /// </summary>
    public int MatchOrder { get; set; }

    public string SourcePattern { get; set; } = string.Empty;
    public List<int> EventIds { get; set; } = new();
    public bool MatchAllEventIds { get; set; }

    // ── Linux 專用比對欄位 ─────────────────────────────────────────────
    public string ProgramPattern { get; set; } = string.Empty;
    public string EventNamePattern { get; set; } = string.Empty;
    public List<string> MessagePatterns { get; set; } = new();

    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;

    /// <summary>命中即列為高風險日（docs/archive/HISTORY.md #1，B1 三級化，取代原「嚴重」等級）</summary>
    public bool ElevatesDayRisk { get; set; }

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

    /// <summary>本機對此規則生效中的抑制設定——多筆並存時取「最寬範圍優先」的代表筆
    /// （回饋十五輪 R3，見 RuleAdminService.ScopeWidthRank），不是任意第一筆。</summary>
    public RuleSuppressionDto? Suppression { get; set; }

    /// <summary>這條規則目前生效中的抑制筆數。1 筆時就是 <see cref="Suppression"/> 本身；
    /// 大於 1 筆代表同時有多個範圍在抑制這條規則（如 Host＋Site 並存），畫面徽章需要誠實
    /// 顯示「已抑制 ×N」而不是讓人誤以為只抑制了一台。</summary>
    public int SuppressionCount { get; set; }

    /// <summary>抑制徽章 tooltip 用的前 3 筆明細（已按 <see cref="Suppression"/> 同一套「最寬範圍優先」
    /// 排序）；筆數更多時畫面提示「其餘 N 筆見『告警抑制』分頁」。</summary>
    public List<RuleSuppressionDto> SuppressionPreview { get; set; } = new();
}

public class SaveRuleRequest
{
    [Required(ErrorMessage = "請輸入規則 Id")]
    [StringLength(100)]
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>'windows'（預設）| 'linux'。是否合法、與其他欄位是否互相對應
    /// 由 RuleValidator 在儲存前把關（§1.3），這裡不重複驗證。</summary>
    public string Platform { get; set; } = "windows";

    [StringLength(255)]
    public string SourcePattern { get; set; } = string.Empty;

    public List<int> EventIds { get; set; } = new();
    public bool MatchAllEventIds { get; set; }

    [StringLength(255)]
    public string ProgramPattern { get; set; } = string.Empty;

    [StringLength(255)]
    public string EventNamePattern { get; set; } = string.Empty;

    public List<string> MessagePatterns { get; set; } = new();

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string Severity { get; set; } = string.Empty;

    /// <summary>命中即列為高風險日（docs/archive/HISTORY.md #1，B1 三級化，取代原「嚴重」等級）</summary>
    public bool ElevatesDayRisk { get; set; }

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

    /// <summary>Rule（預設）｜Signature｜Correlation｜Volume（回饋十五輪 A，見 SuppressionTargetTypes）</summary>
    public string TargetType { get; set; } = "Rule";

    /// <summary>TargetType=Signature 時的簽章鍵；其餘為 null</summary>
    public string? SignatureKey { get; set; }

    /// <summary>TargetType=Correlation 時的關聯模式 Id；其餘為 null</summary>
    public string? CorrelationPatternId { get; set; }

    /// <summary>TargetType=Volume 時的總量類別（error｜audit）；其餘為 null</summary>
    public string? VolumeKind { get; set; }

    /// <summary>非 Rule 目標的人話標籤，畫面直接顯示；TargetType=Rule 時為 null（用 RuleId 查）</summary>
    public string? TargetLabel { get; set; }

    /// <summary>Host（預設）｜Group｜Site（回饋十三輪 F，見 SuppressionScopes）</summary>
    public string Scope { get; set; } = "Host";

    /// <summary>Scope=Host 時的目標主機；其餘 Scope 為空字串</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Scope=Group 時的目標主機群組 Id；其餘 Scope 為 null</summary>
    public long? HostGroupId { get; set; }

    /// <summary>Scope=Group 時的群組名稱（由 HostGroupId 反查帶出，供畫面直接顯示）</summary>
    public string? HostGroupName { get; set; }

    public string Reason { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }

    /// <summary>平台（'windows'/'linux'）：TargetType=Rule 時由 RuleId 反查帶出；Signature／
    /// Correlation 由建立時記錄／模式 Id 推導；Volume 不分平台，恆為 null——
    /// 抑制清單依平台篩選、「抑制此規則」的主機下拉也依此過濾（docs/LINUX-RULES.md §5.1）</summary>
    public string? Platform { get; set; } = "windows";
}

public class AddSuppressionRequest
{
    /// <summary>Rule（預設，對應既有規則抑制路徑）｜Signature｜Correlation｜Volume
    /// （回饋十五輪 A，見 SuppressionTargetTypes）</summary>
    public string TargetType { get; set; } = "Rule";

    /// <summary>TargetType=Rule 時必填；經 POST /api/rules/{ruleId}/suppressions 呼叫時
    /// 由路由參數帶入，不需要在 body 重複填寫</summary>
    public string? RuleId { get; set; }

    /// <summary>TargetType=Signature 時必填（IssueSignatureKey.For 產生的鍵，由前端從
    /// 問題的 LogName/Source/EventId/EntryType 組出）</summary>
    public string? SignatureKey { get; set; }

    /// <summary>TargetType=Correlation 時必填，須為已知模式 Id（CorrelationPatternIds.All 之一）</summary>
    public string? CorrelationPatternId { get; set; }

    /// <summary>TargetType=Volume 時必填（error｜audit，見 VolumeKinds）</summary>
    public string? VolumeKind { get; set; }

    /// <summary>非 Rule 目標的人話標籤，管理頁直接顯示。Signature／Correlation 建議由前端帶入
    /// （前端當下有問題描述文字）；Volume 留空時後端自動帶入固定文字。</summary>
    public string? TargetLabel { get; set; }

    /// <summary>非 Rule 目標的平台（'windows'/'linux'），供清單頁篩選——Signature 由前端帶入
    /// （前端當下知道問題所屬主機的 OS）；Correlation／Volume 留空時後端自動推導或留空。</summary>
    public string? Platform { get; set; }

    /// <summary>Host（預設，對應既有欄位）｜Group｜Site（回饋十三輪 F）</summary>
    public string Scope { get; set; } = "Host";

    /// <summary>Scope=Host 時必填</summary>
    public string? Host { get; set; }

    /// <summary>Scope=Group 時必填</summary>
    public long? HostGroupId { get; set; }

    [Required(ErrorMessage = "請說明抑制原因")]
    [StringLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>null = 永久生效直到手動解除</summary>
    public int? Days { get; set; }
}

/// <summary>
/// Group／Site 抑制的送出前影響面預覽（回饋十四輪 C1）：一鍵讓一條規則在大量主機上噤聲，
/// 送出前先讓人看清楚規模。Host 範圍只影響單台主機，不需要這道關卡（見
/// <see cref="RuleAdminService.PreviewSuppression"/> 的守衛）。
/// </summary>
public class SuppressionPreviewDto
{
    /// <summary>此抑制範圍目前涵蓋的存活主機數</summary>
    public int AffectedHostCount { get; set; }

    /// <summary><see cref="WindowDays"/> 天內，這條規則在 <see cref="AffectedHostCount"/> 台主機上的命中次數合計</summary>
    public long RecentHitCount { get; set; }

    public int WindowDays { get; set; }

    /// <summary>true＝這條規則是 Linux 規則，<see cref="RecentHitCount"/> 是以「同來源程式」
    /// 合計而非精準對應這條規則（lf_top_issues 沒有存 Linux 的 EventKey，無法區分「同 program
    /// 命中不同規則」的情況），畫面應標註可能略高於實際數字</summary>
    public bool ApproximateForLinux { get; set; }
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

/// <summary>內建規則升級（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.9，承接 --import-rules）</summary>
public class RuleImportStatusDto
{
    public int CurrentSeedVersion { get; set; }
    public int LatestSeedVersion { get; set; }
    public bool HasUpdate { get; set; }
}

public class RuleImportItemDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>added | updated | skipped_unchanged | skipped_modified | conflict</summary>
    public string Action { get; set; } = string.Empty;

    public string ActionText { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public class RuleImportPreviewDto
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Conflicts { get; set; }
    public List<RuleImportItemDto> Items { get; set; } = new();
}

public class ApplyRuleImportRequest
{
    /// <summary>連同已修改的內建規則一併覆蓋（保留 Enabled 設定）</summary>
    public bool OverwriteBuiltin { get; set; }
}

public class RuleImportApplyResultDto
{
    public int Added { get; set; }
    public int Updated { get; set; }

    /// <summary>套用後重新驗證的警告（遮蔽偵測＋不合格被跳過的規則），非阻斷性</summary>
    public List<string> Warnings { get; set; } = new();
}
