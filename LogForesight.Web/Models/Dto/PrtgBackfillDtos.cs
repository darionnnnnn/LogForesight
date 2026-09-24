namespace LogForesight.Web.Models.Dto;

/// <summary>指定主機與日期的 PRTG 歷史數值回填要求。</summary>
public sealed class PrtgSelectedBackfillRequest
{
    public IReadOnlyList<long> HostIds { get; set; } = Array.Empty<long>();
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
}

/// <summary>指定歷史範圍的唯讀回填預估。</summary>
public sealed class PrtgSelectedBackfillPreviewDto
{
    public DateTime FromDate { get; init; }
    public DateTime ToDate { get; init; }
    public IReadOnlyList<long> HostIds { get; init; } = Array.Empty<long>();
    public int HostCount { get; init; }
    public int DayCount { get; init; }
    public int EstimatedRequests { get; init; }
    /// <summary>目前可能被成功 exact ok 值取代的 sampled 小時列上限估計。</summary>
    public int EstimatedSampledRowsToReplace { get; init; }
    public int DaysWithTargets { get; init; }
    public bool HasTargets => EstimatedRequests > 0;
    public string Message { get; init; } = "";
}

/// <summary>指定範圍回填啟動結果。</summary>
public sealed class StartPrtgSelectedBackfillResultDto
{
    public bool Started { get; init; }
    public PrtgSelectedBackfillPreviewDto Preview { get; init; } = new();
}
