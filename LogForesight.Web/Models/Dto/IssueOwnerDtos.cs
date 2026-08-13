namespace LogForesight.Web.Models.Dto;

/// <summary>問題負責人規則（回饋十八輪批次F，「問題負責人」管理頁）</summary>
public class IssueOwnerDto
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
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
}

public class SaveIssueOwnerRequest
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
    public List<long> OwnerUserIds { get; set; } = new();
    public string? Note { get; set; }
}

/// <summary>問題選擇器的候選項（近期出現過的問題，供新增規則時挑選；也允許手動輸入不在清單內的值）</summary>
public class RecentIssueOptionDto
{
    public string SourceName { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int HostCount { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>這個問題是否已經有負責人規則——picker 用來標示「已指派」，避免使用者誤以為要新增</summary>
    public bool HasOwner { get; set; }
}
