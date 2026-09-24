namespace LogForesight.Web.Services;

// Healthy is retained for API/health compatibility, but this service never emits it:
// persisted aggregate write counts do not retain sensor identity.
public enum PrtgSnapshotHourState { NoTargets, Healthy, OkCovered, Insufficient, Unknown, ReportedWriteCountMetTarget }

public static class PrtgSnapshotSkipReasonCodes
{
    public const string PrtgDisabled = "prtg-disabled";
    public const string ConnectionNotConfigured = "connection-not-configured";
    public const string StructureSyncActive = "structure-sync-active";
    public const string BackfillActive = "backfill-active";
    public const string NightlyFetchPrtgPhase = "nightly-fetch-prtg-phase";
    public const string MaintenanceWindow = "maintenance-window";
    public const string MaintenanceConfirmed = "maintenance-confirmed";
}

/// <summary>AvailableValues is the persisted snapshot write count; it may double-count retries and proves no per-sensor coverage.</summary>
public sealed record PrtgSnapshotHourDiagnostic(DateTime Hour, PrtgSnapshotHourState State,
    int Targets, int AvailableValues, int Attempts, int Successes, int Skips, int WriteFailures,
    IReadOnlyDictionary<string, int> Reasons)
{
    public IReadOnlyDictionary<string, int> ReasonCodes { get; init; } = new Dictionary<string, int>();
    public int SampledUsableValues { get; init; }
    public int SampledLowCoverageValues { get; init; }
    public int OkValues { get; init; }
    public int DatabaseUsableValues { get; init; }
}

/// <summary>唯讀彙整持久化執行計數及最近 24 個完整小時的索引範圍數值品質；不保留 sensor 級身分。</summary>
public sealed class PrtgSnapshotDiagnosticsService
{
    private readonly PrtgSnapshotDiagnosticsStore _statistics;
    private readonly Func<DateTime, DateTime, IReadOnlyList<LogForesight.Core.Persistence.Sql.PrtgSnapshotValueCoverage>> _readCoverage;

    internal PrtgSnapshotDiagnosticsService(PrtgSnapshotDiagnosticsStore statistics,
        Func<DateTime, DateTime, IReadOnlyList<LogForesight.Core.Persistence.Sql.PrtgSnapshotValueCoverage>>? readCoverage = null)
    { _statistics = statistics; _readCoverage = readCoverage ?? ((_, _) => Array.Empty<LogForesight.Core.Persistence.Sql.PrtgSnapshotValueCoverage>()); }

    public IReadOnlyList<PrtgSnapshotHourDiagnostic> ReadRecent(DateTime now, int completeHours = 24)
    {
        if (completeHours < 1) throw new ArgumentOutOfRangeException(nameof(completeHours));
        var end = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var start = end.AddHours(-completeHours);
        var stats = _statistics.ReadHours().Where(x => x.Hour >= start && x.Hour < end).ToDictionary(x => x.Hour);
        var coverage = _readCoverage(start, end).ToDictionary(x => x.Hour);
        var result = new List<PrtgSnapshotHourDiagnostic>(completeHours);
        for (var hour = start; hour < end; hour = hour.AddHours(1))
        {
            stats.TryGetValue(hour, out var s);
            var targetCount = s?.Targets ?? 0;
            // Aggregate the bounded 24-hour window through IX_lf_prtg_values_period.
            // This reports persisted quality/coverage counts; sensor identity is not retained.
            var available = s?.Sampled ?? 0;
            coverage.TryGetValue(hour, out var db);
            var usable = db?.Usable ?? 0;
            var reportedComplete = s is not null && targetCount > 0 && available >= targetCount && usable >= targetCount
                && s.Attempts > 0 && s.Successes > 0 && s.Skips == 0 && s.WriteFailures == 0;
            var state = s == null ? PrtgSnapshotHourState.Unknown
                : s.Reasons.ContainsKey("startup-partial-hour") ? PrtgSnapshotHourState.Unknown
                : targetCount == 0 ? PrtgSnapshotHourState.NoTargets
                : s.WriteFailures > 0 || s.Skips > 0 || db?.SampledLowCoverage > 0 || usable < targetCount ? PrtgSnapshotHourState.Insufficient
                : db?.Ok >= targetCount ? PrtgSnapshotHourState.OkCovered
                : reportedComplete ? PrtgSnapshotHourState.ReportedWriteCountMetTarget
                : PrtgSnapshotHourState.Unknown;
            result.Add(new PrtgSnapshotHourDiagnostic(hour, state, targetCount, available, s?.Attempts ?? 0,
                s?.Successes ?? 0, s?.Skips ?? 0, s?.WriteFailures ?? 0,
                s?.Reasons ?? new Dictionary<string, int>())
            {
                ReasonCodes = s?.ReasonCodes ?? new Dictionary<string, int>(),
                SampledUsableValues = db?.SampledUsable ?? 0,
                SampledLowCoverageValues = db?.SampledLowCoverage ?? 0,
                OkValues = db?.Ok ?? 0,
                DatabaseUsableValues = usable
            });
        }
        return result;
    }
}
