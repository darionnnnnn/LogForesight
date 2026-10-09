using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>管理者確認的 Core 身分與試點；端點摘要只用於防止設定換站後繼續沿用確認。</summary>
public sealed class PrtgMonitoringPolicy
{
    public string Revision { get; set; } = string.Empty;
    public string CoreSystemId { get; set; } = string.Empty;
    public string SourceGeneration { get; set; } = string.Empty;
    public string EndpointHint { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
    public string ConfirmedBy { get; set; } = string.Empty;
    public string ContinuityEvidenceReference { get; set; } = "";
    public DateTimeOffset? ContinuityConfirmedAtUtc { get; set; }
    public string SourceTimeZoneId { get; set; } = "";
    public string SourceCultureName { get; set; } = "";
    /// <summary>Explicitly probed account zone for raw OLE Automation timestamps; blank stays untrusted.</summary>
    public string RawTimestampTimeZoneId { get; set; } = "";
    /// <summary>Analysis wall-clock zone used for fixed-slot qualification; blank stays untrusted.</summary>
    public string AnalysisTimeZoneId { get; set; } = "";
    public string TimeBasisEvidenceReference { get; set; } = "";
    public bool Ready(string url) => CoreSystemId.Length > 0 && SourceGeneration.Length > 0 &&
        ValidFrom != default && EndpointHint == EfPrtgObservationStore.SourceHintFor(url) &&
        HostIds.Count > 0 && SensorIds.Count > 0 && SourceTimeZoneId.Length > 0 && SourceCultureName.Length > 0;

    public string EffectiveSensorScope(long sensorId, long hostId)
    {
        if (!SensorIds.Contains(sensorId) || !HostIds.Contains(hostId)) return string.Empty;
        var value = string.Join("|", CoreSystemId, SourceGeneration, EndpointHint, sensorId, hostId,
            ValidFrom.UtcTicks, SourceTimeZoneId, SourceCultureName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Stable source/time authority for native status points. It deliberately excludes policy revision
    /// and host/sensor selection so unrelated scope edits do not invalidate unchanged device proofs.
    /// </summary>
    public string SourceAuthorityFingerprint(string url)
    {
        if (!Ready(url) || string.IsNullOrWhiteSpace(RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(AnalysisTimeZoneId) || string.IsNullOrWhiteSpace(TimeBasisEvidenceReference) ||
            SourceGeneration.Length > 256 || EndpointHint.Length > 256 || SourceTimeZoneId.Length > 128 ||
            SourceCultureName.Length > 128 || RawTimestampTimeZoneId.Length > 128 ||
            AnalysisTimeZoneId.Length > 128 || TimeBasisEvidenceReference.Length > 512) return "";
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(SourceTimeZoneId);
            _ = TimeZoneInfo.FindSystemTimeZoneById(RawTimestampTimeZoneId);
            _ = TimeZoneInfo.FindSystemTimeZoneById(AnalysisTimeZoneId);
            _ = CultureInfo.GetCultureInfo(SourceCultureName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { return ""; }
        var components = new[] { "prtg.table.json:devices+sensors:v1", SourceGeneration,
            EndpointHint, SourceTimeZoneId, SourceCultureName, RawTimestampTimeZoneId,
            AnalysisTimeZoneId, TimeBasisEvidenceReference };
        var value = string.Concat(components.Select(component =>
            component.Length.ToString(CultureInfo.InvariantCulture) + ":" + component));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class PrtgMonitoringPolicyStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgMonitoringPolicy>(blob)
{
    public const string BlobKey = "prtg_monitoring_policy";
    // A source/selection replacement may invalidate old + new disjoint 15,000-sensor scopes.
    private const int MaximumAffectedSensors = 30_000;
    private const int IdentityBatchSize = 500;

    /// <summary>以一筆 SQL transaction 執行 policy revision CAS 與受影響 sensor epoch 遞增。</summary>
    public PrtgMonitoringPolicy UpdateWithResourceEpochs(string expectedRevision,
        IEnumerable<long> affectedSensorIds, Action<PrtgMonitoringPolicy> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var sensorIds = affectedSensorIds.Where(id => id > 0).Distinct()
            .Take(MaximumAffectedSensors + 1).ToArray();
        if (sensorIds.Length > MaximumAffectedSensors)
            throw new ArgumentOutOfRangeException(nameof(affectedSensorIds),
                $"Affected sensor scope exceeds {MaximumAffectedSensors} rows.");
        Array.Sort(sensorIds);
        return blob.MutateWithContext((ctx, raw) =>
        {
            var policy = string.IsNullOrWhiteSpace(raw)
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(raw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG 監看政策無效；拒絕更新。");
            if (!string.Equals(policy.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("設定已變更；重新載入後再修改。");

            mutation(policy);
            var now = DateTimeOffset.UtcNow;
            var revisionRows = new Dictionary<string, BlobRow>(StringComparer.Ordinal);
            foreach (var sensorBatch in sensorIds.Chunk(IdentityBatchSize))
            {
                var keysBySensor = sensorBatch.ToDictionary(sensorId => sensorId,
                    sensorId => PrtgResourceIdentityStore.Prefix + sensorId.ToString(CultureInfo.InvariantCulture));
                var sensorByKey = keysBySensor.ToDictionary(pair => pair.Value, pair => pair.Key,
                    StringComparer.Ordinal);
                var requestedKeys = sensorByKey.Keys.ToHashSet(StringComparer.Ordinal);
                var loadedRows = new Dictionary<long, BlobRow>();
                var storedRows = ctx.Blobs.AsNoTracking().Where(row => requestedKeys.Contains(row.BlobKey))
                    .Select(row => new
                    {
                        row.BlobKey,
                        Content = row.Content.Substring(0, PrtgResourceIdentityStore.MaxLedgerBytes + 1),
                        Length = row.Content.Length,
                        row.Version,
                        row.UpdatedAt
                    }).ToList();
                foreach (var stored in storedRows)
                {
                    if (stored.Length > PrtgResourceIdentityStore.MaxLedgerBytes ||
                        System.Text.Encoding.UTF8.GetByteCount(stored.Content) > PrtgResourceIdentityStore.MaxLedgerBytes)
                        throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用舊涵蓋。");
                    var sensorId = sensorByKey[stored.BlobKey];
                    var row = new BlobRow
                    {
                        BlobKey = stored.BlobKey,
                        Content = stored.Content,
                        Version = stored.Version,
                        UpdatedAt = stored.UpdatedAt
                    };
                    ctx.Blobs.Attach(row);
                    loadedRows.Add(sensorId, row);
                }

                var currentIdentities = sensorBatch.Select(sensorId =>
                    PrtgResourceIdentityStore.ReadLoaded(sensorId,
                        loadedRows.TryGetValue(sensorId, out var row) ? row : null)).ToArray();
                PrtgResourceIdentityStore.LoadAuthorityRevisionRows(ctx,
                    currentIdentities.Select(identity => identity.HostId), revisionRows);

                var changedRows = new List<BlobRow>(sensorBatch.Length);
                foreach (var current in currentIdentities)
                {
                    loadedRows.TryGetValue(current.SensorId, out var existingRow);
                    PrtgResourceIdentityStore.AdvanceLoaded(ctx, current, existingRow, now,
                        policy.SourceGeneration, revisionRows, out var changedRow);
                    changedRows.Add(changedRow);
                }

                // Keep all epoch and revision updates inside MutateWithContext's single SQL transaction,
                // but bound tracked identity rows to one 500-row batch instead of retaining 15,000 rows.
                ctx.SaveChanges();
                foreach (var changedRow in changedRows)
                    ctx.Entry(changedRow).State = EntityState.Detached;
            }
            return (JsonSerializer.Serialize(policy, LfJsonOptions.Pretty), policy);
        });
    }
}

/// <summary>逐 sensor 保存，避免所有感測器歷史集中在單一大 blob；每個試點最多保存 31 天。</summary>
public sealed class PrtgSensorTimelineEvidence
{
    public const int MaximumBootstrapSensors = 15_000;
    public const int MaximumBootstrapPagesPerSensor = 20;
    public static readonly TimeSpan SupportedBootstrapWindow = TimeSpan.FromHours(
        ((MaximumBootstrapSensors * (double)MaximumBootstrapPagesPerSensor +
          Math.Ceiling(MaximumBootstrapSensors / (double)PrtgResourceGuardProbe.MaxBatchSize) * 2) / 2 / 3600) / 0.75d);
    public long SensorId { get; set; }
    public long HostId { get; set; }
    public string SourceGeneration { get; set; } = string.Empty;
    public string ResourceGeneration { get; set; } = string.Empty;
    public long IdentityEpoch { get; set; }
    public string ChannelGeneration { get; set; } = string.Empty;
    public string IdentityFingerprint { get; set; } = string.Empty;
    public long MappingRevision { get; set; }
    public string DiskSemanticFingerprint { get; set; } = "";
    public DateTimeOffset? DiskSemanticValidFrom { get; set; }
    public DateTimeOffset? DiskSemanticCheckedAt { get; set; }
    public DateTimeOffset? DiskIncidentStartedAt { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public DateTimeOffset? LastCompleteThrough { get; set; }
    public DateTimeOffset? BootstrapStartedAt { get; set; }
    public DateTimeOffset? BootstrapDeadlineAt { get; set; }
    /// <summary>Cycle that completed a closed initial messages query at or after its fixed as-of time.</summary>
    public string BootstrapCollectionCycleId { get; set; } = "";
    public string BootstrapStatus { get; set; } = "not-started";
    public int BootstrapPagesRead { get; set; }
    public string EffectiveScopeFingerprint { get; set; } = "";
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastServedAt { get; set; }
    public int ConsecutiveAttempts { get; set; }
    public string LeaseOwner { get; set; } = "";
    public DateTimeOffset? LeaseUntil { get; set; }
    public long LeaseVersion { get; set; }
    public List<PrtgTimedState> PendingStates { get; set; } = [];
    public List<string> PendingEventKeys { get; set; } = [];
    public int PendingNextPage { get; set; }
    public string PendingBoundaryHash { get; set; } = "";
    public string QualityReason { get; set; } = "not-observed";
    public List<PrtgSensorCoverage> Coverage { get; set; } = [];
    public List<PrtgTimedState> States { get; set; } = [];

    public void Bind(long sensorId, long hostId, string sourceGeneration, string fingerprint,
        string resourceGeneration, long identityEpoch, string channelGeneration, DateTimeOffset observedAt)
    {
        if (SensorId == sensorId && HostId == hostId && SourceGeneration == sourceGeneration &&
            IdentityFingerprint == fingerprint && ResourceGeneration == resourceGeneration && IdentityEpoch == identityEpoch)
        { ChannelGeneration = channelGeneration; return; }
        SensorId = sensorId; HostId = hostId; SourceGeneration = sourceGeneration;
        ResourceGeneration = resourceGeneration; IdentityEpoch = identityEpoch; ChannelGeneration = channelGeneration;
        IdentityFingerprint = fingerprint;
        DiskSemanticFingerprint = ""; DiskSemanticValidFrom = null; DiskSemanticCheckedAt = null; DiskIncidentStartedAt = null;
        ValidFrom = observedAt; LastCompleteThrough = null; EffectiveScopeFingerprint = "";
        BootstrapStartedAt = observedAt; BootstrapDeadlineAt = null; BootstrapStatus = "capacity-unverified";
        BootstrapPagesRead = 0; NextAttemptAt = null; PendingStates.Clear(); PendingEventKeys.Clear();
        PendingNextPage = 0; PendingBoundaryHash = "";
        Coverage.Clear(); States.Clear(); QualityReason = "identity-warmup"; ConsecutiveAttempts = 0;
    }

    /// <summary>舊測試／診斷資料相容入口；無權威世代，正式消費端必須拒絕。</summary>
    public void Bind(long sensorId, long hostId, string sourceGeneration, string fingerprint, DateTimeOffset observedAt) =>
        Bind(sensorId, hostId, sourceGeneration, fingerprint, Guid.NewGuid().ToString("N"), 0, "", observedAt);

    public IReadOnlyList<PrtgCoveredStatePeriod> Periods(DateTimeOffset from, DateTimeOffset through) =>
        PrtgCoveredStateTimeline.Build(SensorId, SourceGeneration, ResourceGeneration, Coverage, States, from, through);

    public void Accept(DateTimeOffset from, DateTimeOffset through, IEnumerable<PrtgTimedState> states)
    {
        if (from < ValidFrom || through <= from) throw new ArgumentException("涵蓋不可早於已確認身分。");
        var incoming = states.ToArray();
        if (incoming.Any(s => s.SensorObjid != SensorId || s.SourceGeneration != SourceGeneration ||
            s.ResourceGeneration != ResourceGeneration || s.At < from || s.At > through))
            throw new ArgumentException("狀態與涵蓋身分或時間不一致。");
        States = CompactStates(States.Concat(incoming));
        var spans = Coverage.Append(new(SensorId, from, through, SourceGeneration, ResourceGeneration))
            .OrderBy(c => c.From).ToArray();
        Coverage = [];
        foreach (var span in spans)
            if (Coverage.Count > 0 && Coverage[^1].Through >= span.From)
                Coverage[^1] = Coverage[^1] with { Through = Coverage[^1].Through > span.Through ? Coverage[^1].Through : span.Through };
            else Coverage.Add(span);
        var cutoff = through.AddDays(-31);
        // 保留涵蓋起點及其前導狀態，不能裁掉前導後誤認恢復。
        Coverage.RemoveAll(c => c.Through < cutoff);
        var anchor = States.LastOrDefault(s => s.At <= cutoff);
        var first = anchor?.At ?? cutoff;
        Coverage = Coverage.Select(c => c with { From = c.From < first ? first : c.From }).ToList();
        States.RemoveAll(s => s.At < first);
        if (States.Count > 20000) throw new InvalidOperationException("timeline-capacity-exceeded");
        QualityReason = "covered";
        LastAttemptAt = through;
        LastCompleteThrough = through;
        BootstrapStatus = through - ValidFrom >= TimeSpan.FromDays(31) ? "complete" : "capacity-unverified";
        PendingStates.Clear(); PendingEventKeys.Clear(); PendingNextPage = 0; PendingBoundaryHash = "";
        NextAttemptAt = null;
    }

    public static List<PrtgTimedState> CompactStates(IEnumerable<PrtgTimedState> states)
    {
        var result = new List<PrtgTimedState>();
        foreach (var state in states.Distinct().OrderBy(s => s.At))
            if (result.Count == 0 || result[^1].Status != state.Status) result.Add(state);
        return result;
    }

    public bool TryAcquireLease(string owner, DateTimeOffset now, TimeSpan duration, out long version)
    {
        if (LeaseUntil > now && !string.IsNullOrEmpty(LeaseOwner) && LeaseOwner != owner)
        { version = LeaseVersion; return false; }
        LeaseOwner = owner; LeaseUntil = now + duration; version = ++LeaseVersion; return true;
    }

    public bool OwnsLease(string owner, long version, DateTimeOffset now) =>
        LeaseOwner == owner && LeaseVersion == version && LeaseUntil > now;
}

public sealed class PrtgSensorTimelineStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgSensorTimelineEvidence>(blob)
{
    public const string Prefix = "prtg_timeline_";
    public const int MaxSilentReadBytes = 256 * 1024;

    /// <summary>Bounded read used by the silent formal consumer; oversized timelines remain waiting.</summary>
    public PrtgSensorTimelineEvidence? GetBoundedForSilentRule()
    {
        var (prefix, _, reportedLength) = blob.ReadBoundedWithVersion(MaxSilentReadBytes);
        if (prefix is null) return null;
        if (reportedLength > MaxSilentReadBytes || System.Text.Encoding.UTF8.GetByteCount(prefix) > MaxSilentReadBytes)
            return null;
        try { return JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(prefix, LfJsonOptions.Pretty); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Reads at most 12 timelines per SQL page. A 256 Ki-character prefix can expand to roughly
    /// 1 MiB of UTF-8, keeping a maximally escaped/unicode page below the 16 MiB proof-query cap.
    /// </summary>
    public static IReadOnlyDictionary<long, PrtgSensorTimelineEvidence> ReadManyBoundedForSilentRule(
        Func<LfDbContext> contextFactory, IEnumerable<long> sensorIds)
    {
        var result = new Dictionary<long, PrtgSensorTimelineEvidence>();
        foreach (var page in sensorIds.Where(id => id > 0).Distinct().Order().Chunk(12))
        {
            var keys = page.ToDictionary(id => Prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture), id => id,
                StringComparer.Ordinal);
            using var context = contextFactory();
            var rows = context.Blobs.AsNoTracking().Where(row => keys.Keys.Contains(row.BlobKey))
                .Select(row => new
                {
                    row.BlobKey,
                    ContentPrefix = row.Content.Substring(0, MaxSilentReadBytes + 1),
                    CharacterLength = row.Content.Length
                }).ToList();
            foreach (var row in rows)
            {
                if (row.CharacterLength > MaxSilentReadBytes ||
                    System.Text.Encoding.UTF8.GetByteCount(row.ContentPrefix) > MaxSilentReadBytes) continue;
                try
                {
                    var timeline = JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(row.ContentPrefix, LfJsonOptions.Pretty);
                    if (timeline is not null && timeline.SensorId == keys[row.BlobKey]) result[timeline.SensorId] = timeline;
                }
                catch (JsonException) { }
            }
        }
        return result;
    }
}
