namespace LogForesight.Core.Service;

/// <summary>純計算用的每日可用空間百分比資料。百分比只能由呼叫端在語意確認後傳入。</summary>
public sealed record PrtgDiskTrendDay(DateOnly Day, double? AvailablePercent);

/// <summary>趨勢門檻。斜率單位為百分點／日，時間單位為日；均為待實機校準的暫定值。</summary>
public sealed record PrtgDiskTrendThresholds(
    double LowWaterPercent,
    double MinimumDeclinePercentagePointsPerDay,
    double MaximumDaysToDepletion,
    int MinimumValidDays,
    int RecentWindowDays,
    double MinimumDecliningDayRatio)
{
    /// <summary>保守的程式預設值，未經實機 28 日資料校準，不代表正式門檻。</summary>
    public static PrtgDiskTrendThresholds Provisional { get; } = new(
        LowWaterPercent: 20,
        MinimumDeclinePercentagePointsPerDay: 0.5,
        MaximumDaysToDepletion: 30,
        MinimumValidDays: 28,
        RecentWindowDays: 35,
        MinimumDecliningDayRatio: 0.70);
}

public enum PrtgDiskTrendOutcome
{
    Hit,
    NoHit,
    InsufficientData
}

/// <summary>結果與可供預覽說明的計算證據；單位明確使用百分點／日及日。</summary>
public sealed record PrtgDiskTrendResult(
    PrtgDiskTrendOutcome Outcome,
    string Explanation,
    int ValidDayCount,
    DateOnly? LatestDay,
    double? CurrentAvailablePercent,
    double? RobustDeclinePercentagePointsPerDay,
    double? DecliningDayRatio,
    double? EstimatedDaysToDepletion);

/// <summary>單一已確認百分比 sensor 的確定性趨勢判定，不執行任何 I/O。</summary>
public static class PrtgDiskTrendEvaluator
{
    public static PrtgDiskTrendResult Evaluate(
        IReadOnlyCollection<PrtgDiskTrendDay>? dailyData,
        PrtgDiskTrendThresholds? thresholds = null)
    {
        thresholds ??= PrtgDiskTrendThresholds.Provisional;
        if (!ValidThresholds(thresholds))
            return Insufficient("門檻無效；百分比、百分點／日、日數及比例必須在合理範圍內。", 0, null);
        if (dailyData is null || dailyData.Count == 0)
            return Insufficient("沒有每日資料。", 0, null);

        var ordered = dailyData.OrderBy(x => x.Day).ToArray();
        if (ordered.Select(x => x.Day).Distinct().Count() != ordered.Length)
            return Insufficient("每日資料有重複日期，無法確定性評估。", 0, ordered[^1].Day);
        if (ordered.Any(x => x.AvailablePercent is null || !double.IsFinite(x.AvailablePercent.Value)
                             || x.AvailablePercent.Value is < 0 or > 100))
            return Insufficient("每日資料含缺值或 0–100% 範圍外數值。", 0, ordered[^1].Day);

        var latest = ordered[^1].Day;
        var windowStart = latest.AddDays(-(thresholds.RecentWindowDays - 1));
        var recent = ordered.Where(x => x.Day >= windowStart && x.Day <= latest).ToArray();
        if (recent.Length < thresholds.MinimumValidDays
            || recent[^1].Day.DayNumber - recent[0].Day.DayNumber < thresholds.MinimumValidDays - 1)
            return Insufficient($"近期有效日不足：{recent.Length}/{thresholds.MinimumValidDays} 日。", recent.Length, latest);

        var values = recent.Select(x => x.AvailablePercent!.Value).ToArray();
        var deltas = new List<double>();
        for (var i = 1; i < recent.Length; i++)
        {
            var gap = recent[i].Day.DayNumber - recent[i - 1].Day.DayNumber;
            if (gap > 0) deltas.Add((values[i] - values[i - 1]) / gap);
        }
        var fallingRatio = deltas.Count == 0 ? 0 : deltas.Count(x => x < 0) / (double)deltas.Count;
        var slopes = new List<double>();
        for (var i = 0; i < recent.Length; i++)
        for (var j = i + 1; j < recent.Length; j++)
        {
            var days = recent[j].Day.DayNumber - recent[i].Day.DayNumber;
            if (days > 0) slopes.Add((values[j] - values[i]) / days);
        }

        var slope = Median(slopes);
        var decline = -slope;
        var current = values[^1];
        var depletionDays = decline > 0 ? current / decline : (double?)null;
        var evidence = new PrtgDiskTrendResult(PrtgDiskTrendOutcome.NoHit, "", recent.Length, latest,
            current, decline, fallingRatio, depletionDays);

        if (current > thresholds.LowWaterPercent)
            return evidence with { Explanation = $"目前可用空間 {current:F1}% 高於低水位 {thresholds.LowWaterPercent:F1}%。" };
        if (decline < thresholds.MinimumDeclinePercentagePointsPerDay)
            return evidence with { Explanation = $"穩健下降斜率 {decline:F2} 百分點／日未達 {thresholds.MinimumDeclinePercentagePointsPerDay:F2}。" };
        if (fallingRatio < thresholds.MinimumDecliningDayRatio)
            return evidence with { Explanation = $"下降日比例 {fallingRatio:P0} 未達 {thresholds.MinimumDecliningDayRatio:P0}，趨勢不夠持續。" };
        if (depletionDays > thresholds.MaximumDaysToDepletion)
            return evidence with { Explanation = $"預估耗盡時距 {depletionDays:F1} 日，超出 {thresholds.MaximumDaysToDepletion:F1} 日處理窗。" };

        return evidence with
        {
            Outcome = PrtgDiskTrendOutcome.Hit,
            Explanation = $"近期 {recent.Length} 個有效日持續下降；目前 {current:F1}%，穩健下降 {decline:F2} 百分點／日，下降日比例 {fallingRatio:P0}，預估 {depletionDays:F1} 日耗盡。門檻為暫定，尚未實機校準。"
        };
    }

    private static bool ValidThresholds(PrtgDiskTrendThresholds t) =>
        double.IsFinite(t.LowWaterPercent) && t.LowWaterPercent is >= 0 and <= 100
        && double.IsFinite(t.MinimumDeclinePercentagePointsPerDay) && t.MinimumDeclinePercentagePointsPerDay > 0
        && double.IsFinite(t.MaximumDaysToDepletion) && t.MaximumDaysToDepletion > 0
        && t.MinimumValidDays >= 2 && t.RecentWindowDays >= t.MinimumValidDays
        && double.IsFinite(t.MinimumDecliningDayRatio) && t.MinimumDecliningDayRatio is > 0 and <= 1;

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }

    private static PrtgDiskTrendResult Insufficient(string reason, int count, DateOnly? latest) =>
        new(PrtgDiskTrendOutcome.InsufficientData, reason, count, latest, null, null, null, null);
}
