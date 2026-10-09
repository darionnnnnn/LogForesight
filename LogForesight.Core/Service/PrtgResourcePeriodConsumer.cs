using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

public sealed record PrtgResourcePeriodConsumption(
    IReadOnlyList<PrtgResourcePeriodAssessment> Assessments,
    IReadOnlyList<PrtgResourcePressureHint> Hints,
    IReadOnlyList<PrtgFinding> FormalFindings)
{
    public IReadOnlyList<PrtgResourceQualifiedFinding> QualifiedFormalFindings { get; init; } =
        Array.Empty<PrtgResourceQualifiedFinding>();
    public IReadOnlyList<long> SelectedSensorObjids { get; init; } = Array.Empty<long>();
    public IReadOnlyList<PrtgResourceFamily> EnabledFamilies { get; init; } = Array.Empty<PrtgResourceFamily>();
    public string SelectionEpoch { get; init; } = string.Empty;
    public string SelectedSensorFingerprint { get; init; } = string.Empty;
    public IReadOnlyDictionary<long, long> ModeBlobVersionsByHost { get; init; } = new Dictionary<long, long>();
    public IReadOnlyDictionary<long, IReadOnlyList<PrtgResourceGenerationFence>> ResourcePressureReevaluatedResourcesByHost { get; init; } =
        new Dictionary<long, IReadOnlyList<PrtgResourceGenerationFence>>();
    public IReadOnlyList<PrtgResourcePressureModeRevocation> PendingModeRevocations { get; init; } =
        Array.Empty<PrtgResourcePressureModeRevocation>();
    public PrtgResourceClosedDayMetadata? ClosedDay { get; init; }
}

/// <summary>Formal finding plus its exact host day and independently qualified supporting reasons.</summary>
public sealed record PrtgResourceQualifiedFinding(PrtgFinding Finding, DateTime EvidenceDay,
    IReadOnlyList<string> ReasonCodes, bool RequiresSingleWindow);

/// <summary>Trace for the bounded multi-window scan used only by closed-day evaluation.</summary>
public sealed record PrtgResourceClosedDayMetadata(DateTime EvidenceDay,
    IReadOnlyDictionary<long, int> EvaluatedWindowCountsBySensor,
    IReadOnlySet<long> RejectedSensorObjids, bool ExceededBatchRowBound,
    IReadOnlyList<long> SelectedSensorObjids, string SelectionEpoch)
{
    public IReadOnlyDictionary<long, int> ExpectedWindowCountsBySensor { get; init; } =
        new Dictionary<long, int>();
}

/// <summary>
/// Live bounded consumer for the last two completed resource hours. It reads only explicitly
/// requested sensor metadata, current profile/identity, and at most two hourly rows per sensor.
/// </summary>
public sealed class PrtgResourcePeriodConsumer
{
    private sealed record HistoricalAuthoritySnapshot(
        SystemSettings Settings,
        PrtgMonitoringPolicy Policy,
        PrtgTrustedSamplingStrategyContext Strategy,
        IReadOnlyDictionary<long, PrtgResourceIdentity> Identities,
        IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> Profiles,
        IReadOnlyDictionary<long, PrtgResourcePressureSensorMetadata> Metadata,
        PrtgResourceCurrentRuleCatalog Rules);

    public const int MaximumBatchSize = 100;
    public const int MaximumInputHours = 2;
    private readonly StorageBackend _backend;
    private readonly ISystemSettingsStore _settings;
    private readonly EfPrtgStore _store;
    private readonly PrtgTrustedSamplingProfileStore _profiles;
    private readonly PrtgMonitoringPolicyStore _policyStore;
    private readonly PrtgTrustedSamplingStrategyStateStore _strategyStore;
    private readonly PrtgResourcePressureAuthorizationService _authorization;
    internal Action? BeforeClosedDayAuthorityRecheckForTesting { get; set; }

    public static string ComputeSelectedSensorFingerprint(PrtgMonitoringPolicy policy,
        IEnumerable<long> evaluationHostIds, IEnumerable<long> selectedSensorObjids)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var hosts = evaluationHostIds.Where(id => id > 0).Distinct().Order().ToArray();
        var sensors = selectedSensorObjids.Where(id => id > 0).Distinct().Order().ToArray();
        var material = JsonSerializer.Serialize(new
        {
            policy.Revision, policy.SourceGeneration,
            Hosts = hosts, Sensors = sensors
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material)));
    }

    public static string ComputeSelectionEpoch(IEnumerable<PrtgResourceIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var material = JsonSerializer.Serialize(identities.Where(i => i.SensorId > 0)
            .OrderBy(i => i.SensorId).Select(i => new
            {
                i.SensorId, i.HostId, i.DeviceId, i.SourceGeneration,
                i.Generation, i.ChannelGeneration, i.Epoch, i.Active, i.PendingReconciliation
            }).ToArray());
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material)));
    }

    public PrtgResourcePeriodConsumer(StorageBackend backend, ISystemSettingsStore settings)
    {
        _backend = backend;
        _settings = settings;
        _store = backend.PrtgStore();
        _profiles = new PrtgTrustedSamplingProfileStore(backend);
        _policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        _strategyStore = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey));
        _authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
    }

    public PrtgResourcePeriodConsumption EvaluateBatch(IEnumerable<long> sensorObjids, DateTime asOfUtc)
        => EvaluateBatch(sensorObjids, asOfUtc, asOfUtc, null);

    public PrtgResourcePeriodConsumption EvaluateBatch(IEnumerable<long> sensorObjids,
        DateTime evidenceCutoffUtc, DateTime authorityNowUtc)
        => EvaluateBatch(sensorObjids, evidenceCutoffUtc, authorityNowUtc, null);

    public PrtgResourcePeriodConsumption EvaluateBatch(IEnumerable<long> sensorObjids,
        DateTime evidenceCutoffUtc, DateTime authorityNowUtc,
        IReadOnlyDictionary<long, IReadOnlyList<PrtgResourceFormalReasonObservation>>? diskReasonObservations)
        => EvaluateBatch(sensorObjids, evidenceCutoffUtc, authorityNowUtc, diskReasonObservations, null);

    /// <summary>Evaluates policy sensors within the current daily evaluation host scope.</summary>
    public PrtgResourcePeriodConsumption EvaluateBatch(IEnumerable<long> sensorObjids,
        DateTime evidenceCutoffUtc, DateTime authorityNowUtc,
        IReadOnlyDictionary<long, IReadOnlyList<PrtgResourceFormalReasonObservation>>? diskReasonObservations,
        IReadOnlyCollection<long>? evaluationHostIds)
        => EvaluateBatchCore(sensorObjids, evidenceCutoffUtc, authorityNowUtc, diskReasonObservations,
            evaluationHostIds, persistLiveState: true);

    private PrtgResourcePeriodConsumption EvaluateBatchCore(IEnumerable<long> sensorObjids,
        DateTime evidenceCutoffUtc, DateTime authorityNowUtc,
        IReadOnlyDictionary<long, IReadOnlyList<PrtgResourceFormalReasonObservation>>? diskReasonObservations,
        IReadOnlyCollection<long>? evaluationHostIds, bool persistLiveState)
    {
        ArgumentNullException.ThrowIfNull(sensorObjids);
        if (evidenceCutoffUtc.Kind != DateTimeKind.Utc || authorityNowUtc.Kind != DateTimeKind.Utc ||
            evidenceCutoffUtc > authorityNowUtc || authorityNowUtc > DateTime.UtcNow)
            throw new ArgumentException("資源期間與來源資格時間必須是有序的 UTC 時間。", nameof(evidenceCutoffUtc));
        var ids = sensorObjids.Where(id => id > 0).Distinct().Order().ToArray();
        if (ids.Length is < 1 or > MaximumBatchSize)
            throw new ArgumentOutOfRangeException(nameof(sensorObjids), $"每批需為 1 至 {MaximumBatchSize} 個 sensor。");
        var hostScope = evaluationHostIds?.Where(id => id > 0).Distinct().ToHashSet();
        if (hostScope is { Count: > 500 })
            throw new ArgumentOutOfRangeException(nameof(evaluationHostIds), "每日主機範圍需為 1 至 500 個 host。");
        if (hostScope is { Count: 0 }) return new([], [], []);

        var settings = _settings.Get();
        var policy = _policyStore.Get();
        var currentRules = PrtgResourceCurrentRuleCatalog.Load(_backend);
        var sensorMetadata = _store.GetResourcePressureSensorMetadata(ids).ToDictionary(s => s.SensorObjid);
        var identities = _store.GetResourceIdentities(ids);
        var profileMap = _profiles.GetMany(ids);
        var selectedSensorIds = ids.Where(id => sensorMetadata.TryGetValue(id, out var meta) &&
                FamilyForCategory(meta.Category).HasValue && identities.TryGetValue(id, out var identity) &&
                identity.Active && !identity.PendingReconciliation && identity.HostId > 0 &&
                identity.DeviceId == meta.DeviceObjid && !meta.Paused && !meta.DevicePaused &&
                policy.SensorIds.Contains(id) && policy.HostIds.Contains(identity.HostId) &&
                currentRules.For(FamilyForCategory(meta.Category)!.Value) is not null &&
                (hostScope is null || hostScope.Contains(identity.HostId)))
            .ToArray();
        var selectedIdentityRows = selectedSensorIds.Where(identities.ContainsKey)
            .Select(id => identities[id]).ToArray();
        var strategyProfile = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy);
        var strategy = _strategyStore.GetCurrent(policy, PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            strategyProfile.SnapshotIntervalMinutes, authorityNowUtc);

        DateTime wallFrom = default;
        DateTime wallTo = default;
        var wallRangeReady = strategy.Ready && TryCompletedWallHours(evidenceCutoffUtc,
            strategy.AnalysisTimeZoneId, out wallFrom, out wallTo);
        var pressureValues = wallRangeReady && selectedSensorIds.Length > 0
            ? _store.GetResourcePressureValues(selectedSensorIds, wallFrom, wallTo)
            : new EfPrtgStore.PrtgResourcePressureValuesResult([], false, new HashSet<long>());
        var rowsBySensor = pressureValues.Rows.GroupBy(r => r.SensorObjid)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var assessments = new List<PrtgResourcePeriodAssessment>();
        var hints = new List<PrtgResourcePressureHint>();
        var findings = new List<PrtgFinding>();
        var qualifiedFindings = new List<PrtgResourceQualifiedFinding>();
        var hintStores = new Dictionary<long, PrtgResourcePressureHintStore>();
        var currentHints = new Dictionary<long, List<PrtgResourcePressureHint>>();
        var modeBlobVersions = new Dictionary<long, long>();
        var pressureReevaluated = new Dictionary<long, List<PrtgResourceGenerationFence>>();
        var pendingModeRevocations = new List<PrtgResourcePressureModeRevocation>();
        foreach (var sensorId in ids)
        {
            if (!identities.TryGetValue(sensorId, out var identity) || identity.HostId <= 0 ||
                hostScope is not null && !hostScope.Contains(identity.HostId)) continue;
            if (sensorMetadata.TryGetValue(sensorId, out var sensorMeta) &&
                FamilyForCategory(sensorMeta.Category) is not (PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory)) continue;
            var snapshot = new PrtgResourcePressureModeStore(_backend.Blob(
                PrtgResourcePressureModeStore.BlobKey(identity.HostId))).ReadHostSnapshot(identity.HostId);
            CaptureModeVersion(identity.HostId, snapshot.BlobVersion);
            foreach (var revocation in snapshot.Revocations.Where(item => item.SensorObjid == sensorId))
                AddPendingModeRevocation(revocation);
        }
        foreach (var sensorId in ids)
        {
            if (!sensorMetadata.TryGetValue(sensorId, out var metadata) || metadata.Paused || metadata.DevicePaused ||
                !identities.TryGetValue(sensorId, out var identity) || !identity.Active ||
                identity.PendingReconciliation || identity.HostId <= 0 || identity.DeviceId <= 0 ||
                identity.DeviceId != metadata.DeviceObjid ||
                !policy.SensorIds.Contains(sensorId) || !policy.HostIds.Contains(identity.HostId) ||
                hostScope is not null && !hostScope.Contains(identity.HostId))
                continue;

            var family = FamilyForCategory(metadata.Category);
            if (!family.HasValue) continue;
            var currentRule = currentRules.For(family.Value);
            if (currentRule is null) continue;
            bool IsCurrentRuleSnapshot()
            {
                var liveCatalog = PrtgResourceCurrentRuleCatalog.Load(_backend);
                var liveRule = liveCatalog.For(family.Value);
                return liveRule?.Rule.Id == currentRule?.Rule.Id &&
                       liveRule?.Fingerprint == currentRule?.Fingerprint &&
                       liveCatalog.AdmissionFingerprintFor(family.Value) ==
                       currentRules.AdmissionFingerprintFor(family.Value);
            }
            var profileMapHas = profileMap.TryGetValue(sensorId, out var profile);
            PrtgResourceCurrentContext? currentContext = null;
            PrtgResourceMeasurementDefinition? measurement = null;
            string reasonCode;
            string[] missingFacts = [];

            if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl))
            {
                reasonCode = "source-policy-unready";
                missingFacts = ["current_source_policy"];
            }
            else if (!strategy.Ready)
            {
                reasonCode = "current-strategy-unverified";
                missingFacts = strategy.MissingFacts.ToArray();
            }
            else if (!profileMapHas || profile is null)
            {
                reasonCode = "source-profile-missing";
                missingFacts = ["source_metadata_profile"];
            }
            else
            {
                var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy,
                    sensorId, metadata.SensorType, strategy, authorityNowUtc, evidenceCutoffUtc,
                    authorityNowUtc);
                if (!resolution.Ready)
                {
                    reasonCode = resolution.RejectionReason switch
                    {
                        "source_authority_incomplete" => "source-authority-incomplete",
                        "profile_invalid" => "source-profile-invalid",
                        // A rejected profile with the wrong resource quantity must retain the
                        // established semantic waiting reason. This never authorizes context.
                        "profile_binding_semantics_mismatch" when
                            !TryMeasurement(family.Value, profile.Quantity, profile.Unit, out _) =>
                            "measurement-semantics-unverified",
                        _ => "source-profile-stale"
                    };
                    missingFacts = reasonCode == "measurement-semantics-unverified"
                        ? ["measurement_semantics"] : resolution.MissingFacts.ToArray();
                }
                else if (!TryMeasurement(family.Value, profile.Quantity, profile.Unit, out measurement))
                {
                    reasonCode = "measurement-semantics-unverified";
                    missingFacts = ["measurement_semantics"];
                }
                else
                {
                    currentContext = ToCurrentContext(profile, strategy);
                    reasonCode = "ready";
                }
            }

            if (reasonCode == "ready" && pressureValues.RejectedSensorObjids.Contains(sensorId))
            {
                reasonCode = pressureValues.ExceededRowBound
                    ? "trusted-hour-row-bound-exceeded" : "trusted-hour-row-invalid-or-oversized";
                missingFacts = ["bounded_trusted_hour_value_rows"];
            }

            var hours = currentContext is not null && rowsBySensor.TryGetValue(sensorId, out var values)
                ? ToHourlyEvidence(sensorId, values)
                : Array.Empty<PrtgResourceHourlyEvidence>();
            var input = new PrtgResourceReadinessInput(family.Value, currentContext, measurement, hours, evidenceCutoffUtc);
            var decision = reasonCode == "ready"
                ? PrtgResourcePressureEvaluator.Evaluate(input)
                : Insufficient(family.Value, input, reasonCode);
            if (reasonCode == "ready" && decision.Kind == PrtgResourceDecisionKind.Insufficient)
            {
                reasonCode = decision.ReasonCode;
                missingFacts = decision.ReasonCode switch
                {
                    "latest-two-completed-hours-missing" => ["latest_completed_hour_slots"],
                    "hour-coverage-below-75-percent" => ["minimum_75_percent_good_slots_per_hour"],
                    "summary-does-not-match-proved-slots" => ["summary_slot_proof_consistency"],
                    "invalid-hour-proof" => ["trusted_hour_slot_proof"],
                    _ => ["trusted_hour_evidence"]
                };
            }

            var assessment = new PrtgResourcePeriodAssessment(identity.HostId, identity.DeviceId,
                sensorId, family.Value, input, decision, authorityNowUtc, identity.SourceGeneration, identity.Generation,
                identity.ChannelGeneration, identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                profile?.SemanticVersion ?? "", profile?.StrategyFingerprint ?? "", currentRule?.Rule,
                currentRule?.Fingerprint ?? "", currentRules.AdmissionFingerprintFor(family.Value));
            var (authorization, modeSnapshot) = _authorization.GetCurrentAuthorizationSnapshot(assessment, authorityNowUtc);
            if (family.Value is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory)
            {
                CaptureModeVersion(identity.HostId, modeSnapshot.BlobVersion);
                if (modeSnapshot.Revocation is { } revocation) AddPendingModeRevocation(revocation);
            }
            if (currentContext is not null && authorization is not null)
            {
                decision = PrtgResourcePressureEvaluator.EvaluateAuthorized(input, authorization);
                assessment = new PrtgResourcePeriodAssessment(identity.HostId, identity.DeviceId,
                    sensorId, family.Value, input, decision, authorityNowUtc, identity.SourceGeneration, identity.Generation,
                    identity.ChannelGeneration, identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    profile!.SemanticVersion, profile.StrategyFingerprint, currentRule?.Rule,
                    currentRule?.Fingerprint ?? "", currentRules.AdmissionFingerprintFor(family.Value));
            }

            // The source/profile/identity can rotate while slot proofs are being checked. Never
            // persist evidence based on the initial snapshot after that fence has moved.
            if (!IsCurrentSnapshot(sensorId, identity, profile, metadata, strategy, settings,
                    policy, evidenceCutoffUtc, authorityNowUtc) || !IsCurrentRuleSnapshot())
                continue;

            if (!hintStores.TryGetValue(identity.HostId, out var hintStore))
            {
                hintStore = new PrtgResourcePressureHintStore(_backend.Blob(
                    PrtgResourcePressureHintStore.BlobKey(identity.HostId)));
                hintStores.Add(identity.HostId, hintStore);
                currentHints[identity.HostId] = hintStore.GetCurrent(identity.HostId, authorityNowUtc).ToList();
            }
            var hostHints = currentHints[identity.HostId];
            var previous = persistLiveState
                ? hostHints.FirstOrDefault(h => h.SensorObjid == sensorId && h.Family == family.Value)
                : null;
            var hint = ToHint(assessment, previous, missingFacts);
            if (!IsCurrentSnapshot(sensorId, identity, profile, metadata, strategy, settings,
                    policy, evidenceCutoffUtc, authorityNowUtc) || !IsCurrentRuleSnapshot())
                continue;
            if (persistLiveState)
            {
                var sameEvidenceProjection = previous is not null &&
                    JsonSerializer.Serialize(previous with { AsOfUtc = hint.AsOfUtc }) == JsonSerializer.Serialize(hint);
                if (!sameEvidenceProjection) hintStore.Upsert(hint);
                if (!IsCurrentSnapshot(sensorId, identity, profile, metadata, strategy, settings,
                        policy, evidenceCutoffUtc, DateTime.UtcNow) || !IsCurrentRuleSnapshot())
                    continue;
            }

            if ((family.Value is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory) &&
                assessment.SingleWindowHostDay.HasValue && decision.Kind != PrtgResourceDecisionKind.Insufficient &&
                modeSnapshot.BlobVersion > 0 && modeBlobVersions.GetValueOrDefault(identity.HostId) > 0)
            {
                if (!pressureReevaluated.TryGetValue(identity.HostId, out var resources))
                    pressureReevaluated[identity.HostId] = resources = new List<PrtgResourceGenerationFence>();
                resources.RemoveAll(item => item.SensorObjid == sensorId);
                resources.Add(new(sensorId, assessment.SourceGeneration, assessment.ResourceGeneration));
            }
            if (previous is null) hostHints.Add(hint);
            else hostHints[hostHints.IndexOf(previous)] = hint;
            if (family.Value == PrtgResourceFamily.Disk)
            {
                var reasons = new List<PrtgResourceFormalReasonObservation>();
                var lowWaterEvidenceDay = assessment.SingleWindowHostDay;
                var lowWaterState = currentRule is null ||
                    decision.Kind == PrtgResourceDecisionKind.Hit && !lowWaterEvidenceDay.HasValue
                    ? PrtgResourceFormalReasonState.Unknown : decision.Kind switch
                {
                    PrtgResourceDecisionKind.Hit => PrtgResourceFormalReasonState.Active,
                    PrtgResourceDecisionKind.Recovery => PrtgResourceFormalReasonState.Recovered,
                    _ => PrtgResourceFormalReasonState.Unknown
                };
                reasons.Add(new(sensorId, assessment.SourceGeneration, assessment.ResourceGeneration,
                    assessment.ChannelGeneration, assessment.ResourceEpoch, "disk-two-hour-low-water",
                    lowWaterState, assessment.EvidenceFingerprint, SummarizeLowWater(hint, decision),
                    EvidenceDay: lowWaterEvidenceDay, EvidenceAsOfUtc: assessment.EvidenceAsOfUtc));
                if (diskReasonObservations?.TryGetValue(sensorId, out var otherReasons) == true)
                    reasons.AddRange(otherReasons.Select(reason =>
                    {
                        var trendReason = reason.ReasonCode == "disk-seven-day-low-water-trend";
                        var sourceRuleCurrent = !trendReason || currentRules.DiskTrendEnabled &&
                            reason.SourceRuleId == currentRules.DiskTrendRuleId &&
                            reason.SourceRuleFingerprint == currentRules.DiskTrendRuleFingerprint;
                        var currentReason = reason with
                        {
                            EvidenceAsOfUtc = reason.EvidenceAsOfUtc ?? assessment.EvidenceAsOfUtc
                        };
                        return !sourceRuleCurrent || reason.State == PrtgResourceFormalReasonState.Active &&
                            (input.AuthorizedContext is null || !reason.EvidenceDay.HasValue)
                            ? currentReason with { State = PrtgResourceFormalReasonState.Unknown } : currentReason;
                    }));
                if (!IsCurrentSnapshot(sensorId, identity, profile, metadata, strategy, settings,
                        policy, evidenceCutoffUtc, authorityNowUtc) || !IsCurrentRuleSnapshot())
                    continue;
                PrtgResourceFormalEpisode? episode;
                if (persistLiveState)
                {
                    var reconcile = new PrtgResourceFormalEpisodeStore(_backend.Blob(
                        PrtgResourceFormalEpisodeStore.BlobKey(assessment.HostId))).ReconcileWithStatus(assessment, reasons);
                    if (!IsCurrentSnapshot(sensorId, identity, profile, metadata, strategy, settings,
                            policy, evidenceCutoffUtc, DateTime.UtcNow) || !IsCurrentRuleSnapshot())
                        continue;
                    episode = reconcile.Episode;
                    if (episode is null && !reconcile.Accepted && assessment.CurrentRule is not null && input.AuthorizedContext is not null)
                    {
                        // The stale evaluation can still report its own qualified day without
                        // replacing a newer current episode or its tombstone.
                        var historicalEpisode = ToHistoricalDiskEpisode(assessment, reasons);
                        if (historicalEpisode is not null)
                        {
                            var diskFindings = ToDiskEpisodeFindings(assessment, historicalEpisode, currentRules);
                            findings.AddRange(diskFindings.Select(item => item.Finding));
                            qualifiedFindings.AddRange(diskFindings);
                        }
                    }
                }
                else
                {
                    // Closed-day replay is read-only with respect to live disk episode state.
                    episode = ToHistoricalDiskEpisode(assessment, reasons);
                }
                if (episode is not null && assessment.CurrentRule is not null && input.AuthorizedContext is not null)
                {
                    var diskFindings = ToDiskEpisodeFindings(assessment, episode, currentRules);
                    findings.AddRange(diskFindings.Select(item => item.Finding));
                    qualifiedFindings.AddRange(diskFindings);
                }
            }
            else if (decision.Mode == PrtgResourceDecisionMode.FormalRisk &&
                assessment.CurrentRule is not null &&
                (decision.Kind == PrtgResourceDecisionKind.Hit ||
                 hint.EpisodeStartedAtUtc.HasValue && (decision.Kind is
                     PrtgResourceDecisionKind.NoHit or PrtgResourceDecisionKind.Insufficient)))
            {
                var evidenceDay = assessment.SingleWindowHostDay;
                if (evidenceDay.HasValue)
                {
                    var finding = ToFormalFinding(assessment, hint);
                    findings.Add(finding);
                    qualifiedFindings.Add(new(finding, evidenceDay.Value, [finding.RuleCode], RequiresSingleWindow: true));
                }
            }

            assessments.Add(assessment);
            hints.Add(hint);
        }

        var selectedHosts = hostScope ?? selectedIdentityRows.Select(i => i.HostId).Distinct().ToHashSet();
        return new(assessments, hints, findings)
        {
            SelectedSensorObjids = selectedSensorIds,
            EnabledFamilies = selectedSensorIds.Select(id => FamilyForCategory(sensorMetadata[id].Category)!.Value)
                .Distinct().Order().ToArray(),
            SelectionEpoch = ComputeSelectionEpoch(selectedIdentityRows),
            SelectedSensorFingerprint = ComputeSelectedSensorFingerprint(policy, selectedHosts, selectedSensorIds),
            QualifiedFormalFindings = qualifiedFindings,
            ModeBlobVersionsByHost = modeBlobVersions,
            ResourcePressureReevaluatedResourcesByHost = pressureReevaluated.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<PrtgResourceGenerationFence>)pair.Value.ToArray()),
            PendingModeRevocations = pendingModeRevocations
        };

        void CaptureModeVersion(long hostId, long version)
        {
            if (modeBlobVersions.TryGetValue(hostId, out var priorVersion) && priorVersion != version)
                modeBlobVersions[hostId] = -1;
            else if (!modeBlobVersions.ContainsKey(hostId)) modeBlobVersions[hostId] = version;
        }

        void AddPendingModeRevocation(PrtgResourcePressureModeRevocation revocation)
        {
            if (!pendingModeRevocations.Any(item => item.HostId == revocation.HostId &&
                    item.SensorObjid == revocation.SensorObjid && item.Family == revocation.Family &&
                    item.SourceGeneration == revocation.SourceGeneration &&
                    item.ResourceGeneration == revocation.ResourceGeneration))
                pendingModeRevocations.Add(revocation);
        }
    }

    /// <summary>
    /// Evaluates a closed host day explicitly: one ordinary evaluation establishes the latest
    /// window/readiness state, then a pure bounded scan checks historical adjacent windows. The
    /// historical pass never writes hints or formal episodes.
    /// </summary>
    public PrtgResourcePeriodConsumption EvaluateClosedHostDayBatch(IEnumerable<long> sensorObjids,
        DateTime evidenceDay, DateTime authorityNowUtc,
        IReadOnlyDictionary<long, IReadOnlyList<PrtgResourceFormalReasonObservation>>? diskReasonObservations = null,
        IReadOnlyCollection<long>? evaluationHostIds = null)
    {
        ArgumentNullException.ThrowIfNull(sensorObjids);
        var requestedSensorIds = sensorObjids.ToArray();
        if (evidenceDay.Kind != DateTimeKind.Unspecified || evidenceDay.TimeOfDay != TimeSpan.Zero ||
            evidenceDay.Date >= DateTime.Today || authorityNowUtc.Kind != DateTimeKind.Utc || authorityNowUtc > DateTime.UtcNow)
            throw new ArgumentException("歷史資源評估需指定已關閉的主機日與目前 UTC 權限時間。", nameof(evidenceDay));
        DateTime dayEndUtc;
        try
        {
            dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(evidenceDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return EmptyClosedDay(evidenceDay, requestedSensorIds);
        }
        if (dayEndUtc > authorityNowUtc) return EmptyClosedDay(evidenceDay, requestedSensorIds);

        // Keep automatic latest-window hints/episodes on the existing bounded path in this
        // unified job. Historical-day replay below is read-only with respect to those stores.
        var livePolicy = _policyStore.Get();
        var liveEvidenceCutoffUtc = TryCurrentWallHourStartUtc(authorityNowUtc,
            livePolicy.AnalysisTimeZoneId, out var stableLiveCutoffUtc) ? stableLiveCutoffUtc : authorityNowUtc;
        var live = EvaluateBatchCore(requestedSensorIds, liveEvidenceCutoffUtc, authorityNowUtc,
            diskReasonObservations: null, evaluationHostIds: evaluationHostIds, persistLiveState: true);
        // This separate call preserves the established day-close readiness contract without
        // allowing an older cutoff to update live hints or episodes.
        var current = EvaluateBatchCore(requestedSensorIds, dayEndUtc, authorityNowUtc,
            diskReasonObservations, evaluationHostIds, persistLiveState: false);
        if (live.SelectionEpoch != current.SelectionEpoch ||
            !live.SelectedSensorObjids.Order().SequenceEqual(current.SelectedSensorObjids.Order()) ||
            live.SelectedSensorFingerprint != current.SelectedSensorFingerprint)
            return current with { Hints = live.Hints, ClosedDay = new(evidenceDay,
                new Dictionary<long, int>(), current.SelectedSensorObjids.ToHashSet(), false,
                current.SelectedSensorObjids, current.SelectionEpoch) };
        if (current.SelectedSensorObjids.Count == 0)
            return current with { Hints = live.Hints,
                ClosedDay = new(evidenceDay, new Dictionary<long, int>(), new HashSet<long>(), false,
                current.SelectedSensorObjids, current.SelectionEpoch) };

        DateTime hostDayStartUtc;
        try
        {
            hostDayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(evidenceDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return current with { Hints = live.Hints, ClosedDay = new(evidenceDay, new Dictionary<long, int>(),
                current.SelectedSensorObjids.ToHashSet(), false, current.SelectedSensorObjids, current.SelectionEpoch) };
        }
        var hostDayEndUtc = dayEndUtc;
        if (hostDayEndUtc <= hostDayStartUtc || hostDayEndUtc - hostDayStartUtc > TimeSpan.FromHours(26))
            return current with { Hints = live.Hints, ClosedDay = new(evidenceDay, new Dictionary<long, int>(),
                current.SelectedSensorObjids.ToHashSet(), false, current.SelectedSensorObjids, current.SelectionEpoch) };
        var analysisZoneId = current.Assessments.Select(item => item.Input.AuthorizedContext?.AnalysisTimeZoneId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (analysisZoneId is null) return current with { Hints = live.Hints, ClosedDay = new(evidenceDay,
            new Dictionary<long, int>(), current.SelectedSensorObjids.ToHashSet(), false,
            current.SelectedSensorObjids, current.SelectionEpoch) };
        TimeZoneInfo analysisZone;
        try { analysisZone = TimeZoneInfo.FindSystemTimeZoneById(analysisZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return current with { Hints = live.Hints, ClosedDay = new(evidenceDay, new Dictionary<long, int>(), current.SelectedSensorObjids.ToHashSet(), false,
            current.SelectedSensorObjids, current.SelectionEpoch) }; }
        var analysisFromWall = TimeZoneInfo.ConvertTimeFromUtc(hostDayStartUtc, analysisZone);
        var analysisToWall = TimeZoneInfo.ConvertTimeFromUtc(hostDayEndUtc, analysisZone);
        var dayStartWall = DateTime.SpecifyKind(new DateTime(analysisFromWall.Year, analysisFromWall.Month,
            analysisFromWall.Day, analysisFromWall.Hour, 0, 0), DateTimeKind.Unspecified);
        var dayEndWall = DateTime.SpecifyKind(new DateTime(analysisToWall.Year, analysisToWall.Month,
            analysisToWall.Day, analysisToWall.Hour, 0, 0), DateTimeKind.Unspecified);
        if (analysisToWall.Minute > 0 || analysisToWall.Second > 0 || analysisToWall.Ticks % TimeSpan.TicksPerMinute > 0)
            dayEndWall = dayEndWall.AddHours(1);
        if (dayEndWall <= dayStartWall || dayEndWall - dayStartWall > TimeSpan.FromHours(26))
            return current with { Hints = live.Hints, ClosedDay = new(evidenceDay, new Dictionary<long, int>(),
                current.SelectedSensorObjids.ToHashSet(), false, current.SelectedSensorObjids, current.SelectionEpoch) };
        // A window is assigned by the host-local date of its later hour start. Read one
        // additional analysis-zone hour before the day boundary so a 23:00/00:00 pair is complete.
        var scanStartWall = dayStartWall.AddHours(-1);
        var (expectedWindowStarts, unresolvableDayWindow) = ExpectedHostDayWindows(
            scanStartWall, dayEndWall, analysisZone, evidenceDay.Date);
        var values = _store.GetResourcePressureValuesForClosedDay(current.SelectedSensorObjids,
            scanStartWall, dayEndWall);
        var rejected = values.RejectedSensorObjids.ToHashSet();
        var rowsBySensor = values.Rows.GroupBy(row => row.SensorObjid)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.PeriodStart).ToArray());
        var authoritySnapshotBefore = ReadHistoricalAuthoritySnapshot(current.SelectedSensorObjids, authorityNowUtc);
        var counts = new Dictionary<long, int>();
        var expectedCounts = new Dictionary<long, int>();
        var historicalBySensor = new Dictionary<long, PrtgResourceQualifiedFinding>();
        var authorityInvalidSensorIds = new HashSet<long>();
        var authorizationBySensor = new Dictionary<long, PrtgResourcePolicyAuthorization?>();
        foreach (var latest in current.Assessments)
        {
            expectedCounts[latest.SensorObjid] = expectedWindowStarts.Count;
            if (!HistoricalSnapshotCurrent(latest, authoritySnapshotBefore, dayEndUtc, authorityNowUtc))
            {
                rejected.Add(latest.SensorObjid);
                authorityInvalidSensorIds.Add(latest.SensorObjid);
                continue;
            }
            var authorization = _authorization.GetCurrentAuthorization(latest, authorityNowUtc);
            authorizationBySensor[latest.SensorObjid] = authorization;
            if (latest.Input.AuthorizedContext is not { } context || latest.CurrentRule is null ||
                !rowsBySensor.TryGetValue(latest.SensorObjid, out var sensorRows) || rejected.Contains(latest.SensorObjid))
            {
                rejected.Add(latest.SensorObjid);
                continue;
            }
            var hourly = ToHourlyEvidence(latest.SensorObjid, sensorRows);
            var byWallHour = hourly.GroupBy(hour => hour.WallPeriodStart).ToDictionary(group => group.Key, group => group.ToArray());
            var windowCount = 0;
            var invalidWindowEvidence = unresolvableDayWindow;
            PrtgResourceQualifiedFinding? sensorHistoricalFinding = null;
            foreach (var first in expectedWindowStarts)
            {
                var second = first.AddHours(1);
                if (!byWallHour.TryGetValue(second, out var secondRows) ||
                    !byWallHour.TryGetValue(first, out var firstRows) || firstRows.Length != 1 || secondRows.Length != 1)
                    continue;
                var endWall = second.AddHours(1);
                DateTime windowAsOfUtc;
                try
                {
                    windowAsOfUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endWall, DateTimeKind.Unspecified), analysisZone);
                }
                catch (ArgumentException) { continue; }
                if (windowAsOfUtc > authorityNowUtc) continue;
                var pair = new[] { firstRows[0], secondRows[0] };
                var input = new PrtgResourceReadinessInput(latest.Family, context,
                    latest.Input.Measurement, pair, windowAsOfUtc);
                var decision = PrtgResourcePressureEvaluator.Evaluate(input);
                var assessment = MakeAssessment(latest, input, decision);
                if (authorization is not null)
                {
                    decision = PrtgResourcePressureEvaluator.EvaluateAuthorized(input, authorization);
                    assessment = MakeAssessment(latest, input, decision);
                }
                if (assessment.SingleWindowHostDay?.Date != evidenceDay.Date) continue;
                windowCount++;
                if (decision.Kind == PrtgResourceDecisionKind.Insufficient) invalidWindowEvidence = true;
                if (decision.Kind != PrtgResourceDecisionKind.Hit)
                    continue;
                var hint = ToHint(assessment, null, []);
                PrtgResourceQualifiedFinding? qualified;
                if (assessment.Family == PrtgResourceFamily.Disk)
                {
                    var reasons = new List<PrtgResourceFormalReasonObservation>
                    {
                        new(assessment.SensorObjid, assessment.SourceGeneration, assessment.ResourceGeneration,
                            assessment.ChannelGeneration, assessment.ResourceEpoch, "disk-two-hour-low-water",
                            PrtgResourceFormalReasonState.Active, assessment.EvidenceFingerprint,
                            SummarizeLowWater(hint, decision), EvidenceDay: evidenceDay,
                            EvidenceAsOfUtc: assessment.EvidenceAsOfUtc)
                    };
                    if (diskReasonObservations?.TryGetValue(assessment.SensorObjid, out var extras) == true)
                        reasons.AddRange(extras.Where(reason => reason.State == PrtgResourceFormalReasonState.Active &&
                            reason.EvidenceDay?.Date == evidenceDay.Date));
                    var episode = ToHistoricalDiskEpisode(assessment, reasons);
                    var diskFinding = episode is null ? [] : ToDiskEpisodeFindings(assessment, episode,
                        authoritySnapshotBefore.Rules);
                    qualified = diskFinding.FirstOrDefault(item => item.EvidenceDay.Date == evidenceDay.Date);
                }
                else if (decision.Mode == PrtgResourceDecisionMode.FormalRisk)
                {
                    var finding = ToFormalFinding(assessment, hint);
                    qualified = new(finding, evidenceDay, [finding.RuleCode], RequiresSingleWindow: true);
                }
                else qualified = null;

                sensorHistoricalFinding ??= qualified;
            }
            if (sensorHistoricalFinding is not null)
                historicalBySensor[latest.SensorObjid] = sensorHistoricalFinding;
            counts[latest.SensorObjid] = windowCount;
            if (windowCount != expectedWindowStarts.Count || invalidWindowEvidence)
                rejected.Add(latest.SensorObjid);
        }

        BeforeClosedDayAuthorityRecheckForTesting?.Invoke();
        var authoritySnapshotAfter = ReadHistoricalAuthoritySnapshot(current.SelectedSensorObjids, authorityNowUtc);
        foreach (var latest in current.Assessments)
        {
            if (HistoricalSnapshotCurrent(latest, authoritySnapshotBefore, dayEndUtc, authorityNowUtc) &&
                HistoricalSnapshotCurrent(latest, authoritySnapshotAfter, dayEndUtc, authorityNowUtc) &&
                authorizationBySensor.TryGetValue(latest.SensorObjid, out var authorizationBefore) &&
                SameAuthorization(authorizationBefore,
                    _authorization.GetCurrentAuthorization(latest, authorityNowUtc))) continue;
            rejected.Add(latest.SensorObjid);
            authorityInvalidSensorIds.Add(latest.SensorObjid);
            historicalBySensor.Remove(latest.SensorObjid);
        }

        // Missing historical windows keep readiness Unknown but do not erase a credible earlier hit.
        // A changed authority invalidates both the scan result and the day-close base finding.
        var qualifiedFindings = current.QualifiedFormalFindings.Where(item =>
            item.Finding.SensorObjid is not { } sensorId || !authorityInvalidSensorIds.Contains(sensorId)).ToList();
        foreach (var (sensorId, historical) in historicalBySensor)
        {
            var baseDisk = qualifiedFindings.Where(item => item.Finding.SensorObjid == sensorId &&
                item.Finding.SensorCategory == PrtgSensorCategories.Disk).ToArray();
            if (historical.Finding.SensorCategory == PrtgSensorCategories.Disk && baseDisk.Length > 0)
            {
                var mergedReasons = historical.ReasonCodes.Concat(baseDisk.SelectMany(item => item.ReasonCodes))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var baseTrend = baseDisk.FirstOrDefault(item => item.ReasonCodes.Contains(
                    "disk-seven-day-low-water-trend", StringComparer.Ordinal))?.Finding;
                var finding = historical.Finding with
                {
                    ResourceReasonCodes = mergedReasons,
                    TrendSourceRuleId = historical.Finding.TrendSourceRuleId ?? baseTrend?.TrendSourceRuleId,
                    TrendSourceRuleFingerprint = historical.Finding.TrendSourceRuleFingerprint ??
                        baseTrend?.TrendSourceRuleFingerprint,
                    EvidenceWindows = historical.Finding.EvidenceWindows.Concat(baseDisk
                        .SelectMany(item => item.Finding.EvidenceWindows)).Distinct().ToArray(),
                    Detail = baseTrend is null ? historical.Finding.Detail :
                        historical.Finding.Detail + "；" + baseTrend.Detail
                };
                qualifiedFindings.RemoveAll(item => item.Finding.SensorObjid == sensorId &&
                    item.Finding.SensorCategory == PrtgSensorCategories.Disk);
                qualifiedFindings.Add(historical with { Finding = finding, ReasonCodes = mergedReasons,
                    RequiresSingleWindow = !mergedReasons.Contains("disk-seven-day-low-water-trend", StringComparer.Ordinal) });
            }
            else qualifiedFindings.Add(historical);
        }
        qualifiedFindings = MergeCanonicalFindings(qualifiedFindings).ToList();
        var allFindings = qualifiedFindings.Select(item => item.Finding).ToArray();
        return current with
        {
            Hints = live.Hints,
            FormalFindings = allFindings,
            QualifiedFormalFindings = qualifiedFindings,
            ClosedDay = new(evidenceDay, counts, rejected, values.ExceededRowBound,
                current.SelectedSensorObjids, current.SelectionEpoch)
            { ExpectedWindowCountsBySensor = expectedCounts }
        };
    }

    private static PrtgResourcePeriodAssessment MakeAssessment(PrtgResourcePeriodAssessment basis,
        PrtgResourceReadinessInput input, PrtgResourcePressureDecision decision) =>
        new(basis.HostId, basis.DeviceObjid, basis.SensorObjid, basis.Family, input, decision,
            basis.AuthorityAsOfUtc, basis.SourceGeneration, basis.ResourceGeneration, basis.ChannelGeneration,
            basis.ResourceEpoch, basis.SemanticVersion, basis.StrategyVersion, basis.CurrentRule,
            basis.CurrentRuleFingerprint, basis.RuleAdmissionFingerprint);

    private static IReadOnlyList<PrtgResourceQualifiedFinding> MergeCanonicalFindings(
        IEnumerable<PrtgResourceQualifiedFinding> findings) => findings
        .GroupBy(item => (item.Finding.SensorObjid, item.Finding.RuleCode, EvidenceDay: item.EvidenceDay.Date,
            item.Finding.ResourceGeneration))
        .Select(group =>
        {
            var items = group.ToArray();
            if (items.Length == 1) return items[0];
            var representative = items.OrderBy(item => item.Finding.IncidentStartedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.Finding.EvidenceWindows.Count == 0 ? DateTimeOffset.MaxValue :
                    item.Finding.EvidenceWindows.Min(window => window.StartUtc)).First();
            var reasonCodes = items.SelectMany(item => item.ReasonCodes).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var resourceReasons = items.SelectMany(item => item.Finding.ResourceReasonCodes ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var windows = items.SelectMany(item => item.Finding.EvidenceWindows).Distinct()
                .OrderBy(window => window.StartUtc).ThenBy(window => window.EndUtc).ToArray();
            var mergedFinding = representative.Finding with
            {
                Detail = string.Join("；", items.Select(item => item.Finding.Detail)
                    .Distinct(StringComparer.Ordinal)),
                Magnitude = items.Max(item => item.Finding.Magnitude),
                EvidenceWindows = windows,
                ResourceReasonCodes = resourceReasons,
                ThresholdMagnitude = items.Where(item => item.Finding.ThresholdMagnitude.HasValue)
                    .Select(item => item.Finding.ThresholdMagnitude).Max()
            };
            return representative with
            {
                Finding = mergedFinding,
                ReasonCodes = reasonCodes,
                RequiresSingleWindow = items.All(item => item.RequiresSingleWindow)
            };
        })
        .OrderBy(item => item.EvidenceDay.Date)
        .ThenBy(item => item.Finding.SensorObjid)
        .ThenBy(item => item.Finding.RuleCode, StringComparer.Ordinal)
        .ToArray();

    private HistoricalAuthoritySnapshot ReadHistoricalAuthoritySnapshot(
        IReadOnlyCollection<long> sensorIds, DateTime authorityNowUtc)
    {
        var settings = _settings.Get();
        var policy = _policyStore.Get();
        var strategyProfile = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy);
        var strategy = _strategyStore.GetCurrent(policy, PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
            strategyProfile.SnapshotIntervalMinutes, authorityNowUtc);
        var ids = sensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        var identities = _store.GetResourceIdentities(ids);
        var profiles = _profiles.GetMany(ids);
        var metadata = _store.GetResourcePressureSensorMetadata(ids)
            .ToDictionary(item => item.SensorObjid);
        var rules = PrtgResourceCurrentRuleCatalog.Load(_backend);
        return new(settings, policy, strategy, identities, profiles, metadata, rules);
    }

    private static bool HistoricalSnapshotCurrent(PrtgResourcePeriodAssessment expected,
        HistoricalAuthoritySnapshot snapshot, DateTime evidenceCutoffUtc, DateTime authorityNowUtc)
    {
        var settings = snapshot.Settings;
        var policy = snapshot.Policy;
        var strategy = snapshot.Strategy;
        var liveRule = snapshot.Rules.For(expected.Family);
        return snapshot.Identities.TryGetValue(expected.SensorObjid, out var identity) &&
            SameIdentityFields(expected, identity) && snapshot.Metadata.TryGetValue(expected.SensorObjid, out var metadata) &&
            !metadata.Paused && !metadata.DevicePaused && metadata.DeviceObjid == expected.DeviceObjid &&
            FamilyForCategory(metadata.Category) == expected.Family &&
            snapshot.Profiles.TryGetValue(expected.SensorObjid, out var profile) && profile is not null &&
            StableProfileFingerprint(profile) == expected.Input.AuthorizedContext?.ProfileFingerprint &&
            profile.SourceGeneration == expected.SourceGeneration && profile.ResourceGeneration == expected.ResourceGeneration &&
            profile.ChannelGeneration == expected.ChannelGeneration &&
            settings.PrtgEnabled && policy.Ready(settings.PrtgUrl) &&
            policy.SourceGeneration == expected.SourceGeneration &&
            policy.SensorIds.Contains(expected.SensorObjid) && policy.HostIds.Contains(expected.HostId) && strategy.Ready &&
            strategy.StrategyFingerprint == expected.Input.AuthorizedContext?.StrategyVersion &&
            strategy.AnalysisTimeZoneId == expected.Input.AuthorizedContext?.AnalysisTimeZoneId &&
            (liveRule?.Fingerprint ?? "") == expected.CurrentRuleFingerprint &&
            snapshot.Rules.AdmissionFingerprintFor(expected.Family) == expected.RuleAdmissionFingerprint &&
            evidenceCutoffUtc <= authorityNowUtc;
    }

    private static bool SameAuthorization(PrtgResourcePolicyAuthorization? left,
        PrtgResourcePolicyAuthorization? right) => left is null ? right is null : right is not null &&
        left.RequestedMode == right.RequestedMode && left.MaintainAuthorized == right.MaintainAuthorized &&
        left.ServerPersistedTrial == right.ServerPersistedTrial &&
        left.TrialProfileFingerprint == right.TrialProfileFingerprint && left.RulesVersion == right.RulesVersion;

    private static bool SameIdentityFields(PrtgResourcePeriodAssessment expected, PrtgResourceIdentity current) =>
        current.Active && !current.PendingReconciliation && current.SensorId == expected.SensorObjid &&
        current.HostId == expected.HostId && current.DeviceId == expected.DeviceObjid &&
        current.SourceGeneration == expected.SourceGeneration && current.Generation == expected.ResourceGeneration &&
        current.ChannelGeneration == expected.ChannelGeneration &&
        current.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected.ResourceEpoch;

    private static (IReadOnlyList<DateTime> Starts, bool Unresolvable) ExpectedHostDayWindows(
        DateTime fromWall, DateTime toWall, TimeZoneInfo analysisZone, DateTime hostDay)
    {
        var starts = new List<DateTime>();
        var unresolvable = false;
        for (var first = fromWall; first.AddHours(1) < toWall; first = first.AddHours(1))
        {
            var second = first.AddHours(1);
            if (analysisZone.IsInvalidTime(first) || analysisZone.IsInvalidTime(second))
            {
                unresolvable = true;
                continue;
            }
            if (analysisZone.IsAmbiguousTime(first) || analysisZone.IsAmbiguousTime(second))
            {
                unresolvable = true;
                continue;
            }
            DateTime firstUtc;
            DateTime secondUtc;
            try
            {
                firstUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(first, DateTimeKind.Unspecified), analysisZone);
                secondUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(second, DateTimeKind.Unspecified), analysisZone);
            }
            catch (ArgumentException) { unresolvable = true; continue; }
            var laterHostDay = TimeZoneInfo.ConvertTimeFromUtc(secondUtc, TimeZoneInfo.Local).Date;
            if (laterHostDay == hostDay && secondUtc - firstUtc == TimeSpan.FromHours(1))
                starts.Add(DateTime.SpecifyKind(first, DateTimeKind.Unspecified));
            else if (laterHostDay == hostDay && secondUtc - firstUtc != TimeSpan.FromHours(1))
                unresolvable = true;
        }
        return (starts, unresolvable || starts.Count == 0);
    }

    private static bool TryCurrentWallHourStartUtc(DateTime authorityNowUtc, string zoneId, out DateTime cutoffUtc)
    {
        cutoffUtc = default;
        if (authorityNowUtc.Kind != DateTimeKind.Utc) return false;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            var wall = TimeZoneInfo.ConvertTimeFromUtc(authorityNowUtc, zone);
            if (zone.IsInvalidTime(wall) || zone.IsAmbiguousTime(wall)) return false;
            var hour = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0,
                DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(hour) || zone.IsAmbiguousTime(hour)) return false;
            cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(hour, zone);
            return cutoffUtc <= authorityNowUtc;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { return false; }
    }

    private PrtgResourcePeriodConsumption EmptyClosedDay(DateTime evidenceDay, IEnumerable<long> sensorObjids) =>
        new([], [], []) { ClosedDay = new(evidenceDay, new Dictionary<long, int>(),
            sensorObjids.Where(id => id > 0).ToHashSet(), false, Array.Empty<long>(), string.Empty) };

    private bool IsCurrentSnapshot(long sensorId, PrtgResourceIdentity expectedIdentity,
        PrtgTrustedSamplingProfile? expectedProfile, PrtgResourcePressureSensorMetadata expectedMetadata,
        PrtgTrustedSamplingStrategyContext expectedStrategy, SystemSettings expectedSettings,
        PrtgMonitoringPolicy expectedPolicy, DateTime evidenceCutoffUtc, DateTime authorityNowUtc)
    {
        var currentSettings = _settings.Get();
        if (currentSettings.PrtgEnabled != expectedSettings.PrtgEnabled ||
            currentSettings.PrtgUrl != expectedSettings.PrtgUrl ||
            PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy) !=
            PrtgFetchStrategy.Normalize(expectedSettings.PrtgFetchStrategy)) return false;
        var currentPolicy = _policyStore.Get();
        if (currentPolicy.Revision != expectedPolicy.Revision ||
            currentPolicy.SourceGeneration != expectedPolicy.SourceGeneration ||
            currentPolicy.EndpointHint != expectedPolicy.EndpointHint ||
            currentPolicy.SourceTimeZoneId != expectedPolicy.SourceTimeZoneId ||
            currentPolicy.RawTimestampTimeZoneId != expectedPolicy.RawTimestampTimeZoneId ||
            currentPolicy.AnalysisTimeZoneId != expectedPolicy.AnalysisTimeZoneId ||
            !currentPolicy.SensorIds.Order().SequenceEqual(expectedPolicy.SensorIds.Order()) ||
            !currentPolicy.HostIds.Order().SequenceEqual(expectedPolicy.HostIds.Order()) ||
            !currentPolicy.SensorIds.Contains(sensorId) || !currentPolicy.HostIds.Contains(expectedIdentity.HostId))
            return false;
        var currentStrategyProfile = PrtgFetchStrategy.Profile(currentSettings.PrtgFetchStrategy);
        var currentStrategy = _strategyStore.GetCurrent(currentPolicy,
            PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy),
            currentStrategyProfile.SnapshotIntervalMinutes, authorityNowUtc);
        if (currentStrategy.Ready != expectedStrategy.Ready ||
            !currentStrategy.MissingFacts.SequenceEqual(expectedStrategy.MissingFacts) ||
            currentStrategy.StrategyFingerprint != expectedStrategy.StrategyFingerprint ||
            currentStrategy.EffectiveFromHourUtc != expectedStrategy.EffectiveFromHourUtc) return false;

        if (!_store.GetResourceIdentities([sensorId]).TryGetValue(sensorId, out var currentIdentity) ||
            !SameIdentity(expectedIdentity, currentIdentity)) return false;
        var currentMetadata = _store.GetResourcePressureSensorMetadata([sensorId])
            .FirstOrDefault(item => item.SensorObjid == sensorId);
        if (currentMetadata is null || currentMetadata.Paused || currentMetadata.DevicePaused ||
            currentMetadata.DeviceObjid != expectedMetadata.DeviceObjid || currentMetadata.Category != expectedMetadata.Category ||
            currentMetadata.SensorType != expectedMetadata.SensorType) return false;

        var currentProfile = _profiles.GetMany([sensorId]).GetValueOrDefault(sensorId);
        if (expectedProfile is null) return currentProfile is null;
        if (currentProfile is null ||
            currentProfile.MetadataDigest != expectedProfile.MetadataDigest) return false;
        return true;
    }

    private static bool SameIdentity(PrtgResourceIdentity expected, PrtgResourceIdentity current) =>
        current.Active && !current.PendingReconciliation && current.SensorId == expected.SensorId &&
        current.HostId == expected.HostId && current.DeviceId == expected.DeviceId &&
        current.SourceGeneration == expected.SourceGeneration && current.Generation == expected.Generation &&
        current.ChannelGeneration == expected.ChannelGeneration && current.Epoch == expected.Epoch;

    private static PrtgResourceCurrentContext ToCurrentContext(PrtgTrustedSamplingProfile profile,
        PrtgTrustedSamplingStrategyContext strategy) => new(profile.SensorObjid, profile.SourceGeneration,
        profile.ResourceGeneration, profile.ChannelGeneration,
        profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.SemanticVersion,
        profile.StrategyFingerprint, profile.StrategyMinutes, profile.StrategyEffectiveFromHourUtc,
        profile.ConfirmedScanInterval, profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId,
        StableProfileFingerprint(profile));

    internal static string StableProfileFingerprint(PrtgTrustedSamplingProfile profile)
    {
        var contract = JsonSerializer.Serialize(new
        {
            profile.SensorType,
            profile.PrimaryChannelId,
            profile.PrimaryChannelCaption,
            profile.Quantity,
            profile.Unit,
            profile.Scale,
            profile.Direction,
            profile.SemanticVersion,
            profile.StrategyFingerprint,
            profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc,
            profile.ConfirmedScanInterval,
            profile.IntervalRawUnit,
            profile.RawTimestampTimeZoneId,
            profile.SourceApiTimeZoneId,
            profile.AnalysisTimeZoneId
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(contract)));
    }

    private static bool TryMeasurement(PrtgResourceFamily family, PrtgTrustedQuantitySemantic quantity,
        string unit, out PrtgResourceMeasurementDefinition? measurement)
    {
        var semantic = (family, quantity) switch
        {
            (PrtgResourceFamily.Cpu, PrtgTrustedQuantitySemantic.CpuLoadPercent) => PrtgResourceSemantic.CpuUsed,
            (PrtgResourceFamily.Memory, PrtgTrustedQuantitySemantic.MemoryUsedPercent) => PrtgResourceSemantic.MemoryUsed,
            (PrtgResourceFamily.Memory, PrtgTrustedQuantitySemantic.MemoryAvailablePercent) => PrtgResourceSemantic.MemoryRemaining,
            (PrtgResourceFamily.Disk, PrtgTrustedQuantitySemantic.DiskFreePercent) => PrtgResourceSemantic.DiskRemaining,
            // DiskUsedPercent remains unsupported for formal readiness until a disk-used rule exists.
            _ => (PrtgResourceSemantic?)null
        };
        // Trusted snapshots were already normalized by PrtgTrustedSamplingProfileResolver before
        // they entered the physical accumulator. Readiness therefore consumes percent values
        // with a fixed factor of one; applying the profile's raw-source scale here would double-scale.
        if (semantic.HasValue && unit == "%")
        {
            measurement = new(semantic.Value, "%", 1.0, IsVerified: true);
            return true;
        }
        measurement = null;
        return false;
    }

    private static PrtgResourceReadinessInput InvalidInput(PrtgResourceFamily family, DateTime asOfUtc) =>
        new(family, null, null, null, asOfUtc);

    private static PrtgResourcePressureDecision Insufficient(PrtgResourceFamily family,
        PrtgResourceReadinessInput input, string reason) => new(
        PrtgResourceDecisionKind.Insufficient,
        family == PrtgResourceFamily.Disk ? PrtgResourceDecisionMode.FormalRisk : PrtgResourceDecisionMode.Hint,
        family switch
        {
            PrtgResourceFamily.Cpu => "prtg.resource.cpu-sustained-pressure",
            PrtgResourceFamily.Memory => "prtg.resource.memory-sustained-pressure",
            _ => "prtg.resource.disk-low-water"
        },
        PrtgResourcePressureEvaluator.GetProfileFingerprint(family, input.AuthorizedContext),
        reason, family, input.AsOfUtc, Array.Empty<PrtgResourceReadyHour>(), null,
        null, null, null, false);

    private static PrtgResourcePressureHint ToHint(PrtgResourcePeriodAssessment assessment,
        PrtgResourcePressureHint? previous, IReadOnlyList<string> missingFacts)
    {
        var decision = assessment.Decision;
        var firstStart = decision.Window.Count > 0
            ? ConvertWallHourToUtc(decision.Window[0].WallPeriodStart, assessment.Input.AuthorizedContext!.AnalysisTimeZoneId)
            : (DateTime?)null;
        var latestStart = decision.Window.Count > 1
            ? ConvertWallHourToUtc(decision.Window[1].WallPeriodStart, assessment.Input.AuthorizedContext!.AnalysisTimeZoneId)
            : (DateTime?)null;
        var samePriorEpisode = previous?.ProfileFingerprint == decision.ProfileFingerprint &&
            previous.EpisodeStartedAtUtc.HasValue;
        DateTime? episodeStart = decision.Kind == PrtgResourceDecisionKind.Hit
            ? samePriorEpisode ? previous!.EpisodeStartedAtUtc : assessment.EvidenceAsOfUtc
            : (decision.Kind is PrtgResourceDecisionKind.NoHit or PrtgResourceDecisionKind.Insufficient) &&
              samePriorEpisode ? previous!.EpisodeStartedAtUtc : null;
        return new(assessment.HostId, assessment.DeviceObjid, assessment.SensorObjid, assessment.Family,
            decision.Kind, decision.ReasonCode, assessment.AsOfUtc, decision.ProfileFingerprint,
            assessment.SourceGeneration, assessment.ResourceGeneration, assessment.ChannelGeneration,
            assessment.ResourceEpoch, assessment.SemanticVersion, assessment.StrategyVersion,
            decision.EarlierHourAveragePercent, decision.LatestHourAveragePercent,
            decision.WindowCoveragePercent, assessment.EvidenceAsOfUtc, firstStart, latestStart, episodeStart,
            missingFacts.Take(16).ToArray());
    }

    private static PrtgFinding ToFormalFinding(PrtgResourcePeriodAssessment assessment,
        PrtgResourcePressureHint hint)
    {
        var rule = assessment.CurrentRule ?? throw new InvalidOperationException("資源正式 finding 缺少目前啟用規則。");
        var ruleCode = rule.PrtgRuleCode!;
        var description = assessment.Family == PrtgResourceFamily.Disk ? "磁碟可用空間連續兩個完整小時降至低水位" :
            assessment.Family == PrtgResourceFamily.Cpu ? "CPU 使用率連續兩個完整小時達高壓門檻" :
                "記憶體使用率連續兩個完整小時達高壓門檻";
        var threshold = assessment.Family == PrtgResourceFamily.Disk ? 10 : 90;
        var latest = hint.LatestHourAveragePercent;
        var start = hint.EpisodeStartedAtUtc ?? hint.FirstHourStartUtc ?? assessment.EvidenceAsOfUtc;
        var windowText = hint.EarlierHourAveragePercent.HasValue && latest.HasValue
            ? $"最近兩個完整小時平均 {hint.EarlierHourAveragePercent.Value:F1}%／{latest.Value:F1}%，涵蓋 {hint.WindowCoveragePercent:F1}%"
            : "尚未取得足以確認恢復的完整小時證據";
        var detail = $"PRTG {description}；{windowText}；目前資源世代已核對；此 episode 首次判定時間 {start:O}（UTC），非來源故障起始時間。";
        return new(assessment.DeviceObjid, assessment.SensorObjid, ruleCode, detail,
            (int)Math.Round((latest ?? 0) * 10), rule, Acknowledged: false)
        {
            SensorCategory = rule.PrtgSensorCategory,
            SourceGeneration = assessment.SourceGeneration,
            ResourceGeneration = assessment.ResourceGeneration,
            ChannelGeneration = assessment.ChannelGeneration,
            RuleAdmissionFingerprint = assessment.RuleAdmissionFingerprint,
            IncidentStartedAt = start,
            ThresholdMagnitude = threshold,
            EvidenceWindowResolution = "完成小時有效採樣平均；每小時至少 75% 唯一 slot，不代表逐分鐘連續狀態",
            EvidenceWindows = QualifiedPressureWindows(assessment)
        };
    }

    private static IReadOnlyList<PrtgEvidenceWindow> QualifiedPressureWindows(PrtgResourcePeriodAssessment assessment)
    {
        if (assessment.Decision.Kind != PrtgResourceDecisionKind.Hit || assessment.Decision.Window.Count != 2 ||
            assessment.Input.AuthorizedContext is not { } context) return [];
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(context.AnalysisTimeZoneId);
            var result = new List<PrtgEvidenceWindow>(2);
            foreach (var hour in assessment.Decision.Window.OrderBy(hour => hour.WallPeriodStart))
            {
                var start = DateTime.SpecifyKind(hour.WallPeriodStart, DateTimeKind.Unspecified);
                var end = start.AddHours(1);
                if (zone.IsInvalidTime(start) || zone.IsAmbiguousTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(end)) return [];
                var fromUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, zone));
                var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, zone));
                if (endUtc <= fromUtc || endUtc > new DateTimeOffset(assessment.EvidenceAsOfUtc)) return [];
                result.Add(new(fromUtc, endUtc));
            }
            return result;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return []; }
    }

    private static string SummarizeLowWater(PrtgResourcePressureHint hint,
        PrtgResourcePressureDecision decision) =>
        hint.EarlierHourAveragePercent.HasValue && hint.LatestHourAveragePercent.HasValue
            ? $"兩個完整小時可用率 {hint.EarlierHourAveragePercent.Value:F1}%／{hint.LatestHourAveragePercent.Value:F1}%（{decision.ReasonCode}）"
            : $"資源期間尚無足夠可信證據（{decision.ReasonCode}）";

    private static PrtgResourceFormalEpisode? ToHistoricalDiskEpisode(PrtgResourcePeriodAssessment assessment,
        IEnumerable<PrtgResourceFormalReasonObservation> observations)
    {
        var reasons = observations.Where(o => o.State == PrtgResourceFormalReasonState.Active &&
                o.EvidenceDay.HasValue && o.EvidenceAsOfUtc.HasValue)
            .OrderBy(o => o.ReasonCode, StringComparer.Ordinal)
            .Select(o => new PrtgResourceFormalReason(o.ReasonCode, o.EvidenceFingerprint, o.Summary,
                o.EvidenceAsOfUtc!.Value, o.EvidenceDay!.Value, o.SourceRuleId, o.SourceRuleFingerprint))
            .ToArray();
        if (reasons.Length == 0) return null;
        var start = reasons.Min(r => r.FirstObservedAtUtc);
        return new PrtgResourceFormalEpisode(assessment.HostId, assessment.DeviceObjid,
            assessment.SensorObjid, assessment.SourceGeneration, assessment.ResourceGeneration,
            assessment.ChannelGeneration, assessment.ResourceEpoch, start, assessment.AuthorityAsOfUtc,
            reasons, assessment.EvidenceFingerprint) { EvidenceAsOfUtc = assessment.EvidenceAsOfUtc };
    }

    private static IReadOnlyList<PrtgResourceQualifiedFinding> ToDiskEpisodeFindings(PrtgResourcePeriodAssessment assessment,
        PrtgResourceFormalEpisode episode, PrtgResourceCurrentRuleCatalog currentRules)
    {
        var rule = assessment.CurrentRule ?? throw new InvalidOperationException("磁碟 episode 缺少目前啟用規則。");
        var visibleReasons = episode.Reasons.Where(reason => reason.ReasonCode == "disk-two-hour-low-water" ||
            reason.ReasonCode == "disk-seven-day-low-water-trend" && currentRules.DiskTrendEnabled &&
            reason.SourceRuleId == currentRules.DiskTrendRuleId &&
            reason.SourceRuleFingerprint == currentRules.DiskTrendRuleFingerprint).ToArray();
        if (visibleReasons.Length == 0) return Array.Empty<PrtgResourceQualifiedFinding>();
        var evidenceDay = visibleReasons.Max(reason => reason.EvidenceDay);
        if (evidenceDay == default) return Array.Empty<PrtgResourceQualifiedFinding>();
        var hasLowWater = visibleReasons.Any(r => r.ReasonCode == "disk-two-hour-low-water");
        var hasTrend = visibleReasons.Any(r => r.ReasonCode == "disk-seven-day-low-water-trend");
        var description = hasLowWater && hasTrend ? "磁碟低水位與容量趨勢" : hasTrend ? "磁碟容量趨勢" : "磁碟可用空間低水位";
        var reasonText = string.Join("；", visibleReasons.OrderBy(r => r.ReasonCode, StringComparer.Ordinal)
            .Select(r => $"{r.Summary}（首次於 {r.FirstObservedAtUtc:O} UTC 核實）"));
        var detail = $"PRTG {description}；{reasonText}；episode 首次正式判定時間 {episode.EpisodeObservedSinceUtc:O} UTC，" +
            $"不是來源報告的物理故障起始時間；資源世代 {episode.ResourceGeneration}，理由集證據摘要 {episode.ReasonSetFingerprint}。";
        var finding = new PrtgFinding(assessment.DeviceObjid, assessment.SensorObjid, rule.PrtgRuleCode!, detail,
            visibleReasons.Length, rule, Acknowledged: false)
        {
            SensorCategory = PrtgSensorCategories.Disk,
            SourceGeneration = episode.SourceGeneration,
            ResourceGeneration = episode.ResourceGeneration,
            IncidentStartedAt = episode.EpisodeObservedSinceUtc,
            ThresholdMagnitude = visibleReasons.Length,
            EventIdentityRuleCode = PrtgRuleEvaluator.RuleDiskFreeTrend,
            DisplayLabel = $"PRTG {description}（感測器 #{assessment.SensorObjid}）",
            ResourceReasonCodes = visibleReasons.Select(reason => reason.ReasonCode).ToArray(),
            RuleAdmissionFingerprint = assessment.RuleAdmissionFingerprint,
            TrendSourceRuleId = visibleReasons.FirstOrDefault(reason =>
                reason.ReasonCode == "disk-seven-day-low-water-trend")?.SourceRuleId,
            TrendSourceRuleFingerprint = visibleReasons.FirstOrDefault(reason =>
                reason.ReasonCode == "disk-seven-day-low-water-trend")?.SourceRuleFingerprint,
            ChannelGeneration = episode.ChannelGeneration,
            EvidenceWindowResolution = "完成小時有效採樣平均；每小時至少 75% 唯一 slot，不代表逐分鐘連續狀態",
            EvidenceWindows = hasLowWater && visibleReasons.Any(reason => reason.ReasonCode == "disk-two-hour-low-water" &&
                reason.EvidenceDay == assessment.SingleWindowHostDay) ? QualifiedPressureWindows(assessment) : []
        };
        var reasonsForDay = visibleReasons.Where(reason => reason.EvidenceDay == evidenceDay)
            .Select(reason => reason.ReasonCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var trendQualifiesDay = visibleReasons.Any(reason => reason.EvidenceDay == evidenceDay &&
            reason.ReasonCode == "disk-seven-day-low-water-trend");
        return [new(finding, evidenceDay, reasonsForDay, RequiresSingleWindow: !trendQualifiesDay)];
    }

    private static PrtgResourceFamily? FamilyForCategory(string? category) => category?.ToLowerInvariant() switch
    {
        PrtgSensorCategories.Cpu => PrtgResourceFamily.Cpu,
        PrtgSensorCategories.Memory => PrtgResourceFamily.Memory,
        PrtgSensorCategories.Disk => PrtgResourceFamily.Disk,
        _ => null
    };

    private static IReadOnlyList<PrtgResourceHourlyEvidence> ToHourlyEvidence(long sensorId,
        IEnumerable<PrtgValueRow> rows)
    {
        var result = new List<PrtgResourceHourlyEvidence>();
        foreach (var row in rows)
        {
            if (row.TrustVersion != 1 || !string.Equals(row.Quality, PrtgDataQuality.Sampled,
                    StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(row.TrustedProof))
            {
                result.Add(new(row.SensorObjid, row.PeriodStart, null, null,
                    row.AvgValue, row.Coverage));
                continue;
            }
            try
            {
                var proof = PrtgTrustedSampleProof.Deserialize(row.TrustedProof);
                result.Add(new(row.SensorObjid, row.PeriodStart, proof, proof.Slots.Select(s => s.Slot).ToArray(),
                    row.AvgValue, row.Coverage));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                result.Add(new(row.SensorObjid, row.PeriodStart, null, null, row.AvgValue, row.Coverage));
            }
        }
        return result;
    }

    private static bool TryCompletedWallHours(DateTime asOfUtc, string analysisZoneId,
        out DateTime from, out DateTime to)
    {
        from = default;
        to = default;
        if (asOfUtc.Kind != DateTimeKind.Utc) return false;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(analysisZoneId);
            var wall = TimeZoneInfo.ConvertTimeFromUtc(asOfUtc, zone);
            if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall)) return false;
            var currentHour = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified);
            var latest = currentHour.AddHours(-1);
            from = latest.AddHours(-1);
            to = latest.AddHours(1);
            if (zone.IsAmbiguousTime(from) || zone.IsInvalidTime(from) ||
                zone.IsAmbiguousTime(latest) || zone.IsInvalidTime(latest)) return false;
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static DateTime ConvertWallHourToUtc(DateTime wallHour, string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        if (zone.IsAmbiguousTime(wallHour) || zone.IsInvalidTime(wallHour))
            throw new InvalidDataException("Ambiguous resource period wall hour.");
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified), zone), DateTimeKind.Utc);
    }
}
