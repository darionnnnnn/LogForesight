using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

public enum PrtgValueReadinessStatus
{
    InsufficientData,
    Ready,
    Unknown
}

public sealed record PrtgReadinessHour(DateTime PeriodStart, string Quality, double? Coverage);

/// <summary>供值型 readiness 計算使用的單顆磁碟候選與其有限窗口資料。</summary>
public sealed record PrtgValueReadinessInput(
    long SensorObjid,
    long DeviceObjid,
    string Category,
    bool HostMapped,
    bool HostEnabled,
    bool SensorPaused,
    bool DevicePaused,
    bool Whitelisted,
    string? Unit,
    string? MainChannel,
    bool SemanticVerified,
    IReadOnlyList<PrtgReadinessHour> Hours,
    IReadOnlyDictionary<DateTime, long?> DailyMappedHostIds);

public sealed record PrtgValueReadinessResult(
    long SensorObjid,
    PrtgValueReadinessStatus Status,
    int UsableDays,
    int RequiredDays,
    int UsableHours,
    int RequiredHoursPerDay,
    DateTime? LatestUsableHour,
    bool SemanticReady,
    string Reason);

/// <summary>逐 sensor 計算暫定磁碟值型資料門檻；不查詢、不修改持久資料。</summary>
public static class PrtgValueReadiness
{
    public const int WindowDays = 28;
    public const int MinDailyUsableHours = 12;

    public static PrtgValueReadinessResult Evaluate(PrtgValueReadinessInput input, DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(input);
        var empty = new PrtgValueReadinessResult(input.SensorObjid, PrtgValueReadinessStatus.InsufficientData,
            0, WindowDays, 0, MinDailyUsableHours, null, false, "資料不足");
        if (!string.Equals(input.Category, PrtgSensorCategories.Disk, StringComparison.OrdinalIgnoreCase))
            return empty with { Status = PrtgValueReadinessStatus.Unknown, Reason = "非磁碟候選" };
        if (!input.HostMapped || !input.HostEnabled || input.SensorPaused || input.DevicePaused || !input.Whitelisted)
            return empty with { Status = PrtgValueReadinessStatus.Unknown, Reason = "主機／感測器停用、暫停、未對應或白名單不符" };

        var endDate = asOf.Date;
        var startDate = endDate.AddDays(-WindowDays);
        var usableByDay = input.Hours
            .Where(h => h.PeriodStart >= startDate && h.PeriodStart < endDate)
            .Where(IsUsable)
            .GroupBy(h => h.PeriodStart.Date)
            .ToDictionary(g => g.Key, g => g.Select(h => h.PeriodStart).Distinct().Count());
        var usableDays = usableByDay.Count(x => x.Value >= MinDailyUsableHours);
        var usableHours = usableByDay.Values.Sum();

        // Provenance is required only where usable values exist. Empty historical days
        // still contribute no coverage, but do not make a partially populated window unknown.
        var mappingIds = new HashSet<long>();
        foreach (var date in usableByDay.Keys)
        {
            if (!input.DailyMappedHostIds.TryGetValue(date, out var hostId) || !hostId.HasValue)
                return empty with { UsableDays = usableDays, UsableHours = usableHours,
                    Status = PrtgValueReadinessStatus.Unknown, Reason = "有資料日的歷史主機對應無法證明" };
            mappingIds.Add(hostId.Value);
        }
        if (mappingIds.Count > 1)
            return empty with { UsableDays = usableDays, UsableHours = usableHours,
                Status = PrtgValueReadinessStatus.Unknown, Reason = "有資料日的窗口內主機對應曾變更" };

        var latest = input.Hours.Where(h => h.PeriodStart >= startDate && h.PeriodStart < endDate && IsUsable(h))
            .Select(h => (DateTime?)h.PeriodStart).Max();
        var semanticReady = input.SemanticVerified
            && !string.IsNullOrWhiteSpace(input.Unit)
            && !string.IsNullOrWhiteSpace(input.MainChannel);
        var dataReady = usableDays == WindowDays && usableByDay.Count == WindowDays;
        var status = dataReady ? PrtgValueReadinessStatus.Ready : PrtgValueReadinessStatus.InsufficientData;
        var reason = !dataReady ? "資料不足" : semanticReady ? "資料與量測語意已確認" : "資料足夠；單位／主頻道語意待確認";
        return new PrtgValueReadinessResult(input.SensorObjid, status, usableDays, WindowDays,
            usableHours, MinDailyUsableHours, latest, semanticReady, reason);
    }

    public static bool IsUsable(PrtgReadinessHour hour) =>
        string.Equals(hour.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(hour.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase)
            && hour.Coverage.HasValue
            && hour.Coverage.Value >= PrtgValueUsability.SampledMinCoverage);
}
