using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingBindingStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-prtg-binding-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;

    public PrtgTrustedSamplingBindingStoreTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        settings = new(backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = "https://source.example";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative; });
        policy = new(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "policy-r1"; value.CoreSystemId = "core";
            value.SourceGeneration = "source-r1";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); value.HostIds = [7]; value.SensorIds = [11];
            value.SourceTimeZoneId = "UTC"; value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC"; value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "time-basis-evidence-r1"; });
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = 11, DeviceObjid = 22, SensorType = "CPU" }], DateTime.UtcNow);
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        backend.PrtgStore().BindObservedResource(11, 7, "source-r1",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
    }

    [Fact]
    public void SavingAnExplicitBindingOnlyCreatesWaitingConfigurationAndFencesPriorAuthority()
    {
        var before = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var store = new PrtgTrustedSamplingBindingStore(backend);
        var saved = store.Save(Update(currentSettings.Revision, currentPolicy.Revision,
            before.Epoch, before.ChannelGeneration, 0));
        var after = backend.PrtgStore().GetResourceIdentity(11);

        Assert.Equal(1, saved.BindingRevision);
        Assert.Empty(saved.QualificationProofReference);
        Assert.Null(saved.QualificationRawValue);
        Assert.Empty(before.ChannelGeneration);
        Assert.NotEmpty(after.ChannelGeneration);
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(after.ChannelGeneration, saved.ChannelGeneration);
        Assert.NotEqual(before.ChannelGeneration, after.ChannelGeneration);
        Assert.Equal(saved.BindingFingerprint, after.ChannelFingerprint);
        Assert.False(new PrtgTrustedSamplingProfileStore(backend).GetMany([11]).ContainsKey(11));
        Assert.Equal(saved.BindingFingerprint, store.Get(11)!.BindingFingerprint);
    }

    [Fact]
    public void StaleSettingsPolicyIdentityChannelAndBindingRevisionsAreRejected()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var store = new PrtgTrustedSamplingBindingStore(backend);
        var first = store.Save(Update(currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0));
        var current = backend.PrtgStore().GetResourceIdentity(11);

        Assert.Throws<InvalidOperationException>(() => store.Save(Update(currentSettings.Revision,
            currentPolicy.Revision, current.Epoch, current.ChannelGeneration, 0)));
        Assert.Throws<InvalidOperationException>(() => store.Save(Update("stale-settings",
            currentPolicy.Revision, current.Epoch, current.ChannelGeneration, first.BindingRevision)));
        Assert.Throws<InvalidOperationException>(() => store.Save(Update(currentSettings.Revision,
            "stale-policy", current.Epoch, current.ChannelGeneration, first.BindingRevision)));
        Assert.Throws<InvalidOperationException>(() => store.Save(Update(currentSettings.Revision,
            currentPolicy.Revision, current.Epoch + 1, current.ChannelGeneration, first.BindingRevision)));
        Assert.Throws<InvalidOperationException>(() => store.Save(Update(currentSettings.Revision,
            currentPolicy.Revision, current.Epoch, "stale-channel", first.BindingRevision)));
    }

    [Fact]
    public void ChannelObjectIdsCanonicalizeLeadingZerosAndRejectInt64Overflow()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var store = new PrtgTrustedSamplingBindingStore(backend);
        var saved = store.Save(Update(currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0) with { ChannelObjectId = "007" });
        Assert.Equal("7", saved.ChannelObjectId);

        var after = backend.PrtgStore().GetResourceIdentity(11);
        Assert.Throws<ArgumentException>(() => store.Save(Update(currentSettings.Revision, currentPolicy.Revision,
            after.Epoch, after.ChannelGeneration, saved.BindingRevision) with
            { ChannelObjectId = "99999999999999999999" }));
        Assert.Equal("7", store.Get(11)!.ChannelObjectId);
    }

    [Fact]
    public void ExplicitCaptionWhitespaceIsPreservedAsBindingIdentity()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var saved = new PrtgTrustedSamplingBindingStore(backend).Save(Update(currentSettings.Revision,
            currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration, 0) with
            { ExpectedCaption = " Load " });

        Assert.Equal(" Load ", saved.ExpectedCaption);
        Assert.Equal(" Load ", new PrtgTrustedSamplingBindingStore(backend).Get(11)!.ExpectedCaption);
    }

    [Fact]
    public void QualificationRejectsForgedOrStaleProofFence()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var first = new PrtgTrustedSamplingBindingStore(backend).Save(Update(settings.Get().Revision,
            policy.Get().Revision, identity.Epoch, identity.ChannelGeneration, 0));
        var store = new PrtgTrustedSamplingBindingStore(backend);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.RecordQualification(11, first.BindingRevision,
            first.BindingFingerprint, currentSettings.Revision, currentPolicy.Revision,
            91, DateTime.UtcNow.ToOADate(), "unknown-source-version",
            DateTimeOffset.UtcNow, "not-a-proof-hash"));
        Assert.Throws<InvalidOperationException>(() => store.RecordQualification(11, first.BindingRevision,
            new string('A', 64), currentSettings.Revision, currentPolicy.Revision,
            91, DateTime.UtcNow.ToOADate(), "source-r1", DateTimeOffset.UtcNow,
            new string('B', 64)));
        Assert.Empty(store.Get(11)!.QualificationProofReference);
    }

    [Fact]
    public void UnrelatedSettingsAndScopeRevisionsDoNotChangeStableProfileAuthorityOnRefresh()
    {
        var store = backend.PrtgStore();
        var identity = store.GetResourceIdentity(11);
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        var saved = bindingStore.Save(Update(settings.Get().Revision, policy.Get().Revision,
            identity.Epoch, identity.ChannelGeneration, 0));
        var now = DateTime.UtcNow;
        var qualified = bindingStore.RecordQualification(11, saved.BindingRevision,
            saved.BindingFingerprint, settings.Get().Revision, policy.Get().Revision,
            91, now.ToOADate(), "source-r1", new DateTimeOffset(now), new string('B', 64));
        identity = store.GetResourceIdentity(11);
        var profileStore = new PrtgTrustedSamplingProfileStore(backend);
        profileStore.RecordProbeResult(CreateProfile(qualified, identity, new DateTimeOffset(now)));
        var stableBefore = store.ReadResourceAuthorityRevision(7);
        var observationBefore = store.ReadResourceObservationRevisions([7])[7];

        settings.Update(value => value.AiModel += "-unrelated-change");
        policy.Update(value =>
        {
            value.Revision = "policy-r2";
            value.SensorIds.Add(12);
            value.HostIds.Add(8);
        });
        var refreshAt = DateTimeOffset.UtcNow;
        profileStore.RecordProbeResult(CreateProfile(qualified, identity, refreshAt));

        Assert.Equal(stableBefore, store.ReadResourceAuthorityRevision(7));
        Assert.True(store.ReadResourceObservationRevisions([7])[7] > observationBefore);
        Assert.Throws<InvalidOperationException>(() => profileStore.RecordProbeResult(
            CreateProfile(qualified, identity, refreshAt.AddSeconds(1), scale: 0.5)));

        policy.Update(value =>
        {
            value.Revision = "policy-r3";
            value.SourceGeneration = "source-r2";
        });
        Assert.Throws<InvalidOperationException>(() => profileStore.RecordProbeResult(
            CreateProfile(qualified, identity, refreshAt.AddSeconds(2))));
    }

    private PrtgTrustedSamplingProfile CreateProfile(PrtgTrustedSamplingBinding binding,
        PrtgResourceIdentity identity, DateTimeOffset observedAt, double scale = 1)
    {
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var strategyName = PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy);
        var strategyProfile = PrtgFetchStrategy.Profile(strategyName);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(currentPolicy,
            strategyName, strategyProfile.SnapshotIntervalMinutes, observedAt.UtcDateTime.AddDays(-2));
        return PrtgTrustedSamplingProfile.FromProbe(11, identity, "CPU", binding.ChannelObjectId,
            binding.ExpectedCaption, binding.Quantity, binding.Unit, scale, binding.Direction,
            "operator-explicit-v1", strategy.StrategyFingerprint, strategy.StrategyMinutes,
            strategy.EffectiveFromHourUtc, TimeSpan.FromSeconds(60), binding.IntervalRawUnit,
            binding.RawTimestampTimeZoneId, currentPolicy.SourceTimeZoneId,
            binding.AnalysisTimeZoneId, observedAt, "synthetic-source-metadata-reference",
            "synthetic-physical-sample-reference", false, 91, 91,
            observedAt.UtcDateTime.ToOADate(), observedAt.UtcDateTime.ToOADate(),
            PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, binding.BindingRevision,
            binding.BindingFingerprint, binding.ChannelObjectId, binding.QualificationProofReference,
            currentSettings.Revision, currentPolicy.Revision,
            PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(currentPolicy,
                strategyName, strategyProfile.SnapshotIntervalMinutes));
    }

    private static PrtgTrustedSamplingBindingUpdate Update(string settingsRevision, string policyRevision,
        long identityEpoch, string channelGeneration, long bindingRevision) => new(11, settingsRevision,
        policyRevision, identityEpoch, channelGeneration, bindingRevision, "3", "Load",
        PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds", "UTC", "UTC",
        "time-basis-evidence-r1");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
