namespace LogForesight.Web.Models.Dto;

// ── 報表後端統計：處理成效、類型趨勢（WEB-SPEC §9.6）──────────────────────────

/// <summary>分桶描述：期間 ≤ 14 天按日、15～120 天按週、&gt; 120 天按月（<see cref="LogForesight.Web.Services.ReportBucketing"/>）</summary>
public class ReportBucketDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    /// <summary>此桶實際涵蓋的天數（頭尾不足整週／整月時照實際天數）</summary>
    public int Days { get; set; }
}

/// <summary>處理成效：每桶風險日的「現在」三態（三數相加＝Total）</summary>
public class ReportEffectivenessBucketDto : ReportBucketDto
{
    public int Total { get; set; }
    public int Resolved { get; set; }
    public int InProgress { get; set; }
    public int Open { get; set; }
}

/// <summary>未結案風險日的帳齡分段（距 AsOf 的天數）</summary>
public class ReportAgingBandDto
{
    public string Label { get; set; } = string.Empty;
    public int MinDays { get; set; }
    /// <summary>null＝無上限</summary>
    public int? MaxDays { get; set; }
    public int Count { get; set; }
}

public class ReportRecurrenceDto
{
    /// <summary>已處理後觀察期內又出現的「主機 × 問題」組合數</summary>
    public int Recurred { get; set; }
    /// <summary>觀察期滿仍未再發生的組合數</summary>
    public int NotRecurred { get; set; }
    /// <summary>觀察期未滿且尚未再發生的組合數（不計入 Rate）</summary>
    public int Observing { get; set; }
    /// <summary>Recurred ÷ (Recurred＋NotRecurred)；分母 0 時為 null</summary>
    public double? Rate { get; set; }
    public List<ReportRecurringIssueDto> TopRecurring { get; set; } = new();
}

public class ReportRecurringIssueDto
{
    public string Source { get; set; } = string.Empty;
    public int EventId { get; set; }
    /// <summary>顯示用的問題名稱（<see cref="IssueNames.ForRules"/>）；前端直接用，不自行組來源與事件編號</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>期間內此問題被標為已處理的主機數</summary>
    public int HostCount { get; set; }
    /// <summary>其中已處理後又再發生的主機數</summary>
    public int RecurredCount { get; set; }
}

/// <summary>
/// 「未結案」拆三段，每段寫明單位。三段的 <c>HostDays</c>（主機日）相加＝<see cref="ReportEffectivenessDto.UnresolvedTotal"/>：
/// 在處理人手上＝現在狀態為「處理中」的主機日；待派＝「未處理」的主機日中、該主機在待派試跑裡有可派問題的；
/// 不需處理＝其餘「未處理」的主機日（該主機沒有任何可派問題：嚴重度未達、已知雜訊、不再打擾、靜音等閘門擋下，或沒有可接單的人）。
/// 待派的「問題×主機」與在處理人手上的「案件數」是另一種單位，列在各段旁邊供對照，不參與相加。
/// </summary>
public class ReportUnresolvedBreakdownDto
{
    public ReportInHandlersDto InHandlers { get; set; } = new();
    public ReportPendingDispatchDto PendingDispatch { get; set; } = new();
    public ReportNotNeededDto NotNeeded { get; set; } = new();
}

/// <summary>在處理人手上</summary>
public class ReportInHandlersDto
{
    /// <summary>進行中案件數（單位：案件＝一台主機 × 一個問題；只算可見主機）；沒有時為 0</summary>
    public int Cases { get; set; }

    /// <summary>最舊的進行中案件從建立到今天的天數；沒有進行中案件時為 null</summary>
    public int? OldestDays { get; set; }

    /// <summary>單位：主機日</summary>
    public int HostDays { get; set; }
}

/// <summary>待派（沿用待派試跑的結果，不另寫判定）</summary>
public class ReportPendingDispatchDto
{
    /// <summary>期間太長、試跑出現點超過上限：不做試跑，<see cref="IssueHosts"/> 與 <see cref="HostDays"/> 無法拆分（全部「未處理」併入 <see cref="HostDays"/>＝null，不需處理亦 null）</summary>
    public bool TooLarge { get; set; }

    /// <summary>
    /// <see cref="TooLarge"/> 時無法拆開的原因：<c>period</c>＝期間超過待派預設窗口或試跑出現點超過上限（縮短期間即可）；
    /// <c>permission</c>＝檢視者沒有派工權限（待派試跑只對能派工的人執行）。可以拆開時為 null。
    /// </summary>
    public string? UnsplitReason { get; set; }

    /// <summary>可派的「問題 × 主機」數（單位：問題×主機，同一台主機兩個問題算兩個）</summary>
    public int IssueHosts { get; set; }

    /// <summary>單位：主機日；<see cref="TooLarge"/> 時為 null</summary>
    public int? HostDays { get; set; }
}

/// <summary>不需處理</summary>
public class ReportNotNeededDto
{
    /// <summary>單位：主機日；待派試跑超過上限（拆不開）時為 null</summary>
    public int? HostDays { get; set; }
}

public class ReportEffectivenessDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<ReportEffectivenessBucketDto> Buckets { get; set; } = new();
    /// <summary>帳齡的基準日（本次資料中的最後一個紀錄日；無資料時為期間迄日）</summary>
    public string AsOf { get; set; } = string.Empty;
    public List<ReportAgingBandDto> Aging { get; set; } = new();
    /// <summary>未結案風險日數（單位：主機日）；三段拆解見 <see cref="Unresolved"/>，三段的主機日相加＝這個數字</summary>
    public int UnresolvedTotal { get; set; }
    public ReportUnresolvedBreakdownDto Unresolved { get; set; } = new();
    public ReportRecurrenceDto Recurrence { get; set; } = new();
    public ReportEffectivenessDefinitionsDto Definitions { get; set; } = new();
}

public class ReportEffectivenessDefinitionsDto
{
    public string Resolution { get; set; } = string.Empty;
    public string Aging { get; set; } = string.Empty;
    public string Recurrence { get; set; } = string.Empty;
}

/// <summary>類型趨勢：類別 × 時間桶的「問題日」熱度表（問題日＝主機 × 問題 × 日）</summary>
public class ReportCategoryTrendRowDto
{
    public string Category { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<int> Cells { get; set; } = new();
}

public class ReportCategoryTrendDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<ReportBucketDto> Buckets { get; set; } = new();
    public List<ReportCategoryTrendRowDto> Rows { get; set; } = new();
    /// <summary>全表單格最大值（前端算色階用）</summary>
    public int CellMax { get; set; }
}

/// <summary>前期趨勢的一天：DayIndex 與本期 Trend 依「期間第幾天」對齊</summary>
public class ReportPreviousTrendPointDto
{
    public int DayIndex { get; set; }
    public string Date { get; set; } = string.Empty;
    /// <summary>早於保留線的日子為 null（沒有資料，不是 0）；其餘日子是數字，真的沒有高風險才是 0</summary>
    public int? HighRisk { get; set; }
    /// <summary>同 HighRisk：早於保留線為 null</summary>
    public int? MediumRisk { get; set; }
}
