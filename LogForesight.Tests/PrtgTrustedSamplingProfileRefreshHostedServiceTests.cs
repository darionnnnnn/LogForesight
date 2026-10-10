using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using LogForesight.Core.Analysis;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgTrustedSamplingProfileRefreshHostedServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-profile-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly FakeHostStore hosts = new();

    public PrtgTrustedSamplingProfileRefreshHostedServiceTests()
    {
        backend = new(new StorageSettings { Type = "Sqlite" }, root);
        var settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.PrtgEnabled = true; value.PrtgUrl = "https://source.example";
            value.PrtgAuthMode = PrtgAuthModes.Token; value.PrtgApiTokenEnc = "fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        hosts.Upsert(new WebHost { HostName = "enabled-host", Active = true, Source = "netiq" });
    }

    [Fact]
    public async Task FullSweepPersistsCurrentPerSensorReasonsAndBoundedPagesAcrossRestart()
    {
        var ids = Enumerable.Range(10_001, 101).Select(value => (long)value).ToArray();
        SeedScope(ids);
        backend.PrtgStore().UpsertDevices(ids.Select(id => new PrtgDeviceRow { Objid = id + 50_000, Name = $"device-{id}" }).ToArray(), DateTime.UtcNow);
        backend.PrtgStore().UpsertSensors(ids.Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = id + 50_000, Name = $"CPU {id}", SensorType = "CPU",
            Category = PrtgSensorCategories.Cpu, CategorySource = "auto"
        }).ToArray(), DateTime.UtcNow);

        var first = Worker();
        await first.RunSliceAsync(CancellationToken.None);

        var progress = first.ReadProgress();
        Assert.Equal(101, progress.SelectedSensors);
        Assert.Equal(0, progress.EligibleSensors); // no enabled current resource rule
        Assert.Equal("full-scope-traversal-complete", progress.LastOutcome);
        Assert.Equal(0, progress.RawRequestCount);
        var firstPage = first.ReadSensorProgressPage(0, 100);
        Assert.Equal(101, firstPage.Total);
        Assert.Equal(100, firstPage.Rows.Count);
        Assert.Equal(100, firstPage.NextOffset);
        Assert.All(firstPage.Rows, row =>
        {
            Assert.False(row.Eligible);
            Assert.Equal("unavailable", row.Status);
            Assert.Equal("no-enabled-current-rule", row.Reason);
            Assert.True(row.NextAttemptAtUtc > DateTimeOffset.UtcNow);
        });
        Assert.Single(first.ReadSensorProgressPage(100, 100).Rows);

        var restarted = Worker();
        await restarted.RunSliceAsync(CancellationToken.None);
        Assert.Equal(progress.CompletedAtUtc, restarted.ReadProgress().CompletedAtUtc);
        Assert.Equal(firstPage.Rows.Select(row => row.Reason),
            restarted.ReadSensorProgressPage(0, 100).Rows.Select(row => row.Reason));
    }

    [Fact]
    public void SensorPageWritesStayBoundedAndAggregateDeltasAreIdempotent()
    {
        var store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        Assert.True(store.TryAcquire("test-owner", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out var leaseVersion));
        var overview = backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey);
        var page = Enumerable.Range(1, 100).Select(index => new PrtgTrustedSamplingProfileRefreshStateStore.SensorState
        {
            SensorObjid = index, Eligible = true, ContractFingerprint = "contract",
            Status = index <= 60 ? "qualified" : "waiting", Reason = index <= 60 ? "current" : "unsupported",
            NextAttemptAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        }).ToArray();

        // Use the actual durable owner/version columns and full scope key expected by the lease-gated writer.
        overview.Mutate(_ => (JsonSerializer.Serialize(new
        {
            ScopeFingerprint = "A1B2", Cursor = 0, SelectedSensors = 100,
            LeaseOwner = "test-owner", LeaseVersion = leaseVersion,
            LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(2)
        }), true));
        store.SavePage("A1B2", 0, page, "test-owner", leaseVersion);
        var savedPage = store.ReadPage("A1B2", 0);
        Assert.Equal(100, savedPage.Sensors.Count);
        Assert.True(Encoding.UTF8.GetByteCount(backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.PageKey("A1B2", 0)).Read()!)
            <= PrtgTrustedSamplingProfileRefreshStateStore.MaximumPageBytes);
        Assert.Equal(60, store.ReadOverview().Qualified);
        Assert.Equal(40, store.ReadOverview().Waiting);

        store.SavePage("A1B2", 0, page, "test-owner", leaseVersion);
        Assert.Equal(60, store.ReadOverview().Qualified);
        Assert.Equal(40, store.ReadOverview().Waiting);
    }

    [Fact]
    public void RawProofPersistsFencedRefreshRequestWithoutCreatingProfileAndOverridesHourSweepDelay()
    {
        SeedScope([11]);
        backend.PrtgStore().UpsertDevices([new PrtgDeviceRow { Objid = 22, Name = "device-11" }], DateTime.UtcNow);
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow
        {
            Objid = 11, DeviceObjid = 22, Name = "CPU 11", SensorType = "CPU",
            Category = PrtgSensorCategories.Cpu, CategorySource = "auto"
        }], DateTime.UtcNow);
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = 1, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        backend.PrtgStore().BindObservedResource(11, 1, "source",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));

        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var saved = bindings.Save(new(11, settings.Revision, policy.Revision, identity.Epoch,
            identity.ChannelGeneration, 0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", policy.TimeBasisEvidenceReference!));
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.IsRawQualificationPending(saved));
        SeedOrphanProofNotices();
        var observed = DateTimeOffset.UtcNow;
        var qualified = bindings.RecordQualification(11, saved.BindingRevision, saved.BindingFingerprint,
            settings.Revision, policy.Revision, 91, observed.AddSeconds(-1).UtcDateTime.ToOADate(),
            "source-r1", observed, new string('B', 64));
        identity = backend.PrtgStore().GetResourceIdentity(11);

        Assert.NotEmpty(qualified.QualificationProofReference);
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.IsRawQualificationPending(qualified));
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            PrtgFetchStrategy.Conservative, 15);
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.HasCurrentRawProof(11, qualified,
            identity, authorityContext, settings, policy));
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.HasCurrentRawProof(11, saved,
            identity, authorityContext, settings, policy));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
        using (var verify = backend.CreateContext())
        {
            var prefix = PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix;
            Assert.Equal(PrtgTrustedSamplingProfileRefreshStateStore.MaximumPendingProofRefreshNotices,
                verify.Blobs.Count(row => row.BlobKey.StartsWith(prefix)));
            Assert.True(verify.Blobs.Any(row => row.BlobKey == prefix + "11"));
        }
        var store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        var previousSweep = DateTimeOffset.UtcNow.AddHours(1);
        backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.OverviewKey).Mutate(_ =>
            (JsonSerializer.Serialize(new { ScopeFingerprint = "prior", Cursor = 0, NextSweepAtUtc = previousSweep }), true));

        var notice = Assert.Single(store.ReadProofRefreshNotices([11]).Values);
        store.QueueProofRefresh(notice);

        var restartedStore = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        var restored = Assert.Single(restartedStore.ReadProofRefreshNotices([11]).Values);
        Assert.Equal(notice, restored);
        Assert.True(restartedStore.ReadOverview().NextSweepAtUtc.HasValue &&
            restartedStore.ReadOverview().NextSweepAtUtc.Value <= DateTimeOffset.UtcNow);
        Assert.True(restartedStore.EarliestPendingProofRefresh([11]) is { } due && due <= DateTimeOffset.UtcNow);
        identity = backend.PrtgStore().GetResourceIdentity(11);
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.IsCurrentProofRefreshNotice(
            restored, qualified, identity, settings, policy));
        var staleIdentity = new PrtgResourceIdentity
        {
            SensorId = identity.SensorId, Epoch = identity.Epoch + 1, Generation = identity.Generation,
            SourceGeneration = identity.SourceGeneration, DeviceId = identity.DeviceId, HostId = identity.HostId,
            ResourceFingerprint = identity.ResourceFingerprint, InventoryFingerprint = identity.InventoryFingerprint,
            ChannelFingerprint = identity.ChannelFingerprint, ChannelGeneration = identity.ChannelGeneration,
            Active = identity.Active, PendingReconciliation = identity.PendingReconciliation,
            ChangedAtUtc = identity.ChangedAtUtc
        };
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.IsCurrentProofRefreshNotice(
            restored, qualified, staleIdentity, settings, policy));
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.HasCurrentRawProof(11, qualified,
            staleIdentity, authorityContext, settings, policy));
        var cooldownUntil = DateTimeOffset.UtcNow.AddHours(1);
        var now = DateTimeOffset.UtcNow;
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.ShouldHonorCooldown(
            false, cooldownUntil, false, null, now));
        Assert.False(PrtgTrustedSamplingProfileRefreshHostedService.ShouldHonorCooldown(
            false, cooldownUntil, true, now, now));
        Assert.True(PrtgTrustedSamplingProfileRefreshHostedService.ShouldHonorCooldown(
            false, cooldownUntil, true, now.AddMinutes(1), now));
    }

    [Fact]
    public void CompletingAnOldRefreshRequestCannotDeleteANewerProofNotice()
    {
        SeedScope([11]);
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        var now = DateTimeOffset.UtcNow;
        var old = TestNotice(11, 1, new string('A', 64), now) with { PolicyRevision = policy.Revision };
        var current = TestNotice(11, 2, new string('B', 64), now.AddTicks(1)) with { PolicyRevision = policy.Revision };
        store.QueueProofRefresh(old);
        store.QueueProofRefresh(current);

        Assert.False(store.CompleteProofRefresh(old));
        Assert.Equal(current, Assert.Single(store.ReadProofRefreshNotices([11]).Values));
        var deferredAt = now.AddHours(1);
        Assert.True(store.DeferProofRefresh(current, deferredAt));
        Assert.Equal(deferredAt, store.EarliestPendingProofRefresh([11]));
        Assert.True(store.CompleteProofRefresh(current with { NotBeforeUtc = deferredAt }));
        Assert.Null(store.EarliestPendingProofRefresh([11]));
    }

    [Fact]
    public void QueueProofRefreshEvictsOneOrphanAtCapacityWithoutReadingNoticeBodies()
    {
        SeedScope([11]);
        var prefix = PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix;
        using (var ctx = backend.CreateContext())
        {
            ctx.Blobs.AddRange(Enumerable.Range(1, PrtgTrustedSamplingProfileRefreshStateStore.MaximumPendingProofRefreshNotices)
                .Select(index => new BlobRow
                {
                    BlobKey = prefix + (100_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Content = "{\"not-a-current-notice\":true}", UpdatedAt = DateTime.UtcNow, Version = 1
                }));
            ctx.SaveChanges();
        }

        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var notice = TestNotice(11, 1, new string('F', 64), DateTimeOffset.UtcNow) with
            { PolicyRevision = policy.Revision };
        new PrtgTrustedSamplingProfileRefreshStateStore(backend).QueueProofRefresh(notice);

        using var verify = backend.CreateContext();
        var newNoticeKey = prefix + "11";
        Assert.Equal(PrtgTrustedSamplingProfileRefreshStateStore.MaximumPendingProofRefreshNotices,
            verify.Blobs.Count(row => row.BlobKey.StartsWith(prefix)));
        Assert.True(verify.Blobs.Any(row => row.BlobKey == newNoticeKey));
        Assert.Equal(PrtgTrustedSamplingProfileRefreshStateStore.MaximumPendingProofRefreshNotices - 1,
            verify.Blobs.Count(row => row.BlobKey.StartsWith(prefix) && row.BlobKey != newNoticeKey));
    }

    [Fact]
    public async Task WorkerRunSliceSkipsUnqualifiedBindingThenConsumesAtomicProofDespiteHourCooldown()
    {
        SeedScope([11]);
        backend.PrtgStore().UpsertDevices([new PrtgDeviceRow { Objid = 22, Name = "device-11" }], DateTime.UtcNow);
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow
        {
            Objid = 11, DeviceObjid = 22, Name = "CPU 11", SensorType = "CPU",
            Category = PrtgSensorCategories.Cpu, CategorySource = "auto"
        }], DateTime.UtcNow);
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
            [new PrtgHostMapRow { DeviceObjid = 22, HostId = 1, MapDate = DateTime.Today, MapStatus = PrtgMapStatus.Ok }]);
        backend.PrtgStore().BindObservedResource(11, 1, "source",
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
        new KnownIssueRuleStore(backend.Blob("rules")).Save(new RuleFileContent
            { SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules() });

        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var bindings = new PrtgTrustedSamplingBindingStore(backend);
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var saved = bindings.Save(new(11, settings.Revision, policy.Revision, identity.Epoch,
            identity.ChannelGeneration, 0, "3", "Load", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", policy.TimeBasisEvidenceReference!));

        var worker = Worker();
        await worker.RunSliceAsync(CancellationToken.None);
        var pending = Assert.Single(worker.ReadSensorProgressPage(0, 100).Rows);
        Assert.Equal("waiting", pending.Status);
        Assert.Equal("raw-qualification-pending", pending.Reason);
        Assert.Equal(0, worker.ReadProgress().RawRequestCount);

        var observedAt = DateTimeOffset.UtcNow;
        var qualified = bindings.RecordQualification(11, saved.BindingRevision, saved.BindingFingerprint,
            settings.Revision, policy.Revision, 91, observedAt.AddSeconds(-1).UtcDateTime.ToOADate(),
            "source-r1", observedAt, new string('D', 64));
        identity = backend.PrtgStore().GetResourceIdentity(11);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Conservative, 15, DateTime.UtcNow);
        var authorityContext = PrtgTrustedSamplingProfileResolver.AuthorityContextFingerprint(policy,
            PrtgFetchStrategy.Conservative, 15);
        var profile = PrtgTrustedSamplingProfile.FromProbe(11, identity, "CPU", "3", "Load",
            PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "probe-v1",
            strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromMinutes(strategy.StrategyMinutes), "seconds", "UTC", "UTC", "UTC",
            observedAt, "synthetic-metadata-reference", "synthetic-sample-reference", false,
            45, 45, observedAt.AddSeconds(-1).UtcDateTime.ToOADate(), observedAt.AddSeconds(-1).UtcDateTime.ToOADate(),
            PrtgTrustedSamplingProfile.ExplicitBindingAuthorityKind, qualified.BindingRevision,
            qualified.BindingFingerprint, "3", qualified.QualificationProofReference,
            qualified.SettingsRevision, qualified.PolicyRevision, authorityContext);
        new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);

        var stateBlob = backend.Blob(PrtgTrustedSamplingProfileRefreshHostedService.ProgressBlobKey);
        stateBlob.Mutate(raw =>
        {
            var state = JsonSerializer.Deserialize<PrtgTrustedSamplingProfileRefreshStateStore.Overview>(raw!)!;
            state.NextSweepAtUtc = DateTimeOffset.UtcNow.AddHours(1);
            return (JsonSerializer.Serialize(state), true);
        });
        var currentNotice = Assert.Single(new PrtgTrustedSamplingProfileRefreshStateStore(backend).ReadProofRefreshNotices([11]).Values);
        await Worker().RunSliceAsync(CancellationToken.None);

        Assert.Empty(new PrtgTrustedSamplingProfileRefreshStateStore(backend).ReadProofRefreshNotices([11]));
        var refreshed = Assert.Single(worker.ReadSensorProgressPage(0, 100).Rows);
        Assert.Equal("qualified", refreshed.Status);
        Assert.Equal("current-profile-fresh", refreshed.Reason);

        // A still-current proof notice must not hold a paused sensor's whole scope at an immediate rescan.
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow
        {
            Objid = 11, DeviceObjid = 22, Name = "CPU 11", SensorType = "CPU",
            Category = PrtgSensorCategories.Cpu, CategorySource = "auto", Paused = true
        }], DateTime.UtcNow);
        stateBlob.Mutate(raw =>
        {
            var state = JsonSerializer.Deserialize<PrtgTrustedSamplingProfileRefreshStateStore.Overview>(raw!)!;
            state.NextSweepAtUtc = DateTimeOffset.UtcNow.AddHours(1);
            return (JsonSerializer.Serialize(state), true);
        });
        backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix + "11")
            .Mutate(_ => (JsonSerializer.Serialize(currentNotice), true));
        await Worker().RunSliceAsync(CancellationToken.None);
        Assert.Empty(new PrtgTrustedSamplingProfileRefreshStateStore(backend).ReadProofRefreshNotices([11]));
        var paused = Assert.Single(worker.ReadSensorProgressPage(0, 100).Rows);
        Assert.Equal("unavailable", paused.Status);
        Assert.Equal("paused", paused.Reason);
        Assert.True(worker.ReadProgress().NextSweepAtUtc > DateTimeOffset.UtcNow,
            "A consumed current proof notice for a paused sensor must not force a past-due rescan.");
    }

    [Fact]
    public void ProofNoticeReadsRejectOversizedUtf8AndDeepJsonAndIgnoreOrphanScope()
    {
        var store = new PrtgTrustedSamplingProfileRefreshStateStore(backend);
        var now = DateTimeOffset.UtcNow;
        var orphan = TestNotice(12, 1, new string('A', 64), now);
        backend.Blob(PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix + "12")
            .Mutate(_ => (JsonSerializer.Serialize(orphan), true));
        Assert.Null(store.EarliestPendingProofRefresh([11]));
        Assert.Equal(1, store.PurgeOutOfScopeProofRefreshNotices([11]));
        Assert.Null(store.EarliestPendingProofRefresh([11, 12]));

        var key11 = PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix + "11";
        backend.Blob(key11).Mutate(_ => (new string('x',
            PrtgTrustedSamplingProfileRefreshStateStore.MaximumProofRefreshNoticeBytes + 1), true));
        Assert.Throws<InvalidDataException>(() => store.ReadProofRefreshNotices([11]));
        Assert.Throws<InvalidDataException>(() => store.QueueProofRefresh(TestNotice(11, 1,
            new string('B', 64), now)));

        var multibyte = "{\"x\":\"" + new string('界', 1400) + "\"}";
        backend.Blob(key11).Mutate(_ => (multibyte, true));
        Assert.True(multibyte.Length <= PrtgTrustedSamplingProfileRefreshStateStore.MaximumProofRefreshNoticeBytes);
        Assert.Throws<InvalidDataException>(() => store.EarliestPendingProofRefresh([11]));

        var nested = JsonSerializer.Serialize(TestNotice(11, 1, new string('C', 64), now));
        for (var index = 0; index < 34; index++) nested = "{\"x\":" + nested + "}";
        backend.Blob(key11).Mutate(_ => (nested, true));
        Assert.ThrowsAny<JsonException>(() => store.ReadProofRefreshNotices([11]));

        var valid = JsonSerializer.Serialize(TestNotice(11, 1, new string('E', 64), now));
        var nullNode = JsonNode.Parse(valid)!.AsObject();
        nullNode[nameof(PrtgTrustedSamplingProofRefreshRequest.BindingFingerprint)] = null;
        var nullHash = nullNode.ToJsonString();
        backend.Blob(key11).Mutate(_ => (nullHash, true));
        Assert.Throws<InvalidDataException>(() => store.ReadProofRefreshNotices([11]));

        var duplicate = valid[..^1] + ",\"SensorObjid\":11}";
        backend.Blob(key11).Mutate(_ => (duplicate, true));
        Assert.Throws<InvalidDataException>(() => store.ReadProofRefreshNotices([11]));
    }

    private static PrtgTrustedSamplingProofRefreshRequest TestNotice(long sensorId, long revision,
        string proof, DateTimeOffset now) => new(sensorId, revision, new string('C', 64), proof, 1,
        "resource", "source", "channel", new string('D', 64), "settings", "policy", now, now);

    [Fact]
    public async Task ActiveDurableLeasePreventsSecondWorkerFromTakingOver()
    {
        var blob = backend.Blob(PrtgTrustedSamplingProfileRefreshHostedService.ProgressBlobKey);
        blob.Mutate(_ => (JsonSerializer.Serialize(new
        {
            ScopeFingerprint = "existing", Cursor = 0, SelectedSensors = 1,
            LeaseOwner = "first-instance", LeaseVersion = 9,
            LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        }), true));

        await Worker().RunSliceAsync(CancellationToken.None);

        using var saved = JsonDocument.Parse(blob.Read()!);
        Assert.Equal("first-instance", saved.RootElement.GetProperty("LeaseOwner").GetString());
        Assert.Equal(9, saved.RootElement.GetProperty("LeaseVersion").GetInt64());
    }

    private void SeedScope(long[] sensorIds)
    {
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.Revision = "r1"; policy.CoreSystemId = "core"; policy.SourceGeneration = "source";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1); policy.HostIds = [1]; policy.SensorIds = sensorIds.ToList();
            policy.SourceTimeZoneId = "UTC"; policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC"; policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "fixture-time-basis";
        });
    }

    private void SeedOrphanProofNotices()
    {
        var prefix = PrtgTrustedSamplingProfileRefreshStateStore.ProofRefreshNoticePrefix;
        using var ctx = backend.CreateContext();
        ctx.Blobs.AddRange(Enumerable.Range(1, PrtgTrustedSamplingProfileRefreshStateStore.MaximumPendingProofRefreshNotices)
            .Select(index => new BlobRow
            {
                BlobKey = prefix + (100_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Content = "{\"not-a-current-notice\":true}", UpdatedAt = DateTime.UtcNow, Version = 1
            }));
        ctx.SaveChanges();
    }

    private PrtgTrustedSamplingProfileRefreshHostedService Worker() =>
        new(backend, hosts, NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
