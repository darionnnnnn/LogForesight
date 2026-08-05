using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class UserDto
{
    public long UserId { get; set; }
    public string Account { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool Active { get; set; }
    public List<long> GroupIds { get; set; } = new();

    /// <summary>群組名稱（清單畫面直接顯示，前端不必自己 join）</summary>
    public List<string> GroupNames { get; set; } = new();

    /// <summary>最近一次登入時間；null＝從未登入（docs/archive/FEEDBACK-11-PLAN.md §3）</summary>
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// 使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）：管理視角的單一使用者全貌。
/// 工作負載（進行中案件／被指派風險日）**不在這裡**——前端另打既有的
/// `api/handlers/{id}/workload`，不重複第二套投影規則。
/// </summary>
public class UserDetailDto
{
    public UserDto User { get; set; } = new();

    /// <summary>所屬群組（含角色，用來說明「他能用哪些功能」）</summary>
    public List<UserGroupDto> Groups { get; set; } = new();

    /// <summary>
    /// 由所屬群組角色推導的能力字串；**含負責人隱含能力**（§2b）——
    /// 「他為什麼能標記處理狀態」在這裡看得到答案。
    /// </summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>此人（不是檢視者）看得到的主機，含來源標示</summary>
    public List<UserVisibleHostDto> VisibleHosts { get; set; } = new();

    /// <summary>
    /// 被指派歷程：以問題案件（<c>IssueCase</c>）為事實來源，新到舊。
    /// **刻意不用稽核表反查**——稽核有保留天數且要比對 detail JSON，案件本身就是指派的第一手紀錄。
    /// 誠實邊界：案件只保存**目前**處理人，被改派走的案件不再出現在這裡
    /// （那次改派記在該主機的處理歷程 `case_reassign`）。
    /// </summary>
    public List<UserAssignmentHistoryDto> AssignmentHistory { get; set; } = new();
}

/// <summary>
/// 此人看得到的一台主機。<see cref="ViaOwner"/>／<see cref="ViaGroup"/> 是「為什麼看得到」——
/// 兩條路徑可同時成立（既是負責人、部門群組也被授權），因此不是二選一的列舉。
/// </summary>
public class UserVisibleHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? Os { get; set; }
    public List<string> GroupNames { get; set; } = new();

    /// <summary>經群組授權矩陣看得到</summary>
    public bool ViaGroup { get; set; }

    /// <summary>身為這台主機的負責人而看得到（§2b）</summary>
    public bool ViaOwner { get; set; }
}

/// <summary>被指派歷程的一列（案件生命週期事件）</summary>
public class UserAssignmentHistoryDto
{
    public string CaseId { get; set; } = string.Empty;
    public long? HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string IssueLabel { get; set; } = string.Empty;

    /// <summary>案件目前狀態（含結案類）</summary>
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

    /// <summary>true＝案件已結案；false＝仍在此人名下進行中</summary>
    public bool Closed { get; set; }

    /// <summary>建案時間與建案者帳號（誰把這件事交辦給他）</summary>
    public DateTime CreatedAt { get; set; }
    public string CreatedByAccount { get; set; } = string.Empty;

    public DateTime? ClosedAt { get; set; }

    /// <summary>案件涵蓋的風險日區間（首見～最近掛接）</summary>
    public string FirstLinkedDate { get; set; } = string.Empty;
    public string LastLinkedDate { get; set; } = string.Empty;
}

public class SaveUserRequest
{
    [Required(ErrorMessage = "請輸入帳號")]
    [StringLength(255, ErrorMessage = "帳號長度不可超過 255 字元")]
    public string Account { get; set; } = string.Empty;

    [StringLength(255)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(255)]
    public string? Email { get; set; }

    public bool Active { get; set; } = true;
}

public class SetUserGroupsRequest
{
    public List<long> GroupIds { get; set; } = new();
}

/// <summary>
/// 一次新增多個帳號（docs/archive/HISTORY.md #7）。只填帳號＋所屬群組——顯示名稱與 Email
/// 由伺服器端決定（顯示名稱＝帳號、Email 留空），前端多筆模式因此隱藏這兩個欄位。
/// </summary>
public class BatchCreateUsersRequest
{
    [Required(ErrorMessage = "請輸入至少一個帳號")]
    public List<string> Accounts { get; set; } = new();

    public List<long> GroupIds { get; set; } = new();

    public bool Active { get; set; } = true;

    /// <summary>已存在的帳號是否以這批勾選的群組覆蓋其權限；false（預設）＝跳過已存在帳號</summary>
    public bool OverwriteExisting { get; set; }
}

public class BatchCreateUsersResultDto
{
    public List<string> Created { get; set; } = new();

    /// <summary>已存在且 OverwriteExisting=true，群組已被整組取代</summary>
    public List<string> Overwritten { get; set; } = new();

    /// <summary>已存在但 OverwriteExisting=false，維持原樣未動</summary>
    public List<string> Skipped { get; set; } = new();

    /// <summary>去除空白行、去重後，因超長等原因不合格而被捨棄的筆數</summary>
    public int InvalidCount { get; set; }
}

public class UserGroupDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool Builtin { get; set; }
    public bool Active { get; set; }
    public int MemberCount { get; set; }
}

public class SaveUserGroupRequest
{
    public long GroupId { get; set; }

    [Required(ErrorMessage = "請輸入群組名稱")]
    [StringLength(100, ErrorMessage = "群組名稱長度不可超過 100 字元")]
    public string GroupName { get; set; } = string.Empty;

    /// <summary>User | Dev | Manager | Admin。builtin 群組不可變更</summary>
    public string Role { get; set; } = "User";

    public bool Active { get; set; } = true;
}

public class HostGroupDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int HostCount { get; set; }
}

public class SaveHostGroupRequest
{
    public long GroupId { get; set; }

    [Required(ErrorMessage = "請輸入群組名稱")]
    [StringLength(100, ErrorMessage = "群組名稱長度不可超過 100 字元")]
    public string GroupName { get; set; } = string.Empty;

    public bool Active { get; set; } = true;
}

// ── 批次加入群組成員（網段／關鍵字，docs/archive/HISTORY.md §3）─────────────

public class HostGroupMemberQueryRequest
{
    /// <summary>網段：CIDR（10.1.2.0/24）或萬用字元（10.1.2.*）或單一 IP</summary>
    public string? Pattern { get; set; }

    /// <summary>關鍵字：比對 hostname 或 IP（Pattern 與 Query 擇一）</summary>
    public string? Query { get; set; }
}

/// <summary>預覽命中的主機——含現有群組（顯性通知已屬其他群組）與是否已在目標群組</summary>
public class HostGroupMemberCandidateDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public List<string> CurrentGroups { get; set; } = new();
    public bool InOtherGroups { get; set; }
    public bool AlreadyInTarget { get; set; }
}

public class HostGroupMemberPreviewDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int MatchCount { get; set; }
    public int InOtherGroupsCount { get; set; }
    public List<HostGroupMemberCandidateDto> Candidates { get; set; } = new();
}

public class AddHostGroupMembersRequest
{
    public List<long> HostIds { get; set; } = new();

    /// <summary>同時把這些主機移出其原本所屬的其他主機群組（預設否，僅加入）</summary>
    public bool RemoveFromOthers { get; set; }
}

/// <summary>「目前成員」頁籤用：本群組現有成員，附帶移除後是否會變未分組的判斷依據</summary>
public class HostGroupMemberDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }

    /// <summary>扣除本群組後，此主機仍隸屬的其他群組數；0 代表移除後會變成未分組</summary>
    public int OtherGroupCount { get; set; }
}

public class RemoveHostGroupMembersRequest
{
    public List<long> HostIds { get; set; } = new();
}

/// <summary>授權矩陣：列＝使用者群組、欄＝主機群組</summary>
public class AccessMatrixDto
{
    public List<AccessMatrixRowDto> UserGroups { get; set; } = new();
    public List<HostGroupDto> HostGroups { get; set; } = new();
}

public class AccessMatrixRowDto
{
    public long UserGroupId { get; set; }
    public string UserGroupName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public List<long> GrantedHostGroupIds { get; set; } = new();
}

public class SetAccessRequest
{
    public List<long> HostGroupIds { get; set; } = new();
}

public class HostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>Sentinel 回報的主機名稱（批次回填，唯讀）——NetIQ 主機以 IP 登錄時的辨識依據</summary>
    public string? DisplayName { get; set; }

    public string? IpAddress { get; set; }
    public string? NetiqServer { get; set; }
    public string RoleDesc { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    /// <summary>'windows' | 'linux'——決定套用哪個平台的規則面（docs/LINUX-RULES.md）</summary>
    public string Os { get; set; } = "windows";

    public bool Active { get; set; }
    public long? MergedInto { get; set; }
    public DateTime? LastReportAt { get; set; }

    /// <summary>建立時間——「未回報」告警的寬限期依據，剛匯入的主機不該立刻被當成無回報（定案 9）</summary>
    public DateTime CreatedAt { get; set; }

    public List<long> GroupIds { get; set; } = new();
    public List<string> GroupNames { get; set; } = new();
    public List<long> OwnerUserIds { get; set; } = new();
    public List<string> OwnerNames { get; set; } = new();
}

public class SaveHostRequest
{
    [Required(ErrorMessage = "請輸入主機名稱")]
    [StringLength(255, ErrorMessage = "主機名稱長度不可超過 255 字元")]
    public string HostName { get; set; } = string.Empty;

    [StringLength(45)]
    public string? IpAddress { get; set; }

    [StringLength(50)]
    public string? NetiqServer { get; set; }

    [StringLength(500)]
    public string? RoleDesc { get; set; }

    /// <summary>'windows'（預設）| 'linux'</summary>
    public string Os { get; set; } = "windows";

    public bool Active { get; set; } = true;
}

public class SetIdsRequest
{
    public List<long> Ids { get; set; } = new();
}

/// <summary>批次改群組（docs/archive/FEEDBACK-5-PLAN.md §8）</summary>
public class SetGroupsBatchRequest
{
    public List<long> HostIds { get; set; } = new();
    public List<long> GroupIds { get; set; } = new();

    /// <summary>"add"（加入，保留既有群組）｜ "replace"（取代，改為僅這些群組）</summary>
    public string Mode { get; set; } = "add";
}

public class HostGroupsBatchResultDto
{
    public int UpdatedCount { get; set; }
    public List<SkippedHostDto> Skipped { get; set; } = new();
}

public class SkippedHostDto
{
    public string HostName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class MergeHostRequest
{
    public long SourceHostId { get; set; }
    public long TargetHostId { get; set; }
}

/// <summary>主篩選列的主機選單項目（已依授權過濾）</summary>
public class VisibleHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string RoleDesc { get; set; } = string.Empty;
    public DateTime? LastReportAt { get; set; }
}

/// <summary>主機群組篩選選項（§5.4 D-4）——比完整 HostGroupDto 精簡，一般使用者用不到 MemberCount/Active</summary>
public class HostGroupOptionDto
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
}
