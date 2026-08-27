using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

/// <summary>問題檔案（回饋十八輪批次F 建立「問題負責人」管理頁、回饋十九輪批次F 擴充機房結論）</summary>
public class IssueOwnerDto
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }

    /// <summary>顯示用的問題標籤（重用 SourceEventLabel 概念）：EventId 0 時只顯示來源名稱（若有規則 key 則顯示 Source（EventKey）），不顯示 (0) 避免誤讀為計數</summary>
    public string DisplayLabel { get; set; } = string.Empty;

    public List<long> OwnerUserIds { get; set; } = new();

    /// <summary>「顯示名稱(帳號)」，供清單直接呈現（§9 顯示格式一致慣例）</summary>
    public List<string> OwnerNames { get; set; } = new();

    public string? Note { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByAccount { get; set; }
    public string? UpdatedByDisplayName { get; set; }

    /// <summary>近期出現統計（picker 與清單共用同一份聚合，見 IssueOwnerAdminService.RecentIssues）：
    /// 查無出現紀錄時（例如手動預先指派尚未出現過的問題）為 null，畫面顯示「尚未出現」。</summary>
    public int? RecentHostCount { get; set; }
    public DateTime? RecentLastSeen { get; set; }

    // ── 機房結論（回饋十九輪批次F，§2 決策一）─────────────────────────────────

    /// <summary>結案四態之一（resolved/wont_fix/false_positive/known_noise）；null＝目前沒有結論</summary>
    public string? ConclusionStatus { get; set; }

    /// <summary>結論狀態的顯示文字（同統一標記的既有用詞），無結論時為 null</summary>
    public string? ConclusionStatusText { get; set; }

    public string? ConclusionNote { get; set; }
    public DateTime? ConcludedAt { get; set; }
    public string? ConcludedByAccount { get; set; }
    public string? ConcludedByDisplayName { get; set; }

    /// <summary>之後新出現的主機日是否自動套用這個結論</summary>
    public bool AutoApply { get; set; }
}

public class SaveIssueOwnerRequest
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
    public List<long> OwnerUserIds { get; set; } = new();
    public string? Note { get; set; }
}

/// <summary>
/// 設定機房結論（回饋十九輪批次F）：統一標記勾選「之後自動套用」（見 <see cref="LogForesight.Web.Services.IssueHandlingCommandService.BulkCloseIssue"/>）
/// 與問題檔案頁各自的入口都收斂到 <see cref="LogForesight.Web.Services.IssueOwnerAdminService.SetConclusion"/>——
/// 只有一份「保留既有負責人／備註，只改結論欄」的合併邏輯。
/// </summary>
public class SetIssueConclusionRequest
{
    /// <summary>只收結案四態（resolved／wont_fix／false_positive／known_noise）</summary>
    [Required]
    public string Status { get; set; } = string.Empty;

    /// <summary>原因**必填**——這是代全體下結論的操作，理由是紀錄的一部分（同統一標記的既有慣例）</summary>
    [Required(ErrorMessage = "請填寫原因")]
    [StringLength(1000, ErrorMessage = "原因長度不可超過 1000 字元")]
    public string Note { get; set; } = string.Empty;

    public bool AutoApply { get; set; }
}

/// <summary>問題選擇器的候選項（近期出現過的問題，供新增規則時挑選；也允許手動輸入不在清單內的值）</summary>
public class RecentIssueOptionDto
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }

    /// <summary>顯示用的問題標籤（重用 SourceEventLabel 概念）：EventId 0 時只顯示來源名稱（若有規則 key 則顯示 Source（EventKey）），不顯示 (0) 避免誤讀為計數</summary>
    public string DisplayLabel { get; set; } = string.Empty;

    public int HostCount { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>這個問題是否已經有負責人規則——picker 用來標示「已指派」，避免使用者誤以為要新增</summary>
    public bool HasOwner { get; set; }
}
