using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Persistence;

public sealed record PrtgTrustedSamplingBindingUpdate(
    long SensorObjid,
    string ExpectedSettingsRevision,
    string ExpectedPolicyRevision,
    long ExpectedIdentityEpoch,
    string ExpectedChannelGeneration,
    long ExpectedBindingRevision,
    string ChannelObjectId,
    string ExpectedCaption,
    PrtgTrustedQuantitySemantic Quantity,
    string Unit,
    double Scale,
    string Direction,
    string IntervalRawUnit,
    string RawTimestampTimeZoneId,
    string AnalysisTimeZoneId,
    string TimeBasisEvidenceReference);

/// <summary>Stores operator-authored per-sensor authority separately from source probe evidence.</summary>
public sealed class PrtgTrustedSamplingBindingStore(StorageBackend backend)
{
    private const int MaximumSensorsPerBatch = 100;

    public PrtgTrustedSamplingBinding? Get(long sensorId)
    {
        if (sensorId <= 0) throw new ArgumentOutOfRangeException(nameof(sensorId));
        var blob = backend.Blob(Key(sensorId));
        var (json, _, length) = blob.ReadBoundedWithVersion(PrtgTrustedSamplingBinding.MaximumSerializedBytes);
        if (length > PrtgTrustedSamplingBinding.MaximumSerializedBytes || json is not null &&
            Encoding.UTF8.GetByteCount(json) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
            throw new InvalidDataException("PRTG 採樣 binding 超過 8 KiB；拒絕使用截斷值。");
        if (string.IsNullOrWhiteSpace(json)) return null;
        var binding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(json, LfJsonOptions.Pretty)
            ?? throw new InvalidDataException("PRTG 採樣 binding 無效；拒絕使用舊授權。");
        if (binding.SensorObjid != sensorId) throw new InvalidDataException("PRTG 採樣 binding sensor ID 不符。");
        binding.Validate();
        return binding;
    }

    public IReadOnlyDictionary<long, PrtgTrustedSamplingBinding> GetMany(IEnumerable<long> sensorIds)
    {
        var ids = sensorIds.Where(id => id > 0).Distinct().Order().Take(MaximumSensorsPerBatch + 1).ToArray();
        if (ids.Length > MaximumSensorsPerBatch)
            throw new ArgumentOutOfRangeException(nameof(sensorIds), "每頁最多讀取 100 個 sampling binding。");
        if (ids.Length == 0) return new Dictionary<long, PrtgTrustedSamplingBinding>();
        var keys = ids.Select(Key).ToArray();
        using var context = backend.CreateContext();
        var rows = context.Blobs.AsNoTracking().Where(row => keys.Contains(row.BlobKey))
            .Select(row => new { row.BlobKey, Content = row.Content.Substring(0,
                PrtgTrustedSamplingBinding.MaximumSerializedBytes + 1), Length = row.Content.Length })
            .ToDictionary(row => row.BlobKey, row => row);
        var result = new Dictionary<long, PrtgTrustedSamplingBinding>();
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(Key(id), out var row)) continue;
            if (row.Length > PrtgTrustedSamplingBinding.MaximumSerializedBytes ||
                Encoding.UTF8.GetByteCount(row.Content) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
                throw new InvalidDataException("PRTG 採樣 binding 超過 8 KiB；拒絕使用截斷值。");
            var binding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(row.Content, LfJsonOptions.Pretty)
                ?? throw new InvalidDataException("PRTG 採樣 binding 無效；拒絕使用舊授權。");
            if (binding.SensorObjid != id) throw new InvalidDataException("PRTG 採樣 binding sensor ID 不符。");
            binding.Validate();
            result.Add(id, binding);
        }
        return result;
    }

    /// <summary>Compare-and-swap settings, policy, resource, channel, and binding revisions in one transaction.</summary>
    public PrtgTrustedSamplingBinding Save(PrtgTrustedSamplingBindingUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.SensorObjid <= 0 || update.ExpectedIdentityEpoch <= 0 || update.ExpectedBindingRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(update));

        var blob = backend.Blob(Key(update.SensorObjid));
        return blob.MutateWithContext((ctx, raw) =>
        {
            var settingsRaw = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == "system_settings")
                .Select(row => row.Content).SingleOrDefault();
            var policyRaw = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == PrtgMonitoringPolicyStore.BlobKey)
                .Select(row => row.Content).SingleOrDefault();
            var settings = string.IsNullOrWhiteSpace(settingsRaw)
                ? new SystemSettings()
                : JsonSerializer.Deserialize<SystemSettings>(settingsRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("System settings invalid.");
            var policy = string.IsNullOrWhiteSpace(policyRaw)
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG policy invalid.");
            if (!string.Equals(settings.Revision, update.ExpectedSettingsRevision, StringComparison.Ordinal) ||
                !string.Equals(policy.Revision, update.ExpectedPolicyRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("settings-or-policy-revision-changed");
            if (!policy.Ready(settings.PrtgUrl) || !policy.SensorIds.Contains(update.SensorObjid))
                throw new InvalidOperationException("binding-sensor-outside-ready-policy");
            if (!settings.PrtgEnabled)
                throw new InvalidOperationException("binding-requires-enabled-source");

            PrtgTrustedSamplingBinding? prior = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (Encoding.UTF8.GetByteCount(raw) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
                    throw new InvalidDataException("Existing sampling binding exceeds 8 KiB.");
                prior = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(raw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("Existing sampling binding is empty.");
                prior.Validate();
                if (prior.SensorObjid != update.SensorObjid) throw new InvalidDataException("Binding key mismatch.");
            }
            if ((prior?.BindingRevision ?? 0) != update.ExpectedBindingRevision)
                throw new InvalidOperationException("binding-revision-changed");

            var identity = PrtgResourceIdentityStore.Read(ctx, update.SensorObjid);
            if (!identity.Active || identity.PendingReconciliation || identity.Epoch != update.ExpectedIdentityEpoch ||
                identity.ChannelGeneration != update.ExpectedChannelGeneration ||
                identity.SourceGeneration != policy.SourceGeneration || !policy.HostIds.Contains(identity.HostId))
                throw new InvalidOperationException("resource-or-channel-fence-changed");

            // A newly observed resource has no channel generation until its first explicit
            // binding installs a fingerprint. The empty value is still an exact CAS fence.
            if (identity.ChannelGeneration.Length == 0 &&
                (prior is not null || identity.ChannelFingerprint.Length != 0))
                throw new InvalidOperationException("unbound-channel-fence-invalid");
            var pendingChannelGeneration = identity.ChannelGeneration.Length == 0
                ? "unbound-initial-channel" : identity.ChannelGeneration;
            var nextRevision = checked((prior?.BindingRevision ?? 0) + 1);
            var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
            var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
            var authorityContextFingerprint = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                policy, strategyName, strategyMinutes);
            var candidate = PrtgTrustedSamplingBinding.Create(update.SensorObjid, update.ChannelObjectId,
                update.ExpectedCaption, update.Quantity, update.Unit, update.Scale, update.Direction,
                update.IntervalRawUnit, update.RawTimestampTimeZoneId, update.AnalysisTimeZoneId,
                update.TimeBasisEvidenceReference, settings.Revision, policy.Revision,
                authorityContextFingerprint,
                identity.SourceGeneration, identity.Generation, identity.Epoch, pendingChannelGeneration,
                nextRevision);
            if (prior is not null && prior.SemanticFingerprint == candidate.SemanticFingerprint &&
                prior.AuthorityContextFingerprint == candidate.AuthorityContextFingerprint &&
                prior.SourceGeneration == candidate.SourceGeneration && prior.ResourceGeneration == candidate.ResourceGeneration &&
                prior.IdentityEpoch == candidate.IdentityEpoch && prior.ChannelGeneration == candidate.ChannelGeneration)
                return (raw!, prior);

            var now = DateTimeOffset.UtcNow;
            var nextIdentity = PrtgResourceIdentityStore.Set(ctx, identity.SensorId, identity.SourceGeneration,
                identity.DeviceId, identity.HostId, identity.ResourceFingerprint, identity.InventoryFingerprint,
                candidate.BindingFingerprint, true, now, reconciled: true);
            if (!nextIdentity.Active || nextIdentity.PendingReconciliation ||
                nextIdentity.SensorId != identity.SensorId || nextIdentity.Epoch != identity.Epoch ||
                nextIdentity.Generation != identity.Generation || nextIdentity.SourceGeneration != identity.SourceGeneration ||
                nextIdentity.DeviceId != identity.DeviceId || nextIdentity.HostId != identity.HostId ||
                nextIdentity.ResourceFingerprint != identity.ResourceFingerprint ||
                nextIdentity.InventoryFingerprint != identity.InventoryFingerprint ||
                string.IsNullOrWhiteSpace(nextIdentity.ChannelGeneration) ||
                nextIdentity.ChannelFingerprint != candidate.BindingFingerprint)
                throw new InvalidOperationException("binding-install-identity-fence-changed");
            candidate = PrtgTrustedSamplingBinding.Create(update.SensorObjid, update.ChannelObjectId,
                update.ExpectedCaption, update.Quantity, update.Unit, update.Scale, update.Direction,
                update.IntervalRawUnit, update.RawTimestampTimeZoneId, update.AnalysisTimeZoneId,
                update.TimeBasisEvidenceReference, settings.Revision, policy.Revision,
                authorityContextFingerprint,
                nextIdentity.SourceGeneration, nextIdentity.Generation, nextIdentity.Epoch,
                nextIdentity.ChannelGeneration, nextRevision);
            if (candidate.BindingFingerprint != nextIdentity.ChannelFingerprint)
                throw new InvalidOperationException("binding-install-fingerprint-mismatch");

            var profileKey = PrtgTrustedSamplingProfile.StorePrefix + update.SensorObjid.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var profileRow = ctx.Blobs.SingleOrDefault(row => row.BlobKey == profileKey);
            if (profileRow is not null) ctx.Blobs.Remove(profileRow);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, identity.HostId, now);
            return (JsonSerializer.Serialize(candidate, LfJsonOptions.Pretty), candidate);
        }, maxCurrentCharacters: PrtgTrustedSamplingBinding.MaximumSerializedBytes,
        isolationLevel: System.Data.IsolationLevel.Serializable);
    }

    /// <summary>Publish one raw XML qualification proof only while the saved binding and fences remain current.</summary>
    internal PrtgTrustedSamplingBinding RecordQualification(long sensorId, long expectedBindingRevision,
        string expectedBindingFingerprint, string expectedSettingsRevision, string expectedPolicyRevision,
        double rawValue, double measuredOaDate,
        string sourceVersion, DateTimeOffset observedAtUtc, string proofReference,
        PrtgQualificationWriteFence? qualificationFence = null)
    {
        if (sensorId <= 0 || expectedBindingRevision <= 0 || !Hex64(expectedBindingFingerprint) ||
            !double.IsFinite(rawValue) || !Hex64(proofReference) ||
            string.IsNullOrWhiteSpace(sourceVersion) || sourceVersion.Length > 128 ||
            observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero || observedAtUtc > DateTimeOffset.UtcNow ||
            measuredOaDate is < -657435 or >= 2958466 || !double.IsFinite(measuredOaDate))
            throw new ArgumentOutOfRangeException(nameof(sensorId));
        var blob = backend.Blob(Key(sensorId));
        return blob.MutateWithContext((ctx, raw) =>
        {
            if (string.IsNullOrWhiteSpace(raw) || Encoding.UTF8.GetByteCount(raw) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
                throw new InvalidOperationException("management-binding-missing");
            var prior = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(raw, LfJsonOptions.Pretty)
                ?? throw new InvalidDataException("Existing sampling binding is empty.");
            prior.Validate();
            if (prior.SensorObjid != sensorId || prior.BindingRevision != expectedBindingRevision ||
                prior.BindingFingerprint != expectedBindingFingerprint)
                throw new InvalidOperationException("binding-revision-changed");
            var rawZone = TimeZoneInfo.FindSystemTimeZoneById(prior.RawTimestampTimeZoneId);
            var sampleWall = DateTime.SpecifyKind(DateTime.FromOADate(measuredOaDate), DateTimeKind.Unspecified);
            if (rawZone.IsAmbiguousTime(sampleWall) || rawZone.IsInvalidTime(sampleWall))
                throw new InvalidDataException("qualification-measurement-time-ambiguous");
            var sampleUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(sampleWall, rawZone));
            if (sampleUtc > observedAtUtc || observedAtUtc - sampleUtc > TimeSpan.FromHours(1))
                throw new InvalidDataException("qualification-measurement-time-out-of-range");

            var settingsRaw = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == "system_settings")
                .Select(row => row.Content).SingleOrDefault();
            var policyRaw = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == PrtgMonitoringPolicyStore.BlobKey)
                .Select(row => row.Content).SingleOrDefault();
            var settings = string.IsNullOrWhiteSpace(settingsRaw) ? new SystemSettings() :
                JsonSerializer.Deserialize<SystemSettings>(settingsRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("System settings invalid.");
            var policy = string.IsNullOrWhiteSpace(policyRaw) ? new PrtgMonitoringPolicy() :
                JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("PRTG policy invalid.");
            var identity = PrtgResourceIdentityStore.Read(ctx, sensorId);
            if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) || !policy.SensorIds.Contains(sensorId) ||
                !policy.HostIds.Contains(identity.HostId) ||
                settings.Revision != expectedSettingsRevision || policy.Revision != expectedPolicyRevision ||
                prior.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
                prior.AnalysisTimeZoneId != policy.AnalysisTimeZoneId ||
                prior.TimeBasisEvidenceReference != policy.TimeBasisEvidenceReference ||
                prior.AuthorityContextFingerprint != PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                    policy, PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
                    PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes))
                throw new InvalidOperationException("settings-or-policy-revision-changed");

            if (!identity.Active || identity.PendingReconciliation || identity.Epoch != prior.IdentityEpoch ||
                identity.SourceGeneration != prior.SourceGeneration ||
                identity.Generation != prior.ResourceGeneration || identity.ChannelGeneration != prior.ChannelGeneration ||
                identity.ChannelFingerprint != prior.BindingFingerprint)
                throw new InvalidOperationException("resource-or-channel-fence-changed");

            var qualified = prior.WithQualification(proofReference, rawValue, measuredOaDate,
                sourceVersion, observedAtUtc);
            if (qualificationFence is not null && (qualificationFence.SensorObjid != sensorId ||
                qualificationFence.BindingRevision != expectedBindingRevision ||
                qualificationFence.BindingFingerprint != expectedBindingFingerprint ||
                qualificationFence.SettingsRevision != expectedSettingsRevision ||
                qualificationFence.PolicyRevision != expectedPolicyRevision))
                throw new InvalidOperationException("qualification-job-binding-fence-mismatch");
            var now = DateTimeOffset.UtcNow;
            var nextIdentity = PrtgResourceIdentityStore.Set(ctx, sensorId, identity.SourceGeneration,
                identity.DeviceId, identity.HostId, identity.ResourceFingerprint, identity.InventoryFingerprint,
                qualified.BindingFingerprint, true, now, reconciled: true);
            qualified = qualified with { IdentityEpoch = nextIdentity.Epoch,
                ResourceGeneration = nextIdentity.Generation, ChannelGeneration = nextIdentity.ChannelGeneration };
            qualified = qualified.RefreshBindingFingerprint();
            qualified.Validate();
            if (qualificationFence is not null)
                new PrtgQualificationJobStateStore(backend).RecordQualificationInContext(
                    ctx, qualificationFence, qualified, now);
            PrtgResourceIdentityStore.IncrementAuthorityRevision(ctx, identity.HostId, now);
            return (JsonSerializer.Serialize(qualified, LfJsonOptions.Pretty), qualified);
        }, maxCurrentCharacters: PrtgTrustedSamplingBinding.MaximumSerializedBytes,
        isolationLevel: System.Data.IsolationLevel.Serializable,
        serializeSqlServerWriterKey: qualificationFence is null ? null : PrtgHistoricAdmissionStore.BlobKey);
    }

    private static string Key(long sensorId) => PrtgTrustedSamplingBinding.StorePrefix +
        sensorId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool Hex64(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
