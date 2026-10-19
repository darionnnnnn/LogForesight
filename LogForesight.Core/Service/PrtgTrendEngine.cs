namespace LogForesight.Core.Service;

public enum PrtgTrendDirection { HigherIsWorse, LowerIsWorse }
public enum PrtgTrendOutcome { Hit, NoHit, InsufficientData }

/// <summary>一條趨勢規則的門檻（全部可在規則頁編輯；數值為暫定、未經實機校準）</summary>
public sealed record PrtgTrendThresholds(
    int WindowDays,                  // 觀察窗（日），預設 35
    int MinimumValidDays,            // 最少有效日，預設 28
    double MinimumSlopePerDay,       // 惡化方向的最小穩健斜率（每日；百分點或毫秒），ping 規則不用（設 0）
    double MinimumWorseningDayRatio, // 相鄰有效日朝惡化方向變動的比例下限（0–1）
    double TargetValue,              // 門檻值：記憶體 90（%）、CPU 70（%）、ping 50（ms）
    int HorizonDays,                 // 預測期（日），記憶體用；預設 7
    int RecentDays,                  // 近期天數，CPU 平均與 ping 中位數用；預設 7
    double BaselineMultiplier)       // ping：近期中位數須 ≥ 前段中位數 × 此倍數；其餘規則設 1
{
    public static PrtgTrendThresholds MemoryProvisional { get; } = new(35, 28, 0.3, 0.70, 90, 7, 7, 1);
    public static PrtgTrendThresholds CpuProvisional { get; } = new(35, 28, 0.3, 0.70, 70, 7, 7, 1);
    public static PrtgTrendThresholds PingProvisional { get; } = new(35, 28, 0, 0.60, 50, 7, 7, 1.5);

    public bool IsValid() =>
        WindowDays >= MinimumValidDays
        && MinimumValidDays >= 2
        && WindowDays <= 400
        && double.IsFinite(MinimumSlopePerDay) && MinimumSlopePerDay >= 0
        && double.IsFinite(MinimumWorseningDayRatio) && MinimumWorseningDayRatio is > 0 and <= 1
        && double.IsFinite(TargetValue) && TargetValue >= 0
        && HorizonDays is >= 1 and <= 365
        && RecentDays >= 1 && RecentDays <= MinimumValidDays
        && double.IsFinite(BaselineMultiplier) && BaselineMultiplier >= 1;
}

public sealed record PrtgTrendResult(
    PrtgTrendOutcome Outcome,
    string Explanation,
    int ValidDayCount,
    DateOnly? LatestDay,
    double? CurrentValue,
    double? WorseningSlopePerDay,
    double? WorseningDayRatio,
    double? EstimatedDaysToTarget);

public static class PrtgTrendEngine
{
    public static PrtgTrendResult EvaluateMemoryUsed(
        IReadOnlyCollection<(DateOnly Day, double UsedPercent)> daily,
        PrtgTrendThresholds t)
    {
        var error = Preprocess(
            daily?.Select(x => (x.Day, x.UsedPercent)).ToArray(),
            t,
            0,
            100,
            "每日資料含 0–100% 範圍外數值。",
            out var windowPoints,
            out var latestDay);
        if (error is not null) return error;

        var current = windowPoints[^1].Value;
        var slope = TheilSenSlope(windowPoints);
        var worseningSlope = slope;
        var worseningRatio = WorseningRatio(windowPoints, PrtgTrendDirection.HigherIsWorse);

        double? estimatedDays = current >= t.TargetValue
            ? 0.0
            : (slope > 0 ? (t.TargetValue - current) / slope : null);

        bool reachedTargetOrHorizon = current >= t.TargetValue
            || (estimatedDays.HasValue && estimatedDays.Value <= t.HorizonDays);

        if (worseningSlope < t.MinimumSlopePerDay)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"穩健上升斜率 {worseningSlope:F2} 百分點／日未達 {t.MinimumSlopePerDay:F2}。",
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                estimatedDays);
        }

        if (worseningRatio < t.MinimumWorseningDayRatio)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"惡化日比例 {worseningRatio:P0} 未達 {t.MinimumWorseningDayRatio:P0}，趨勢不夠持續。",
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                estimatedDays);
        }

        if (!reachedTargetOrHorizon)
        {
            var explanation = estimatedDays.HasValue
                ? $"預估 {estimatedDays.Value:F1} 日達到 {t.TargetValue:F1}%，超出 {t.HorizonDays:F1} 日預測期。"
                : $"目前 {current:F1}% 未達門檻 {t.TargetValue:F1}%，且斜率未朝目標上升。";

            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                explanation,
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                estimatedDays);
        }

        return new PrtgTrendResult(
            PrtgTrendOutcome.Hit,
            $"近期 {windowPoints.Length} 個有效日持續上升；目前 {current:F1}%，穩健上升 {worseningSlope:F2} 百分點／日，惡化日比例 {worseningRatio:P0}，預估 {estimatedDays:F1} 日達到門檻 {t.TargetValue:F1}%。門檻為暫定，尚未實機校準。",
            windowPoints.Length,
            latestDay,
            current,
            worseningSlope,
            worseningRatio,
            estimatedDays);
    }

    public static PrtgTrendResult EvaluateCpuLoad(
        IReadOnlyCollection<(DateOnly Day, double UsedPercent)> daily,
        PrtgTrendThresholds t)
    {
        var error = Preprocess(
            daily?.Select(x => (x.Day, x.UsedPercent)).ToArray(),
            t,
            0,
            100,
            "每日資料含 0–100% 範圍外數值。",
            out var windowPoints,
            out var latestDay);
        if (error is not null) return error;

        var current = windowPoints[^1].Value;
        var slope = TheilSenSlope(windowPoints);
        var worseningSlope = slope;
        var worseningRatio = WorseningRatio(windowPoints, PrtgTrendDirection.HigherIsWorse);
        var recentAvg = windowPoints[^t.RecentDays..].Average(x => x.Value);

        if (worseningSlope < t.MinimumSlopePerDay)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"穩健上升斜率 {worseningSlope:F2} 百分點／日未達 {t.MinimumSlopePerDay:F2}。",
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                null);
        }

        if (worseningRatio < t.MinimumWorseningDayRatio)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"惡化日比例 {worseningRatio:P0} 未達 {t.MinimumWorseningDayRatio:P0}，趨勢不夠持續。",
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                null);
        }

        if (recentAvg < t.TargetValue)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"近 {t.RecentDays} 日平均 {recentAvg:F1}% 未達門檻 {t.TargetValue:F1}%。",
                windowPoints.Length,
                latestDay,
                current,
                worseningSlope,
                worseningRatio,
                null);
        }

        return new PrtgTrendResult(
            PrtgTrendOutcome.Hit,
            $"近期 {windowPoints.Length} 個有效日持續上升；目前 {current:F1}%（近 {t.RecentDays} 日平均 {recentAvg:F1}%），穩健上升 {worseningSlope:F2} 百分點／日，惡化日比例 {worseningRatio:P0}。門檻為暫定，尚未實機校準。",
            windowPoints.Length,
            latestDay,
            current,
            worseningSlope,
            worseningRatio,
            null);
    }

    public static PrtgTrendResult EvaluatePingLatency(
        IReadOnlyCollection<(DateOnly Day, double Milliseconds)> daily,
        PrtgTrendThresholds t)
    {
        var error = Preprocess(
            daily?.Select(x => (x.Day, x.Milliseconds)).ToArray(),
            t,
            0,
            double.PositiveInfinity,
            "每日資料含範圍外數值（小於 0 毫秒）。",
            out var windowPoints,
            out var latestDay);
        if (error is not null) return error;

        var recentSlice = windowPoints[^t.RecentDays..];
        var earlierSlice = windowPoints[..^t.RecentDays];

        if (earlierSlice.Length < t.RecentDays)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.InsufficientData,
                $"基準期有效日不足：{earlierSlice.Length}／{t.RecentDays} 日。",
                windowPoints.Length,
                latestDay,
                null, null, null, null);
        }

        var baselineSlice = earlierSlice.TakeLast(28).ToArray();
        var baseline = Median(baselineSlice.Select(x => x.Value).ToList());
        var recent = Median(recentSlice.Select(x => x.Value).ToList());

        var slope = TheilSenSlope(windowPoints);
        var worseningSlope = slope;
        var worseningRatio = WorseningRatio(windowPoints, PrtgTrendDirection.HigherIsWorse);

        double currentValue = recent;

        if (worseningRatio < t.MinimumWorseningDayRatio)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"惡化日比例 {worseningRatio:P0} 未達 {t.MinimumWorseningDayRatio:P0}，趨勢不夠持續。",
                windowPoints.Length,
                latestDay,
                currentValue,
                worseningSlope,
                worseningRatio,
                null);
        }

        var requiredBaselineTarget = baseline * t.BaselineMultiplier;
        if (recent < requiredBaselineTarget)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"近期中位數 {recent:F1} ms 未達基準期中位數 {baseline:F1} ms 的 {t.BaselineMultiplier:F1} 倍（門檻 {requiredBaselineTarget:F1} ms）。",
                windowPoints.Length,
                latestDay,
                currentValue,
                worseningSlope,
                worseningRatio,
                null);
        }

        if (recent < t.TargetValue)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.NoHit,
                $"近期中位數 {recent:F1} ms 未達門檻 {t.TargetValue:F1} ms。",
                windowPoints.Length,
                latestDay,
                currentValue,
                worseningSlope,
                worseningRatio,
                null);
        }

        return new PrtgTrendResult(
            PrtgTrendOutcome.Hit,
            $"近期 {windowPoints.Length} 個有效日持續惡化；目前 {currentValue:F1} ms（基準期中位數 {baseline:F1} ms），穩健上升 {worseningSlope:F2} ms／日，惡化日比例 {worseningRatio:P0}。門檻為暫定，尚未實機校準。",
            windowPoints.Length,
            latestDay,
            currentValue,
            worseningSlope,
            worseningRatio,
            null);
    }

    internal static double TheilSenSlope(IReadOnlyList<(DateOnly Day, double Value)> points)
    {
        if (points.Count < 2) return 0;
        var ordered = points.OrderBy(p => p.Day).ToArray();
        var slopes = new List<double>(ordered.Length * (ordered.Length - 1) / 2);
        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = i + 1; j < ordered.Length; j++)
            {
                var days = ordered[j].Day.DayNumber - ordered[i].Day.DayNumber;
                if (days > 0)
                {
                    slopes.Add((ordered[j].Value - ordered[i].Value) / days);
                }
            }
        }
        return Median(slopes);
    }

    internal static double WorseningRatio(IReadOnlyList<(DateOnly Day, double Value)> points, PrtgTrendDirection direction)
    {
        if (points.Count < 2) return 0;
        var ordered = points.OrderBy(p => p.Day).ToArray();
        var deltas = new List<double>(ordered.Length - 1);
        for (var i = 1; i < ordered.Length; i++)
        {
            var gap = ordered[i].Day.DayNumber - ordered[i - 1].Day.DayNumber;
            if (gap > 0)
            {
                deltas.Add((ordered[i].Value - ordered[i - 1].Value) / gap);
            }
        }
        if (deltas.Count == 0) return 0;
        var worseningCount = direction == PrtgTrendDirection.HigherIsWorse
            ? deltas.Count(x => x > 0)
            : deltas.Count(x => x < 0);
        return worseningCount / (double)deltas.Count;
    }

    internal static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2.0 : values[middle];
    }

    private static PrtgTrendResult? Preprocess(
        (DateOnly Day, double Value)[]? daily,
        PrtgTrendThresholds t,
        double minAllowed,
        double maxAllowed,
        string rangeErrorMessage,
        out (DateOnly Day, double Value)[] windowPoints,
        out DateOnly? latestDay)
    {
        windowPoints = [];
        latestDay = null;

        if (t is null || !t.IsValid())
        {
            return new PrtgTrendResult(PrtgTrendOutcome.InsufficientData, "門檻無效。", 0, null, null, null, null, null);
        }

        if (daily is null || daily.Length == 0)
        {
            return new PrtgTrendResult(PrtgTrendOutcome.InsufficientData, $"有效日 0／{t.MinimumValidDays}。", 0, null, null, null, null, null);
        }

        var ordered = daily.OrderBy(x => x.Day).ToArray();
        var latest = ordered[^1].Day;
        latestDay = latest;

        if (ordered.Select(x => x.Day).Distinct().Count() != ordered.Length)
        {
            return new PrtgTrendResult(PrtgTrendOutcome.InsufficientData, "每日資料有重複日期，無法確定性評估。", 0, latest, null, null, null, null);
        }

        if (ordered.Any(x => !double.IsFinite(x.Value)))
        {
            return new PrtgTrendResult(PrtgTrendOutcome.InsufficientData, "每日資料含缺值或非有限值。", 0, latest, null, null, null, null);
        }

        if (ordered.Any(x => x.Value < minAllowed || x.Value > maxAllowed))
        {
            return new PrtgTrendResult(PrtgTrendOutcome.InsufficientData, rangeErrorMessage, 0, latest, null, null, null, null);
        }

        var windowStart = latest.AddDays(-(t.WindowDays - 1));
        windowPoints = ordered.Where(x => x.Day >= windowStart && x.Day <= latest).ToArray();

        if (windowPoints.Length < t.MinimumValidDays
            || windowPoints[^1].Day.DayNumber - windowPoints[0].Day.DayNumber < t.MinimumValidDays - 1)
        {
            return new PrtgTrendResult(
                PrtgTrendOutcome.InsufficientData,
                $"有效日 {windowPoints.Length}／{t.MinimumValidDays}。",
                windowPoints.Length,
                latestDay,
                null, null, null, null);
        }

        return null;
    }
}
