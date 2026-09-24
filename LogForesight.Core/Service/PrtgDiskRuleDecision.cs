using System.Globalization;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

public enum PrtgDiskDecisionMode { Preview, Formal }

public enum PrtgDiskDecisionExclusion
{
    None,
    NotDisk,
    HostUnmapped,
    ReadinessNotReady,
    SemanticNotReady,
    EvidenceInvalid,
    RuleMissing,
    RuleDisabled,
    RuleMismatch,
    RulePlatformMismatch,
    StaleDataAsOf,
    TrendNoHit,
    TrendInsufficientData
}

/// <summary>All proof and inputs required to make a disk trend decision; percentages are already semantically verified by the caller.</summary>
public sealed record PrtgDiskRuleDecisionInput(
    long SensorObjid,
    long DeviceObjid,
    long CurrentHostId,
    string? Category,
    PrtgValueReadinessResult? Readiness,
    PrtgDiskSemanticEvidenceValidity? SemanticEvidenceValidity,
    IReadOnlyCollection<PrtgDiskTrendDay>? DailyPercentSeries,
    DateOnly CompletedDay,
    KnownIssueRule? Rule,
    PrtgDiskDecisionMode Mode);

public sealed record PrtgDiskRuleDecisionResult(
    PrtgDiskDecisionExclusion Exclusion,
    string Reason,
    PrtgDiskTrendResult? Trend,
    PrtgFinding? Finding)
{
    public bool Eligible => Exclusion == PrtgDiskDecisionExclusion.None;
    public bool WouldHit => Trend?.Outcome == PrtgDiskTrendOutcome.Hit;
}

/// <summary>Pure gate and adapter from proven disk percentages to the existing PRTG finding contract.</summary>
public static class PrtgDiskRuleDecision
{
    public const string RuleCode = "disk_free_trend";

    public static PrtgDiskRuleDecisionResult Evaluate(PrtgDiskRuleDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(input.Category, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase))
            return Exclude(PrtgDiskDecisionExclusion.NotDisk, "感測器分類不是 disk");
        if (input.CurrentHostId <= 0)
            return Exclude(PrtgDiskDecisionExclusion.HostUnmapped, "目前主機未對應");
        if (input.Readiness is not { Status: PrtgValueReadinessStatus.Ready } readiness)
            return Exclude(PrtgDiskDecisionExclusion.ReadinessNotReady, "逐 sensor 數值就緒狀態不是 Ready");
        if (!readiness.SemanticReady)
            return Exclude(PrtgDiskDecisionExclusion.SemanticNotReady, "readiness 尚未確認量測語意");
        var validity = input.SemanticEvidenceValidity;
        if (validity is not { IsValid: true, Evidence: not null } || validity.Evidence.SensorObjid != input.SensorObjid
            || validity.Evidence.DeviceObjid != input.DeviceObjid || validity.Evidence.HostId != input.CurrentHostId)
            return Exclude(PrtgDiskDecisionExclusion.EvidenceInvalid, validity?.InvalidReason ?? "磁碟語意證據無效或對應不符");

        var rule = input.Rule;
        if (rule is null)
            return Exclude(PrtgDiskDecisionExclusion.RuleMissing, "沒有磁碟趨勢規則");
        if (!string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase))
            return Exclude(PrtgDiskDecisionExclusion.RulePlatformMismatch, "規則平台不是 prtg");
        if (!string.Equals(rule.PrtgRuleCode, RuleCode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rule.PrtgSensorCategory, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase)
            || rule.PrtgDiskTrendThresholds is null)
            return Exclude(PrtgDiskDecisionExclusion.RuleMismatch, "規則代碼、分類或趨勢門檻不符");
        if (input.Mode == PrtgDiskDecisionMode.Formal && !rule.Enabled)
            return Exclude(PrtgDiskDecisionExclusion.RuleDisabled, "正式模式不接受停用規則");

        // The completed-day boundary is explicit. Later rows cannot leak into either the trend or its finding.
        var boundedSeries = input.DailyPercentSeries?.Where(x => x.Day <= input.CompletedDay).ToArray();
        var trend = PrtgDiskTrendEvaluator.Evaluate(boundedSeries, rule.PrtgDiskTrendThresholds);
        if (trend.Outcome == PrtgDiskTrendOutcome.InsufficientData)
            return new(PrtgDiskDecisionExclusion.TrendInsufficientData, trend.Explanation, trend, null);
        if (trend.Outcome != PrtgDiskTrendOutcome.Hit)
            return new(PrtgDiskDecisionExclusion.TrendNoHit, trend.Explanation, trend, null);
        if (trend.LatestDay != input.CompletedDay)
            return new(PrtgDiskDecisionExclusion.StaleDataAsOf,
                $"最新有效趨勢日為 {trend.LatestDay:yyyy-MM-dd}，不等於指定完成日 {input.CompletedDay:yyyy-MM-dd}。",
                trend, null);
        if (input.Mode == PrtgDiskDecisionMode.Preview)
            return new(PrtgDiskDecisionExclusion.None, trend.Explanation, trend, null);

        var current = trend.CurrentAvailablePercent!.Value;
        var latestDay = trend.LatestDay!.Value;
        var detail = string.Format(CultureInfo.InvariantCulture,
            "磁碟可用空間趨勢：host {0}，sensor {1}，目前 {2:F1}%；有效日 {3} 日／可用小時 {4} 小時；穩健下降 {5:F2} 百分點／日；預估 {6:F1} 日耗盡；資料截至 {7:yyyy-MM-dd}（門檻暫定：低水位 {8:F1}%，下降至少 {9:F2} 百分點／日，耗盡 {10:F1} 日內）。",
            input.CurrentHostId, input.SensorObjid, current, trend.ValidDayCount, readiness.UsableHours,
            trend.RobustDeclinePercentagePointsPerDay!.Value, trend.EstimatedDaysToDepletion!.Value,
            latestDay, rule.PrtgDiskTrendThresholds.LowWaterPercent,
            rule.PrtgDiskTrendThresholds.MinimumDeclinePercentagePointsPerDay,
            rule.PrtgDiskTrendThresholds.MaximumDaysToDepletion);
        var finding = new PrtgFinding(input.DeviceObjid, input.SensorObjid, RuleCode, detail,
            trend.ValidDayCount, rule, Acknowledged: false) { SensorCategory = PrtgSensorCategories.Disk };
        return new(PrtgDiskDecisionExclusion.None, trend.Explanation, trend, finding);
    }

    private static PrtgDiskRuleDecisionResult Exclude(PrtgDiskDecisionExclusion reason, string detail) =>
        new(reason, detail, null, null);
}
