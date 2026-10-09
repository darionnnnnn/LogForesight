using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Service;

/// <summary>
/// Immutable per-finding snapshot for a formal CPU/memory automatic-delivery start.
/// Persist this when an intent is queued; retries must pass the same value to
/// <see cref="TryValidateCurrentStart"/> rather than capturing a newer mode revision.
/// </summary>
public sealed record PrtgResourceFormalDeliveryFence(
    long HostId,
    long ParentRecordId,
    long ResourceModeBlobVersion,
    long SensorObjid,
    PrtgResourceFamily Family,
    string SourceGeneration,
    string ResourceGeneration,
    string ChannelGeneration,
    string RuleId,
    string RuleFingerprint,
    string RuleAdmissionFingerprint,
    string IssueIdentityHash)
{
    private const int MaxParentJsonCharacters = 8 * 1024 * 1024;
    private const int MaxRulesJsonCharacters = 2 * 1024 * 1024;
    private const int MaxRules = 10_000;

    // Scoped to one workflow-blob claim transaction; never retain across transactions.
    internal sealed class ValidationCache
    {
        internal readonly Dictionary<PrtgResourceFamily, (string RuleId, string Admission, string Fingerprint)> Rules = [];
        internal bool RulesLoaded;
        internal IReadOnlyList<KnownIssueRule>? ValidRules;
    }

    /// <summary>
    /// Read-only validation intended to run inside the caller's durable-start transaction.
    /// It proves the issue still belongs to this exact qualified parent, the manifest's
    /// required mode revision is still current, and the exact rule/grant remains active.
    /// </summary>
    public static bool TryValidateCurrentStart(LfDbContext ctx, long hostId, long parentRecordId,
        LogIssueSignature issue, PrtgResourceFormalDeliveryFence? expectedFence,
        out PrtgResourceFormalDeliveryFence fence)
        => TryValidateCurrentStart(ctx, hostId, parentRecordId, issue, expectedFence, new ValidationCache(), out fence);

    internal static bool TryValidateCurrentStart(LfDbContext ctx, long hostId, long parentRecordId,
        LogIssueSignature issue, PrtgResourceFormalDeliveryFence? expectedFence, ValidationCache cache,
        out PrtgResourceFormalDeliveryFence fence)
    {
        fence = null!;
        if (ctx is null || hostId <= 0 || parentRecordId <= 0 || issue is null ||
            !TryGetTarget(issue, out var family, out var sensorObjid, out var sourceGeneration,
                out var resourceGeneration, out var channelGeneration)) return false;

        try
        {
            var parentRow = ctx.DailyRecords.AsNoTracking().Where(row => row.RecordId == parentRecordId &&
                    row.HostId == hostId)
                .Select(row => new
                {
                    row.RecordDate,
                    Content = row.ContentJson.Substring(0, MaxParentJsonCharacters + 1),
                    Length = row.ContentJson.Length
                })
                .FirstOrDefault();
            if (parentRow is null || string.IsNullOrWhiteSpace(parentRow.Content) ||
                parentRow.Length > MaxParentJsonCharacters || parentRow.Content.Length > MaxParentJsonCharacters ||
                System.Text.Encoding.UTF8.GetByteCount(parentRow.Content) > MaxParentJsonCharacters) return false;

            var parent = JsonSerializer.Deserialize<DailyAnalysisRecord>(parentRow.Content);
            if (parent is null) return false;
            parent.RecordId = parentRecordId;
            parent.HostId = hostId;
            // EF's SQL date projection can lose DateTime.Kind. Preserve the serialized
            // wall-day representation used by the parent fingerprint, but require that
            // its calendar day still matches the authoritative record-date column.
            if (parent.Date.Date != parentRow.RecordDate.Date) return false;
            parent.TopIssues ??= [];
            var manifest = parent.PrtgManifest;
            if (!HostDayWorkflowFingerprint.HasValidPrtgManifest(parent) ||
                manifest is not { ResourceModeFenceRequired: true, ResourceModeBlobVersion: >= 0 } ||
                manifest.ParentRecordId != parentRecordId ||
                !parent.TopIssues.Any(current => SameFinding(current, issue)) ||
                !TryParseIdentity(issue.EventKey, out var eventCode, out var eventSensor, out var eventSource, out var eventResource) ||
                !TryFamily(eventCode, out var eventFamily) || eventFamily != family ||
                eventSensor != sensorObjid || eventSource != sourceGeneration || eventResource != resourceGeneration)
                return false;

            if (!CurrentIdentityAndProfileMatch(ctx, hostId, sensorObjid, family, sourceGeneration,
                    resourceGeneration, channelGeneration, manifest.ResourceAuthorityRevision, manifest.PolicyRevision, out var profile,
                    out var identity)) return false;

            var modeRow = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == PrtgResourcePressureModeStore.BlobKey(hostId))
                .Select(row => new
                {
                    Content = row.Content.Substring(0, PrtgResourcePressureModeStore.MaximumDocumentCharacters + 1),
                    Length = row.Content.Length,
                    row.Version
                }).FirstOrDefault();
            if (modeRow is null || modeRow.Version != manifest.ResourceModeBlobVersion ||
                modeRow.Version < 1 || string.IsNullOrWhiteSpace(modeRow.Content) ||
                modeRow.Length > PrtgResourcePressureModeStore.MaximumDocumentCharacters ||
                modeRow.Content.Length > PrtgResourcePressureModeStore.MaximumDocumentCharacters ||
                System.Text.Encoding.UTF8.GetByteCount(modeRow.Content) > PrtgResourcePressureModeStore.MaximumDocumentCharacters) return false;

            var mode = JsonSerializer.Deserialize<PrtgResourcePressureModeDocument>(modeRow.Content, LfJsonOptions.Pretty);
            if (mode?.Grants is null || mode.Revocations is null ||
                mode.Grants.Count > PrtgResourcePressureModeStore.MaximumResourcesPerHost ||
                mode.Revocations.Count > PrtgResourcePressureModeStore.MaximumRevocationsPerHost ||
                mode.Grants.Any(grant => grant is null || !ValidGrantShape(grant, hostId)) ||
                mode.Revocations.Any(item => item is null || !ValidRevocationShape(item, hostId))) return false;
            var grant = mode.Grants.SingleOrDefault(candidate => candidate.HostId == hostId &&
                candidate.SensorObjid == sensorObjid && candidate.Family == family);
            if (grant is not { FormalEnabled: true, MaintainAuthorized: true, TrialConsumed: true } ||
                grant.SourceGeneration != sourceGeneration || grant.ResourceGeneration != resourceGeneration ||
                grant.ChannelGeneration != channelGeneration || grant.RuleId != issue.RuleId ||
                grant.RulesVersion != PrtgResourcePressureEvaluator.RulesVersion) return false;

            var admissionFingerprint = ReadCurrentRuleAdmission(ctx, family, issue.RuleId, cache,
                out var currentRuleFingerprint);
            if (admissionFingerprint is null || currentRuleFingerprint != grant.RuleFingerprint ||
                issue.PrtgRuleAdmissionFingerprint != admissionFingerprint ||
                grant.ResourceEpoch != identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                grant.SemanticVersion != profile.SemanticVersion || grant.StrategyVersion != profile.StrategyFingerprint)
                return false;

            var profileContext = new PrtgResourceCurrentContext(profile.SensorObjid, profile.SourceGeneration,
                profile.ResourceGeneration, profile.ChannelGeneration,
                profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), profile.SemanticVersion,
                profile.StrategyFingerprint, profile.StrategyMinutes, profile.StrategyEffectiveFromHourUtc,
                profile.ConfirmedScanInterval, profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId,
                PrtgResourcePeriodConsumer.StableProfileFingerprint(profile));
            if (grant.ProfileFingerprint != PrtgResourcePressureEvaluator.GetProfileFingerprint(family, profileContext))
                return false;

            var candidate = new PrtgResourceFormalDeliveryFence(hostId, parentRecordId,
                manifest.ResourceModeBlobVersion, sensorObjid, family, sourceGeneration, resourceGeneration,
                channelGeneration, issue.RuleId!, grant.RuleFingerprint, admissionFingerprint,
                Hash(IssueSignatureKey.For(issue)));
            if (expectedFence is not null && expectedFence != candidate) return false;
            fence = candidate;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or
                                   InvalidOperationException or FormatException or OverflowException or NullReferenceException)
        {
            // Malformed or ambiguous persisted authority is a fail-closed delivery decision.
            return false;
        }
    }

    /// <summary>Recognizes only the two governed automatic CPU/memory resource rules.</summary>
    public static bool IsTargetPressureIssue(LogIssueSignature? issue)
    {
        if (issue is null) return false;
        // Historical pressure identities still require a current formal-mode fence.
        // Recognizing them here prevents legacy rows bypassing AI/case/mail withdrawal;
        // TryGetTarget deliberately accepts only the governed canonical rule identities.
        static bool IsPressureIdentity(string code) => IsTargetRuleCode(code) ||
            code is "prtg.resource.cpu-sustained-pressure" or "prtg.resource.memory-sustained-pressure";
        if (PrtgFindingMapper.TryGetRuleCode(issue.Source, out var sourceCode) && IsPressureIdentity(sourceCode)) return true;
        if (string.IsNullOrWhiteSpace(issue.EventKey)) return false;
        var parts = issue.EventKey.Split(':');
        return parts.Length > 1 && parts[0] == "prtg" && IsPressureIdentity(parts[1]);
    }

    private static bool TryGetTarget(LogIssueSignature issue, out PrtgResourceFamily family,
        out long sensorObjid, out string sourceGeneration, out string resourceGeneration,
        out string channelGeneration)
    {
        family = default;
        sensorObjid = 0;
        sourceGeneration = resourceGeneration = channelGeneration = string.Empty;
        if (!PrtgFindingMapper.IsPrtg(issue) || issue.Suppressed || string.IsNullOrWhiteSpace(issue.EventKey) ||
            !PrtgFindingMapper.TryGetRuleCode(issue.Source, out var ruleCode) ||
            !TryFamily(ruleCode, out family) || issue.RuleId is not { Length: > 0 and <= 128 } ||
            issue.PrtgSourceGeneration is not { Length: > 0 and <= 128 } source ||
            issue.PrtgResourceGeneration is not { Length: > 0 and <= 128 } resource ||
            issue.PrtgChannelGeneration is not { Length: > 0 and <= 128 } channel ||
            issue.PrtgRuleAdmissionFingerprint is not { Length: 64 } admission || !admission.All(Uri.IsHexDigit) ||
            !TryParseIdentity(issue.EventKey, out var eventRuleCode, out sensorObjid, out var eventSource, out var eventResource) ||
            !TryFamily(eventRuleCode, out var eventFamily) || eventFamily != family ||
            eventSource != source || eventResource != resource) return false;
        sourceGeneration = source;
        resourceGeneration = resource;
        channelGeneration = channel;
        return true;
    }

    private static bool TryParseIdentity(string? eventKey, out string ruleCode, out long sensorObjid,
        out string sourceGeneration, out string resourceGeneration)
    {
        ruleCode = string.Empty;
        sensorObjid = 0;
        sourceGeneration = resourceGeneration = string.Empty;
        if (string.IsNullOrWhiteSpace(eventKey)) return false;
        var parts = eventKey.Split(':');
        return parts.Length == 5 && parts[0] == "prtg" && IsTargetRuleCode(parts[1]) &&
            (ruleCode = parts[1]).Length > 0 &&
            long.TryParse(parts[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out sensorObjid) && sensorObjid > 0 &&
            parts[3] is { Length: > 0 and <= 128 } && parts[4] is { Length: > 0 and <= 128 } &&
            (sourceGeneration = parts[3]).Length > 0 && (resourceGeneration = parts[4]).Length > 0;
    }

    private static bool SameFinding(LogIssueSignature current, LogIssueSignature requested) =>
        !current.Suppressed && !requested.Suppressed &&
        StringComparer.Ordinal.Equals(IssueSignatureKey.For(current), IssueSignatureKey.For(requested)) &&
        current.EventKey == requested.EventKey && current.Source == requested.Source &&
        current.RuleId == requested.RuleId && current.PrtgSourceGeneration == requested.PrtgSourceGeneration &&
        current.PrtgResourceGeneration == requested.PrtgResourceGeneration &&
        current.PrtgChannelGeneration == requested.PrtgChannelGeneration &&
        current.PrtgRuleAdmissionFingerprint == requested.PrtgRuleAdmissionFingerprint;

    private static bool ValidGrantShape(PrtgResourcePressureModeGrant grant, long hostId) =>
        grant.HostId == hostId && grant.SensorObjid > 0 &&
        grant.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        grant.UpdatedAtUtc.Kind == DateTimeKind.Utc && grant.UpdatedAtUtc != default &&
        grant.TrialExpiresAtUtc.Kind == DateTimeKind.Utc && grant.TrialExpiresAtUtc != default &&
        IsSha256(grant.ProfileFingerprint) && grant.RulesVersion == PrtgResourcePressureEvaluator.RulesVersion &&
        grant.SourceGeneration is { Length: > 0 and <= 128 } && grant.ResourceGeneration is { Length: > 0 and <= 128 } &&
        grant.ChannelGeneration is { Length: > 0 and <= 128 } && grant.ResourceEpoch is { Length: > 0 and <= 64 } &&
        grant.SemanticVersion is { Length: > 0 and <= 128 } && grant.StrategyVersion is { Length: > 0 and <= 128 } &&
        grant.RuleId is { Length: > 0 and <= 128 } && IsSha256(grant.RuleFingerprint) && IsSha256(grant.TrialResultHash);

    private static bool ValidRevocationShape(PrtgResourcePressureModeRevocation item, long hostId) =>
        item.HostId == hostId && item.SensorObjid > 0 && item.Family is PrtgResourceFamily.Cpu or PrtgResourceFamily.Memory &&
        item.DisabledAtUtc.Kind == DateTimeKind.Utc && item.DisabledAtUtc != default &&
        (item.SourceGeneration is { Length: 0 } && item.ResourceGeneration is { Length: 0 } ||
         item.SourceGeneration is { Length: > 0 and <= 128 } && item.ResourceGeneration is { Length: > 0 and <= 128 });

    private static string? ReadCurrentRuleAdmission(LfDbContext ctx, PrtgResourceFamily family,
        string? requestedRuleId, ValidationCache cache, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (cache.Rules.TryGetValue(family, out var cached))
        {
            fingerprint = cached.Fingerprint;
            return cached.RuleId.Length > 0 && cached.RuleId == requestedRuleId ? cached.Admission : null;
        }
        if (!cache.RulesLoaded)
        {
            cache.RulesLoaded = true;
            var raw = ReadBoundedBlob(ctx, "rules", MaxRulesJsonCharacters);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("Rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array ||
                rulesElement.GetArrayLength() > MaxRules) return null;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true, Converters = { new JsonStringEnumConverter() } };
            var rules = JsonSerializer.Deserialize<List<KnownIssueRule>>(rulesElement.GetRawText(), options);
            if (rules is null) return null;
            cache.ValidRules = RuleValidator.Validate(rules).ValidRules;
        }
        if (cache.ValidRules is null) return null;
        var (ruleCode, category) = family == PrtgResourceFamily.Cpu
            ? (PrtgRuleEvaluator.RuleResourceCpuPressure, PrtgSensorCategories.Cpu)
            : (PrtgRuleEvaluator.RuleResourceMemoryPressure, PrtgSensorCategories.Memory);
        var matches = cache.ValidRules.Where(rule => rule.Enabled &&
            string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgRuleCode == ruleCode && string.Equals(rule.PrtgSensorCategory, category, StringComparison.OrdinalIgnoreCase) &&
            rule.PrtgDiskTrendThresholds is null).ToArray();
        if (matches.Length != 1 || matches[0].Id != requestedRuleId)
        {
            cache.Rules[family] = (string.Empty, string.Empty, string.Empty);
            return null;
        }
        fingerprint = PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(matches[0]);
        var admission = fingerprint;
        cache.Rules[family] = (matches[0].Id, admission, fingerprint);
        return admission;
    }

    private static bool CurrentIdentityAndProfileMatch(LfDbContext ctx, long hostId, long sensorObjid,
        PrtgResourceFamily family, string sourceGeneration, string resourceGeneration, string channelGeneration,
        long expectedAuthorityRevision, string expectedPolicyRevision, out PrtgTrustedSamplingProfile profile,
        out PrtgResourceIdentity identity)
    {
        profile = null!;
        identity = null!;
        var revision = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey ==
                PrtgResourceIdentityStore.AuthorityRevisionKey(hostId)).Select(row => row.Version).FirstOrDefault();
        if (expectedAuthorityRevision <= 0 || revision != expectedAuthorityRevision) return false;

        var identityRow = ReadBoundedBlob(ctx, PrtgResourceIdentityStore.Prefix + sensorObjid.ToString(
            System.Globalization.CultureInfo.InvariantCulture), PrtgResourceIdentityStore.MaxLedgerBytes);
        if (identityRow is null) return false;
        identity = JsonSerializer.Deserialize<PrtgResourceIdentity>(identityRow, LfJsonOptions.Pretty)!;
        if (identity is null || identity.SensorId != sensorObjid || identity.HostId != hostId || identity.Epoch <= 0 ||
            !identity.Active || identity.PendingReconciliation || identity.SourceGeneration != sourceGeneration ||
            identity.Generation != resourceGeneration || identity.ChannelGeneration != channelGeneration) return false;

        var sensor = ctx.PrtgSensors.AsNoTracking().Where(row => row.Objid == sensorObjid)
            .Select(row => new { row.DeviceObjid, row.SensorType, row.Category, row.Paused }).SingleOrDefault();
        if (sensor is null || sensor.Paused || sensor.DeviceObjid != identity.DeviceId ||
            !string.Equals(sensor.Category, family.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        var identityDeviceId = identity.DeviceId;
        var devicePaused = ctx.PrtgDevices.AsNoTracking().Where(row => row.Objid == identityDeviceId)
            .Select(row => (bool?)row.Paused).SingleOrDefault();
        if (devicePaused is not false) return false;

        var settingsRaw = ReadBoundedBlob(ctx, "system_settings", 1024 * 1024);
        var settings = settingsRaw is null ? null : JsonSerializer.Deserialize<SystemSettings>(settingsRaw, LfJsonOptions.Pretty);
        if (settings is not { PrtgEnabled: true } || string.IsNullOrWhiteSpace(settings.PrtgUrl)) return false;
        var policyRaw = ReadBoundedBlob(ctx, PrtgMonitoringPolicyStore.BlobKey, 64 * 1024);
        var policy = policyRaw is null ? null : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty);
        if (policy is null || !policy.Ready(settings.PrtgUrl) || !policy.HostIds.Contains(hostId) ||
            !policy.SensorIds.Contains(sensorObjid) || policy.SourceGeneration != sourceGeneration ||
            string.IsNullOrWhiteSpace(policy.Revision) || policy.Revision != expectedPolicyRevision) return false;
        var strategyRaw = ReadBoundedBlob(ctx, PrtgTrustedSamplingStrategyStateStore.BlobKey, 16 * 1024);
        var strategyProfile = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy);
        var strategy = PrtgTrustedSamplingStrategyStateStore.ReadCurrent(strategyRaw, policy,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), strategyProfile.SnapshotIntervalMinutes);
        if (!strategy.Ready) return false;

        var profileRaw = ReadBoundedBlob(ctx, PrtgTrustedSamplingProfile.StorePrefix + sensorObjid.ToString(
            System.Globalization.CultureInfo.InvariantCulture), PrtgTrustedSamplingProfile.MaximumSerializedBytes);
        if (profileRaw is null) return false;
        profile = JsonSerializer.Deserialize<PrtgTrustedSamplingProfile>(profileRaw, LfJsonOptions.Pretty)!;
        if (profile is null) return false;
        var semanticMatches = family switch
        {
            PrtgResourceFamily.Cpu => profile.Quantity == PrtgTrustedQuantitySemantic.CpuLoadPercent,
            PrtgResourceFamily.Memory => profile.Quantity is PrtgTrustedQuantitySemantic.MemoryUsedPercent or
                PrtgTrustedQuantitySemantic.MemoryAvailablePercent,
            _ => false
        };
        if (!semanticMatches || profile.Unit != "%" || profile.Scale != 1) return false;
        var nowUtc = DateTime.UtcNow;
        return PrtgTrustedSamplingProfileResolver.Resolve(profile, identity, policy, sensorObjid,
            sensor.SensorType, strategy, nowUtc, nowUtc, nowUtc).Ready;
    }

    private static string? ReadBoundedBlob(LfDbContext ctx, string key, int maximumCharacters)
    {
        var row = ctx.Blobs.AsNoTracking().Where(blob => blob.BlobKey == key)
            .Select(blob => new { Content = blob.Content.Substring(0, maximumCharacters + 1), Length = blob.Content.Length })
            .SingleOrDefault();
        if (row is null) return null;
        if (row.Length > maximumCharacters || row.Content.Length > maximumCharacters ||
            System.Text.Encoding.UTF8.GetByteCount(row.Content) > maximumCharacters)
            throw new InvalidDataException("PRTG authority blob exceeded its bounded read.");
        return row.Content;
    }

    private static bool TryFamily(string ruleCode, out PrtgResourceFamily family)
    {
        family = ruleCode switch
        {
            PrtgRuleEvaluator.RuleResourceCpuPressure => PrtgResourceFamily.Cpu,
            PrtgRuleEvaluator.RuleResourceMemoryPressure => PrtgResourceFamily.Memory,
            _ => default
        };
        return ruleCode is PrtgRuleEvaluator.RuleResourceCpuPressure or PrtgRuleEvaluator.RuleResourceMemoryPressure;
    }

    private static bool IsTargetRuleCode(string code) =>
        code is PrtgRuleEvaluator.RuleResourceCpuPressure or PrtgRuleEvaluator.RuleResourceMemoryPressure;

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
