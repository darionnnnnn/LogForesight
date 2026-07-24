using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 主機頁的 NetIQ 清單概況：可選的 Sentinel 名單，以及需要人處理的佇列數。
/// 佇列數不是統計儀表，是**待辦**——每一項都代表某些主機今晚不會被檢查、
/// 或檢查了卻沒有人看得到。
/// </summary>
public class NetiqOverviewDto
{
    public List<string> SentinelNames { get; set; } = new();

    /// <summary>尚未確定所屬 Sentinel（不進日常輪巡，待批次自動確認或人工指定）</summary>
    public int PendingAssignmentCount { get; set; }

    /// <summary>IP 衝突的組數（每組只有最早建立的那台會被輪巡）</summary>
    public int IpConflictCount { get; set; }

    /// <summary>未分組主機數（依授權模型只有 admin 看得到）</summary>
    public int UngroupedCount { get; set; }

    /// <summary>IP 衝突的明細，供畫面直接列出每組並提供處置</summary>
    public List<IpConflictGroupDto> IpConflicts { get; set; } = new();
}

public class IpConflictGroupDto
{
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>依建立順序；第一台是實際會被輪巡的那台</summary>
    public List<IpConflictHostDto> Hosts { get; set; } = new();
}

public class IpConflictHostDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? NetiqServer { get; set; }
    public string RoleDesc { get; set; } = string.Empty;

    /// <summary>false = 因 IP 衝突今晚不會被輪巡</summary>
    public bool IsPolled { get; set; }
}

public class AddNetiqHostRequest
{
    [Required(ErrorMessage = "請輸入 IP 位址")]
    [StringLength(45)]
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>可留空＝待歸屬，由批次自動確認在哪一台 Sentinel 上</summary>
    [StringLength(50)]
    public string? NetiqServer { get; set; }

    [StringLength(500)]
    public string? RoleDesc { get; set; }
}

public class BulkAddNetiqHostsRequest
{
    /// <summary>可留空＝整批都待歸屬</summary>
    [StringLength(50)]
    public string? NetiqServer { get; set; }

    /// <summary>一行一台，格式 <c>IP[,角色描述]</c>；`#` 開頭為註解、空行忽略</summary>
    [Required(ErrorMessage = "請貼上主機清單")]
    public string Lines { get; set; } = string.Empty;
}

/// <summary>
/// 批次貼上的結果。**不合法的行不會擋下整批**（沿用 txt 清單「警告並略過」的語意），
/// 但每一行為什麼被略過都要講清楚——否則使用者只知道「少了幾台」，卻不知道是哪幾台。
/// </summary>
public class BulkAddResultDto
{
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public List<BulkAddLineDto> Skipped { get; set; } = new();
}

public class BulkAddLineDto
{
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class SetHostActiveRequest
{
    public bool Active { get; set; }
}

// ── 主動探索匯入（docs/SCALE-2000-PLAN.md §1）───────────────────────────────

public class NetiqScanRequest
{
    [Required]
    public string Server { get; set; } = string.Empty;
}

public class NetiqScanResultDto
{
    public string Token { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public List<NetiqSubnetDto> Subnets { get; set; } = new();
}

public class NetiqSubnetDto
{
    public string Cidr { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int ExistingCount { get; set; }

    /// <summary>與「因 Sentinel 移除而停用」的主機重疊——匯入即復活重綁</summary>
    public int OrphanOverlapCount { get; set; }

    public List<NetiqScanHostDto> Hosts { get; set; } = new();
}

public class NetiqScanHostDto
{
    public string HostName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>使用中的既有主機（再勾＝更新顯示名/Sentinel 歸屬）</summary>
    public bool Exists { get; set; }

    /// <summary>與停用的孤兒主機重疊（原屬某 Sentinel、因移除而停用）</summary>
    public bool OrphanOverlap { get; set; }

    /// <summary>OrphanOverlap 時：原本所屬的 Sentinel 名稱</summary>
    public string? OrphanedFrom { get; set; }
}

public class NetiqImportRequest
{
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>使用者勾選要匯入的 IP（＝HostName）</summary>
    public List<string> SelectedIps { get; set; } = new();

    /// <summary>
    /// 各網段的群組指派（docs/NETIQ-WEB-CONFIG-PLAN.md 定案 8）。省略/空清單＝維持
    /// Phase 3 的行為（全部落在未分組安全預設）；只影響本次新增的主機，既有主機的
    /// 群組一律不動。
    /// </summary>
    public List<NetiqSubnetGroupAssignment> GroupAssignments { get; set; } = new();
}

/// <summary>單一網段的群組指派方式</summary>
public class NetiqSubnetGroupAssignment
{
    [Required]
    public string Cidr { get; set; } = string.Empty;

    /// <summary>skip（預設，未分組）| existing（用 HostGroupId）| new（用 NewGroupName 建立）</summary>
    public string Mode { get; set; } = "skip";

    public long? HostGroupId { get; set; }

    [StringLength(100)]
    public string? NewGroupName { get; set; }
}

/// <summary>「新增即掃描」精靈步驟 1：連線設定。掃描成功才建立 Sentinel（定案 6：掃描即帳密驗證）</summary>
public class CreateAndScanSentinelRequest
{
    [Required(ErrorMessage = "請輸入 Sentinel 名稱")]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    [StringLength(255)]
    public string? BaseUrl { get; set; }

    [Required(ErrorMessage = "請輸入探索帳號")]
    [StringLength(100)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "請輸入探索密碼")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>NetIQ 掃描匯入的即時套用結果（定案 7：不再有排入/取消的中間狀態）</summary>
public class NetiqImportResultDto
{
    public string ServerName { get; set; } = string.Empty;
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Revived { get; set; }
}
