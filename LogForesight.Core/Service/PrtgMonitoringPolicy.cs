using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>Explicit PRTG account message date/clock format; blank legacy values stay unauthorized.</summary>
    public string MessageDateFormat { get; set; } = "";
    /// <summary>Monotonic confirmation generation for message timestamp interpretation only.</summary>
    public long MessageTimeAuthorityRevision { get; set; }
    /// <summary>Monotonic generation for raw/source time interpretation; legacy documents default to zero.</summary>
    public long RawAuthorityRevision { get; set; }
    /// <summary>Operator-recorded interpretation zone for raw OLE Automation timestamps; saving it is not remote readback or native proof.</summary>
    public string RawTimestampTimeZoneId { get; set; } = "";
    /// <summary>Operator-selected analysis wall-clock zone used for fixed-slot qualification; blank stays untrusted.</summary>
    public string AnalysisTimeZoneId { get; set; } = "";
    /// <summary>Bounded operator-supplied provenance reference; it does not itself qualify a source sample.</summary>
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

    public string MessageTimeFingerprint(string url) => PrtgMessageDateTimeParser.BuildFingerprint(this, url);

    /// <summary>
    /// Stable source/time authority for native status points. It deliberately excludes policy revision
    /// and host/sensor selection so unrelated scope edits do not invalidate unchanged device proofs.
    /// </summary>
    public string SourceAuthorityFingerprint(string url)
    {
        if (RawAuthorityRevision < 0 || !Ready(url) || string.IsNullOrWhiteSpace(RawTimestampTimeZoneId) ||
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
        var components = new List<string> { "prtg.table.json:devices+sensors:v1", SourceGeneration,
            EndpointHint, SourceTimeZoneId, SourceCultureName, RawTimestampTimeZoneId,
            AnalysisTimeZoneId, TimeBasisEvidenceReference };
        if (RawAuthorityRevision > 0)
            components.Add(RawAuthorityRevisionComponent(RawAuthorityRevision));
        var value = string.Concat(components.Select(component =>
            component.Length.ToString(CultureInfo.InvariantCulture) + ":" + component));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static string RawAuthorityRevisionComponent(long revision)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        return "raw-authority-revision=" + revision.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class PrtgMonitoringPolicyStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgMonitoringPolicy>(blob)
{
    public const string BlobKey = "prtg_monitoring_policy";
    // A source/selection replacement may invalidate old + new disjoint 15,000-sensor scopes.
    private const int MaximumAffectedSensors = 30_000;
    private const int IdentityBatchSize = 500;

    private static string SerializeBoundedPolicy(PrtgMonitoringPolicy policy)
    {
        var serialized = JsonSerializer.Serialize(policy, LfJsonOptions.Pretty);
        if (serialized.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("PRTG policy exceeds its UTF-16 document limit.");
        return serialized;
    }

    public new PrtgMonitoringPolicy Get() => ReadBoundedValueWithVersion(2 * 1024 * 1024).Value;

    private readonly record struct RawAuthorityTuple(string? CoreSystemId, string? SourceGeneration,
        string? EndpointHint, long ValidFromUtcTicks,
        string? SourceTimeZoneId, string? SourceCultureName, string? RawTimestampTimeZoneId,
        string? AnalysisTimeZoneId, string? TimeBasisEvidenceReference)
    {
        public static RawAuthorityTuple Capture(PrtgMonitoringPolicy policy) => new(
            policy.CoreSystemId, policy.SourceGeneration, policy.EndpointHint, policy.ValidFrom.UtcTicks,
            policy.SourceTimeZoneId, policy.SourceCultureName, policy.RawTimestampTimeZoneId,
            policy.AnalysisTimeZoneId, policy.TimeBasisEvidenceReference);
    }

    private static void AdvanceRawAuthorityRevision(PrtgMonitoringPolicy policy,
        RawAuthorityTuple before, long previousRevision)
    {
        if (previousRevision < 0)
            throw new InvalidDataException("Raw authority revision is invalid; policy update was rejected.");

        policy.RawAuthorityRevision = previousRevision;
        if (before == RawAuthorityTuple.Capture(policy)) return;
        if (previousRevision == long.MaxValue)
            throw new InvalidOperationException("Raw authority revision reached its maximum; raw authority cannot be reused.");
        policy.RawAuthorityRevision = checked(previousRevision + 1);
    }

    /// <summary>Updates policy through the legacy singleton API while preserving the raw authority counter.</summary>
    public new PrtgMonitoringPolicy Update(Action<PrtgMonitoringPolicy> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return blob.MutateWithContext((_, raw) =>
        {
            var policy = raw is null
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(raw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG 監看政策無效；拒絕更新。");
            var priorRevision = policy.RawAuthorityRevision;
            var before = RawAuthorityTuple.Capture(policy);
            mutation(policy);
            AdvanceRawAuthorityRevision(policy, before, priorRevision);
            return (SerializeBoundedPolicy(policy), policy);
        }, maxCurrentCharacters: 2 * 1024 * 1024,
            isolationLevel: System.Data.IsolationLevel.Serializable,
            enforceCurrentUtf8ByteCap: false);
    }

    /// <summary>
    /// CAS-saves an explicit raw/analysis time basis without advancing resource identity. In the
    /// same transaction it revokes only existing disk evidence for affected sensors, preserving
    /// message timeline identity and all unknown JSON fields.
    /// </summary>
    public PrtgMonitoringPolicy UpdateTimeBasisWithRawEvidenceInvalidation(string expectedRevision,
        string expectedSettingsRevision, IEnumerable<long> affectedSensorIds,
        Action<PrtgMonitoringPolicy> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var sensorIds = affectedSensorIds.Where(id => id > 0).Distinct()
            .Take(MaximumAffectedSensors + 1).ToArray();
        if (sensorIds.Length > MaximumAffectedSensors)
            throw new ArgumentOutOfRangeException(nameof(affectedSensorIds),
                $"Affected sensor scope exceeds {MaximumAffectedSensors} rows.");
        var affected = sensorIds.ToHashSet();

        return blob.MutateWithContext((ctx, raw) =>
        {
            var settingsRow = EfJsonBlobStore.ReadBoundedInContext(ctx, "system_settings", 2 * 1024 * 1024);
            if (settingsRow.ReportedLength > 2 * 1024 * 1024 || settingsRow.Prefix is null)
                throw new InvalidDataException("System settings exceed their bounded read limit.");
            var settings = JsonSerializer.Deserialize<SystemSettings>(settingsRow.Prefix, LfJsonOptions.Pretty)
                ?? throw new InvalidDataException("System settings invalid.");
            if (!string.Equals(settings.Revision, expectedSettingsRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("System settings changed; reload before updating time basis.");
            if (settings.PrtgEnabled)
                throw new InvalidOperationException("time-basis-requires-disabled-source");

            var policy = raw is null
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(raw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG 監看政策無效；拒絕更新。");
            if (!string.Equals(policy.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("設定已變更；重新載入後再修改。");

            var priorRawAuthorityRevision = policy.RawAuthorityRevision;
            var priorRawAuthority = RawAuthorityTuple.Capture(policy);

            RevokeExistingDiskEvidence(ctx, PrtgDiskSemanticEvidenceStore.BlobKey,
                PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters,
                content => PrtgDiskTimeBasisInvalidator.InvalidateSemantic(content, affected));
            RevokeExistingDiskEvidence(ctx, PrtgDiskVerificationResultStore.BlobKey,
                PrtgDiskVerificationResultStore.MaximumSnapshotCharacters,
                content => PrtgDiskTimeBasisInvalidator.InvalidateVerification(content, affected));

            mutation(policy);
            AdvanceRawAuthorityRevision(policy, priorRawAuthority, priorRawAuthorityRevision);
            return (SerializeBoundedPolicy(policy), policy);
        }, maxCurrentCharacters: 2 * 1024 * 1024,
            isolationLevel: System.Data.IsolationLevel.Serializable,
            enforceCurrentUtf8ByteCap: false);
    }

    private static void RevokeExistingDiskEvidence(LfDbContext ctx, string key, int maximumCharacters,
        Func<string, string> invalidate)
    {
        var read = EfJsonBlobStore.ReadBoundedInContext(ctx, key, maximumCharacters);
        if (read.Version == 0 && read.Prefix is null) return;
        if (read.ReportedLength > maximumCharacters || read.Prefix is null)
            throw new InvalidDataException($"Blob '{key}' exceeds its bounded read limit; time basis was not changed.");
        var candidate = invalidate(read.Prefix);
        if (string.Equals(candidate, read.Prefix, StringComparison.Ordinal)) return;

        // The bounded physical read intentionally returns only content/version. Load the
        // already-bounded row through EF so both concurrency tokens (Version and UpdatedAt)
        // retain their actual database originals for the transaction's update.
        var row = ctx.Blobs.FirstOrDefault(candidate => candidate.BlobKey == key &&
            candidate.Version == read.Version);
        if (row is null || !string.Equals(row.Content, read.Prefix, StringComparison.Ordinal))
            throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException(
                $"Blob '{key}' changed after its bounded read.");
        row.Content = candidate;
        row.Version = checked(row.Version + 1);
        row.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>以一筆 SQL transaction 執行 policy revision CAS 與受影響 sensor epoch 遞增。</summary>
    public PrtgMonitoringPolicy UpdateWithResourceEpochs(string expectedRevision,
        IEnumerable<long> affectedSensorIds, Action<PrtgMonitoringPolicy> mutation,
        string? expectedSettingsRevision = null,
        IEnumerable<long>? rawEvidenceInvalidationSensorIds = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var sensorIds = affectedSensorIds.Where(id => id > 0).Distinct()
            .Take(MaximumAffectedSensors + 1).ToArray();
        if (sensorIds.Length > MaximumAffectedSensors)
            throw new ArgumentOutOfRangeException(nameof(affectedSensorIds),
                $"Affected sensor scope exceeds {MaximumAffectedSensors} rows.");
        Array.Sort(sensorIds);
        var rawInvalidationIds = (rawEvidenceInvalidationSensorIds ?? [])
            .Where(id => id > 0).Distinct().Take(MaximumAffectedSensors + 1).ToArray();
        if (rawInvalidationIds.Length > MaximumAffectedSensors)
            throw new ArgumentOutOfRangeException(nameof(rawEvidenceInvalidationSensorIds),
                $"Raw evidence invalidation scope exceeds {MaximumAffectedSensors} rows.");
        var rawInvalidationSet = rawInvalidationIds.ToHashSet();
        return blob.MutateWithContext((ctx, raw) =>
        {
            if (expectedSettingsRevision is not null)
            {
                var settingsRow = EfJsonBlobStore.ReadBoundedInContext(ctx, "system_settings", 2 * 1024 * 1024);
                if (settingsRow.ReportedLength > 2 * 1024 * 1024 || settingsRow.Prefix is null)
                    throw new InvalidDataException("System settings exceed their bounded read limit.");
                var settings = JsonSerializer.Deserialize<SystemSettings>(settingsRow.Prefix, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("System settings invalid.");
                if (!string.Equals(settings.Revision, expectedSettingsRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException("System settings changed; reload before updating time basis.");
            }
            var policy = raw is null
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(raw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG 監看政策無效；拒絕更新。");
            if (!string.Equals(policy.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("設定已變更；重新載入後再修改。");

            var priorRawAuthorityRevision = policy.RawAuthorityRevision;
            var priorRawAuthority = RawAuthorityTuple.Capture(policy);
            mutation(policy);
            AdvanceRawAuthorityRevision(policy, priorRawAuthority, priorRawAuthorityRevision);
            if (rawInvalidationSet.Count > 0)
            {
                RevokeExistingDiskEvidence(ctx, PrtgDiskSemanticEvidenceStore.BlobKey,
                    PrtgDiskSemanticEvidenceStore.MaximumSnapshotCharacters,
                    content => PrtgDiskTimeBasisInvalidator.InvalidateSemantic(content, rawInvalidationSet));
                RevokeExistingDiskEvidence(ctx, PrtgDiskVerificationResultStore.BlobKey,
                    PrtgDiskVerificationResultStore.MaximumSnapshotCharacters,
                    content => PrtgDiskTimeBasisInvalidator.InvalidateVerification(content, rawInvalidationSet));
            }
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
                var storedRows = BoundedJsonBlobBatchReader.Read(ctx, requestedKeys,
                    PrtgResourceIdentityStore.MaxLedgerBytes);
                foreach (var item in storedRows)
                {
                    var stored = item.Value;
                    if (stored.Length > PrtgResourceIdentityStore.MaxLedgerBytes ||
                        stored.Content is null ||
                        System.Text.Encoding.UTF8.GetByteCount(stored.Content) > PrtgResourceIdentityStore.MaxLedgerBytes)
                        throw new InvalidDataException("PRTG 資源身分紀錄超過 8 KiB 上限；拒絕沿用舊涵蓋。");
                    var sensorId = sensorByKey[item.Key];
                    var row = new BlobRow
                    {
                        BlobKey = item.Key,
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
            return (SerializeBoundedPolicy(policy), policy);
        }, maxCurrentCharacters: 2 * 1024 * 1024,
            isolationLevel: System.Data.IsolationLevel.Serializable,
            enforceCurrentUtf8ByteCap: false);
    }
}

/// <summary>逐 sensor 保存，避免所有感測器歷史集中在單一大 blob；每個試點最多保存 31 天。</summary>
public sealed class PrtgSensorTimelineEvidence
{
    public const int MessageStatusSemanticVersionCurrent = 1;
    public const int MaximumBootstrapSensors = 15_000;
    public const int MaximumBootstrapPagesPerSensor = 20;
    public static readonly TimeSpan SupportedBootstrapWindow = TimeSpan.FromHours(
        ((MaximumBootstrapSensors * (double)MaximumBootstrapPagesPerSensor +
          Math.Ceiling(MaximumBootstrapSensors / (double)PrtgResourceGuardProbe.MaxBatchSize) * 2) / 2 / 3600) / 0.75d);
    public PrtgSensorTimelineEvidence() => MessageStatusSemanticVersion = MessageStatusSemanticVersionCurrent;

    // Missing property in old JSON intentionally becomes legacy (0). New instances use the parameterless constructor.
    [JsonConstructor]
    public PrtgSensorTimelineEvidence(int? messageStatusSemanticVersion) =>
        MessageStatusSemanticVersion = messageStatusSemanticVersion ?? 0;

    public int? MessageStatusSemanticVersion { get; set; }
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
    public string PendingMessageTimeFingerprint { get; set; } = "";
    public string MessageTimeFingerprint { get; set; } = "";
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
        PendingNextPage = 0; PendingBoundaryHash = ""; PendingMessageTimeFingerprint = "";
        Coverage.Clear(); States.Clear(); QualityReason = "identity-warmup"; ConsecutiveAttempts = 0;
    }

    /// <summary>舊測試／診斷資料相容入口；無權威世代，正式消費端必須拒絕。</summary>
    public void Bind(long sensorId, long hostId, string sourceGeneration, string fingerprint, DateTimeOffset observedAt) =>
        Bind(sensorId, hostId, sourceGeneration, fingerprint, Guid.NewGuid().ToString("N"), 0, "", observedAt);

    public IReadOnlyList<PrtgCoveredStatePeriod> Periods(DateTimeOffset from, DateTimeOffset through) =>
        MessageStatusSemanticVersion == MessageStatusSemanticVersionCurrent && !string.IsNullOrWhiteSpace(MessageTimeFingerprint)
            ? PrtgCoveredStateTimeline.Build(SensorId, SourceGeneration, ResourceGeneration, Coverage, States, from, through)
            : [];

    public void Accept(DateTimeOffset from, DateTimeOffset through, IEnumerable<PrtgTimedState> states)
    {
        if (from < ValidFrom || through <= from) throw new ArgumentException("涵蓋不可早於已確認身分。");
        if (string.IsNullOrWhiteSpace(MessageTimeFingerprint))
            throw new InvalidOperationException("message-date-format-unconfigured");
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
        PendingMessageTimeFingerprint = "";
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
    /// Reads at most 12 complete bounded timelines per SQL page. A 256 Ki-character document can expand to roughly
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
            var rows = BoundedJsonBlobBatchReader.Read(context, keys.Keys, MaxSilentReadBytes);
            foreach (var item in rows)
            {
                var row = item.Value;
                if (row.Length > MaxSilentReadBytes || row.Content is null ||
                    System.Text.Encoding.UTF8.GetByteCount(row.Content) > MaxSilentReadBytes) continue;
                try
                {
                    var timeline = JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(row.Content, LfJsonOptions.Pretty);
                    if (timeline is not null && timeline.SensorId == keys[item.Key]) result[timeline.SensorId] = timeline;
                }
                catch (JsonException) { }
            }
        }
        return result;
    }
}
