namespace LogForesight.Web.Models.Dto;

/// <summary>問題查詢的清單列</summary>
public class RecordListItemDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>當日出現的風險類型（依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>有關聯訊號（攻擊鏈/故障鏈）——緊急程度排序的第二順位依據</summary>
    public bool HasCorrelation { get; set; }

    /// <summary>資料不完整或 Security log 未讀取，清單上要標示「沒告警 ≠ 沒問題」</summary>
    public bool HasCoverageGap { get; set; }

    public bool AiAnalyzed { get; set; }

    /// <summary>處理狀態（由問題層級推導：全結案→已處理、部分→處理中、未標記→退回日層級）</summary>
    public string HandlingStatus { get; set; } = HandlingStatuses.Open;
    public string HandlingStatusText { get; set; } = string.Empty;

    /// <summary>
    /// 處理人 Id（docs/archive/FEEDBACK-4-PLAN.md §6）——供前端把處理人姓名連到
    /// 「/handlers/{id}」工作頁；日層級未指派、案件 fallback 也沒有時為 null。
    /// </summary>
    public long? HandlerId { get; set; }

    /// <summary>處理人顯示名稱（§9 起為純顯示名，不含後綴；(帳號) 與「（案件）」由前端組）</summary>
    public string? HandlerName { get; set; }

    /// <summary>處理人帳號（§9：前端以 formatUserName 組「顯示名稱(帳號)」）</summary>
    public string? HandlerAccount { get; set; }

    /// <summary>處理人來自案件 fallback（Q5）：前端於名稱後加「（案件）」標示</summary>
    public bool HandlerFromCase { get; set; }

    public bool IsOverdue { get; set; }

    /// <summary>當日問題結案進度（清單顯示 N/M 已處理）；TotalIssues 為 0 時前端不顯示</summary>
    public int TotalIssues { get; set; }
    public int ClosedIssues { get; set; }
}

/// <summary>
/// 問題查詢「依主機」視角的彙總列（把同一台主機的多天合併成一列）。
/// 回答的是「哪台主機在這段期間最該擔心」，與明細視角看的是同一批紀錄、同一組篩選。
/// </summary>
public class RecordHostGroupDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public int HighRiskDays { get; set; }
    public int MediumRiskDays { get; set; }
    public int LowRiskDays { get; set; }

    /// <summary>有關聯訊號（攻擊鏈／故障鏈）的日數——緊急程度排序的第二順位</summary>
    public int CorrelationDays { get; set; }

    /// <summary>期間內出現過的風險類型（跨日去重，依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    public string LatestDate { get; set; } = string.Empty;
    public string LatestRiskLevel { get; set; } = string.Empty;
    public string LatestHeadline { get; set; } = string.Empty;
}

/// <summary>
/// 問題查詢「依日期」視角的彙總列（把同一天的多台主機合併成一列）。
/// 回答的是「哪一天整體最不平靜」，適合看事件是否集中爆發。
/// </summary>
public class RecordDateGroupDto
{
    public string Date { get; set; } = string.Empty;
    public int HighRiskHosts { get; set; }
    public int MediumRiskHosts { get; set; }
    public int LowRiskHosts { get; set; }

    /// <summary>當天有關聯訊號的主機數</summary>
    public int CorrelationHosts { get; set; }

    /// <summary>當天出現過的風險類型（跨主機去重，依最高嚴重度排序）</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>當天有風險紀錄的主機數（去重）</summary>
    public int HostCount { get; set; }
}

/// <summary>
/// 問題查詢「依問題」視角的彙總列（docs/archive/FEEDBACK-4-PLAN.md §4）：一列一個問題
/// （Source＋EventId），回答「這個問題影響多大範圍、誰在處理」。
/// </summary>
public class IssueGroupDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Category { get; set; } = string.Empty;

    /// <summary>期間內這個問題出現過的最高嚴重度</summary>
    public string MaxSeverity { get; set; } = string.Empty;

    /// <summary>影響範圍：出現過這個問題的相異主機數</summary>
    public int HostCount { get; set; }

    /// <summary>出現過的主機日總數（同一台主機多天各算一次）</summary>
    public int DayCount { get; set; }

    /// <summary>全部主機加總的事件次數</summary>
    public int TotalCount { get; set; }

    public string LastSeen { get; set; } = string.Empty;
    public string? KnownIssue { get; set; }

    /// <summary>「N 台未處理／M 台處理中／K 台已處理」三態摘要（依各主機進行中案件與
    /// 最近一次出現的標記彙總）</summary>
    public string HandlingSummary { get; set; } = string.Empty;

    /// <summary>進行中案件的處理人（去重、依姓名排序）——帶 Id 讓前端把姓名連到
    /// 處理人工作頁（docs/archive/FEEDBACK-4-PLAN.md §4/§6）；超過 3 人的「○○○ 等 N 人」收斂
    /// 由前端做（伺服器端收斂成純文字，收斂後的第一個名字就沒有 Id 可連了）</summary>
    public List<IssueGroupHandlerDto> Handlers { get; set; } = new();
}

/// <summary>依問題視角「處理人」欄的單一處理人（姓名＋工作頁連結用的 Id）</summary>
public class IssueGroupHandlerDto
{
    public long HandlerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）——
    /// 格式化本身是前端的事，這裡只補齊素材</summary>
    public string Account { get; set; } = string.Empty;
}

/// <summary>風險日詳情</summary>
public class RecordDetailDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>Sentinel 回報的主機名稱（NetIQ 來源專用）——NetIQ 主機以 IP 登錄，光看 HostName 認不出是哪台機器</summary>
    public string? HostDisplayName { get; set; }

    public string? HostIpAddress { get; set; }

    /// <summary>'windows' | 'linux'（docs/LINUX-RULES.md）</summary>
    public string HostOs { get; set; } = "windows";

    public string HostRoleDesc { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;

    /// <summary>日風險等級的判定依據說明（docs/archive/HISTORY.md #11），已轉為白話文字；
    /// null＝舊紀錄（本欄位問世前寫入），前端顯示通用說明。</summary>
    public string? RiskBasisText { get; set; }

    /// <summary>因全站嚴重度顯示設定被隱藏的問題數（0＝無隱藏或非 SiteHidden 模式）；
    /// 風險等級判定不受此設定影響，這個數字只用來解釋「為什麼看到的問題比判定依據少」</summary>
    public int HiddenIssueCount { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string TrendAssessment { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool AiAnalyzed { get; set; }

    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int AuditEventCount { get; set; }

    public List<IssueDto> TopIssues { get; set; } = new();
    public List<CategorySummaryDto> Categories { get; set; } = new();
    public List<string> TrendAlerts { get; set; } = new();
    public List<string> CorrelationAlerts { get; set; } = new();
    public List<DeepDiveDto> DeepDives { get; set; } = new();

    /// <summary>資料完整性申報（§「沒告警 ≠ 沒問題」）</summary>
    public bool DataIncomplete { get; set; }
    public bool? SecurityLogAvailable { get; set; }
    public List<string> UncoveredChecks { get; set; } = new();

    public bool HasReport { get; set; }
    public WeeklyCheckupDto? WeeklyCheckup { get; set; }

    /// <summary>目前登入者是否可維護處理狀態——前端據此決定逐列狀態是可操作還是唯讀（防線在後端）</summary>
    public bool CanHandle { get; set; }

    /// <summary>系統設定的未處理計算嚴重度——前端嚴重度篩選預設值依此初始化（見系統管理 > 設定）</summary>
    public List<string> UnhandledSeverities { get; set; } = new();

    /// <summary>層級顯示模式：DefaultHidden／Locked／GlobalFilter（見 SystemSettings.SeverityDisplayMode）</summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";
}

public class IssueDto
{
    public string LogName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public int Count { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;

    /// <summary>命中即列為高風險日（docs/archive/HISTORY.md #1，B1 三級化）：前端顯示「重大」
    /// 徽章——這條問題就是讓當天判定為高風險日的原因，取代原本「嚴重」等級給人的直覺。</summary>
    public bool ElevatesDayRisk { get; set; }

    public string? KnownIssue { get; set; }
    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public int DistinctMessageCount { get; set; }
    public string? KeyDetails { get; set; }
    public List<string> SampleMessages { get; set; } = new();
    public bool Suppressed { get; set; }

    /// <summary>趨勢的白話描述（含比對數字），前端直接顯示不再自行組裝</summary>
    public string TrendText { get; set; } = string.Empty;
    public string Trend { get; set; } = string.Empty;

    /// <summary>問題簽章的穩定鍵（IssueSignatureKey）——逐列設定處理狀態時回傳給後端</summary>
    public string IssueKey { get; set; } = string.Empty;

    /// <summary>命中的規則 Id（未命中規則為 null）——標「已知雜訊」時可據此一鍵建立抑制規則</summary>
    public string? RuleId { get; set; }

    /// <summary>此問題的處理狀態（方案 B）：resolved/wont_fix/false_positive/known_noise/open，
    /// 空字串＝從未標記過（實際顯示是「未處理」還是自動推導的預設值，見下列兩個旗標）</summary>
    public string HandlingStatus { get; set; } = string.Empty;
    public string HandlingStatusText { get; set; } = string.Empty;

    /// <summary>
    /// true＝從未標記過且為低風險——前端顯示「不處理（預設）」而非「未處理」，
    /// 並提供「確認不處理」／「調回未處理」兩個動作（§5.1 D-1 #2）。
    /// </summary>
    public bool IsDefaultUnhandled { get; set; }

    /// <summary>
    /// true＝從未標記過，但同主機同簽章有已知雜訊記憶——前端顯示「已知雜訊（自動）」，
    /// 提供「調回未處理」動作（§5.1 D-1 #3）。與 IsDefaultUnhandled 互斥（記憶優先於低風險預設）。
    /// </summary>
    public bool IsAutoNoise { get; set; }

    /// <summary>記憶當初標記時留下的備註，IsAutoNoise 為 true 時才有意義</summary>
    public string? NoiseNote { get; set; }

    /// <summary>預計完成日（yyyy-MM-dd）——只有 HandlingStatus=in_progress 時才有意義</summary>
    public string? DueDate { get; set; }

    /// <summary>
    /// 這個問題目前的進行中案件（docs/archive/FEEDBACK-4-PLAN.md §2），null＝沒有進行中案件。
    /// 案件涵蓋這個問題的其他日子也會同步這個狀態——前端據此顯示「○○○ 處理中（1/10 起）」，
    /// 解釋為什麼某些問題的狀態會「自己動」。
    /// </summary>
    /// <summary>案件處理人 Id（docs/archive/FEEDBACK-4-PLAN.md §6）——供前端把案件徽章連到其工作頁</summary>
    public long? CaseHandlerId { get; set; }
    public string? CaseHandlerName { get; set; }
    public string? CaseStatus { get; set; }
    public string? CaseFirstLinkedDate { get; set; }

    /// <summary>
    /// true＝這個問題簽章在本主機更早的日期有結案過的紀錄（逐日標記或已結案案件皆算）
    /// ——docs/archive/FEEDBACK-5-PLAN.md §4「之前處理過的問題再次發生」。前端據此顯示「先前處理」
    /// 按鈕，點開 modal 打 <c>issue-history</c> 端點查詳情；本欄只是「有沒有」的旗標，
    /// 不帶內容，避免每個問題都攜帶一份可能用不到的歷史清單。
    /// </summary>
    public bool HasPriorHandling { get; set; }

    /// <summary>
    /// 規則命中問題的處置參考（知識庫），null＝未命中規則或該規則無知識內容。
    /// 掛在問題列下方（可展開），讓「這個問題怎麼辦」與問題本身直接對齊，
    /// 不必再到獨立的深入分析卡玩多對多連連看。
    /// </summary>
    public IssueGuidanceDto? Guidance { get; set; }
}

/// <summary>單一問題的處置參考——欄位對應 KnownIssueRule 的白話知識庫（也是 txt 報告「處置參考」的來源）</summary>
public class IssueGuidanceDto
{
    public string Explanation { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>
/// 單一問題簽章在本主機的先前處理歷史（docs/archive/FEEDBACK-5-PLAN.md §4）：
/// <c>Cases</c> 是已結案案件摘要（較接近「上次怎麼解的」的答案），
/// <c>Entries</c> 是逐日結案標記（只含結案類，倒序）。兩者可能重疊
/// （案件同步時會把逐日列的 CaseId 標起來，見 <see cref="IssueHistoryEntryDto.FromCase"/>），
/// 前端分開呈現不強行去重——案件摘要答「這個問題的處理協調史」，逐日列答「哪幾天發生過」。
/// </summary>
public class IssueHistoryDto
{
    public List<IssueHistoryCaseDto> Cases { get; set; } = new();
    public List<IssueHistoryEntryDto> Entries { get; set; } = new();
}

public class IssueHistoryCaseDto
{
    public string? HandlerName { get; set; }
    public string FirstLinkedDate { get; set; } = string.Empty;
    public string LastLinkedDate { get; set; } = string.Empty;
    public string? ClosedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? Note { get; set; }
}

public class IssueHistoryEntryDto
{
    public string Date { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string ActorAccount { get; set; } = string.Empty;

    /// <summary>顯示格式統一「顯示名稱(帳號)」的素材（docs/archive/FEEDBACK-8-PLAN.md #6）；
    /// 查無對應使用者時為 null，前端退回只顯示帳號</summary>
    public string? ActorDisplayName { get; set; }

    /// <summary>true＝這筆標記出自案件同步（非人工逐日手動標的）</summary>
    public bool FromCase { get; set; }
}

public class CategorySummaryDto
{
    public string Category { get; set; } = string.Empty;
    public int IssueCount { get; set; }
    public int TotalEvents { get; set; }
    public string MaxSeverity { get; set; } = string.Empty;
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    /// <summary>命中「重大」旗標的問題數（docs/archive/HISTORY.md #1）</summary>
    public int ElevatesCount { get; set; }
}

public class DeepDiveDto
{
    public string Category { get; set; } = string.Empty;
    public List<DeepDiveFindingDto> Findings { get; set; } = new();
}

public class DeepDiveFindingDto
{
    public string Problem { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public List<string> LikelyCauses { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

public class WeeklyCheckupDto
{
    public string CheckupDate { get; set; } = string.Empty;
    public bool HasFindings { get; set; }
    public string Conclusion { get; set; } = string.Empty;
}

/// <summary>主機時間軸的單日格子</summary>
public class TimelineDayDto
{
    public string Date { get; set; } = string.Empty;
    public string? RiskLevel { get; set; }
    public string Headline { get; set; } = string.Empty;
    public bool HasRecord { get; set; }
    public bool HasCoverageGap { get; set; }
}

public class HostDetailDto
{
    public long HostId { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string RoleDesc { get; set; } = string.Empty;
    public string? IpAddress { get; set; }

    /// <summary>'windows' | 'linux'（docs/LINUX-RULES.md）</summary>
    public string Os { get; set; } = "windows";

    public string? NetiqServer { get; set; }
    public DateTime? LastReportAt { get; set; }
    public List<string> GroupNames { get; set; } = new();
    public List<string> OwnerNames { get; set; } = new();
    public List<TimelineDayDto> Timeline { get; set; } = new();
    public WeeklyCheckupDto? LatestCheckup { get; set; }

    /// <summary>期間內問題彙總（docs/archive/FEEDBACK-3-PLAN.md #4）：問題查詢「依主機」下鑽進來
    /// 原本只看得到時間軸色格，逐格點日期才看得到問題——這裡直接列出期間內出現過的
    /// 問題（依 Source+EventId 分組），每列連結到最近一次出現的那天詳情</summary>
    public List<HostIssueSummaryDto> TopSignatures { get; set; } = new();
}

/// <summary>主機詳情頁「重點問題（期間彙總）」的單列（docs/archive/FEEDBACK-3-PLAN.md #4）。
/// 與 <see cref="LogForesight.Web.Services.IssueClusterDto"/>（跨主機同簽章聚類，供 AI 歸納）
/// 分組鍵相同（Source+EventId）但彙總維度不同——這裡是單一主機跨日，關心的是「出現幾天」
/// 而不是「幾台主機」。</summary>
public class HostIssueSummaryDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    public string Category { get; set; } = string.Empty;

    /// <summary>期間內出現過的最高嚴重度（High/Medium/Low）</summary>
    public string MaxSeverity { get; set; } = string.Empty;

    public int TotalCount { get; set; }

    /// <summary>出現過這個問題的天數（不是次數——一天出現多次只算一天）</summary>
    public int DaysSeen { get; set; }

    /// <summary>最近一次出現的日期（yyyy-MM-dd），連結到該日詳情</summary>
    public string LastSeenDate { get; set; } = string.Empty;

    /// <summary>最近一次出現時的說明——規則的已知問題文字可能被管理者事後編輯過，
    /// 各天的舊紀錄各自保留分析當下的快照，取最近一天的最新</summary>
    public string? KnownIssue { get; set; }
}

/// <summary>
/// 主機詳情頁「重點問題」某一列展開後的發生明細（docs/archive/FEEDBACK-4-PLAN.md §3）：
/// 這個問題（Source+EventId）在期間內逐日出現的頻率、間隔與各日處理狀態。
/// </summary>
public class HostIssueOccurrenceDto
{
    public HostIssueOccurrenceStatsDto Stats { get; set; } = new();

    /// <summary>有進行中或最近結案案件時才有值——上次（或目前）誰在處理、怎麼結的</summary>
    public HostIssueOccurrenceCaseDto? Case { get; set; }

    public List<HostIssueOccurrenceDayDto> Occurrences { get; set; } = new();
}

public class HostIssueOccurrenceStatsDto
{
    /// <summary>出現天數（不是次數）</summary>
    public int DaysSeen { get; set; }

    public int TotalCount { get; set; }

    /// <summary>平均間隔天數；只出現過一天時無意義，回 null</summary>
    public double? AvgGapDays { get; set; }

    /// <summary>最長連續出現天數（日曆日相鄰才算連續）</summary>
    public int LongestStreak { get; set; }

    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
}

public class HostIssueOccurrenceCaseDto
{
    public long? HandlerId { get; set; }
    public string? HandlerName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string FirstLinkedDate { get; set; } = string.Empty;

    /// <summary>null＝案件仍進行中</summary>
    public DateTime? ClosedAt { get; set; }
}

public class HostIssueOccurrenceDayDto
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
    public string RiskLevel { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;

    /// <summary>這個標記是不是案件同步展開寫入的（而非使用者當天手動標的）——前端顯示「案件同步」小字</summary>
    public bool FromCase { get; set; }
}

public class RecordSearchRequest
{
    public List<long>? HostIds { get; set; }

    /// <summary>主機群組篩選（§5.4 D-4）：展開為主機集合後與 HostIds 取聯集，
    /// 兩千台規模下「看某個部門的主機」比一台台勾選實際得多</summary>
    public List<long>? GroupIds { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public List<string>? RiskLevels { get; set; }
    public List<string>? Categories { get; set; }
    public string? Severity { get; set; }
    public int? EventId { get; set; }
    public string? Source { get; set; }

    /// <summary>處理狀態篩選（待辦清單的下鑽目標）</summary>
    public List<string>? Statuses { get; set; }

    /// <summary>只看逾期未處理</summary>
    public bool? Overdue { get; set; }

    /// <summary>
    /// 表頭排序（docs/WEB-SPEC.md §9.2）。null／不合法值＝維持各視角原本的預設排序。
    /// 明細視角：date | host | risk。依主機視角：host | highRisk | mediumRisk | lowRisk | correlation。
    /// 依日期視角：date | hostCount | highRisk | mediumRisk | lowRisk | correlation。
    /// </summary>
    public string? SortKey { get; set; }

    public bool Ascending { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
