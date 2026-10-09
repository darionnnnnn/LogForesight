using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core;

namespace LogForesight.Web.Services;

public sealed record PrtgTrustedSamplingChannelProbeRow(
    long? ChannelObjectId, string? Caption, string? Unit,
    double? RawValue, bool EqualsSensorSnapshotValue, bool? SourceMarkedPrimary,
    string? QuantitySemantic, double? Scale, string? Direction, double? LastCheckRaw,
    string? IntervalRawUnit, string? SemanticVersion);

public sealed record PrtgTrustedSamplingProbeRow(
    long SensorObjid, string Status, IReadOnlyList<string> ReturnedSensorFields,
    long? ParentObjid, string? SensorType, int? StatusRaw, double? LastValueRaw,
    double? LastCheckRaw, double? IntervalRaw, IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> Channels,
    bool ChannelsTruncated, IReadOnlyList<string> MissingAuthorityFields,
    string ResourceGeneration, long IdentityEpoch, string ChannelGeneration,
    DateTimeOffset ProbedAtUtc, bool ProfileRecorded);

/// <summary>
/// Bounded read-only baseline source capability probe. It never fabricates a trust profile from
/// fields not present in the deployed PRTG table contract and does not call historicdata or one
/// property endpoint per sensor on the recurring snapshot path.
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
        Action<TimeSpan>? onResponseRead = null)
    {
        if (requestedIds.Count is < 1 or > MaxSensorIds || requestedIds.Any(id => id <= 0) ||
            requestedIds.Distinct().Count() != requestedIds.Count)
            throw new ArgumentOutOfRangeException(nameof(requestedIds), "每次最多探測 5 顆且 objid 必須唯一。" );

        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        var policy = policyStore.Get();
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || requestedIds.Any(id => !policy.SensorIds.Contains(id)))
            throw new InvalidOperationException("source-policy-not-ready-or-outside-pilot");

        var ids = requestedIds.Order().ToArray();
        var store = backend.PrtgStore();
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
        using var client = clientFactory(settings);
        client.RequestPurpose = requestPurpose;
        client.AdmissionPlanFingerprint = admissionPlanFingerprint;
        client.TableRequestSent = onRequestStarted;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        foreach (var id in ids)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var before = identities[id];
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
            var after = backend.PrtgStore().GetResourceIdentity(id);
            var latestPolicy = policyStore.Get();
            var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (!SameIdentity(before, after) ||
                !allowUnrelatedPolicyRevisionChanges && latestPolicy.Revision != policy.Revision ||
                latestPolicy.SourceGeneration != policy.SourceGeneration || !latestPolicy.SensorIds.Contains(id) ||
                !latestPolicy.HostIds.Contains(after.HostId) ||
                !latestPolicy.Ready(latestSettings.PrtgUrl) || settings.PrtgUrl != latestSettings.PrtgUrl ||
                settings.PrtgFetchStrategy != latestSettings.PrtgFetchStrategy || !latestSettings.PrtgEnabled ||
                policy.RawTimestampTimeZoneId != latestPolicy.RawTimestampTimeZoneId ||
                policy.SourceTimeZoneId != latestPolicy.SourceTimeZoneId ||
                policy.AnalysisTimeZoneId != latestPolicy.AnalysisTimeZoneId ||
                policy.TimeBasisEvidenceReference != latestPolicy.TimeBasisEvidenceReference)
                throw new InvalidOperationException($"probe-fence-changed:{id}");

            var probedAtUtc = DateTimeOffset.UtcNow;
            var missing = MissingAuthority(sensor, channels, truncated, policy);
            if (!strategy.Ready) missing.AddRange(strategy.MissingFacts.Select(f => $"strategy:{f}"));
            var profileRecorded = false;
            if (sensor.ParentObjid != after.DeviceId || string.IsNullOrWhiteSpace(sensor.Cumsince) ||
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(sensor.ParentObjid?.ToString(CultureInfo.InvariantCulture) ?? "",
                    sensor.SensorType ?? "", sensor.Cumsince ?? "", 0) != after.ResourceFingerprint)
                missing.Add("source:observed_resource_identity_mismatch");
            if (missing.Count == 0)
            {
                try
                {
                    var primary = channels.Single(channel => channel.SourceMarkedPrimary == true);
                    var interval = primary.IntervalRawUnit == "seconds"
                        ? TimeSpan.FromSeconds(sensor.IntervalRaw!.Value) : TimeSpan.FromMinutes(sensor.IntervalRaw!.Value);
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(policy.RawTimestampTimeZoneId);
                    var wall = DateTime.SpecifyKind(DateTime.FromOADate(sensor.LastCheckRaw!.Value), DateTimeKind.Unspecified);
                    if (zone.IsAmbiguousTime(wall) || zone.IsInvalidTime(wall))
                        throw new InvalidDataException("source_measurement_time_ambiguous");
                    var measuredAtUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wall, zone));
                    var maximumAge = TimeSpan.FromMinutes(strategyMinutes) + interval + interval;
                    if (measuredAtUtc > probedAtUtc || probedAtUtc - measuredAtUtc > maximumAge)
                        throw new InvalidDataException("source_measurement_not_recent");
                    var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sensorJson + "\n" + channelsJson)));
                    var physicalReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        string.Join("|", id, primary.ChannelObjectId, sensor.LastCheckRaw.Value.ToString("R", CultureInfo.InvariantCulture),
                            sensor.LastValueRaw!.Value.ToString("R", CultureInfo.InvariantCulture)))));
                    var quantity = Enum.Parse<PrtgTrustedQuantitySemantic>(primary.QuantitySemantic!, true);
                    if (interval <= TimeSpan.Zero || interval > TimeSpan.FromHours(1))
                        throw new InvalidDataException("source_interval_out_of_range");
                    var normalized = primary.Direction switch
                    { "inverse" => -sensor.LastValueRaw!.Value, "absolute" => Math.Abs(sensor.LastValueRaw!.Value), _ => sensor.LastValueRaw!.Value };
                    if (!double.IsFinite(normalized) || normalized is < 0 or > 100)
                        throw new InvalidDataException("source_quantity_out_of_range");
                    var priorProfile = store.GetTrustedSamplingProfiles([id]).GetValueOrDefault(id);
                    var channelChanged = priorProfile is not null &&
                        (priorProfile.PrimaryChannelId != primary.ChannelObjectId!.Value.ToString(CultureInfo.InvariantCulture) ||
                         priorProfile.PrimaryChannelCaption != primary.Caption || priorProfile.Quantity != quantity ||
                         priorProfile.Unit != primary.Unit || priorProfile.Scale != primary.Scale ||
                         priorProfile.Direction != primary.Direction || priorProfile.SemanticVersion != primary.SemanticVersion);
                    if (string.IsNullOrWhiteSpace(after.ChannelGeneration) || channelChanged)
                    {
                        if ((leaseOwner is null) != (leaseVersion is null))
                            throw new ArgumentException("Profile refresh lease owner and version must be supplied together.");
                        var channelFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(new { primary.ChannelObjectId, primary.Caption, quantity,
                                primary.Unit, primary.Scale, primary.Direction, primary.SemanticVersion }))));
                        after = store.SetObservedChannel(id, policy.SourceGeneration, channelFingerprint, after.Generation,
                            leaseOwner, leaseVersion);
                    }
                    var profile = PrtgTrustedSamplingProfile.FromProbe(id, after, sensor.SensorType!,
                        primary.ChannelObjectId!.Value.ToString(CultureInfo.InvariantCulture), primary.Caption!,
                        Enum.Parse<PrtgTrustedQuantitySemantic>(primary.QuantitySemantic!, true), primary.Unit!,
                        primary.Scale!.Value, primary.Direction!, primary.SemanticVersion!, strategy.StrategyFingerprint,
                        strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, interval, primary.IntervalRawUnit!,
                        policy.RawTimestampTimeZoneId, policy.SourceTimeZoneId, policy.AnalysisTimeZoneId, probedAtUtc,
                        reference, physicalReference, true, sensor.LastValueRaw!.Value, primary.RawValue!.Value,
                        sensor.LastCheckRaw.Value, primary.LastCheckRaw!.Value);
                    profileRecorded = profilePublisher is null
                        ? RecordNormally(profile)
                        : profilePublisher(profile);
                    if (!profileRecorded) throw new OperationCanceledException("profile-refresh-lease-lost-before-publish");
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
                { missing.Add("source:profile_validation_failed"); }
            }
            output.Add(new(id, profileRecorded ? "ready" : "waiting", sensor.ReturnedFields,
                sensor.ParentObjid, sensor.SensorType, sensor.StatusRaw, sensor.LastValueRaw,
                sensor.LastCheckRaw, sensor.IntervalRaw, channels, truncated, missing,
                after.Generation, after.Epoch, after.ChannelGeneration, probedAtUtc, profileRecorded));
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

    private static List<string> MissingAuthority(SensorRow sensor,
        IReadOnlyList<PrtgTrustedSamplingChannelProbeRow> channels, bool truncated, PrtgMonitoringPolicy policy)
    {
        var missing = new List<string>();
        if (sensor.LastCheckRaw is null) missing.Add("source:lastcheck_raw");
        if (sensor.StatusRaw != 3) missing.Add("source:good_sample_status_required");
        if (sensor.ParentObjid is null || sensor.ParentObjid <= 0 || string.IsNullOrWhiteSpace(sensor.SensorType))
            missing.Add("source:parent_or_sensor_type");
        if (sensor.LastValueRaw is null) missing.Add("source:lastvalue_raw");
        if (sensor.IntervalRaw is null) missing.Add("source:interval_raw");
        if (string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference)) missing.Add("source:raw_timestamp_timezone_basis");
        if (string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId)) missing.Add("analysis_timezone");
        if (string.IsNullOrWhiteSpace(sensor.RawTimestampTimeZoneId) ||
            sensor.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId) missing.Add("source:raw_timestamp_timezone_id");
        var primaryChannels = channels.Where(c => c.SourceMarkedPrimary == true).ToArray();
        if (sensor.LastCheckRaw is null || primaryChannels.Length != 1 || primaryChannels[0].LastCheckRaw is null ||
            Math.Abs(primaryChannels[0].LastCheckRaw!.Value - sensor.LastCheckRaw!.Value) > 1.0 / 86400)
            missing.Add("channels:physical_sample_time_alignment_not_exact");
        if (truncated) missing.Add("channels:truncated");
        if (channels.Count == 0 || channels.Any(c => c.ChannelObjectId is null or < 0 || string.IsNullOrWhiteSpace(c.Caption)))
            missing.Add("channels:actual_id_or_caption");
        if (primaryChannels.Length != 1 || !primaryChannels[0].EqualsSensorSnapshotValue)
            missing.Add("channels:physical_sample_value_alignment_not_exact_for_primary");
        if (primaryChannels.Length != 1 || primaryChannels[0].ChannelObjectId is null ||
            string.IsNullOrWhiteSpace(primaryChannels[0].Caption))
            missing.Add("channels:selected_primary_channel_authority");
        if (primaryChannels.Length != 1 || !Enum.TryParse<PrtgTrustedQuantitySemantic>(primaryChannels[0].QuantitySemantic,
                true, out var quantity) || !Enum.IsDefined(quantity) || quantity == PrtgTrustedQuantitySemantic.Unknown ||
            string.IsNullOrWhiteSpace(primaryChannels[0].Unit) || !primaryChannels[0].Scale.HasValue ||
            primaryChannels[0].Scale != 1 || primaryChannels[0].Unit != "%" || primaryChannels[0].Direction is not ("direct" or "inverse" or "absolute") ||
            string.IsNullOrWhiteSpace(primaryChannels[0].SemanticVersion))
            missing.Add("channels:quantity_unit_scale_direction_semantics");
        if (sensor.IntervalRaw is null or <= 0 || primaryChannels.Length != 1 ||
            primaryChannels[0].IntervalRawUnit is not ("seconds" or "minutes"))
            missing.Add("source:scan_interval_unit");
        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    private static JsonDocument ParseBounded(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes) throw new InvalidDataException("source-probe-response-over-limit");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
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
