using LogForesight.Core.Service;

namespace LogForesight.Web.Models.Dto;

/// <summary>
/// 單一校準項的狀態 DTO
/// </summary>
public sealed class CalibrationItemStatusDto
{
    /// <summary>校準項目名稱</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>狀態英文列舉名稱（Insufficient / Available / Sufficient / Unavailable，樣式判斷用）</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>狀態中文描述（不足／可用／充足／無法取得，前端顯示用）</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>累積量的關鍵數字</summary>
    public Dictionary<string, object> KeyMetrics { get; set; } = new();

    /// <summary>門檻現值</summary>
    public Dictionary<string, object> CurrentThresholds { get; set; } = new();

    /// <summary>補充說明條列</summary>
    public List<string> Explanations { get; set; } = new();

    /// <summary>是否達標（可用或充足）</summary>
    public bool IsEligible { get; set; }

    public static CalibrationItemStatusDto From(CalibrationItemAssessment item)
    {
        var isEligible = item.Status is CalibrationStatus.Available or CalibrationStatus.Sufficient;
        return new CalibrationItemStatusDto
        {
            ItemName = item.ItemName,
            Status = item.Status.ToString(),
            StatusText = item.StatusText,
            // 來源是行程層級的快取物件，這裡複製一份——DTO 若拿原參考，任何呼叫端對集合的
            // 修改都會污染整個行程的快取
            KeyMetrics = new Dictionary<string, object>(item.KeyMetrics),
            CurrentThresholds = new Dictionary<string, object>(item.CurrentThresholds),
            Explanations = new List<string>(item.Explanations),
            IsEligible = isEligible
        };
    }
}

/// <summary>
/// 四項校準指標的綜合判定摘要 DTO
/// </summary>
public sealed class CalibrationStatusDto
{
    public CalibrationItemStatusDto PrtgValueBaseline { get; set; } = new();
    public CalibrationItemStatusDto PrtgRuleThresholds { get; set; } = new();
    public CalibrationItemStatusDto TriggeredFetchMagnitude { get; set; } = new();
    public CalibrationItemStatusDto ResidualCredentialThresholds { get; set; } = new();

    /// <summary>四項是否全部達到可用以上（決定是否允許直接匯出）</summary>
    public bool CanExport { get; set; }

    public static CalibrationStatusDto From(CalibrationAssessmentSummary summary)
    {
        var item1 = CalibrationItemStatusDto.From(summary.PrtgValueBaseline);
        var item2 = CalibrationItemStatusDto.From(summary.PrtgRuleThresholds);
        var item3 = CalibrationItemStatusDto.From(summary.TriggeredFetchMagnitude);
        var item4 = CalibrationItemStatusDto.From(summary.ResidualCredentialThresholds);

        return new CalibrationStatusDto
        {
            PrtgValueBaseline = item1,
            PrtgRuleThresholds = item2,
            TriggeredFetchMagnitude = item3,
            ResidualCredentialThresholds = item4,
            CanExport = item1.IsEligible && item2.IsEligible && item3.IsEligible && item4.IsEligible
        };
    }
}
