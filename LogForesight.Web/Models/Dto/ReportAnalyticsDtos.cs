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

public class ReportEffectivenessDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public List<ReportEffectivenessBucketDto> Buckets { get; set; } = new();
    /// <summary>帳齡的基準日（本次資料中的最後一個紀錄日；無資料時為期間迄日）</summary>
    public string AsOf { get; set; } = string.Empty;
    public List<ReportAgingBandDto> Aging { get; set; } = new();
    public int UnresolvedTotal { get; set; }
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
