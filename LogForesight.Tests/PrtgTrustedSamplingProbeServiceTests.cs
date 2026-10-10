using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingProbeServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-trusted-profile-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;

    public PrtgTrustedSamplingProbeServiceTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        settings = new(backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = "https://source.example";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "local-fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative; });
        policy = new(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value => { value.Revision = "r1"; value.CoreSystemId = "core"; value.SourceGeneration = "source";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.Get().PrtgUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); value.HostIds = [7]; value.SensorIds = [11];
            value.SourceTimeZoneId = "UTC"; value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC"; value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "fixture-source-time-basis"; });
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = 11, DeviceObjid = 22, SensorType = "CPU" }], DateTime.UtcNow);
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        backend.PrtgStore().BindObservedResource(11, 7, "source",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
    }

    [Fact]
    public async Task LegacySyntheticPrimaryMarkerDoesNotCreateAProfileWithoutExplicitBinding()
    {
        var row = Assert.Single(await Service("complete").ProbeAsync([11], CancellationToken.None));
        Assert.False(row.ProfileRecorded);
        Assert.Equal("waiting", row.Status);
        Assert.Contains("management:binding_missing", row.MissingAuthorityFields);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task CompleteProbeFieldsWithoutExplicitManagementBindingStayWaiting()
    {
        // This synthetic handler deliberately emits the legacy fictional authority fields. It is
        // a negative contract test only: it does not claim those fields exist in a native PRTG
        // response. The replacement flow must require a separately saved per-resource binding.
        var row = Assert.Single(await Service("complete").ProbeAsync([11], CancellationToken.None));

        Assert.False(row.ProfileRecorded);
        Assert.Equal("waiting", row.Status);
        Assert.Contains("management:binding_missing", row.MissingAuthorityFields);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task SavedBindingUnknownNativePropertyAndResolverSeamRemainWaiting()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var binding = new PrtgTrustedSamplingBindingStore(backend).Save(new(11,
            currentSettings.Revision, currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration,
            0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));

        var row = Assert.Single(await Service("complete").ProbeAsync([11], CancellationToken.None));
        Assert.Equal(1, binding.BindingRevision);
        Assert.Equal("waiting", row.Status);
        Assert.False(row.ProfileRecorded);
        Assert.Contains("source:primarychannel_property_shape_unrecognized", row.MissingAuthorityFields);
        Assert.Contains("qualification:raw_channel_id_time_proof_required", row.MissingAuthorityFields);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));

        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(
            policy.Get(), PrtgFetchStrategy.Conservative, 15, DateTime.UtcNow);
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(null,
            backend.PrtgStore().GetResourceIdentity(11), policy.Get(), 11, "CPU",
            strategy, DateTime.UtcNow, DateTime.UtcNow);
        Assert.False(resolution.Ready);
        Assert.Equal("source_authority_incomplete", resolution.RejectionReason);
    }

    [Fact]
    public async Task SavedCaptionIsComparedExactlyAndNativeWhitespaceIsNotTrimmed()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        new PrtgTrustedSamplingBindingStore(backend).Save(new(11,
            settings.Get().Revision, policy.Get().Revision, identity.Epoch, identity.ChannelGeneration,
            0, "3", " Load ", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));

        var row = Assert.Single(await Service("complete").ProbeAsync([11], CancellationToken.None));
        Assert.False(row.ProfileRecorded);
        Assert.Contains("channels:bound_id_caption_mismatch_or_ambiguous", row.MissingAuthorityFields);
        Assert.Equal("Load", Assert.Single(row.Channels).Caption);
    }

    [Theory]
    [InlineData("primary-nested-result")]
    [InlineData("primary-namespaced-result")]
    [InlineData("primary-duplicate-result")]
    [InlineData("primary-result-alias")]
    [InlineData("primary-depth-over-limit")]
    public async Task SavedBindingRejectsUnprovenPrimaryPropertyXmlShapes(string scenario)
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        new PrtgTrustedSamplingBindingStore(backend).Save(new(11,
            currentSettings.Revision, currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration,
            0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));

        var row = Assert.Single(await Service(scenario).ProbeAsync([11], CancellationToken.None));

        Assert.False(row.ProfileRecorded);
        Assert.Equal("waiting", row.Status);
        Assert.Contains("source:primarychannel_property_shape_unrecognized", row.MissingAuthorityFields);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task MissingNativeUnitUsesExplicitBindingButConflictingNativeUnitWaits()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        new PrtgTrustedSamplingBindingStore(backend).Save(new(11,
            currentSettings.Revision, currentPolicy.Revision, identity.Epoch, identity.ChannelGeneration,
            0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));

        var missingUnit = Assert.Single(await Service("synthetic-explicit-binding").ProbeAsync([11], CancellationToken.None));
        Assert.False(missingUnit.ProfileRecorded);
        Assert.Contains("qualification:raw_channel_id_time_proof_required", missingUnit.MissingAuthorityFields);
        Assert.DoesNotContain("channels:native_unit_conflicts_binding", missingUnit.MissingAuthorityFields);
        Assert.Null(Assert.Single(missingUnit.Channels).Unit);

        var conflict = Assert.Single(await Service("bytes-conflict").ProbeAsync([11], CancellationToken.None));
        Assert.False(conflict.ProfileRecorded);
        Assert.Equal("waiting", conflict.Status);
        Assert.Contains("channels:native_unit_conflicts_binding", conflict.MissingAuthorityFields);

        var scaleConflict = Assert.Single(await Service("scale-conflict").ProbeAsync([11], CancellationToken.None));
        Assert.Contains("channels:native_scale_conflicts_binding", scaleConflict.MissingAuthorityFields);
        var directionConflict = Assert.Single(await Service("direction-conflict").ProbeAsync([11], CancellationToken.None));
        Assert.Contains("channels:native_direction_conflicts_binding", directionConflict.MissingAuthorityFields);
    }

    [Fact]
    public async Task SyntheticExplicitBindingSaveQualificationPublishResolverSnapshotSeam_NoNativeSourceAcceptance()
    {
        // Application-contract integration only. The handler below is synthetic and does not
        // certify that any deployed/native PRTG version returns these response shapes.
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var strategyStore = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey));
        var strategy = strategyStore.GetCurrent(currentPolicy, PrtgFetchStrategy.Conservative, 15,
            DateTime.UtcNow.AddHours(-2));
        Assert.True(strategy.Ready);

        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var saved = bindings.Save(new(11, currentSettings.Revision, currentPolicy.Revision,
            identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds",
            "UTC", "UTC", "fixture-source-time-basis"));
        Assert.Empty(saved.QualificationProofReference);

        // A binding saved at one global revision remains semantically qualified after an unrelated
        // AI setting/scope addition; the qualification write still captures current full revisions.
        settings.Update(value => value.AiModel += "-before-qualification");
        policy.Update(value => { value.Revision = "r2"; value.SensorIds.Add(12); value.HostIds.Add(8); });
        currentSettings = settings.Get();
        currentPolicy = policy.Get();

        var qualifiedProbe = Assert.Single(await Service("synthetic-explicit-binding").ProbeAsync([11],
            CancellationToken.None, allowQualification: true,
            expectedBindingRevision: saved.BindingRevision,
            expectedBindingFingerprint: saved.BindingFingerprint));
        Assert.True(qualifiedProbe.ProfileRecorded);
        Assert.Equal("ready", qualifiedProbe.Status);
        Assert.NotNull(qualifiedProbe.NativePrimaryChannelPropertyId);
        var qualifiedBinding = Assert.IsType<PrtgTrustedSamplingBinding>(bindings.Get(11));
        Assert.NotEmpty(qualifiedBinding.QualificationProofReference);
        Assert.NotEqual(currentSettings.Revision, qualifiedBinding.SettingsRevision);
        Assert.NotEqual(currentPolicy.Revision, qualifiedBinding.PolicyRevision);
        var qualifiedIdentity = backend.PrtgStore().GetResourceIdentity(11);
        Assert.Equal(qualifiedBinding.BindingFingerprint, qualifiedIdentity.ChannelFingerprint);
        Assert.Equal(qualifiedBinding.ChannelGeneration, qualifiedIdentity.ChannelGeneration);
        var profile = Assert.Single(backend.PrtgStore().GetTrustedSamplingProfiles([11]).Values);
        Assert.Equal(qualifiedBinding.BindingFingerprint, profile.BindingFingerprint);

        var now = DateTime.UtcNow;
        var currentIdentity = backend.PrtgStore().GetResourceIdentity(11);
        var currentStrategy = strategyStore.GetCurrent(currentPolicy, PrtgFetchStrategy.Conservative, 15, now);
        var resolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, currentIdentity, currentPolicy,
            11, "CPU", currentStrategy, now, now);
        Assert.True(resolution.Ready, resolution.RejectionReason);
        Assert.Equal("3", resolution.Context!.SelectedPrimaryChannelId);
        Assert.True(resolution.Context.RequireCurrentNativePrimaryChannelId);

        // Global revisions are transaction CAS tokens, not permanent semantic authority.
        settings.Update(value => value.AiModel += "-unrelated-setting-change");
        policy.Update(value => { value.Revision = "r3"; value.SensorIds.Add(12); value.HostIds.Add(8); });
        currentSettings = settings.Get();
        currentPolicy = policy.Get();
        var expandedScopeStrategy = strategyStore.GetCurrent(currentPolicy,
            PrtgFetchStrategy.Conservative, 15, DateTime.UtcNow);
        var afterUnrelatedRevision = PrtgTrustedSamplingProfileResolver.Resolve(profile,
            backend.PrtgStore().GetResourceIdentity(11), currentPolicy, 11, "CPU",
            expandedScopeStrategy, DateTime.UtcNow, DateTime.UtcNow);
        Assert.True(afterUnrelatedRevision.Ready, afterUnrelatedRevision.RejectionReason);
        Assert.NotEqual(currentSettings.Revision, qualifiedBinding.SettingsRevision);
        Assert.NotEqual(currentPolicy.Revision, qualifiedBinding.PolicyRevision);

        var rawJson = JsonSerializer.Serialize(new
        {
            objid = 11, lastvalue_raw = "93", lastcheck_raw = profile.ComparedSnapshotMeasurementOaDate
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            interval_raw = "60", status_raw = 3, primarychannel = 3
        });
        var parsed = new PrtgTrustedSnapshotParser().Parse(rawJson, resolution.Context);
        Assert.True(parsed.AcceptedForAccumulation, parsed.RejectionReason);
        Assert.Equal(93, parsed.Sample!.Value);

        var beforeChange = backend.PrtgStore().GetResourceIdentity(11);
        var rebound = bindings.Save(new(11, currentSettings.Revision, currentPolicy.Revision,
            beforeChange.Epoch, beforeChange.ChannelGeneration, qualifiedBinding.BindingRevision,
            "4", "Other", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));
        Assert.Equal(qualifiedBinding.BindingRevision + 1, rebound.BindingRevision);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
        var afterChange = backend.PrtgStore().GetResourceIdentity(11);
        var staleResolution = PrtgTrustedSamplingProfileResolver.Resolve(profile, afterChange, currentPolicy,
            11, "CPU", currentStrategy, now, now);
        Assert.False(staleResolution.Ready);
        Assert.Throws<InvalidOperationException>(() => bindings.Save(new(11, currentSettings.Revision,
            currentPolicy.Revision, afterChange.Epoch, afterChange.ChannelGeneration,
            qualifiedBinding.BindingRevision, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis")));
    }

    [Fact]
    public async Task ProfileProbeAndSnapshotTableCallsShareOneBudgetAndActualHandlerCounter()
    {
        var clock = new PrtgRequestBudgetTests.TestPrtgClock(DateTimeOffset.UtcNow);
        var budget = new PrtgRequestBudget(clock);
        var counter = new RequestCounter();
        var service = new PrtgTrustedSamplingProbeService(backend, value =>
            PrtgClientFactory.Create(value, new Handler("complete", policy, counter), budget));
        using var snapshotClient = PrtgClientFactory.Create(settings.Get(), new Handler("complete", policy, counter), budget);

        var profileTask = service.ProbeAsync([11], CancellationToken.None);
        await WaitForWaiterAsync(budget, 1, TimeSpan.FromSeconds(5));
        Assert.Equal(2, counter.RequestCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        var profileRows = await profileTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Assert.Single(profileRows).ProfileRecorded);
        Assert.Equal(4, counter.RequestCount);

        var snapshot = snapshotClient.GetJsonAsync("/api/table.json?content=sensors&columns=objid&count=1");
        await WaitForWaiterAsync(budget, 1, TimeSpan.FromSeconds(5));
        Assert.Equal(4, counter.RequestCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await snapshot.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, counter.RequestCount);
        Assert.Equal(0, budget.InFlightCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Joint_profile_lane_plan_runs_grouped_brackets_and_keeps_unbound_sensors_waiting(int count)
    {
        var ids = Enumerable.Range(11, count).Select(value => (long)value).ToArray();
        if (count > 1)
        {
            policy.Update(value => value.SensorIds = ids.ToList());
            var maps = new List<PrtgHostMapRow>();
            foreach (var id in ids)
            {
                var deviceId = 22 + id - 11;
                if (id != 11)
                {
                    backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = id, DeviceObjid = deviceId, SensorType = "CPU" }], DateTime.UtcNow);
                }
                maps.Add(new PrtgHostMapRow { DeviceObjid = deviceId, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok });
            }
            backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, maps);
        // Host mappings are installed before binding the observed identities. This models the
        // production identity fence and keeps the first profile publication from looking like
        // a mid-probe scope mutation.
        foreach (var id in ids)
        {
            var deviceId = 22 + id - 11;
            backend.PrtgStore().BindObservedResource(id, 7, "source",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(deviceId.ToString(), "CPU", "created", 0));
        }
        }

        var currentSettings = settings.Get();
        var currentPolicy = policy.Get();
        var contract = PrtgProfileTransportCapacityPilot.BuildContract(backend, null, currentSettings,
            currentPolicy, ids);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new PrtgSnapshotCapacityEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            50, 1, 5, now, 1, 1, 600, .25, "qualified");
        var profile = new PrtgProfileTransportEstimate(PrtgSnapshotCapacityStatus.CapacityQualified,
            count, 5, now, .1, count * .1, 62100, .25, "qualified");
        var joint = PrtgJointCapacityEvaluator.Evaluate(snapshot, profile,
            new PrtgRequestBudgetUsage(0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero), currentSettings.PrtgTimeoutSeconds);
        var plan = PrtgJointCapacityEvaluator.CreatePlan(joint, contract.SourceFingerprint,
            new string('F', 64), contract.ScopeFingerprint, contract.StrategyFingerprint,
            new string('7', 64), contract.RequestShapeFingerprint, contract.VersionFingerprint, now,
            currentSettings.Revision, currentPolicy.Revision) with
        { Owner = "probe-test", Version = 1, LeaseUntilUtc = now.AddHours(1) };
        var clock = new AdvancingClock(now);
        var budget = new PrtgRequestBudget(clock);
        budget.SetAdmissionPlan(plan);
        var counter = new RequestCounter();
        var service = new PrtgTrustedSamplingProbeService(backend, value =>
            PrtgClientFactory.Create(value, new Handler("complete", policy, counter), budget));

        var rows = await service.ProbeAsync(ids, CancellationToken.None, requestPurpose: PrtgRequestPurpose.ProfileRefresh,
            admissionPlanFingerprint: plan.Fingerprint);

        Assert.Equal(count, rows.Count);
        Assert.All(rows, row => Assert.False(row.ProfileRecorded));
        Assert.Equal(PrtgProfileTransportCapacityEvaluator.ExpectedRequests(count), counter.RequestCount);
        Assert.Equal(0, budget.InFlightCount);
        Assert.True(clock.Elapsed <= TimeSpan.FromSeconds(30),
            $"The bounded {count}-sensor profile request group took {clock.Elapsed}.");
        var afterProbe = PrtgProfileTransportCapacityPilot.BuildContract(backend, null,
            settings.Get(), policy.Get(), ids);
        Assert.Equal(contract.ScopeFingerprint, afterProbe.ScopeFingerprint);
    }

    [Theory]
    [InlineData("group-missing")]
    [InlineData("group-foreign")]
    [InlineData("group-duplicate")]
    [InlineData("group-truncated")]
    [InlineData("group-filter-ignored-extra")]
    [InlineData("group-after-missing")]
    public async Task Grouped_bracket_rejects_nonexact_sensor_sets_without_publishing(string scenario)
    {
        var ids = Enumerable.Range(11, 5).Select(value => (long)value).ToArray();
        policy.Update(value => value.SensorIds = ids.ToList());
        var maps = ids.Select((id, index) => new PrtgHostMapRow
        { DeviceObjid = 22 + index, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }).ToArray();
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, maps);
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            if (id != 11)
                backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = id, DeviceObjid = 22 + index, SensorType = "CPU" }], DateTime.UtcNow);
            backend.PrtgStore().BindObservedResource(id, 7, "source",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint((22 + index).ToString(), "CPU", "created", 0));
        }
        var budget = new PrtgRequestBudget();
        var counter = new RequestCounter();
        var service = new PrtgTrustedSamplingProbeService(backend, value =>
            PrtgClientFactory.Create(value, new Handler(scenario, policy, counter), budget));
        var publishCalls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ProbeAsync(ids, CancellationToken.None,
            profilePublisher: _ => { publishCalls++; return true; }));

        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles(ids));
        Assert.Equal(0, publishCalls);
        Assert.Equal(scenario == "group-after-missing" ? 12 : 1, counter.RequestCount);
    }

    [Fact]
    public async Task Exact_five_sensor_group_is_bracketed_and_published_after_all_fences()
    {
        var ids = Enumerable.Range(11, 5).Select(value => (long)value).ToArray();
        policy.Update(value => value.SensorIds = ids.ToList());
        var maps = ids.Select((id, index) => new PrtgHostMapRow
        { DeviceObjid = 22 + index, HostId = 7, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }).ToArray();
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, maps);
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            if (id != 11)
                backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = id, DeviceObjid = 22 + index, SensorType = "CPU" }], DateTime.UtcNow);
            backend.PrtgStore().BindObservedResource(id, 7, "source",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint((22 + index).ToString(), "CPU", "created", 0));
            var identity = backend.PrtgStore().GetResourceIdentity(id);
            var currentSettings = settings.Get();
            var currentPolicy = policy.Get();
            var saved = bindingStore.Save(new(id, currentSettings.Revision, currentPolicy.Revision,
                identity.Epoch, identity.ChannelGeneration, 0, "3", "Load",
                PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "seconds", "UTC", "UTC",
                "fixture-source-time-basis"));
            bindingStore.RecordQualification(id, saved.BindingRevision, saved.BindingFingerprint,
                currentSettings.Revision, currentPolicy.Revision, 93, DateTime.UtcNow.AddSeconds(-2).ToOADate(),
                "fixture-version", DateTimeOffset.UtcNow, new string('B', 64));
        }
        var budget = new PrtgRequestBudget();
        var counter = new RequestCounter();
        var service = new PrtgTrustedSamplingProbeService(backend, value =>
            PrtgClientFactory.Create(value, new Handler("group-ready", policy, counter), budget));

        var rows = await service.ProbeAsync(ids, CancellationToken.None);

        Assert.Equal(12, counter.RequestCount);
        Assert.Equal(ids, rows.Select(row => row.SensorObjid).ToArray());
        Assert.All(rows, row => Assert.True(row.ProfileRecorded, string.Join(',', row.MissingAuthorityFields)));
        Assert.Equal(ids.Order(), backend.PrtgStore().GetTrustedSamplingProfiles(ids).Keys.Order());
    }

    [Theory]
    [InlineData("native-baseline")]
    [InlineData("different-parent")]
    [InlineData("changed-creation")]
    [InlineData("no-primary")]
    [InlineData("different-sample-time")]
    [InlineData("different-sample-value")]
    [InlineData("bad-scale")]
    [InlineData("stale-sample")]
    [InlineData("future-sample")]
    [InlineData("bad-interval")]
    [InlineData("unknown-status")]
    public async Task MissingOrConflictingFactsNeverCreateTrust(string scenario)
    {
        var row = Assert.Single(await Service(scenario).ProbeAsync([11], CancellationToken.None));
        Assert.False(row.ProfileRecorded);
        Assert.Equal("waiting", row.Status);
        Assert.NotEmpty(row.MissingAuthorityFields);
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task SourceScopeChangedDuringProbeRejectsBeforeAnyProfileWrite()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service("scope-changed").ProbeAsync([11], CancellationToken.None));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task WorkerProfilePublishRechecksLeaseInsideTheProfileWriteTransaction()
    {
        var leaseBlob = backend.Blob(PrtgTrustedSamplingProfileRefreshHostedService.ProgressBlobKey);
        leaseBlob.Mutate(_ => (JsonSerializer.Serialize(new
        {
            LeaseOwner = "worker", LeaseVersion = 1, LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        }), true));
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        new PrtgTrustedSamplingBindingStore(backend).Save(new(11,
            settings.Get().Revision, policy.Get().Revision, identity.Epoch, identity.ChannelGeneration,
            0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1,
            "direct", "seconds", "UTC", "UTC", "fixture-source-time-basis"));
        var service = Service("synthetic-explicit-binding");

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ProbeAsync([11], CancellationToken.None,
            profilePublisher: profile =>
            {
                leaseBlob.Mutate(_ => (JsonSerializer.Serialize(new
                {
                    LeaseOwner = "takeover", LeaseVersion = 2, LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                }), true));
                return new PrtgTrustedSamplingProfileStore(backend).RecordProbeResultUnderLease(profile, "worker", 1);
            }, leaseOwner: "worker", leaseVersion: 1, allowQualification: true));

        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    private PrtgTrustedSamplingProbeService Service(string scenario)
    {
        var budget = new PrtgRequestBudget();
        budget.SetHistoricCoordinator(new SqlPrtgHistoricRequestCoordinator(backend));
        return new PrtgTrustedSamplingProbeService(backend, value =>
            PrtgClientFactory.Create(value, new Handler(scenario, policy), budget));
    }

    private sealed class RequestCounter
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public void Increment() => Interlocked.Increment(ref requestCount);
    }

    private sealed class Handler(string scenario, PrtgMonitoringPolicyStore policy, RequestCounter? counter = null) : HttpMessageHandler
    {
        private readonly double measured = DateTime.UtcNow.AddSeconds(-2).ToOADate();
        private int groupedSensorResponses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            counter?.Increment();
            var id = ReadRequestedId(request.RequestUri!.Query);
            var query = request.RequestUri.Query;
            var groupIds = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.StartsWith("filter_objid=", StringComparison.Ordinal))
                .Select(part => long.Parse(Uri.UnescapeDataString(part[13..]), System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var deviceId = 22 + id - 11;
            var sourceTime = scenario == "stale-sample" ? measured - 1 : scenario == "future-sample" ? measured + 1 : measured;
            if (request.RequestUri.AbsolutePath.EndsWith("getobjectproperty.htm", StringComparison.Ordinal))
            {
                var property = scenario switch
                {
                    "synthetic-explicit-binding" or "group-ready" or "bytes-conflict" => "<prtg><result>3</result></prtg>",
                    "primary-nested-result" => "<prtg><wrapper><result>3</result></wrapper></prtg>",
                    "primary-namespaced-result" => "<prtg xmlns=\"urn:fixture\"><result>3</result></prtg>",
                    "primary-duplicate-result" => "<prtg><result>3</result><result>3</result></prtg>",
                    "primary-result-alias" => "<prtg><result>3</result><Result>3</Result></prtg>",
                    "primary-depth-over-limit" => "<prtg><a><b><c><d><e><f><g><h><i><result>3</result></i></h></g></f></e></d></c></b></a></prtg>",
                    _ => "<not-prtg><result>3</result></not-prtg>"
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(property, Encoding.UTF8, "application/xml") });
            }
            if (scenario == "synthetic-explicit-binding" && request.RequestUri.AbsolutePath.EndsWith("historicdata.xml", StringComparison.Ordinal))
            {
                var oa = measured.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                var xml = $"<histdata totalcount=\"1\"><prtg-version>synthetic-contract-only</prtg-version><item><datetime_raw>{oa}</datetime_raw><value_raw channel=\"Load\" channelid=\"3\">93</value_raw></item></histdata>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(xml, Encoding.UTF8, "application/xml") });
            }
            object response;
            if (query.Contains("content=sensors"))
            {
                var sensorIds = groupIds.Length > 0 ? groupIds.ToList() : [id];
                if (groupIds.Length > 0)
                {
                    groupedSensorResponses++;
                    if (scenario is "group-missing" or "group-truncated" || scenario == "group-after-missing" && groupedSensorResponses == 2)
                        sensorIds.RemoveAt(sensorIds.Count - 1);
                    if (scenario is "group-foreign" or "group-filter-ignored-extra") sensorIds.Add(999_999);
                    if (scenario == "group-duplicate") sensorIds.Add(sensorIds[0]);
                }
                response = new { sensors = sensorIds.Select(sensorId => new { objid = sensorId,
                    parentid = scenario == "different-parent" ? deviceId + 1 : 22 + sensorId - 11,
                    type = "CPU", cumsince_raw = scenario == "changed-creation" ? "recreated" : "created",
                    status_raw = scenario == "unknown-status" ? 1 : 3, lastvalue_raw = 93.0,
                    lastcheck_raw = sourceTime, interval_raw = scenario == "bad-interval" ? 0 : 60,
                    raw_timestamp_timezone_id = "UTC" }) };
            }
            else if (scenario == "native-baseline")
                response = new { channels = new[] { new { objid = 3, name = "Load", lastvalue_raw = 93.0 } } };
            else if (scenario is "synthetic-explicit-binding" or "group-ready")
                response = new { channels = new[] { new { objid = 3, name = "Load", lastvalue_raw = 93.0 } } };
            else
            {
                if (scenario == "scope-changed") policy.Update(value => value.Revision = "r2");
                response = new { channels = new[] { new { objid = 3, name = "Load",
                    unit = scenario == "bytes-conflict" ? "Bytes" : "%",
                    lastvalue_raw = scenario == "different-sample-value" ? 92 : 93,
                    isprimary = scenario != "no-primary", quantity = "CpuLoadPercent",
                    scale = scenario is "bad-scale" or "scale-conflict" ? 2 : 1,
                    direction = scenario == "direction-conflict" ? "inverse" : "direct",
                    lastcheck_raw = sourceTime + (scenario == "different-sample-time" ? 1.0 / 1440 : 0),
                    interval_unit = "seconds", semantic_version = "fixture-cpu-v1" } } };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }

        private static long ReadRequestedId(string query)
        {
            var token = query.Split('&').FirstOrDefault(part => part.StartsWith("id=", StringComparison.Ordinal));
            return token is not null && long.TryParse(token.AsSpan(3), out var id) ? id : 11;
        }
    }

    private static async Task WaitForWaiterAsync(PrtgRequestBudget budget, int expected, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (budget.WaiterCount >= expected) return;
            await Task.Delay(5);
        }
        throw new TimeoutException($"Timed out waiting for {expected} shared PRTG budget waiter.");
    }

    private sealed class AdvancingClock(DateTimeOffset initial) : IPrtgClock
    {
        private readonly object _lock = new();
        private DateTimeOffset _now = initial;
        private TimeSpan _elapsed;
        public DateTimeOffset UtcNow { get { lock (_lock) return _now; } }
        public TimeSpan Elapsed { get { lock (_lock) return _elapsed; } }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock) { _now += delay; _elapsed += delay; }
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // The path was created by this fixture under the process temporary directory.
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
