using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core;

namespace LogForesight.Web.Services;

public sealed record PrtgTrustedSamplingChannelProbeRow(
    [property: JsonConverter(typeof(NullableInt64StringJsonConverter))] long? ChannelObjectId, string? Caption, string? Unit,
    double? RawValue, bool EqualsSensorSnapshotValue, bool? SourceMarkedPrimary,
    string? QuantitySemantic, double? Scale, string? Direction, double? LastCheckRaw,
    string? IntervalRawUnit, string? SemanticVersion);

/// <summary>Keep native Int64 channel IDs lossless when probe rows cross the JavaScript JSON boundary.</summary>
public sealed class NullableInt64StringJsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var fromString) && fromString >= 0)
            return fromString;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var fromNumber) && fromNumber >= 0)
            return fromNumber;
        throw new JsonException("Channel object ID must be a nonnegative Int64 decimal value.");
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (!value.HasValue) writer.WriteNullValue();
        else if (value.Value >= 0) writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
        else throw new JsonException("Channel object ID must be nonnegative.");
    }
}

public sealed record PrtgTrustedSamplingProbeRow(
    long SensorObjid, string Status, IReadOnlyList<string> ReturnedSensorFields,
    long? ParentObjid, string? SensorType, int? StatusRaw, double? LastValueRaw,
    double? LastCheckRaw, double? IntervalRaw, IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> Channels,
    bool ChannelsTruncated, IReadOnlyList<string> MissingAuthorityFields,
    string ResourceGeneration, long IdentityEpoch, string ChannelGeneration,
    DateTimeOffset ProbedAtUtc, bool ProfileRecorded,
    string? NativePrimaryChannelPropertyId = null)
{
    /// <summary>True only when this probe call wrote a new qualification proof.</summary>
    public bool QualificationRecorded { get; init; }
}

/// <summary>
/// Bounded profile refresh. Recurring refresh uses grouped before/after sensor brackets and two
/// per-sensor Table-budget calls, with the primarychannel property inside each bracket. Historic XML is used
/// only for an explicit operator qualification when the saved binding has no raw-channel proof.
/// </summary>
public sealed class PrtgTrustedSamplingProbeService
{
    private readonly StorageBackend backend;
    private readonly Func<SystemSettings, PrtgClient> clientFactory;
    public PrtgTrustedSamplingProbeService(StorageBackend backend)
        : this(backend, settings => PrtgClientFactory.Create(settings)) { }
    internal PrtgTrustedSamplingProbeService(StorageBackend backend, Func<SystemSettings, PrtgClient> clientFactory)
    { this.backend = backend; this.clientFactory = clientFactory; }
    public const int MaxSensorIds = 5;
    public const int MaxChannelsPerSensor = 100;
    private const int MaxResponseBytes = 512 * 1024;

    public async Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>> ProbeAsync(
        IReadOnlyCollection<long> requestedIds, CancellationToken ct, Action? onRequestStarted = null,
        Func<PrtgTrustedSamplingProfile, bool>? profilePublisher = null,
        string? leaseOwner = null, long? leaseVersion = null,
        bool allowUnrelatedPolicyRevisionChanges = false,
        PrtgRequestPurpose requestPurpose = PrtgRequestPurpose.General,
        string? admissionPlanFingerprint = null,
        Action? onRequestAttempted = null,
        Action<TimeSpan>? onResponseRead = null,
        bool allowQualification = false,
        long? expectedBindingRevision = null,
        string? expectedBindingFingerprint = null,
        bool qualificationOnly = false,
        PrtgQualificationWriteFence? qualificationFence = null,
        bool qualificationPilotOnly = false,
        Action<TimeSpan>? onSensorElapsed = null,
        Action<string>? onHistoricSourceVersion = null,
        Action<TimeSpan>? onTableBudgetAdmissionWait = null)
    {
        if (requestedIds.Count is < 1 or > MaxSensorIds || requestedIds.Any(id => id <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentOutOfRangeException(nameof(requestedIds), "每次最多探測 5 顆且 objid 必須唯一。" );
        if (qualificationOnly && (requestedIds.Count != 1 || qualificationFence is null ||
            qualificationFence.SensorObjid != requestedIds.Single()))
            throw new InvalidOperationException("qualification-only-requires-single-sensor-write-fence");
        if (qualificationOnly && qualificationPilotOnly)
            throw new InvalidOperationException("qualification-modes-are-exclusive");
        if ((qualificationPilotOnly || allowQualification && !qualificationOnly) && requestedIds.Count != 1)
            throw new InvalidOperationException("raw-historic-qualification-requires-one-sensor-per-admission");

        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var policy = policyStore.Get();
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || requestedIds.Any(id => !policy.SensorIds.Contains(id)))
            throw new InvalidOperationException("source-policy-not-ready-or-outside-pilot");

        var ids = requestedIds.Order().ToArray();
        var store = backend.PrtgStore();
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        var bindings = bindingStore.GetMany(ids);
        var identities = store.GetResourceIdentities(ids);
        foreach (var id in ids)
        {
            if (!identities.TryGetValue(id, out var identity) || !identity.Active || identity.PendingReconciliation ||
                identity.SourceGeneration != policy.SourceGeneration || identity.Epoch <= 0 ||
                !policy.HostIds.Contains(identity.HostId))
                throw new InvalidOperationException($"resource-identity-not-current:{id}");
        }

        var output = new List<PrtgTrustedSamplingProbeRow>(ids.Length);
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy, strategyName, strategyMinutes, DateTime.UtcNow);
        var authorityContextFingerprint = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            policy, strategyName, strategyMinutes);
        using var client = clientFactory(settings);
        client.RequestPurpose = qualificationOnly ? PrtgRequestPurpose.ProfileRefresh : requestPurpose;
        client.AdmissionPlanFingerprint = admissionPlanFingerprint;
        client.TableRequestSent = onRequestStarted;
        client.TableBudgetAdmissionWaitObserved = onTableBudgetAdmissionWait;
        using var historicReservation = qualificationOnly
            ? await client.Budget!.ReserveQualificationHistoricAsync(qualificationFence!.JobId,
                qualificationFence.Owner, qualificationFence.LeaseVersion, ct)
            : qualificationPilotOnly || allowQualification && ids.Length == 1 &&
                bindings.TryGetValue(ids[0], out var existingBinding) &&
                string.IsNullOrWhiteSpace(existingBinding.QualificationProofReference)
                ? await client.Budget!.ReserveOtherHistoricAsync(ct) : null;
        using var historicReservationScope = historicReservation is null
            ? null : client.Budget!.UseHistoricReservation(historicReservation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(PrtgQualificationCapacityEvaluator.PerSensorProbeDeadlineSeconds));
        if (ids.Length > 1 && !qualificationOnly && !qualificationPilotOnly && !allowQualification)
            return await ProbeGroupedRefreshAsync(ids, settings, policy, policyStore, bindings, identities, strategy,
                strategyMinutes, authorityContextFingerprint, client, deadline.Token, profilePublisher, leaseOwner,
                leaseVersion, allowUnrelatedPolicyRevisionChanges, expectedBindingRevision, expectedBindingFingerprint,
                onRequestAttempted, onResponseRead, onSensorElapsed, ct);
        foreach (var id in ids)
        {
            var sensorStarted = Stopwatch.StartNew();
            deadline.Token.ThrowIfCancellationRequested();
            var before = identities[id];
            bindings.TryGetValue(id, out var binding);
            if (binding is not null && (expectedBindingRevision.HasValue && binding.BindingRevision != expectedBindingRevision ||
                expectedBindingFingerprint is not null && binding.BindingFingerprint != expectedBindingFingerprint))
                throw new InvalidOperationException($"binding-revision-changed:{id}");
            onRequestAttempted?.Invoke();
            var sensorJson = await client.GetBoundedJsonAsync(
                $"api/table.json?content=sensors&id={id}&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince&count=2",
                MaxResponseBytes, deadline.Token, onResponseRead: onResponseRead);
            var sensor = ParseSingleSensor(sensorJson, id);
            onRequestAttempted?.Invoke();
            var channelsJson = await client.GetBoundedJsonAsync(
                $"api/table.json?content=channels&id={id}&columns=objid,name,lastvalue_raw,unit&usecaption=1&count={MaxChannelsPerSensor}",
                MaxResponseBytes, deadline.Token, onResponseRead: onResponseRead);
            var channels = ParseChannels(channelsJson, sensor.LastValueRaw, out var truncated);
            onRequestAttempted?.Invoke();
            var primaryPropertyResponse = await client.GetBoundedXmlAsync(
                $"api/getobjectproperty.htm?id={id}&name=primarychannel", MaxResponseBytes, deadline.Token);
            string? nativePrimaryId;
            try { nativePrimaryId = ParseNativePrimaryProperty(primaryPropertyResponse.Content); }
            catch (InvalidDataException) { nativePrimaryId = null; }
            onRequestAttempted?.Invoke();
            var sensorAfterJson = await client.GetBoundedJsonAsync(
                $"api/table.json?content=sensors&id={id}&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince&count=2",
                MaxResponseBytes, deadline.Token, onResponseRead: onResponseRead);
            var sensorAfter = ParseSingleSensor(sensorAfterJson, id);
            var after = backend.PrtgStore().GetResourceIdentity(id);
            var latestPolicy = policyStore.Get();
            var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (!SameIdentity(before, after) ||
                !allowUnrelatedPolicyRevisionChanges && latestPolicy.Revision != policy.Revision ||
                latestPolicy.SourceGeneration != policy.SourceGeneration || !latestPolicy.SensorIds.Contains(id) ||
                !latestPolicy.HostIds.Contains(after.HostId) ||
                !latestPolicy.Ready(latestSettings.PrtgUrl) || settings.PrtgUrl != latestSettings.PrtgUrl ||
                settings.Revision != latestSettings.Revision ||
                settings.PrtgFetchStrategy != latestSettings.PrtgFetchStrategy || !latestSettings.PrtgEnabled ||
                policy.RawTimestampTimeZoneId != latestPolicy.RawTimestampTimeZoneId ||
                policy.SourceTimeZoneId != latestPolicy.SourceTimeZoneId ||
                policy.AnalysisTimeZoneId != latestPolicy.AnalysisTimeZoneId ||
                policy.TimeBasisEvidenceReference != latestPolicy.TimeBasisEvidenceReference ||
                authorityContextFingerprint != PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                    latestPolicy, PrtgFetchStrategy.Normalize(latestSettings.PrtgFetchStrategy),
                    PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(latestSettings.PrtgFetchStrategy)).SnapshotIntervalMinutes))
                throw new InvalidOperationException($"probe-fence-changed:{id}");

            var probedAtUtc = DateTimeOffset.UtcNow;
            var missing = MissingAuthority(sensor, sensorAfter, channels, truncated, policy, binding, nativePrimaryId);
            if (!strategy.Ready) missing.AddRange(strategy.MissingFacts.Select(f => $"strategy:{f}"));
            var profileRecorded = false;
            var qualificationRecorded = false;
            if (sensor.ParentObjid != after.DeviceId || string.IsNullOrWhiteSpace(sensor.Cumsince) ||
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(sensor.ParentObjid?.ToString(CultureInfo.InvariantCulture) ?? "",
                    sensor.SensorType ?? "", sensor.Cumsince ?? "", 0) != after.ResourceFingerprint)
                missing.Add("source:observed_resource_identity_mismatch");
            if (binding is null) missing.Add("management:binding_missing");
            else if (!binding.Matches(id, authorityContextFingerprint, policy.SourceGeneration,
                         before.Generation, before.Epoch, before.ChannelGeneration) ||
                     binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference ||
                     binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
                     binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId)
                missing.Add("management:binding_fence_or_time_basis_changed");
            if (missing.Count == 0 && binding is not null &&
                (qualificationPilotOnly || string.IsNullOrWhiteSpace(binding.QualificationProofReference) && (allowQualification || qualificationOnly)))
            {
                try
                {
                    onRequestAttempted?.Invoke();
                    var historic = await client.GetBoundedXmlAsync(
                        $"api/historicdata.xml?id={id}&sdate=now-1h&edate=now&avg=0&usecaption=1",
                        PrtgHistoricXmlReader.MaximumBytes, deadline.Token);
                    var proof = QualifyHistoric(binding, historic.Content, sensor, probedAtUtc);
                    onHistoricSourceVersion?.Invoke(proof.SourceVersion);
                    if (!qualificationPilotOnly)
                    {
                        binding = bindingStore.RecordQualification(id, binding.BindingRevision, binding.BindingFingerprint,
                            latestSettings.Revision, latestPolicy.Revision,
                            proof.RawValue, proof.MeasuredOaDate, proof.SourceVersion, probedAtUtc, proof.ProofReference,
                            qualificationOnly ? qualificationFence : null);
                        qualificationRecorded = true;
                        after = backend.PrtgStore().GetResourceIdentity(id);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
                { missing.Add("qualification:bounded_raw_historic_proof_unavailable"); }
            }
            if (!qualificationPilotOnly && (binding is null || string.IsNullOrWhiteSpace(binding.QualificationProofReference)))
                missing.Add("qualification:raw_channel_id_time_proof_required");
            if (missing.Count == 0 && binding is not null && !qualificationOnly && !qualificationPilotOnly)
            {
                try
                {
                    var primary = channels.Single(channel => channel.ChannelObjectId?.ToString(CultureInfo.InvariantCulture) == binding.ChannelObjectId);
                    var interval = binding.IntervalRawUnit == "seconds"
                        ? TimeSpan.FromSeconds(sensor.IntervalRaw!.Value) : TimeSpan.FromMinutes(sensor.IntervalRaw!.Value);
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(binding.RawTimestampTimeZoneId);
                    var wall = DateTime.SpecifyKind(DateTime.FromOADate(sensor.LastCheckRaw!.Value), DateTimeKind.Unspecified);
                    if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall))
                        throw new InvalidDataException("source_measurement_time_ambiguous");
                    var measuredAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone));
                    var maximumAge = TimeSpan.FromMinutes(strategyMinutes) + interval + interval;
                    if (measuredAtUtc > probedAtUtc || probedAtUtc - measuredAtUtc > maximumAge)
                        throw new InvalidDataException("source_measurement_not_recent");
                    var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        sensorJson + "\n" + channelsJson + "\n" + primaryPropertyResponse.Content + "\n" + sensorAfterJson)));
                    var physicalReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        string.Join("|", id, primary.ChannelObjectId, sensor.LastCheckRaw.Value.ToString("R", CultureInfo.InvariantCulture),
                            sensor.LastValueRaw!.Value.ToString("R", CultureInfo.InvariantCulture)))));
                    if (interval <= TimeSpan.Zero || interval > TimeSpan.FromHours(1))
                        throw new InvalidDataException("source_interval_out_of_range");
                    var normalized = binding.Direction switch
                    { "inverse" => -sensor.LastValueRaw!.Value * binding.Scale,
                      "absolute" => Math.Abs(sensor.LastValueRaw!.Value) * binding.Scale,
                      _ => sensor.LastValueRaw!.Value * binding.Scale };
                    if (!double.IsFinite(normalized) || normalized is < 0 or > 100)
                        throw new InvalidDataException("source_quantity_out_of_range");
                    var profile = PrtgTrustedSamplingProfile.FromProbe(id, after, sensor.SensorType!,
                        binding.ChannelObjectId, binding.ExpectedCaption, binding.Quantity, binding.Unit,
                        binding.Scale, binding.Direction, "operator-explicit-v1", strategy.StrategyFingerprint,
                        strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, interval, binding.IntervalRawUnit,
                        binding.RawTimestampTimeZoneId, policy.SourceTimeZoneId, binding.AnalysisTimeZoneId, probedAtUtc,
                        reference, physicalReference, false, sensor.LastValueRaw!.Value, primary.RawValue!.Value,
                        sensor.LastCheckRaw.Value, sensor.LastCheckRaw.Value,
                        PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, binding.BindingRevision,
                        binding.BindingFingerprint, nativePrimaryId!, binding.QualificationProofReference,
                        latestSettings.Revision, latestPolicy.Revision, authorityContextFingerprint);
                    profileRecorded = profilePublisher is null
                        ? RecordNormally(profile)
                        : profilePublisher(profile);
                    if (!profileRecorded) throw new OperationCanceledException("profile-refresh-lease-lost-before-publish");
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
                { missing.Add("source:profile_validation_failed"); }
            }
            sensorStarted.Stop();
            onSensorElapsed?.Invoke(sensorStarted.Elapsed);
            output.Add(new(id, profileRecorded ? "ready" : qualificationOnly && missing.Count == 0 ? "qualified" :
                qualificationPilotOnly && missing.Count == 0 ? "pilot-qualified" : "waiting", sensor.ReturnedFields,
                sensor.ParentObjid, sensor.SensorType, sensor.StatusRaw, sensor.LastValueRaw,
                sensor.LastCheckRaw, sensor.IntervalRaw, channels, truncated, missing,
                after.Generation, after.Epoch, after.ChannelGeneration, probedAtUtc, profileRecorded, nativePrimaryId)
                { QualificationRecorded = qualificationRecorded });
        }
        return output;

        bool RecordNormally(PrtgTrustedSamplingProfile profile)
        {
            new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);
            return true;
        }
    }

    private sealed record GroupedSensorStage(long Id, PrtgResourceIdentity BeforeIdentity,
        PrtgTrustedSamplingBinding? Binding, SensorRow Before, IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> Channels,
        bool ChannelsTruncated, string ChannelsJson, string PrimaryPropertyXml, string? NativePrimaryId,
        Stopwatch SensorTimer);

    private async Task<IReadOnlyList<PrtgTrustedSamplingProbeRow>> ProbeGroupedRefreshAsync(
        long[] ids, SystemSettings settings, PrtgMonitoringPolicy policy, PrtgMonitoringPolicyStore policyStore,
        IReadOnlyDictionary<long, PrtgTrustedSamplingBinding> bindings,
        IReadOnlyDictionary<long, PrtgResourceIdentity> identities,
        PrtgTrustedSamplingStrategyContext strategy, int strategyMinutes, string authorityContextFingerprint,
        PrtgClient client, CancellationToken ct, Func<PrtgTrustedSamplingProfile, bool>? profilePublisher,
        string? leaseOwner, long? leaseVersion, bool allowUnrelatedPolicyRevisionChanges,
        long? expectedBindingRevision, string? expectedBindingFingerprint,
        Action? onRequestAttempted, Action<TimeSpan>? onResponseRead, Action<TimeSpan>? onSensorElapsed,
        CancellationToken callerToken)
    {
        var filter = PrtgResourceGuardProbe.BuildObjidFilter(ids);
        var beforeUrl = $"api/table.json?content=sensors&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince&count={ids.Length + 1}{filter}";
        var beforeTimer = Stopwatch.StartNew();
        onRequestAttempted?.Invoke();
        var beforeJson = await client.GetBoundedJsonAsync(beforeUrl, MaxResponseBytes, ct,
            onResponseRead: onResponseRead);
        var beforeRows = ParseExactSensorSet(beforeJson, ids);
        beforeTimer.Stop();

        var stages = new List<GroupedSensorStage>(ids.Length);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var sensorTimer = Stopwatch.StartNew();
            var beforeIdentity = identities[id];
            bindings.TryGetValue(id, out var binding);
            if (binding is not null && (expectedBindingRevision.HasValue && binding.BindingRevision != expectedBindingRevision ||
                expectedBindingFingerprint is not null && binding.BindingFingerprint != expectedBindingFingerprint))
                throw new InvalidOperationException($"binding-revision-changed:{id}");

            var sensor = beforeRows[id];
            onRequestAttempted?.Invoke();
            var channelsJson = await client.GetBoundedJsonAsync(
                $"api/table.json?content=channels&id={id}&columns=objid,name,lastvalue_raw,unit&usecaption=1&count={MaxChannelsPerSensor}",
                MaxResponseBytes, ct, onResponseRead: onResponseRead);
            var channels = ParseChannels(channelsJson, sensor.LastValueRaw, out var truncated);
            onRequestAttempted?.Invoke();
            var primaryResponse = await client.GetBoundedXmlAsync(
                $"api/getobjectproperty.htm?id={id}&name=primarychannel", MaxResponseBytes, ct);
            string? nativePrimaryId;
            try { nativePrimaryId = ParseNativePrimaryProperty(primaryResponse.Content); }
            catch (InvalidDataException) { nativePrimaryId = null; }
            sensorTimer.Stop();
            stages.Add(new(id, beforeIdentity, binding, sensor, channels, truncated, channelsJson,
                primaryResponse.Content, nativePrimaryId, sensorTimer));
        }

        var afterUrl = $"api/table.json?content=sensors&columns=objid,parentid,type,status,lastvalue,lastcheck,interval,cumsince&count={ids.Length + 1}{filter}";
        var afterTimer = Stopwatch.StartNew();
        onRequestAttempted?.Invoke();
        var afterJson = await client.GetBoundedJsonAsync(afterUrl, MaxResponseBytes, ct,
            onResponseRead: onResponseRead);
        var afterRows = ParseExactSensorSet(afterJson, ids);
        afterTimer.Stop();

        var latestPolicy = policyStore.Get();
        var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var currentStrategy = PrtgFetchStrategy.Normalize(latestSettings.PrtgFetchStrategy);
        if (!allowUnrelatedPolicyRevisionChanges && latestPolicy.Revision != policy.Revision ||
            latestPolicy.SourceGeneration != policy.SourceGeneration ||
            !latestPolicy.Ready(latestSettings.PrtgUrl) || settings.PrtgUrl != latestSettings.PrtgUrl ||
            settings.Revision != latestSettings.Revision || settings.PrtgFetchStrategy != latestSettings.PrtgFetchStrategy ||
            !latestSettings.PrtgEnabled || policy.RawTimestampTimeZoneId != latestPolicy.RawTimestampTimeZoneId ||
            policy.SourceTimeZoneId != latestPolicy.SourceTimeZoneId || policy.AnalysisTimeZoneId != latestPolicy.AnalysisTimeZoneId ||
            policy.TimeBasisEvidenceReference != latestPolicy.TimeBasisEvidenceReference ||
            authorityContextFingerprint != PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                latestPolicy, currentStrategy, PrtgFetchStrategy.Profile(currentStrategy).SnapshotIntervalMinutes))
            throw new InvalidOperationException("probe-fence-changed:group-context");

        var afterIdentities = backend.PrtgStore().GetResourceIdentities(ids);
        var latestBindings = new PrtgTrustedSamplingBindingStore(backend).GetMany(ids);
        var probedAtUtc = DateTimeOffset.UtcNow;
        var prepared = new List<(GroupedSensorStage Stage, PrtgResourceIdentity AfterIdentity,
            List<string> Missing, PrtgTrustedSamplingProbeRow Row, PrtgTrustedSamplingProfile? Profile)>(ids.Length);
        foreach (var stage in stages)
        {
            var id = stage.Id;
            if (!afterIdentities.TryGetValue(id, out var afterIdentity) || !SameIdentity(stage.BeforeIdentity, afterIdentity) ||
                !latestPolicy.SensorIds.Contains(id) || !latestPolicy.HostIds.Contains(afterIdentity.HostId))
                throw new InvalidOperationException($"probe-fence-changed:{id}");
            var hasCurrentBinding = latestBindings.TryGetValue(id, out var currentBinding);
            if ((stage.Binding is null) != !hasCurrentBinding ||
                stage.Binding is not null && hasCurrentBinding && currentBinding is not null &&
                (stage.Binding.BindingRevision != currentBinding.BindingRevision ||
                 stage.Binding.BindingFingerprint != currentBinding.BindingFingerprint))
                throw new InvalidOperationException($"binding-revision-changed:{id}");

            var sensorAfter = afterRows[id];
            var missing = MissingAuthority(stage.Before, sensorAfter, stage.Channels, stage.ChannelsTruncated,
                policy, stage.Binding, stage.NativePrimaryId);
            if (!strategy.Ready) missing.AddRange(strategy.MissingFacts.Select(f => $"strategy:{f}"));
            if (stage.Before.ParentObjid != afterIdentity.DeviceId || string.IsNullOrWhiteSpace(stage.Before.Cumsince) ||
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(stage.Before.ParentObjid?.ToString(CultureInfo.InvariantCulture) ?? "",
                    stage.Before.SensorType ?? "", stage.Before.Cumsince ?? "", 0) != afterIdentity.ResourceFingerprint)
                missing.Add("source:observed_resource_identity_mismatch");
            var binding = stage.Binding;
            if (binding is null) missing.Add("management:binding_missing");
            else if (!binding.Matches(id, authorityContextFingerprint, policy.SourceGeneration,
                         stage.BeforeIdentity.Generation, stage.BeforeIdentity.Epoch, stage.BeforeIdentity.ChannelGeneration) ||
                     binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference ||
                     binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
                     binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId)
                missing.Add("management:binding_fence_or_time_basis_changed");
            if (binding is null || string.IsNullOrWhiteSpace(binding.QualificationProofReference))
                missing.Add("qualification:raw_channel_id_time_proof_required");

            PrtgTrustedSamplingProfile? profile = null;
            if (missing.Count == 0 && binding is not null)
            {
                try
                {
                    var primary = stage.Channels.Single(channel => channel.ChannelObjectId?.ToString(CultureInfo.InvariantCulture) == binding.ChannelObjectId);
                    var interval = binding.IntervalRawUnit == "seconds"
                        ? TimeSpan.FromSeconds(stage.Before.IntervalRaw!.Value) : TimeSpan.FromMinutes(stage.Before.IntervalRaw!.Value);
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(binding.RawTimestampTimeZoneId);
                    var wall = DateTime.SpecifyKind(DateTime.FromOADate(stage.Before.LastCheckRaw!.Value), DateTimeKind.Unspecified);
                    if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall))
                        throw new InvalidDataException("source_measurement_time_ambiguous");
                    var measuredAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone));
                    var maximumAge = TimeSpan.FromMinutes(strategyMinutes) + interval + interval;
                    if (measuredAtUtc > probedAtUtc || probedAtUtc - measuredAtUtc > maximumAge)
                        throw new InvalidDataException("source_measurement_not_recent");
                    var primaryXml = stage.PrimaryPropertyXml;
                    var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        beforeJson + "\n" + stage.ChannelsJson + "\n" + primaryXml + "\n" + afterJson)));
                    var physicalReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        string.Join("|", id, primary.ChannelObjectId, stage.Before.LastCheckRaw.Value.ToString("R", CultureInfo.InvariantCulture),
                            stage.Before.LastValueRaw!.Value.ToString("R", CultureInfo.InvariantCulture)))));
                    if (interval <= TimeSpan.Zero || interval > TimeSpan.FromHours(1))
                        throw new InvalidDataException("source_interval_out_of_range");
                    var normalized = binding.Direction switch
                    { "inverse" => -stage.Before.LastValueRaw!.Value * binding.Scale,
                      "absolute" => Math.Abs(stage.Before.LastValueRaw!.Value) * binding.Scale,
                      _ => stage.Before.LastValueRaw!.Value * binding.Scale };
                    if (!double.IsFinite(normalized) || normalized is < 0 or > 100)
                        throw new InvalidDataException("source_quantity_out_of_range");
                    profile = PrtgTrustedSamplingProfile.FromProbe(id, afterIdentity, stage.Before.SensorType!,
                        binding.ChannelObjectId, binding.ExpectedCaption, binding.Quantity, binding.Unit,
                        binding.Scale, binding.Direction, "operator-explicit-v1", strategy.StrategyFingerprint,
                        strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, interval, binding.IntervalRawUnit,
                        binding.RawTimestampTimeZoneId, policy.SourceTimeZoneId, binding.AnalysisTimeZoneId, probedAtUtc,
                        reference, physicalReference, false, stage.Before.LastValueRaw!.Value, primary.RawValue!.Value,
                        stage.Before.LastCheckRaw.Value, stage.Before.LastCheckRaw.Value,
                        PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, binding.BindingRevision,
                        binding.BindingFingerprint, stage.NativePrimaryId!, binding.QualificationProofReference,
                        latestSettings.Revision, latestPolicy.Revision, authorityContextFingerprint);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
                { missing.Add("source:profile_validation_failed"); }
            }
            var row = new PrtgTrustedSamplingProbeRow(id, profile is null ? "waiting" : "prepared",
                stage.Before.ReturnedFields, stage.Before.ParentObjid, stage.Before.SensorType, stage.Before.StatusRaw,
                stage.Before.LastValueRaw, stage.Before.LastCheckRaw, stage.Before.IntervalRaw, stage.Channels,
                stage.ChannelsTruncated, missing, afterIdentity.Generation, afterIdentity.Epoch,
                afterIdentity.ChannelGeneration, probedAtUtc, false, stage.NativePrimaryId);
            prepared.Add((stage, afterIdentity, missing, row, profile));
        }

        // All remote snapshots and every current identity/context/binding fence are checked before
        // the first profile row is published. Persistence remains a separate CAS per sensor.
        callerToken.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        var output = new List<PrtgTrustedSamplingProbeRow>(ids.Length);
        foreach (var item in prepared)
        {
            callerToken.ThrowIfCancellationRequested();
            ct.ThrowIfCancellationRequested();
            item.Stage.SensorTimer.Stop();
            onSensorElapsed?.Invoke(item.Stage.SensorTimer.Elapsed +
                TimeSpan.FromTicks((beforeTimer.Elapsed.Ticks + afterTimer.Elapsed.Ticks) / ids.Length));
            var recorded = false;
            if (item.Profile is not null)
            {
                recorded = profilePublisher is null
                    ? RecordNormally(item.Profile)
                    : profilePublisher(item.Profile);
                if (!recorded) throw new OperationCanceledException("profile-refresh-lease-lost-before-publish");
            }
            output.Add(item.Row with { Status = recorded ? "ready" : "waiting", ProfileRecorded = recorded });
        }
        return output;

        bool RecordNormally(PrtgTrustedSamplingProfile profile)
        {
            new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);
            return true;
        }
    }


    private sealed record SensorRow(long? ParentObjid, string? SensorType, int? StatusRaw,
        double? LastValueRaw, double? LastCheckRaw, double? IntervalRaw, string[] ReturnedFields,
        string? Cumsince, string? RawTimestampTimeZoneId);

    private static SensorRow ParseSingleSensor(string json, long expectedId)
    {
        using var document = ParseBounded(json);
        if (!document.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("sensor-table-missing");
        var exact = rows.EnumerateArray().Where(row => ReadLong(row, "objid") == expectedId).ToArray();
        if (exact.Length != 1) throw new InvalidDataException("sensor-row-not-unique");
        var row = exact[0];
        return new(ReadLong(row, "parentid"), ReadString(row, "type"), ReadInt(row, "status_raw"),
            ReadDouble(row, "lastvalue_raw"), ReadDouble(row, "lastcheck_raw"), ReadDouble(row, "interval_raw"),
            row.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Distinct().Order().ToArray(),
            ReadString(row, "cumsince_raw") ?? ReadString(row, "cumsince"),
            ReadString(row, "raw_timestamp_timezone_id"));
    }

    private static IReadOnlyDictionary<long, SensorRow> ParseExactSensorSet(string json, IReadOnlyCollection<long> expectedIds)
    {
        using var document = ParseBounded(json);
        if (!document.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array ||
            rows.GetArrayLength() != expectedIds.Count)
            throw new InvalidDataException("sensor-table-group-count-mismatch");
        var expected = expectedIds.ToHashSet();
        var actual = new Dictionary<long, SensorRow>(expectedIds.Count);
        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadLong(row, "objid");
            if (!id.HasValue || !expected.Contains(id.Value) || actual.ContainsKey(id.Value))
                throw new InvalidDataException("sensor-table-group-id-set-mismatch");
            actual.Add(id.Value, new(ReadLong(row, "parentid"), ReadString(row, "type"), ReadInt(row, "status_raw"),
                ReadDouble(row, "lastvalue_raw"), ReadDouble(row, "lastcheck_raw"), ReadDouble(row, "interval_raw"),
                row.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Distinct().Order().ToArray(),
                ReadString(row, "cumsince_raw") ?? ReadString(row, "cumsince"),
                ReadString(row, "raw_timestamp_timezone_id")));
        }
        if (actual.Count != expected.Count || !actual.Keys.ToHashSet().SetEquals(expected))
            throw new InvalidDataException("sensor-table-group-id-set-mismatch");
        return actual;
    }

    private static IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> ParseChannels(
        string json, double? sensorValue, out bool truncated)
    {
        using var document = ParseBounded(json);
        if (!document.RootElement.TryGetProperty("channels", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("channels-table-missing");
        truncated = rows.GetArrayLength() >= MaxChannelsPerSensor ||
            document.RootElement.TryGetProperty("treesize", out var size) && ReadLong(size) > rows.GetArrayLength();
        return rows.EnumerateArray().Take(MaxChannelsPerSensor).Select(row =>
        {
            var raw = ReadDouble(row, "lastvalue_raw");
            return new PrtgTrustedSamplingChannelProbeRow(ReadLong(row, "objid"), ReadString(row, "name"),
                ReadString(row, "unit"), raw, sensorValue.HasValue && raw.HasValue && sensorValue.Value == raw.Value,
                ReadBool(row, "isprimary"), ReadString(row, "quantity"), ReadDouble(row, "scale"),
                ReadString(row, "direction"), ReadDouble(row, "lastcheck_raw"),
                ReadString(row, "interval_unit"), ReadString(row, "semantic_version"));
        }).ToArray();
    }

    private sealed record HistoricQualification(double RawValue, double MeasuredOaDate,
        string SourceVersion, string ProofReference);

    private static HistoricQualification QualifyHistoric(PrtgTrustedSamplingBinding binding,
        string xml, SensorRow sensor, DateTimeOffset observedAtUtc)
    {
        var document = PrtgHistoricXmlReader.Parse(xml);
        if (document.Samples.Count == 0 || string.IsNullOrWhiteSpace(document.Version))
            throw new InvalidDataException("historic-qualification-empty-or-unversioned");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(binding.RawTimestampTimeZoneId);
        var selected = document.Samples.Select(sample =>
        {
            var wall = DateTime.SpecifyKind(DateTime.FromOADate(sample.MeasuredOaDate), DateTimeKind.Unspecified);
            if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall))
                throw new InvalidDataException("historic-qualification-time-ambiguous");
            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone));
            return (Sample: sample, Utc: utc);
        }).Where(row => row.Utc <= observedAtUtc && observedAtUtc - row.Utc <= TimeSpan.FromHours(1))
          .OrderByDescending(row => row.Utc).ToArray();
        if (selected.Length == 0) throw new InvalidDataException("historic-qualification-no-recent-row");
        var latest = selected[0];
        var rows = latest.Sample.Channels.Where(channel => channel.ChannelId == binding.ChannelObjectId).ToArray();
        if (rows.Length != 1 || rows[0].Caption != binding.ExpectedCaption)
            throw new InvalidDataException("historic-qualification-channel-identity-mismatch");
        if (!sensor.LastValueRaw.HasValue || !sensor.LastCheckRaw.HasValue ||
            rows[0].RawValue != sensor.LastValueRaw.Value ||
            Math.Abs(latest.Sample.MeasuredOaDate - sensor.LastCheckRaw.Value) > 1.0 / 86400)
            throw new InvalidDataException("historic-qualification-not-the-sensor-last-primary-sample");
        var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml + "\n" + binding.BindingFingerprint +
            "\n" + rows[0].ChannelId + "\n" + rows[0].RawValue.ToString("R", CultureInfo.InvariantCulture) +
            "\n" + latest.Sample.MeasuredOaDate.ToString("R", CultureInfo.InvariantCulture))));
        return new(rows[0].RawValue, latest.Sample.MeasuredOaDate, document.Version, proof);
    }

    internal static string ParseNativePrimaryProperty(string response)
    {
        // The bounded property endpoint is accepted only as the exact unnamespaced PRTG XML
        // envelope with one direct, leaf result containing a nonnegative integer ID. Unknown
        // shapes stay waiting until deployment qualification observes a supported native response.
        try
        {
            if (Encoding.UTF8.GetByteCount(response) > MaxResponseBytes)
                throw new InvalidDataException("primarychannel-property-shape-unrecognized");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false
            };
            using (var depthScan = XmlReader.Create(new StringReader(response), settings))
            {
                while (depthScan.Read())
                    if (depthScan.Depth > 8)
                        throw new InvalidDataException("primarychannel-property-shape-unrecognized");
            }
            using var input = new StringReader(response);
            using var reader = XmlReader.Create(input, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || root.Name != XName.Get("prtg") || root.HasAttributes ||
                root.Elements().Count() != 1 ||
                root.Descendants().Count(element => element.Name.LocalName == "result") != 1)
                throw new InvalidDataException("primarychannel-property-shape-unrecognized");
            var result = root.Elements(XName.Get("result")).SingleOrDefault();
            if (result is null || result.HasAttributes || result.Elements().Any())
                throw new InvalidDataException("primarychannel-property-shape-unrecognized");
            var value = result.Value.Trim();
            if (value.Length is < 1 or > 20 || !value.All(char.IsAsciiDigit) ||
                !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 0)
                throw new InvalidDataException("primarychannel-property-shape-unrecognized");
            return id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("primarychannel-property-shape-unrecognized", ex);
        }
    }

    private static List<string> MissingAuthority(SensorRow sensor, SensorRow after,
        IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> channels, bool truncated, PrtgMonitoringPolicy policy,
        PrtgTrustedSamplingBinding? binding, string? nativePrimaryId)
    {
        var missing = new List<string>();
        if (sensor.LastCheckRaw is null) missing.Add("source:lastcheck_raw");
        if (sensor.StatusRaw != 3 || after.StatusRaw != 3) missing.Add("source:good_sample_status_required");
        if (sensor.ParentObjid is null || sensor.ParentObjid <= 0 || string.IsNullOrWhiteSpace(sensor.SensorType))
            missing.Add("source:parent_or_sensor_type");
        if (sensor.ParentObjid != after.ParentObjid || sensor.SensorType != after.SensorType ||
            sensor.Cumsince != after.Cumsince || sensor.IntervalRaw != after.IntervalRaw)
            missing.Add("source:sensor_metadata_bracket_changed");
        if (sensor.LastValueRaw is null || after.LastValueRaw is null || sensor.LastValueRaw != after.LastValueRaw)
            missing.Add("source:lastvalue_raw_or_bracket_changed");
        if (sensor.LastCheckRaw is null || after.LastCheckRaw is null || sensor.LastCheckRaw != after.LastCheckRaw)
            missing.Add("source:lastcheck_raw_or_bracket_changed");
        if (sensor.IntervalRaw is null) missing.Add("source:interval_raw");
        if (string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference)) missing.Add("source:raw_timestamp_timezone_basis");
        if (string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId)) missing.Add("analysis_timezone");
        if (binding is null) return missing;
        if (nativePrimaryId is null) missing.Add("source:primarychannel_property_shape_unrecognized");
        if (binding.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            binding.AnalysisTimeZoneId != policy.AnalysisTimeZoneId ||
            binding.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference)
            missing.Add("management:binding_time_basis_mismatch");
        if (nativePrimaryId is null || nativePrimaryId != binding.ChannelObjectId)
            missing.Add("channels:bound_channel_not_current_native_primary");
        if (truncated) missing.Add("channels:truncated");
        var selected = channels.Where(c => c.ChannelObjectId?.ToString(CultureInfo.InvariantCulture) == binding.ChannelObjectId).ToArray();
        if (selected.Length != 1 || selected[0].ChannelObjectId is null or < 0 ||
            string.IsNullOrWhiteSpace(selected[0].Caption) || selected[0].Caption != binding.ExpectedCaption)
            missing.Add("channels:bound_id_caption_mismatch_or_ambiguous");
        if (channels.Count(c => string.Equals(c.Caption, binding.ExpectedCaption, StringComparison.Ordinal)) > 1)
            missing.Add("channels:bound_caption_ambiguous");
        if (selected.Length != 1 || selected[0].RawValue is null || sensor.LastValueRaw is null ||
            selected[0].RawValue.Value != sensor.LastValueRaw.Value)
            missing.Add("channels:bound_raw_value_not_equal_to_primary_sensor_snapshot");
        // Unit is operator-authored binding data. Missing native unit is unknown, not proof of a
        // different unit; an explicit contradictory native unit remains a hard conflict.
        if (selected.Length == 1)
        {
            if (!string.IsNullOrWhiteSpace(selected[0].Unit) && !IsPercentUnit(selected[0].Unit))
                missing.Add("channels:native_unit_conflicts_binding");
            if (selected[0].Scale.HasValue && selected[0].Scale.Value != binding.Scale)
                missing.Add("channels:native_scale_conflicts_binding");
            if (!string.IsNullOrWhiteSpace(selected[0].Direction) &&
                !string.Equals(selected[0].Direction, binding.Direction, StringComparison.OrdinalIgnoreCase))
                missing.Add("channels:native_direction_conflicts_binding");
        }
        if (sensor.IntervalRaw is null or <= 0 || binding.IntervalRawUnit is not ("seconds" or "minutes"))
            missing.Add("source:scan_interval_unit");
        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool IsPercentUnit(string? unit) => string.Equals(unit, "%", StringComparison.Ordinal) ||
        string.Equals(unit, "percent", StringComparison.OrdinalIgnoreCase);

    private static JsonDocument ParseBounded(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes) throw new InvalidDataException("source-probe-response-over-limit");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
        try { RejectDuplicateKeys(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("source-probe-duplicate-json-key");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static bool SameIdentity(PrtgResourceIdentity left, PrtgResourceIdentity right) =>
        left.SensorId == right.SensorId && left.Epoch == right.Epoch && left.Generation == right.Generation &&
        left.SourceGeneration == right.SourceGeneration && left.DeviceId == right.DeviceId &&
        left.HostId == right.HostId && left.ChannelGeneration == right.ChannelGeneration &&
        left.ResourceFingerprint == right.ResourceFingerprint && left.InventoryFingerprint == right.InventoryFingerprint &&
        left.ChannelFingerprint == right.ChannelFingerprint &&
        left.Active == right.Active && left.PendingReconciliation == right.PendingReconciliation;

    private static long? ReadLong(JsonElement token)
    {
        if (token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out var number)) return number;
        return token.ValueKind == JsonValueKind.String && long.TryParse(token.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
    private static long? ReadLong(JsonElement row, string name) => TryProperty(row, name, out var value) ? ReadLong(value) : null;
    private static int? ReadInt(JsonElement row, string name) => TryProperty(row, name, out var value) && ReadLong(value) is long n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;
    private static bool? ReadBool(JsonElement row, string name)
    {
        if (!TryProperty(row, name, out var token)) return null;
        if (token.ValueKind is JsonValueKind.True or JsonValueKind.False) return token.GetBoolean();
        return token.ValueKind == JsonValueKind.String && bool.TryParse(token.GetString(), out var value) ? value : null;
    }
    private static double? ReadDouble(JsonElement row, string name)
    {
        if (!TryProperty(row, name, out var token)) return null;
        if (token.ValueKind == JsonValueKind.Number && token.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        return token.ValueKind == JsonValueKind.String && double.TryParse(token.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : null;
    }
    private static string? ReadString(JsonElement row, string name) =>
        TryProperty(row, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryProperty(JsonElement row, string name, out JsonElement value)
    {
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out value)) return true;
        if (row.ValueKind == JsonValueKind.Object)
            foreach (var p in row.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}
