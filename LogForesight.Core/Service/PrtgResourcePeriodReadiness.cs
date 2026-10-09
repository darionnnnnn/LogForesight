using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

public enum PrtgResourceFamily { Cpu, Memory, Disk }
public enum PrtgResourceSemantic { CpuUsed, MemoryUsed, MemoryRemaining, DiskRemaining }
public enum PrtgResourceDecisionMode { Hint, FormalRisk }
public enum PrtgResourceDecisionKind { Hit, Recovery, NoHit, Insufficient }

/// <summary>Explicitly authorized identity and proof context for one resource/channel.</summary>
public sealed record PrtgResourceCurrentContext(
    long SensorObjid,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string ResourceEpoch,
    string SemanticVersion,
    string StrategyVersion,
    int StrategyMinutes,
    DateTime StrategyEffectiveFromHour,
    TimeSpan ConfirmedScanInterval,
    string RawTimestampTimeZoneId,
    string AnalysisTimeZoneId,
    string ProfileFingerprint = "");

/// <summary>Typed semantic contract. Values are accepted only after this metadata is verified.</summary>
public sealed record PrtgResourceMeasurementDefinition(
    PrtgResourceSemantic Semantic,
    string Unit,
    double ScaleToPercent,
    bool IsVerified);

/// <summary>An hourly aggregate paired with its slot proof; aggregate fields are cross-checked.</summary>
public sealed record PrtgResourceHourlyEvidence(
    long SensorObjid,
    DateTime WallPeriodStart,
    PrtgTrustedSampleProof? ContextProof,
    IReadOnlyCollection<int>? QualityGoodSlots,
    double? SummaryAverage,
    double? SummaryCoveragePercent);

public sealed record PrtgResourceReadinessInput(
    PrtgResourceFamily Family,
    PrtgResourceCurrentContext? AuthorizedContext,
    PrtgResourceMeasurementDefinition? Measurement,
    IReadOnlyCollection<PrtgResourceHourlyEvidence>? Hours,
    DateTime AsOfUtc);

public sealed record PrtgResourceReadyHour(
    DateTime WallPeriodStart,
    double AveragePercent,
    double CoveragePercent,
    int GoodSlots,
    int RequiredSlots);

public sealed record PrtgResourcePeriodReadiness(
    bool IsReady,
    string ReasonCode,
    string ProfileFingerprint,
    PrtgResourceFamily Family,
    DateTime? AsOfUtc,
    IReadOnlyList<PrtgResourceReadyHour> Window,
    double? WindowCoveragePercent);

/// <summary>
/// Validates bounded, consecutive, completed wall hours from trusted physical sample slots.
/// It deliberately does not persist state or infer readiness from older hours.
/// </summary>
public static class PrtgResourcePeriodReadinessEvaluator
{
    public const int RequiredConsecutiveHours = 2;
    public const double MinimumCoveragePercent = 75.0;
    public const int MaximumInputHours = 72;

    public static PrtgResourcePeriodReadiness Evaluate(PrtgResourceReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var fingerprint = PrtgResourcePressureEvaluator.GetProfileFingerprint(input.Family, input.AuthorizedContext);
        if (input.AuthorizedContext is null || input.Measurement is null || input.Hours is null)
            return Fail(input.Family, fingerprint, "missing-context-or-evidence", input.AsOfUtc);
        var context = input.AuthorizedContext;
        if (context.SensorObjid <= 0 || input.AsOfUtc.Kind != DateTimeKind.Utc || input.AsOfUtc > DateTime.UtcNow ||
            !ValidContext(context) || !ValidMeasurement(input.Family, input.Measurement))
            return Fail(input.Family, fingerprint,
                input.AsOfUtc.Kind == DateTimeKind.Utc && input.AsOfUtc > DateTime.UtcNow
                    ? "future-as-of" : "invalid-authorized-context-or-semantics", input.AsOfUtc);
        if (input.Hours.Count > MaximumInputHours)
            return Fail(input.Family, fingerprint, "input-window-exceeds-bound", input.AsOfUtc);
        if (!TryZone(context.AnalysisTimeZoneId, out var zone))
            return Fail(input.Family, fingerprint, "invalid-analysis-time-zone", input.AsOfUtc);

        var asOfLocal = TimeZoneInfo.ConvertTimeFromUtc(input.AsOfUtc, zone);
        if (zone.IsAmbiguousTime(asOfLocal) || zone.IsInvalidTime(asOfLocal))
            return Fail(input.Family, fingerprint, "ambiguous-as-of-wall-time", input.AsOfUtc);
        var currentWallHour = WallHour(asOfLocal);
        var latestCompleted = currentWallHour.AddHours(-1);
        var previous = latestCompleted.AddHours(-1);
        if (zone.IsAmbiguousTime(latestCompleted) || zone.IsInvalidTime(latestCompleted) ||
            zone.IsAmbiguousTime(previous) || zone.IsInvalidTime(previous))
            return Fail(input.Family, fingerprint, "completed-wall-hour-unavailable", input.AsOfUtc);

        var expected = new[] { previous, latestCompleted };
        var found = new Dictionary<DateTime, PrtgResourceReadyHour>();
        foreach (var hour in input.Hours)
        {
            if (hour is null || hour.SensorObjid != context.SensorObjid ||
                hour.WallPeriodStart.Kind != DateTimeKind.Unspecified ||
                hour.WallPeriodStart.Minute != 0 || hour.WallPeriodStart.Second != 0 ||
                hour.WallPeriodStart.Ticks % TimeSpan.TicksPerHour != 0)
                return Fail(input.Family, fingerprint, "invalid-hour-identity", input.AsOfUtc);
            if (hour.WallPeriodStart > latestCompleted)
                return Fail(input.Family, fingerprint, "open-or-future-hour", input.AsOfUtc);
            if (!expected.Contains(hour.WallPeriodStart)) continue; // Historical and open-hour data cannot backfill.
            if (!TryValidateHour(hour, context, input.AsOfUtc, input.Measurement, out var readyHour, out var reason))
                return Fail(input.Family, fingerprint, reason, input.AsOfUtc);
            if (!found.TryAdd(hour.WallPeriodStart, readyHour!))
                return Fail(input.Family, fingerprint, "duplicate-hour", input.AsOfUtc);
        }
        if (found.Count != RequiredConsecutiveHours || expected.Any(h => !found.ContainsKey(h)))
            return Fail(input.Family, fingerprint, "latest-two-completed-hours-missing", input.AsOfUtc);
        var window = expected.Select(h => found[h]).ToArray();
        if (window.Any(h => h.CoveragePercent < MinimumCoveragePercent))
            return Fail(input.Family, fingerprint, "hour-coverage-below-75-percent", input.AsOfUtc, window);
        return new(true, "ready", fingerprint, input.Family, input.AsOfUtc, window,
            window.Average(h => h.CoveragePercent));
    }

    private static bool TryValidateHour(PrtgResourceHourlyEvidence item, PrtgResourceCurrentContext context,
        DateTime asOfUtc, PrtgResourceMeasurementDefinition measurement, out PrtgResourceReadyHour? result,
        out string reason)
    {
        result = null;
        reason = "invalid-hour-proof";
        var proof = item.ContextProof;
        if (proof is null || item.QualityGoodSlots is null || !proof.IsStructurallyValid() ||
            !Matches(proof, context) || !proof.MatchesHour(item.WallPeriodStart)) return false;
        var slots = proof.Slots;
        if (slots.Any(s => s.MeasuredAt > asOfUtc || s.ReceivedAt > asOfUtc))
        { reason = "future-or-untrusted-as-of-sample"; return false; }
        var expectedSlots = 60 / context.StrategyMinutes;
        var proofSlots = slots.Select(s => s.Slot).ToHashSet();
        var goodSlots = item.QualityGoodSlots.ToHashSet();
        if (goodSlots.Count != item.QualityGoodSlots.Count || goodSlots.Any(s => s < 0 || s >= expectedSlots) ||
            !goodSlots.SetEquals(proofSlots) || slots.Count > expectedSlots)
        { reason = "quality-slots-do-not-match-proof"; return false; }
        var coverage = goodSlots.Count * 100.0 / expectedSlots;
        var average = slots.Average(s => s.Value * measurement.ScaleToPercent);
        if (slots.Any(s => !double.IsFinite(s.Value) || s.Value * measurement.ScaleToPercent is < 0 or > 100) ||
            !double.IsFinite(average) || average is < 0 or > 100 ||
            !double.IsFinite(item.SummaryAverage ?? double.NaN) ||
            !double.IsFinite(item.SummaryCoveragePercent ?? double.NaN) ||
            (item.SummaryAverage!.Value is < 0 or > 100) ||
            (item.SummaryCoveragePercent!.Value is < 0 or > 100) ||
            Math.Abs(item.SummaryAverage.Value - average) > 0.000001 ||
            Math.Abs(item.SummaryCoveragePercent.Value - coverage) > 0.000001)
        { reason = "summary-does-not-match-proved-slots"; return false; }
        var normalized = item.SummaryAverage.Value;
        if (measurement.Semantic == PrtgResourceSemantic.MemoryRemaining) normalized = 100 - normalized;
        result = new(item.WallPeriodStart, normalized, coverage, goodSlots.Count, expectedSlots);
        reason = "ready";
        return true;
    }

    private static bool Matches(PrtgTrustedSampleProof proof, PrtgResourceCurrentContext context) =>
        proof.SourceGeneration == context.SourceGeneration && proof.ResourceGeneration == context.ResourceGeneration &&
        proof.ChannelGeneration == context.ChannelGeneration && proof.ResourceEpoch == context.ResourceEpoch &&
        proof.SemanticVersion == context.SemanticVersion && proof.StrategyVersion == context.StrategyVersion &&
        proof.StrategyMinutes == context.StrategyMinutes && proof.StrategyEffectiveFromHour == context.StrategyEffectiveFromHour &&
        proof.ConfirmedScanInterval == context.ConfirmedScanInterval &&
        proof.RawTimestampTimeZoneId == context.RawTimestampTimeZoneId &&
        proof.AnalysisTimeZoneId == context.AnalysisTimeZoneId;

    private static bool ValidContext(PrtgResourceCurrentContext c) =>
        !string.IsNullOrWhiteSpace(c.SourceGeneration) && !string.IsNullOrWhiteSpace(c.ResourceGeneration) &&
        !string.IsNullOrWhiteSpace(c.ChannelGeneration) && !string.IsNullOrWhiteSpace(c.ResourceEpoch) &&
        !string.IsNullOrWhiteSpace(c.SemanticVersion) && !string.IsNullOrWhiteSpace(c.StrategyVersion) &&
        c.StrategyMinutes is 5 or 15 && c.StrategyEffectiveFromHour.Kind == DateTimeKind.Utc &&
        c.ConfirmedScanInterval > TimeSpan.Zero && c.ConfirmedScanInterval <= TimeSpan.FromHours(1) &&
        TryZone(c.RawTimestampTimeZoneId, out _) && TryZone(c.AnalysisTimeZoneId, out _);

    private static bool ValidMeasurement(PrtgResourceFamily family, PrtgResourceMeasurementDefinition m) =>
        m.IsVerified && m.Unit == "%" && m.ScaleToPercent == 1.0 && double.IsFinite(m.ScaleToPercent) &&
        (family switch
        {
            PrtgResourceFamily.Cpu => m.Semantic == PrtgResourceSemantic.CpuUsed,
            PrtgResourceFamily.Memory => m.Semantic is PrtgResourceSemantic.MemoryUsed or PrtgResourceSemantic.MemoryRemaining,
            PrtgResourceFamily.Disk => m.Semantic == PrtgResourceSemantic.DiskRemaining,
            _ => false
        });

    private static DateTime WallHour(DateTime value) => new(value.Year, value.Month, value.Day, value.Hour, 0, 0,
        DateTimeKind.Unspecified);
    private static bool TryZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static PrtgResourcePeriodReadiness Fail(PrtgResourceFamily family, string fingerprint, string reason,
        DateTime asOfUtc, IReadOnlyList<PrtgResourceReadyHour>? window = null) =>
        new(false, reason, fingerprint, family, asOfUtc.Kind == DateTimeKind.Utc ? asOfUtc : null,
            window ?? Array.Empty<PrtgResourceReadyHour>(), window?.Count > 0 ? window.Average(h => h.CoveragePercent) : null);
}
