using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>持久的逐 sensor 公平游標；逐 sensor lease 與重試時間仍與其 timeline 同一 blob 原子寫入。</summary>
public sealed class PrtgSensorTimelineProgress
{
    public const int DefaultBootstrapDeadlineHours = 72;
    public const int MinimumBootstrapDeadlineHours = 1;
    public const int MaximumBootstrapDeadlineHours = 720;

    public int BootstrapDeadlineHours { get; set; } = DefaultBootstrapDeadlineHours;
    public int BootstrapSettingsRevision { get; set; }
    public string BootstrapCycleId { get; set; } = "";
    public string BootstrapSourceGeneration { get; set; } = "";
    public string BootstrapScopeFingerprint { get; set; } = "";
    public DateTimeOffset? BootstrapCycleStartedAtUtc { get; set; }
    public DateTimeOffset? BootstrapCycleDeadlineAtUtc { get; set; }
    public DateTimeOffset? BootstrapCycleAsOfUtc { get; set; }
    public int BootstrapCycleSelectedSensors { get; set; }
    public long BootstrapCycleStartSensorId { get; set; }
    public long BootstrapCycleSweepEndSensorId { get; set; }
    public bool BootstrapCycleSweepPassedEnd { get; set; }
    public string BootstrapCycleOutcome { get; set; } = "not-started";
    public string BootstrapCycleReason { get; set; } = "not-started";
    public PrtgTimelineBootstrapCycleSummary? PreviousBootstrapCycle { get; set; }
    public long LastServedSensorId { get; set; }
    public DateTimeOffset? LastRoundStartedAt { get; set; }
    public DateTimeOffset? LastRoundCompletedAt { get; set; }
    public string LastRoundOutcome { get; set; } = "not-started";
}

public sealed record PrtgTimelineBootstrapCycleSummary(string CycleId, string SourceGeneration,
    string ScopeFingerprint, DateTimeOffset? StartedAtUtc, DateTimeOffset? DeadlineAtUtc, string Outcome);

public sealed class PrtgSensorTimelineProgressStore(EfJsonBlobStore blob)
    : JsonBlobSingleton<PrtgSensorTimelineProgress>(blob)
{
    public const string BlobKey = "prtg_sensor_timeline_progress_v1";

    public static string ScopeFingerprint(PrtgMonitoringPolicy policy, SystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(settings);
        var material = JsonSerializer.Serialize(new
        {
            policy.SourceGeneration,
            policy.EndpointHint,
            Sensors = policy.SensorIds.Where(id => id > 0).Distinct().Order().ToArray(),
            Hosts = policy.HostIds.Where(id => id > 0).Distinct().Order().ToArray(),
            policy.SourceTimeZoneId,
            policy.SourceCultureName,
            policy.RawTimestampTimeZoneId,
            policy.AnalysisTimeZoneId,
            policy.TimeBasisEvidenceReference,
            settings.PrtgUrl
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Atomically guards a Maintain edit against the cycle/settings revision shown by GET.</summary>
    public bool TryUpdateExpected(string expectedCycleId, string expectedCycleOutcome,
        int expectedSettingsRevision, long expectedPolicyVersion, long expectedSettingsVersion,
        long expectedScopeRevision, IReadOnlyDictionary<string, long> expectedFenceVersions,
        Action<PrtgSensorTimelineProgress> mutation,
        Func<PrtgSensorTimelineProgress, bool>? precondition = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        try
        {
            blob.MutateWithContext((context, currentJson) =>
            {
                var row = context.Blobs.SingleOrDefault(item => item.BlobKey == BlobKey);
                var expectedVersions = expectedFenceVersions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                expectedVersions[PrtgMonitoringPolicyStore.BlobKey] = expectedPolicyVersion;
                expectedVersions["system_settings"] = expectedSettingsVersion;
                expectedVersions[EfPrtgStore.ScopeRevisionBlobKey] = expectedScopeRevision;
                var versions = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var keys in expectedVersions.Keys.Chunk(500))
                {
                    var rows = context.Blobs.AsNoTracking().Where(item => keys.Contains(item.BlobKey))
                        .Select(item => new { item.BlobKey, item.Version }).ToArray();
                    foreach (var version in rows) versions[version.BlobKey] = version.Version;
                }
                var current = string.IsNullOrWhiteSpace(currentJson)
                    ? new PrtgSensorTimelineProgress()
                    : JsonSerializer.Deserialize<PrtgSensorTimelineProgress>(currentJson, LfJsonOptions.Pretty)
                        ?? throw new JsonException("Timeline progress is invalid.");
                if (current.BootstrapCycleId != expectedCycleId ||
                    current.BootstrapCycleOutcome != expectedCycleOutcome ||
                    current.BootstrapSettingsRevision != expectedSettingsRevision ||
                    expectedVersions.Any(pair => versions.GetValueOrDefault(pair.Key) != pair.Value) ||
                    precondition is not null && !precondition(current) ||
                    row is null && (expectedCycleId.Length != 0 || expectedCycleOutcome != "not-started" || expectedSettingsRevision != 0))
                    throw new StaleTimelineProgressException();
                mutation(current);
                return (JsonSerializer.Serialize(current, LfJsonOptions.Pretty), true);
            });
            return true;
        }
        catch (StaleTimelineProgressException) { return false; }
    }

    private sealed class StaleTimelineProgressException : Exception { }
}

public sealed record PrtgTimelineWorkItem(long SensorId, long HostId);

/// <summary>共用公平選取規則；先輪到游標後方，失敗 sensor 由自身 NextAttemptAt 退避。</summary>
public static class PrtgSensorTimelineWorkSelector
{
    public static IReadOnlyList<PrtgTimelineWorkItem> Select(
        IEnumerable<PrtgTimelineWorkItem> candidates, long lastServedSensorId,
        DateTimeOffset now, int maximum,
        Func<long, PrtgSensorTimelineEvidence> readEvidence, out DateTimeOffset? nextEligibleAt)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        var ordered = candidates.DistinctBy(c => c.SensorId).OrderBy(c => c.SensorId).ToArray();
        var rotated = ordered.Where(c => c.SensorId > lastServedSensorId)
            .Concat(ordered.Where(c => c.SensorId <= lastServedSensorId));
        var selected = new List<PrtgTimelineWorkItem>(Math.Min(maximum, ordered.Length));
        nextEligibleAt = null;
        foreach (var candidate in rotated)
        {
            if (selected.Count == maximum) break;
            var evidence = readEvidence(candidate.SensorId);
            var notBefore = Max(evidence.NextAttemptAt, evidence.LeaseUntil);
            if (notBefore > now)
            {
                if (nextEligibleAt == null || notBefore < nextEligibleAt) nextEligibleAt = notBefore;
                continue;
            }
            selected.Add(candidate);
        }
        return selected;
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left == null ? right : right == null || left > right ? left : right;
}
