namespace LogForesight.Core.Models;

/// <summary>有效歷史判定的版本與原始敘事，僅供有整日可見權限者查閱。</summary>
public sealed class HistoricalRiskReview
{
    public int Version { get; set; }
    public string Status { get; set; } = "pending";
    public string Reason { get; set; } = string.Empty;
    public string OriginalRiskLevel { get; set; } = string.Empty;
    public string? OriginalRiskBasis { get; set; }
    public string OriginalHeadline { get; set; } = string.Empty;
    public string OriginalSummary { get; set; } = string.Empty;
    public DateTime ReviewedAtUtc { get; set; }
}
