using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// Test-only closure for historical fixtures that seed profiles directly. Publish exercises the
/// actual binding CAS; PublishEfFixture installs a complete synthetic binding, qualification and
/// profile across the SQL blob rows consumed by the strict persistence guard. Neither path is
/// native-source evidence or a production fixture.
/// </summary>
internal static class PrtgConsumerProfileFixtureClosure
{
    /// <summary>
    /// Creates the exact synthetic SQL facts required by RecordTrustedSamplingProfile: current
    /// enabled settings and ready policy, current active identity with matching channel fingerprint,
    /// a valid qualified binding row, then the explicit profile row. It does not alter production
    /// APIs or represent native-source evidence.
    /// </summary>
    public static PrtgTrustedSamplingProfile PublishEfFixture(EfPrtgStore store,
        EfJsonBlobStore policyBlob, string settingsRevision, string strategyName,
        PrtgTrustedSamplingProfile sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policyBlob);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        var normalizedStrategy = PrtgFetchStrategy.Normalize(strategyName);
        var strategyMinutes = PrtgFetchStrategy.Profile(normalizedStrategy).SnapshotIntervalMinutes;
        var seed = policyBlob.MutateWithContext((ctx, policyRaw) =>
        {
            var policy = string.IsNullOrWhiteSpace(policyRaw)
                ? new PrtgMonitoringPolicy()
                : JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policyRaw, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("Synthetic fixture policy row is invalid.");
            var identity = PrtgResourceIdentityStore.Read(ctx, sourceProfile.SensorObjid);
            if (!identity.Active || identity.PendingReconciliation || identity.Epoch <= 0 ||
                string.IsNullOrWhiteSpace(identity.SourceGeneration) || string.IsNullOrWhiteSpace(identity.Generation) ||
                string.IsNullOrWhiteSpace(identity.ChannelGeneration) || identity.HostId <= 0)
                throw new InvalidOperationException("Synthetic SQL closure requires an active current resource identity.");

            var settingsRow = ctx.Blobs.SingleOrDefault(row => row.BlobKey == "system_settings");
            var settings = settingsRow is null || string.IsNullOrWhiteSpace(settingsRow.Content)
                ? new SystemSettings()
                : JsonSerializer.Deserialize<SystemSettings>(settingsRow.Content, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("Synthetic fixture system settings row is invalid.");
            settings.PrtgEnabled = true;
            if (string.IsNullOrWhiteSpace(settings.PrtgUrl)) settings.PrtgUrl = "https://fixture.invalid";
            settings.PrtgFetchStrategy = normalizedStrategy;
            settings.Revision = settingsRevision;

            policy.Revision = string.IsNullOrWhiteSpace(policy.Revision) ? "synthetic-policy-revision" : policy.Revision;
            policy.CoreSystemId = string.IsNullOrWhiteSpace(policy.CoreSystemId) ? "synthetic-consumer-fixture" : policy.CoreSystemId;
            policy.SourceGeneration = identity.SourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            if (policy.ValidFrom == default) policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            if (!policy.HostIds.Contains(identity.HostId)) policy.HostIds.Add(identity.HostId);
            if (!policy.SensorIds.Contains(sourceProfile.SensorObjid)) policy.SensorIds.Add(sourceProfile.SensorObjid);
            policy.SourceTimeZoneId = sourceProfile.SourceApiTimeZoneId;
            policy.SourceCultureName = string.IsNullOrWhiteSpace(policy.SourceCultureName) ? "en-US" : policy.SourceCultureName;
            policy.RawTimestampTimeZoneId = sourceProfile.RawTimestampTimeZoneId;
            policy.AnalysisTimeZoneId = sourceProfile.AnalysisTimeZoneId;
            if (string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference))
                policy.TimeBasisEvidenceReference = "synthetic-time-basis-evidence";

            var channelId = long.TryParse(sourceProfile.PrimaryChannelId, NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsedChannelId) && parsedChannelId >= 0
                ? parsedChannelId.ToString(CultureInfo.InvariantCulture)
                : sourceProfile.SensorObjid.ToString(CultureInfo.InvariantCulture);
            var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
                policy, normalizedStrategy, strategyMinutes);
            var bindingStoreKey = PrtgTrustedSamplingBinding.StorePrefix +
                sourceProfile.SensorObjid.ToString(CultureInfo.InvariantCulture);
            var priorBindingRow = ctx.Blobs.SingleOrDefault(row => row.BlobKey == bindingStoreKey);
            PrtgTrustedSamplingBinding? priorBinding = null;
            if (priorBindingRow is not null && !string.IsNullOrWhiteSpace(priorBindingRow.Content))
            {
                priorBinding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(
                    priorBindingRow.Content, LfJsonOptions.Pretty)
                    ?? throw new InvalidDataException("Existing synthetic fixture binding is invalid.");
                priorBinding.Validate();
            }
            var bindingRevision = priorBinding?.BindingRevision ?? 0;
            var candidate = PrtgTrustedSamplingBinding.Create(sourceProfile.SensorObjid, channelId,
                sourceProfile.PrimaryChannelCaption, sourceProfile.Quantity, sourceProfile.Unit,
                sourceProfile.Scale, sourceProfile.Direction, sourceProfile.IntervalRawUnit,
                policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId, policy.TimeBasisEvidenceReference,
                settings.Revision, policy.Revision, authorityContext, identity.SourceGeneration,
                identity.Generation, identity.Epoch, identity.ChannelGeneration, bindingRevision > 0 ? bindingRevision : 1);

            var canPreserveQualification = priorBinding is not null &&
                !string.IsNullOrWhiteSpace(priorBinding.QualificationProofReference) &&
                identity.ChannelFingerprint == priorBinding.BindingFingerprint &&
                priorBinding.Matches(sourceProfile.SensorObjid, authorityContext, identity.SourceGeneration,
                    identity.Generation, identity.Epoch, identity.ChannelGeneration) &&
                priorBinding.SemanticFingerprint == candidate.SemanticFingerprint &&
                priorBinding.AuthorityContextFingerprint == candidate.AuthorityContextFingerprint;
            PrtgTrustedSamplingBinding binding;
            PrtgResourceIdentity updatedIdentity;
            if (canPreserveQualification)
            {
                // Match BindingStore.Save idempotency: metadata refresh retains the binding revision,
                // qualification proof, and channel generation. CAS revisions are refreshed so the
                // SQL profile publication guard sees the current settings/policy snapshot.
                binding = priorBinding!;
                if (binding.SettingsRevision != settings.Revision || binding.PolicyRevision != policy.Revision)
                    binding = binding with
                    {
                        SettingsRevision = settings.Revision,
                        PolicyRevision = policy.Revision
                    };
                binding.Validate();
                updatedIdentity = identity;
            }
            else
            {
                var nextRevision = checked((priorBinding?.BindingRevision ?? 0) + 1);
                candidate = PrtgTrustedSamplingBinding.Create(sourceProfile.SensorObjid, channelId,
                    sourceProfile.PrimaryChannelCaption, sourceProfile.Quantity, sourceProfile.Unit,
                    sourceProfile.Scale, sourceProfile.Direction, sourceProfile.IntervalRawUnit,
                    policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId, policy.TimeBasisEvidenceReference,
                    settings.Revision, policy.Revision, authorityContext, identity.SourceGeneration,
                    identity.Generation, identity.Epoch, identity.ChannelGeneration, nextRevision);
                var qualificationObserved = DateTimeOffset.UtcNow;
                var proofReference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    "synthetic-ef-binding-proof\n" + sourceProfile.SensorObjid.ToString(CultureInfo.InvariantCulture) +
                    "\n" + candidate.BindingFingerprint)));
                var qualificationMeasured = qualificationObserved.AddSeconds(-30).UtcDateTime.ToOADate();
                binding = candidate.WithQualification(proofReference, sourceProfile.ComparedPrimaryChannelValue,
                    qualificationMeasured, "synthetic-test-only", qualificationObserved);

                updatedIdentity = PrtgResourceIdentityStore.Set(ctx, identity.SensorId,
                    identity.SourceGeneration, identity.DeviceId, identity.HostId, identity.ResourceFingerprint,
                    identity.InventoryFingerprint, binding.BindingFingerprint, true, DateTimeOffset.UtcNow,
                    reconciled: true);
                binding = binding with { IdentityEpoch = updatedIdentity.Epoch,
                    ResourceGeneration = updatedIdentity.Generation, ChannelGeneration = updatedIdentity.ChannelGeneration };
                binding.Validate();
            }

            var bindingRow = ctx.Blobs.SingleOrDefault(row => row.BlobKey == binding.StoreKey);
            var bindingJson = JsonSerializer.Serialize(binding, LfJsonOptions.Pretty);
            if (bindingRow is null)
                ctx.Blobs.Add(new BlobRow { BlobKey = binding.StoreKey, Content = bindingJson,
                    Version = 1, UpdatedAt = DateTime.Now });
            else if (!StringComparer.Ordinal.Equals(bindingRow.Content, bindingJson))
            {
                bindingRow.Content = bindingJson;
                bindingRow.Version++;
                bindingRow.UpdatedAt = DateTime.Now;
            }

            var settingsJson = JsonSerializer.Serialize(settings, LfJsonOptions.Pretty);
            if (settingsRow is null)
                ctx.Blobs.Add(new BlobRow { BlobKey = "system_settings", Content = settingsJson,
                    Version = 1, UpdatedAt = DateTime.Now });
            else
            {
                settingsRow.Content = settingsJson;
                settingsRow.Version++;
                settingsRow.UpdatedAt = DateTime.Now;
            }

            var result = new EfFixtureSeed(updatedIdentity, binding, policy, settings);
            return (JsonSerializer.Serialize(policy, LfJsonOptions.Pretty), result);
        });

        var profile = PrtgTrustedSamplingProfile.FromProbe(sourceProfile.SensorObjid, seed.Identity,
            sourceProfile.SensorType, seed.Binding.ChannelObjectId, sourceProfile.PrimaryChannelCaption,
            sourceProfile.Quantity, sourceProfile.Unit, sourceProfile.Scale, sourceProfile.Direction,
            sourceProfile.SemanticVersion,
            PrtgTrustedSamplingProfileResolver.StrategyFingerprint(seed.Policy.SourceGeneration,
                seed.Policy.RawTimestampTimeZoneId, seed.Policy.SourceTimeZoneId,
                seed.Policy.AnalysisTimeZoneId, normalizedStrategy, strategyMinutes),
            strategyMinutes, sourceProfile.StrategyEffectiveFromHourUtc, sourceProfile.ConfirmedScanInterval,
            seed.Binding.IntervalRawUnit, seed.Binding.RawTimestampTimeZoneId, seed.Policy.SourceTimeZoneId,
            seed.Binding.AnalysisTimeZoneId, sourceProfile.SourceMetadataObservedAtUtc,
            sourceProfile.SourceMetadataReference, sourceProfile.PhysicalSampleReference, false,
            sourceProfile.ComparedSnapshotValue, sourceProfile.ComparedPrimaryChannelValue,
            sourceProfile.ComparedSnapshotMeasurementOaDate, sourceProfile.ComparedPrimaryChannelMeasurementOaDate,
            PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, seed.Binding.BindingRevision,
            seed.Binding.BindingFingerprint, seed.Binding.ChannelObjectId,
            seed.Binding.QualificationProofReference, seed.Binding.SettingsRevision,
            seed.Binding.PolicyRevision, seed.Binding.AuthorityContextFingerprint);
        store.RecordTrustedSamplingProfile(profile);
        return profile;
    }

    /// <summary>Returns true only when a valid, qualified synthetic binding still owns this identity's channel fence.</summary>
    public static bool HasCurrentQualifiedBinding(EfJsonBlobStore policyBlob, PrtgResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(policyBlob);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.SensorId <= 0 || !identity.Active || identity.PendingReconciliation ||
            identity.Epoch <= 0 || identity.ChannelFingerprint is not { Length: 64 } ||
            !identity.ChannelFingerprint.All(Uri.IsHexDigit)) return false;
        return policyBlob.MutateWithContext((ctx, currentPolicy) =>
        {
            if (string.IsNullOrWhiteSpace(currentPolicy))
                throw new InvalidOperationException("Synthetic qualified-binding lookup requires an existing policy row.");
            var key = PrtgTrustedSamplingBinding.StorePrefix + identity.SensorId.ToString(CultureInfo.InvariantCulture);
            var content = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey == key)
                .Select(row => row.Content).SingleOrDefault();
            if (string.IsNullOrWhiteSpace(content) || content.Length > PrtgTrustedSamplingBinding.MaximumSerializedBytes ||
                Encoding.UTF8.GetByteCount(content) > PrtgTrustedSamplingBinding.MaximumSerializedBytes)
                return (currentPolicy, false);
            var binding = JsonSerializer.Deserialize<PrtgTrustedSamplingBinding>(content, LfJsonOptions.Pretty);
            if (binding is null) return (currentPolicy, false);
            binding.Validate();
            return (currentPolicy, binding.SensorObjid == identity.SensorId &&
                binding.BindingFingerprint == identity.ChannelFingerprint &&
                !string.IsNullOrWhiteSpace(binding.QualificationProofReference) &&
                binding.Matches(identity.SensorId, binding.AuthorityContextFingerprint,
                    identity.SourceGeneration, identity.Generation, identity.Epoch, identity.ChannelGeneration));
        }, skipUnchangedContent: true);
    }

    private sealed record EfFixtureSeed(PrtgResourceIdentity Identity, PrtgTrustedSamplingBinding Binding,
        PrtgMonitoringPolicy Policy, SystemSettings Settings);

    /// <summary>Builds the explicit model for resolver-only tests that do not own a persistence store.</summary>
    public static PrtgTrustedSamplingProfile CreateProfileOnly(PrtgResourceIdentity identity,
        PrtgMonitoringPolicy policy, string settingsRevision, string strategyName,
        PrtgTrustedSamplingProfile sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        if (identity.SensorId != sourceProfile.SensorObjid || !identity.Active || identity.PendingReconciliation ||
            identity.Epoch <= 0 || identity.SourceGeneration != policy.SourceGeneration ||
            identity.ChannelFingerprint is not { Length: 64 } || !identity.ChannelFingerprint.All(Uri.IsHexDigit) ||
            !policy.SensorIds.Contains(identity.SensorId) || !policy.HostIds.Contains(identity.HostId) ||
            string.IsNullOrWhiteSpace(settingsRevision) || string.IsNullOrWhiteSpace(policy.Revision) ||
            string.IsNullOrWhiteSpace(policy.RawTimestampTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.SourceTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.AnalysisTimeZoneId) ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference))
            throw new InvalidOperationException("Synthetic profile closure requires current identity and complete policy/time-basis scope.");

        // Descriptive IDs in older consumer fixtures are replaced with a legal decimal test ID.
        // This is fixture data only and makes no assertion about native PRTG channel IDs.
        var channelId = long.TryParse(sourceProfile.PrimaryChannelId, NumberStyles.None,
            CultureInfo.InvariantCulture, out var parsedChannelId) && parsedChannelId >= 0
            ? parsedChannelId.ToString(CultureInfo.InvariantCulture)
            : identity.SensorId.ToString(CultureInfo.InvariantCulture);
        var normalizedStrategy = PrtgFetchStrategy.Normalize(strategyName);
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(
            policy, normalizedStrategy, sourceProfile.StrategyMinutes);
        var proofReference = sourceProfile.AuthorityKind == PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind &&
            !string.IsNullOrWhiteSpace(sourceProfile.QualificationProofReference)
            ? sourceProfile.QualificationProofReference
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "synthetic-no-native-qualification\n" + identity.SensorId.ToString(CultureInfo.InvariantCulture) +
                "\n" + identity.ChannelFingerprint + "\n" + channelId)));
        var bindingRevision = sourceProfile.AuthorityKind == PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind &&
            sourceProfile.BindingRevision > 0 ? sourceProfile.BindingRevision : 1;
        var strategyFingerprint = PrtgTrustedSamplingProfileResolver.StrategyFingerprint(
            policy.SourceGeneration, policy.RawTimestampTimeZoneId, policy.SourceTimeZoneId,
            policy.AnalysisTimeZoneId, normalizedStrategy, sourceProfile.StrategyMinutes);
        var binding = PrtgTrustedSamplingBinding.Create(identity.SensorId, channelId,
            sourceProfile.PrimaryChannelCaption, sourceProfile.Quantity, sourceProfile.Unit,
            sourceProfile.Scale, sourceProfile.Direction, sourceProfile.IntervalRawUnit,
            policy.RawTimestampTimeZoneId, policy.AnalysisTimeZoneId,
            policy.TimeBasisEvidenceReference, settingsRevision, policy.Revision,
            authorityContext, identity.SourceGeneration, identity.Generation, identity.Epoch,
            identity.ChannelGeneration, bindingRevision);
        var qualificationMeasuredAt = DateTime.SpecifyKind(
            DateTime.FromOADate(sourceProfile.ComparedPrimaryChannelMeasurementOaDate), DateTimeKind.Utc);
        var qualificationObservedAt = sourceProfile.SourceMetadataObservedAtUtc;
        if (qualificationObservedAt.UtcDateTime <= qualificationMeasuredAt)
            qualificationObservedAt = new DateTimeOffset(qualificationMeasuredAt.AddSeconds(1));
        binding = binding.WithQualification(proofReference, sourceProfile.ComparedPrimaryChannelValue,
            sourceProfile.ComparedPrimaryChannelMeasurementOaDate, "synthetic-fixture-qualification-v1",
            qualificationObservedAt);
        // Resolver-only fixtures still carry a synthetic, semantically real binding fingerprint.
        // Update the same in-memory identity consumed by their caller so the SQL-backed identity
        // and explicit profile continue to represent one exact authority.
        identity.ChannelFingerprint = binding.BindingFingerprint;
        var profile = PrtgTrustedSamplingProfile.FromProbe(identity.SensorId, identity,
            sourceProfile.SensorType, channelId, sourceProfile.PrimaryChannelCaption, sourceProfile.Quantity,
            sourceProfile.Unit, sourceProfile.Scale, sourceProfile.Direction, sourceProfile.SemanticVersion,
            strategyFingerprint, sourceProfile.StrategyMinutes, sourceProfile.StrategyEffectiveFromHourUtc,
            sourceProfile.ConfirmedScanInterval, sourceProfile.IntervalRawUnit,
            policy.RawTimestampTimeZoneId, policy.SourceTimeZoneId, policy.AnalysisTimeZoneId,
            sourceProfile.SourceMetadataObservedAtUtc, sourceProfile.SourceMetadataReference,
            sourceProfile.PhysicalSampleReference, false, sourceProfile.ComparedSnapshotValue,
            sourceProfile.ComparedPrimaryChannelValue, sourceProfile.ComparedSnapshotMeasurementOaDate,
            sourceProfile.ComparedPrimaryChannelMeasurementOaDate,
            PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, bindingRevision,
            binding.BindingFingerprint, channelId, proofReference, settingsRevision, policy.Revision,
            authorityContext);
        return profile;
    }

    public static PrtgTrustedSamplingProfile Publish(StorageBackend backend,
        PrtgTrustedSamplingProfile sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var identity = backend.PrtgStore().GetResourceIdentity(sourceProfile.SensorObjid);
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl) ||
            !policy.SensorIds.Contains(sourceProfile.SensorObjid) || !policy.HostIds.Contains(identity.HostId) ||
            sourceProfile.SourceGeneration != identity.SourceGeneration)
            throw new InvalidOperationException("Synthetic explicit-binding fixture requires an already-ready source scope.");
        if (sourceProfile.RawTimestampTimeZoneId != policy.RawTimestampTimeZoneId ||
            sourceProfile.AnalysisTimeZoneId != policy.AnalysisTimeZoneId ||
            string.IsNullOrWhiteSpace(policy.TimeBasisEvidenceReference))
            throw new InvalidOperationException("Synthetic explicit-binding fixture requires matching time-basis policy.");

        // Older tests used descriptive channel labels as IDs. The closure maps those legacy-only
        // labels to one explicit synthetic numeric ID; no source response or native support is implied.
        var channelId = sourceProfile.PrimaryChannelId.Length > 0 && sourceProfile.PrimaryChannelId.All(char.IsAsciiDigit)
            ? sourceProfile.PrimaryChannelId
            : "1";
        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var prior = bindings.Get(sourceProfile.SensorObjid);
        var expectedBindingRevision = prior?.BindingRevision ?? 0;
        var saved = bindings.Save(new(sourceProfile.SensorObjid, settings.Revision, policy.Revision,
            identity.Epoch, identity.ChannelGeneration, expectedBindingRevision, channelId,
            sourceProfile.PrimaryChannelCaption, sourceProfile.Quantity, sourceProfile.Unit,
            sourceProfile.Scale, sourceProfile.Direction, sourceProfile.IntervalRawUnit,
            policy.RawTimestampTimeZoneId!, policy.AnalysisTimeZoneId!, policy.TimeBasisEvidenceReference!));
        if (string.IsNullOrWhiteSpace(saved.QualificationProofReference))
        {
            var observed = DateTimeOffset.UtcNow;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(saved.RawTimestampTimeZoneId);
            var wallTime = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(observed.AddSeconds(-2), zone).DateTime,
                DateTimeKind.Unspecified);
            while (zone.IsAmbiguousTime(wallTime) || zone.IsInvalidTime(wallTime))
                wallTime = wallTime.AddSeconds(-1);
            var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "synthetic-no-native-acceptance\n" + sourceProfile.SensorObjid.ToString(CultureInfo.InvariantCulture) +
                "\n" + saved.BindingRevision.ToString(CultureInfo.InvariantCulture) + "\n" + saved.BindingFingerprint)));
            saved = bindings.RecordQualification(sourceProfile.SensorObjid, saved.BindingRevision,
                saved.BindingFingerprint, settings.Revision, policy.Revision,
                sourceProfile.ComparedSnapshotValue, wallTime.ToOADate(),
                "synthetic-test-only", observed, proof);
        }

        identity = backend.PrtgStore().GetResourceIdentity(sourceProfile.SensorObjid);
        var qualified = bindings.Get(sourceProfile.SensorObjid)
            ?? throw new InvalidOperationException("Synthetic explicit binding disappeared after qualification.");
        var profile = PrtgTrustedSamplingProfile.FromProbe(sourceProfile.SensorObjid, identity,
            sourceProfile.SensorType, qualified.ChannelObjectId, qualified.ExpectedCaption,
            qualified.Quantity, qualified.Unit, qualified.Scale, qualified.Direction,
            sourceProfile.SemanticVersion, sourceProfile.StrategyFingerprint, sourceProfile.StrategyMinutes,
            sourceProfile.StrategyEffectiveFromHourUtc, sourceProfile.ConfirmedScanInterval,
            qualified.IntervalRawUnit, qualified.RawTimestampTimeZoneId, sourceProfile.SourceApiTimeZoneId,
            qualified.AnalysisTimeZoneId, sourceProfile.SourceMetadataObservedAtUtc,
            sourceProfile.SourceMetadataReference, sourceProfile.PhysicalSampleReference, false,
            sourceProfile.ComparedSnapshotValue, sourceProfile.ComparedPrimaryChannelValue,
            sourceProfile.ComparedSnapshotMeasurementOaDate,
            sourceProfile.ComparedPrimaryChannelMeasurementOaDate,
            PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, qualified.BindingRevision,
            qualified.BindingFingerprint, qualified.ChannelObjectId, qualified.QualificationProofReference,
            qualified.SettingsRevision, qualified.PolicyRevision,
            PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
                PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy),
                PrtgFetchStrategy.Profile(PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy)).SnapshotIntervalMinutes));
        new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);
        return profile;
    }
}
