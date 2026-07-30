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

    /// <summary>'windows'（預設）| 'linux'</summary>
    public string Os { get; set; } = "windows";
}

public class BulkAddNetiqHostsRequest
{
    /// <summary>可留空＝整批都待歸屬</summary>
    [StringLength(50)]
    public string? NetiqServer { get; set; }

    /// <summary>一行一台，格式 <c>IP[,角色描述]</c>；`#` 開頭為註解、空行忽略</summary>
    [Required(ErrorMessage = "請貼上主機清單")]
    public string Lines { get; set; } = string.Empty;

    /// <summary>'windows'（預設）| 'linux'——整批同一種作業系統，混合平台請分兩批貼上</summary>
    public string Os { get; set; } = "windows";
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

// ── 主動探索匯入（docs/HISTORY.md §1）───────────────────────────────

public class NetiqScanRequest
{
    [Required]
    public string Server { get; set; } = string.Empty;

    /// <summary>網段前綴（如「10.232.11」）或 CIDR（如「10.232.11.0/24」）。**必填**——
    /// 探索是「掃一個網段」而不是盲掃全站（docs/NETIQ-API-PLAN.md §3.4：全站事件量實測單台
    /// Sentinel 近 24h 達 2400 萬筆，任何固定窗口涵蓋率都很差）。格式規則見
    /// <c>SentinelQueryBuilder.NormalizeSubnetPrefix</c>。</summary>
    [Required(ErrorMessage = "請輸入要掃描的網段")]
    [StringLength(20)]
    public string SubnetPrefix { get; set; } = string.Empty;
}

public class NetiqScanResultDto
{
    public string Token { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public List<NetiqSubnetDto> Subnets { get; set; } = new();

    /// <summary>人看的涵蓋範圍說明，精靈直接顯示——網段範圍掃描只涵蓋「窗口內有事件回報」的主機，
    /// 這個限制必須顯性告知，不能讓人誤以為是網段的完整名單。</summary>
    public string CoverageNote { get; set; } = string.Empty;

    /// <summary>需要人工留意的異常（如查詢被截斷）。非空時精靈要以醒目樣式顯示。</summary>
    public List<string> Warnings { get; set; } = new();
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
    /// 各網段的群組指派（docs/HISTORY.md 定案 8）。省略/空清單＝維持
    /// Phase 3 的行為（全部落在未分組安全預設）；只影響本次新增的主機，既有主機的
    /// 群組一律不動。
    /// </summary>
    public List<NetiqSubnetGroupAssignment> GroupAssignments { get; set; } = new();

    /// <summary>
    /// 本次匯入的主機作業系統（docs/LINUX-RULES-PLAN.md §3）。**只套用在本次新增的主機**——
    /// 既有主機（含復活的孤兒）的 OS 一律不動，與群組指派同一原則：匯入不是隱性改設定。
    ///
    /// 目前無法自 Sentinel 事件自動判別 OS（要等 probe 確認有無可判別欄位），所以由人在精靈
    /// 上選；混合平台的網段請分兩次掃描匯入。
    /// </summary>
    public string Os { get; set; } = WebHost.OsWindows;
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

/// <summary>NetIQ 掃描匯入的即時套用結果（定案 7：不再有排入/取消的中間狀態）</summary>
public class NetiqImportResultDto
{
    public string ServerName { get; set; } = string.Empty;
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Revived { get; set; }
}

// ── 連線與節流參數（「系統管理 > NetIQ 維護」頁）───────────────────────────
// 讀取直接回傳 Core 的 NetiqOptions（欄位與原本的 NetiqOptionsDto 逐一相同、
// 無需遮蔽敏感欄位，這層 DTO 只是零加值的複本，已移除）。

public class UpdateNetiqOptionsRequest
{
    [Range(0, 60_000)]
    public int QueryDelayMs { get; set; }

    [Range(1, 5000)]
    public int PageSize { get; set; }

    [Range(1, 10_000_000)]
    public int MaxResultsPerJob { get; set; }

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; }

    [Range(0, 20)]
    public int RetryCount { get; set; }

    public bool AllowInvalidCertificates { get; set; }

    /// <summary>docs/FEEDBACK-3-PLAN.md #1：正式環境的預期值是 1（只查前一天），
    /// 上限 14 與趨勢窗口天數對齊——回補比趨勢分析用得到的更多天沒有意義</summary>
    [Range(1, 14)]
    public int BackfillDays { get; set; }

    /// <summary>docs/FEEDBACK-3-PLAN.md #2：同時處理幾台 Sentinel，1＝完全依序處理</summary>
    [Range(1, 8)]
    public int MaxParallelServers { get; set; }

    /// <summary>docs/FEEDBACK-4-PLAN.md §5：詢問 AI 詢問當下是否向 Sentinel 即時查詢現場事件</summary>
    public bool ChatLiveFetchEnabled { get; set; }
}
