using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Joins one bounded page of native status snapshots to current identity and timelines.</summary>
public sealed class PrtgSilentPresenceFormalConsumer(StorageBackend backend)
{
    private const int DevicePageSize = 10;

    public IReadOnlyList<PrtgFinding> Evaluate(DateTime day, PrtgMonitoringPolicy policy,
        string configuredUrl, IReadOnlyDictionary<long, long> currentDeviceToHost, IReadOnlyList<KnownIssueRule> rules)
        => EvaluateWithReadiness(day, policy, configuredUrl, currentDeviceToHost, rules).Findings;

    public PrtgSilentPresenceEvaluation EvaluateWithReadiness(DateTime day, PrtgMonitoringPolicy policy,
        string configuredUrl, IReadOnlyDictionary<long, long> currentDeviceToHost, IReadOnlyList<KnownIssueRule> rules)
    {
        var results = currentDeviceToHost.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair =>
            new PrtgSilentPresenceDeviceReadiness(pair.Key, pair.Value, PrtgSilentPresenceReadinessState.Waiting,
                "source-proof-unavailable", string.Empty, Array.Empty<PrtgFinding>()));
        PrtgSilentPresenceEvaluation Return(IReadOnlyList<PrtgFinding>? findings = null) =>
            new(findings ?? results.Values.SelectMany(result => result.Findings).ToArray(), results);
        if (currentDeviceToHost.Count == 0 || day != day.Date) return Return();
        var silentRule = rules.Any(rule => rule.Enabled && string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgRuleCode == PrtgRuleEvaluator.RuleSilent && rule.PrtgThreshold == PrtgRuleCatalog.DefaultSilentThreshold &&
            rule.PrtgSensorCategory is null);
        if (!silentRule)
        {
            foreach (var pair in currentDeviceToHost)
                results[pair.Key] = results[pair.Key] with { State = PrtgSilentPresenceReadinessState.ExplicitlyExcluded,
                    Reason = "silent-rule-disabled" };
            return Return();
        }
        var sourceAuthorityFingerprint = policy.SourceAuthorityFingerprint(configuredUrl);
        if (sourceAuthorityFingerprint.Length == 0)
        {
            foreach (var pair in currentDeviceToHost)
                results[pair.Key] = results[pair.Key] with { Reason = "source-authority-unverified" };
            return Return();
        }
        var configuredRetention = new SystemSettingsStore(backend.Blob("system_settings")).Get().RetentionDays;
        var proofRetentionDays = PrtgSilentPresenceSnapshotStore.EffectiveRetentionDays(configuredRetention);
        if (day.Date < DateTime.Today.AddDays(-proofRetentionDays))
        {
            foreach (var pair in currentDeviceToHost)
                results[pair.Key] = results[pair.Key] with { Reason = "source-proof-expired" };
            return Return();
        }
        // DailyAnalysisRecord.Date is the service/host local calendar day. Source-day keys
        // are resolved separately below in the PRTG source timezone.
        var requestedWindow = ResolveDailyWindowUtc(day);
        if (requestedWindow is null) return Return();
        TimeZoneInfo sourceZone;
        try { sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return Return(); }
        var sourceStartDay = TimeZoneInfo.ConvertTime(requestedWindow.Value.Start, sourceZone).Date;
        var sourceEndDay = TimeZoneInfo.ConvertTime(requestedWindow.Value.End.AddTicks(-1), sourceZone).Date;
        if (sourceEndDay < sourceStartDay) return Return();
        var sourceDays = Enumerable.Range(0, (sourceEndDay - sourceStartDay).Days + 1)
            .Select(offset => sourceStartDay.AddDays(offset)).ToArray();
        var initialScopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var identityFingerprints = new Dictionary<long, string>();
        var sensorIdsByDevice = new Dictionary<long, long[]>();

        foreach (var devicePage in currentDeviceToHost.OrderBy(pair => pair.Key).Chunk(DevicePageSize))
        {
            var snapshots = PrtgSilentPresenceSnapshotStore.ReadManyForWindow(backend.CreateContext,
                devicePage.Select(pair => pair.Key), sourceDays, requestedWindow.Value.Start, requestedWindow.Value.End);
            foreach (var mapping in devicePage)
            {
                var expectedHost = mapping.Value;
                if (!snapshots.TryGetValue(mapping.Key, out var snapshot)) continue;
                // The captured inventory and device-scoped mapping fingerprint close this proof.
                // A later global scope revision caused by another device must not invalidate it;
                // mutations during this bounded evaluation are still fenced below.
                if (snapshot.ReadQuality != PrtgPresenceReadQuality.Complete ||
                    snapshot.MappingFingerprint != PrtgSilentPresenceMappingFingerprint.Compute(snapshot.DeviceObjid, expectedHost) ||
                    snapshot.SourceAuthorityFingerprint != sourceAuthorityFingerprint || snapshot.SourceGeneration != policy.SourceGeneration ||
                    expectedHost <= 0 || !policy.HostIds.Contains(expectedHost) || snapshot.HostId != expectedHost || !snapshot.SourceAsOf.HasValue ||
                    !snapshot.DeviceStatusAsOf.HasValue || !snapshot.CapturedAtUtc.HasValue ||
                    snapshot.Sensors.Count is < 1 or > PrtgSilentAbsenceEvaluator.MaxSensorsPerDevice ||
                    snapshot.ReportedSensorCount != snapshot.Sensors.Count ||
                    !IsWithinRequestedWindow(snapshot.SourceAsOf.Value, requestedWindow.Value) ||
                    snapshot.SourceAsOf.Value.Offset != TimeSpan.Zero || snapshot.DeviceStatusAsOf.Value.Offset != TimeSpan.Zero ||
                    snapshot.CapturedAtUtc.Value.Offset != TimeSpan.Zero ||
                    Math.Abs((snapshot.SourceAsOf.Value - snapshot.DeviceStatusAsOf.Value).TotalMinutes) > 2 ||
                    Math.Abs((snapshot.CapturedAtUtc.Value - snapshot.SourceAsOf.Value).TotalMinutes) > 2 ||
                    !HasValidInventoryFingerprint(snapshot))
                {
                    results[mapping.Key] = results[mapping.Key] with { Reason = "source-inventory-time-or-mapping-proof-incomplete" };
                    continue;
                }

                var sensorIds = snapshot.Sensors.Select(sensor => sensor.SensorObjid).ToArray();
                var identities = backend.PrtgStore().GetResourceIdentities(sensorIds);
                // At most one device (512 timelines) is materialized at once. SQL reads use 12-key
                // pages and each value is capped independently, so fleet size does not multiply RAM.
                var timelines = PrtgSensorTimelineStore.ReadManyBoundedForSilentRule(backend.CreateContext, sensorIds);
                var sourceAsOf = snapshot.SourceAsOf.Value;
                var sensors = new List<PrtgSilentSensorEvidence>(snapshot.Sensors.Count);
                foreach (var row in snapshot.Sensors)
                {
                    timelines.TryGetValue(row.SensorObjid, out var timeline);
                    identities.TryGetValue(row.SensorObjid, out var identity);
                    DateTimeOffset? unknownSince = null;
                    if (!row.Paused && (identity is null || !PrtgResourceQualification.IsCurrent(timeline, identity,
                        policy.SourceGeneration, row.SensorObjid, snapshot.DeviceObjid, snapshot.HostId) ||
                        !HasCoveredSourceState(row, timeline, identity, sourceAsOf)))
                    {
                        sensors.Clear();
                        break;
                    }
                    if (!string.Equals(row.Category, PrtgSensorCategories.Availability, StringComparison.OrdinalIgnoreCase) && timeline is not null)
                    {
                        var currentUnknown = timeline.Periods(sourceAsOf.AddDays(-31), sourceAsOf)
                            .Where(period => PrtgSensorStatuses.IsUnknownOrEmpty(period.Status) && period.From <= sourceAsOf && period.Through >= sourceAsOf)
                            .OrderByDescending(period => period.From).FirstOrDefault();
                        if (currentUnknown is not null) unknownSince = currentUnknown.EnteredAt;
                    }
                    var input = new PrtgSensorStatusInput(row.SensorObjid, row.DeviceObjid, row.SourceStatus,
                        row.SensorType, row.Category);
                    sensors.Add(new(input, row.SourceStatus, sourceAsOf, unknownSince, row.Paused, timeline, identity));
                }
                if (sensors.Count != snapshot.Sensors.Count)
                {
                    results[mapping.Key] = results[mapping.Key] with { Reason = "sensor-identity-or-timeline-not-current-at-source-asof" };
                    continue;
                }
                var evidence = new PrtgSilentDeviceEvidence(snapshot.DeviceObjid, snapshot.HostId, snapshot.SourceGeneration,
                    PrtgPresenceReadQuality.Complete, ScopeComplete: true, HasUnfilteredObjects: false,
                    snapshot.DevicePaused, snapshot.Sensors.Count(sensor => !sensor.Paused), sensors)
                {
                    PolicyRevision = snapshot.PolicyRevision,
                    ScopeRevision = snapshot.ScopeRevision,
                    InventoryFingerprint = snapshot.InventoryFingerprint,
                    MappingFingerprint = snapshot.MappingFingerprint,
                    ReadReason = snapshot.ReadReason,
                    RequestedAnalysisDay = day.Date,
                    RequestedWindowStartUtc = requestedWindow.Value.Start,
                    RequestedWindowEndUtc = requestedWindow.Value.End,
                    SourceDay = snapshot.SourceDay,
                    SourceAsOf = snapshot.SourceAsOf,
                    DeviceStatusAsOf = snapshot.DeviceStatusAsOf,
                    SourceAuthorityFingerprint = snapshot.SourceAuthorityFingerprint
                };
                var deviceFindings = PrtgSilentAbsenceEvaluator.Evaluate(day, [evidence], rules);
                var fingerprint = DeviceEvidenceFingerprint(snapshot, identities, timelines);
                identityFingerprints[mapping.Key] = IdentityFingerprint(identities);
                sensorIdsByDevice[mapping.Key] = sensorIds;
                var readinessState = deviceFindings.Count > 0 ? PrtgSilentPresenceReadinessState.QualifiedHit :
                    snapshot.DevicePaused || snapshot.Sensors.All(sensor => sensor.Paused)
                        ? PrtgSilentPresenceReadinessState.ExplicitlyExcluded : PrtgSilentPresenceReadinessState.QualifiedNoHit;
                results[mapping.Key] = new(mapping.Key, expectedHost,
                    readinessState,
                    readinessState switch
                    {
                        PrtgSilentPresenceReadinessState.QualifiedHit => "qualified-silent-hit",
                        PrtgSilentPresenceReadinessState.ExplicitlyExcluded => "device-or-all-sensors-paused",
                        _ => "qualified-no-silent-hit"
                    }, fingerprint, deviceFindings);
            }
        }

        if (backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != initialScopeRevision)
        {
            foreach (var key in results.Keys.ToArray())
                if (results[key].State is PrtgSilentPresenceReadinessState.QualifiedHit or PrtgSilentPresenceReadinessState.QualifiedNoHit)
                    results[key] = results[key] with { State = PrtgSilentPresenceReadinessState.Waiting,
                        Reason = "scope-changed-during-evaluation", Findings = Array.Empty<PrtgFinding>() };
            return Return();
        }
        var finalPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!string.Equals(finalPolicy.Revision, policy.Revision, StringComparison.Ordinal) ||
            !string.Equals(finalPolicy.SourceAuthorityFingerprint(configuredUrl), sourceAuthorityFingerprint, StringComparison.Ordinal))
        {
            foreach (var key in results.Keys.ToArray())
                if (results[key].State is PrtgSilentPresenceReadinessState.QualifiedHit or PrtgSilentPresenceReadinessState.QualifiedNoHit)
                    results[key] = results[key] with { State = PrtgSilentPresenceReadinessState.Waiting,
                        Reason = "source-policy-changed-during-evaluation", Findings = Array.Empty<PrtgFinding>() };
            return Return();
        }
        foreach (var (deviceId, expectedFingerprint) in identityFingerprints)
        {
            var current = backend.PrtgStore().GetResourceIdentities(sensorIdsByDevice[deviceId]);
            if (!string.Equals(expectedFingerprint, IdentityFingerprint(current), StringComparison.Ordinal) ||
                !finalPolicy.HostIds.Contains(currentDeviceToHost[deviceId]))
                results[deviceId] = results[deviceId] with { State = PrtgSilentPresenceReadinessState.Waiting,
                    Reason = "resource-identity-or-policy-changed-during-evaluation", Findings = Array.Empty<PrtgFinding>() };
        }
        return Return();
    }

    private static bool HasCoveredSourceState(PrtgSilentSensorSnapshot sensor, PrtgSensorTimelineEvidence? proof,
        PrtgResourceIdentity identity, DateTimeOffset sourceAsOf)
    {
        if (proof is not { QualityReason: "covered" }) return false;
        var state = proof.States.Where(item => item.At <= sourceAsOf && item.SourceGeneration == proof.SourceGeneration &&
                item.ResourceGeneration == proof.ResourceGeneration).OrderBy(item => item.At).LastOrDefault();
        if (state is null || !StatusEquivalent(sensor.SourceStatus, state.Status)) return false;
        return proof.Coverage.Any(coverage => coverage.SensorObjid == sensor.SensorObjid &&
            coverage.SourceGeneration == identity.SourceGeneration && coverage.ResourceGeneration == identity.Generation &&
            coverage.From <= sourceAsOf && coverage.Through > sourceAsOf);
    }

    private static bool StatusEquivalent(string? source, string? timeline) =>
        string.Equals(source, timeline, StringComparison.OrdinalIgnoreCase) ||
        PrtgSensorStatuses.IsDown(source) && PrtgSensorStatuses.IsDown(timeline) ||
        PrtgSensorStatuses.IsWarning(source) && PrtgSensorStatuses.IsWarning(timeline) ||
        PrtgSensorStatuses.IsUp(source) && PrtgSensorStatuses.IsUp(timeline) ||
        PrtgSensorStatuses.IsUnknownOrEmpty(source) && PrtgSensorStatuses.IsUnknownOrEmpty(timeline);

    private static string DeviceEvidenceFingerprint(PrtgSilentDeviceSnapshot snapshot,
        IReadOnlyDictionary<long, PrtgResourceIdentity> identities,
        IReadOnlyDictionary<long, PrtgSensorTimelineEvidence> timelines)
    {
        var facts = new List<string>
        {
            snapshot.SourceAuthorityFingerprint, snapshot.PolicyRevision, snapshot.SourceGeneration,
            snapshot.MappingFingerprint, snapshot.InventoryFingerprint, snapshot.SourceDay?.ToString("O") ?? "",
            snapshot.SourceAsOf?.ToString("O") ?? "", snapshot.DeviceStatusAsOf?.ToString("O") ?? "",
            IdentityFingerprint(identities)
        };
        facts.AddRange(timelines.OrderBy(pair => pair.Key).Select(pair =>
        {
            var asOf = snapshot.SourceAsOf!.Value;
            var from = asOf.AddDays(-31);
            var semantic = pair.Value.States.Where(state => state.At >= from && state.At <= asOf)
                .OrderBy(state => state.At).Select(state => $"s:{state.At:O}={state.Status}")
                .Concat(pair.Value.Periods(from, asOf).OrderBy(period => period.From)
                    .Select(period => $"p:{period.From:O}/{period.Through:O}/{period.Status}/{period.EnteredAt:O}"));
            return $"{pair.Key}:{pair.Value.ResourceGeneration}:{pair.Value.IdentityEpoch}:" + HostDayWorkflowFingerprint.HashParts(semantic);
        }));
        return HostDayWorkflowFingerprint.HashParts(facts);
    }

    private static string IdentityFingerprint(IReadOnlyDictionary<long, PrtgResourceIdentity> identities)
    {
        var canonical = string.Join("\n", identities.OrderBy(pair => pair.Key).Select(pair =>
        {
            var value = pair.Value;
            return $"{pair.Key}|{value.Epoch}|{value.Generation}|{value.SourceGeneration}|{value.DeviceId}|{value.HostId}|" +
                $"{value.ResourceFingerprint}|{value.InventoryFingerprint}|{value.ChannelFingerprint}|{value.ChannelGeneration}|" +
                $"{value.Active}|{value.PendingReconciliation}";
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsWithinRequestedWindow(DateTimeOffset sourceAsOf,
        (DateTimeOffset Start, DateTimeOffset End) requestedWindow) =>
        sourceAsOf >= requestedWindow.Start && sourceAsOf < requestedWindow.End;

    private static (DateTimeOffset Start, DateTimeOffset End)? ResolveDailyWindowUtc(DateTime day)
    {
        var zone = TimeZoneInfo.Local;
        var localStart = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(day.Date.AddDays(1), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localStart) || zone.IsInvalidTime(localEnd) ||
            zone.IsAmbiguousTime(localStart) || zone.IsAmbiguousTime(localEnd)) return null;
        var start = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        var end = TimeZoneInfo.ConvertTimeToUtc(localEnd, zone);
        return (new DateTimeOffset(start), new DateTimeOffset(end));
    }

    private static bool HasValidInventoryFingerprint(PrtgSilentDeviceSnapshot snapshot)
    {
        if (snapshot.Sensors.Any(sensor => sensor.DeviceObjid != snapshot.DeviceObjid || sensor.SensorObjid <= 0) ||
            snapshot.Sensors.Select(sensor => sensor.SensorObjid).Distinct().Count() != snapshot.Sensors.Count) return false;
        var canonical = string.Join("\n", snapshot.Sensors.OrderBy(x => x.SensorObjid)
            .Select(x => $"{x.SensorObjid}|{x.DeviceObjid}|{x.SensorType}|{x.Category}|{x.SourceStatus}|{x.Paused}"));
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return string.Equals(actual, snapshot.InventoryFingerprint, StringComparison.Ordinal);
    }
}
