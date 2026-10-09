using System.Net;
using System.Text;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

[Collection("PrtgSnapshotSharedBudget")]
public sealed class PrtgSnapshotCapacityAdmissionTests
{
    [Fact]
    public async Task Sql_settings_cold_bootstrap_pilot_admits_exact_scope_and_cas_blocks_precheck_race()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-capacity-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "test.db")}"
            }, directory);
            var settingsStore = new SystemSettingsStore(backend.Blob("system_settings"));
            var hostStore = new HostStore(backend.Blob("hosts"));
            var host = hostStore.Upsert(new WebHost { HostName = "capacity-host", IpAddress = "192.0.2.10", Active = true });
            Assert.Equal(1, host.HostId);
            var prtg = backend.PrtgStore();
            var today = DateTime.Today;
            prtg.ReplaceHostMapForDate(today, new[]
            {
                new PrtgHostMapRow { MapDate = today, DeviceObjid = 10, HostId = host.HostId,
                    HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok }
            });
            prtg.UpsertSensors(new[]
            {
                Sensor(501, 10, "ping"), Sensor(502, 10, "cpu"), Sensor(503, 10, "memory")
            }, DateTime.Now);

            var settingsService = CreateSettingsService(backend, settingsStore);
            var racingService = new RacingSettingsService(settingsService, settingsStore);
            var controller = new SettingsController(racingService, new AiUsageStore(backend.Blob("ai_usage")),
                new RecordingAuditService(), backend: backend, hosts: hostStore)
            {
                SnapshotCapacityClientFactory = settings => PrtgClientFactory.Create(settings,
                    new CapacityTableHandler())
            };

            var policy = new PrtgMonitoringPolicy
            {
                Revision = Guid.NewGuid().ToString("N"),
                // Resolver-only fixture: this enables host/sensor intersection in target selection.
                // It is not native PRTG authority or a chronology/24-hour capacity claim.
                CoreSystemId = "capacity-fixture",
                SourceGeneration = "capacity-fixture-generation",
                EndpointHint = EfPrtgObservationStore.SourceHintFor("https://prtg.example.test"),
                ValidFrom = DateTimeOffset.UtcNow,
                HostIds = [host.HostId],
                SensorIds = [501, 502, 503],
                SourceTimeZoneId = "UTC",
                SourceCultureName = "en-US"
            };
            new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(p =>
            {
                p.Revision = policy.Revision;
                p.CoreSystemId = policy.CoreSystemId;
                p.SourceGeneration = policy.SourceGeneration;
                p.EndpointHint = policy.EndpointHint;
                p.ValidFrom = policy.ValidFrom;
                p.HostIds = policy.HostIds;
                p.SensorIds = policy.SensorIds;
                p.SourceTimeZoneId = policy.SourceTimeZoneId;
                p.SourceCultureName = policy.SourceCultureName;
            });
            foreach (var sensor in new[] { (Id: 501L, Device: 10L, Type: "ping"),
                         (Id: 502L, Device: 10L, Type: "cpu"), (Id: 503L, Device: 10L, Type: "memory") })
                prtg.BindObservedResource(sensor.Id, host.HostId, policy.SourceGeneration,
                    PrtgTimelineResourceIdentity.BuildResourceFingerprint(sensor.Device.ToString(), sensor.Type, "created", 0));

            controller.UpdatePrtg(new UpdatePrtgSettingsRequest
            {
                PrtgEnabled = false,
                PrtgUrl = "https://prtg.example.test",
                PrtgAuthMode = PrtgAuthModes.Token,
                PrtgApiToken = "fixture-secret",
                PrtgSensorTypeWhitelist = ["ping", "cpu"],
                PrtgFetchStrategy = PrtgFetchStrategy.Conservative,
                PrtgTimeoutSeconds = 30
            });
            Assert.False(settingsStore.Get().PrtgEnabled);

            var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            var profileGuardHandler = new CapacityProfileTableHandler();
            policyStore.Update(p =>
            {
                p.Revision = Guid.NewGuid().ToString("N");
                p.SensorIds = p.SensorIds.Append(504).Distinct().Order().ToList();
            });
            var strictNativeProbe = new PrtgProfileTransportCapacityPilot(backend,
                value => PrtgClientFactory.Create(value, profileGuardHandler, new PrtgRequestBudget()), hostStore);
            var identityGuard = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                strictNativeProbe.RunAsync([501, 502, 503, 504], CancellationToken.None));
            Assert.Equal("profile-capacity-resource-identity-not-current", identityGuard.Message);
            Assert.Equal(0, profileGuardHandler.RequestCount);
            policyStore.Update(p =>
            {
                p.Revision = policy.Revision;
                p.SensorIds = policy.SensorIds;
            });

            var firstPilot = (await controller.RunPrtgCapacityPilot(CancellationToken.None)).Data!;
            Assert.Equal("capacity-qualified", firstPilot.Status);
            Assert.Equal(2, firstPilot.CapacitySampleBatchSize);
            Assert.Equal(5, firstPilot.MatchingFullBatchSamples);
            Assert.Equal(5, firstPilot.RequestsSent);
            await RunFiveProfileCapacitySamples(backend, hostStore, settingsStore);
            Assert.Empty(prtg.GetTrustedSamplingProfiles([501, 502, 503]));

            racingService.MutateBeforeNextPrtgUpdate = true;
            var race = Assert.Throws<DomainException>(() => controller.UpdatePrtg(new UpdatePrtgSettingsRequest
            { PrtgEnabled = true }));
            Assert.Equal(ApiErrorCodes.Conflict, race.Code);
            Assert.False(settingsStore.Get().PrtgEnabled);

            var secondPilot = (await controller.RunPrtgCapacityPilot(CancellationToken.None)).Data!;
            Assert.Equal("capacity-qualified", secondPilot.Status);
            await RunFiveProfileCapacitySamples(backend, hostStore, settingsStore);
            policyStore.Update(p => p.Revision = "");
            AssertAdmissionBlocked(controller, new UpdatePrtgSettingsRequest { PrtgEnabled = true });
            Assert.False(settingsStore.Get().PrtgEnabled);
            policyStore.Update(p => p.Revision = policy.Revision);
            controller.UpdatePrtg(new UpdatePrtgSettingsRequest { PrtgEnabled = true });
            Assert.True(settingsStore.Get().PrtgEnabled);
            var savedPlan = new PrtgCapacityAdmissionPlanStore(
                backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey)).ReadCurrent(DateTimeOffset.UtcNow);
            Assert.NotNull(savedPlan);
            var savedContract = PrtgProfileTransportCapacityPilot.BuildContract(backend, hostStore,
                settingsStore.Get(), new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get(),
                [501, 502, 503]);
            Assert.Equal(savedContract.RequestShapeFingerprint, savedPlan!.RequestShapeFingerprint);
            Assert.Equal(savedContract.VersionFingerprint, savedPlan.RuntimeVersionFingerprint);
            var savedSelection = PrtgSnapshotTargetResolver.Resolve(backend, hostStore, settingsStore.Get(),
                new SentinelStore(backend.Blob("sentinels")).GetAll());
            Assert.True(PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hostStore,
                settingsStore.Get(), savedSelection, out var runtimePlan, out var runtimeReason), runtimeReason);
            Assert.Equal(savedPlan.Fingerprint, runtimePlan!.Fingerprint);

            // The old plan carries proof for the previous complete policy scope. An
            // authorized-but-not-yet-mirrored ID changes that scope; runtime must compare
            // the stale proof and fail closed, without demanding a current identity merely
            // to rebuild the transport context.
            policyStore.Update(p =>
            {
                p.Revision = Guid.NewGuid().ToString("N");
                p.SensorIds = p.SensorIds.Append(504).Distinct().Order().ToList();
            });
            var expandedPolicy = policyStore.Get();
            var expandedContract = PrtgProfileTransportCapacityPilot.BuildTransportContextContract(
                settingsStore.Get(), expandedPolicy, hostStore.CapturePrtgSnapshot());
            Assert.NotEqual(savedPlan!.ProfileScopeFingerprint, expandedContract.ScopeFingerprint);
            var expandedSelection = PrtgSnapshotTargetResolver.Resolve(backend, hostStore,
                settingsStore.Get(), new SentinelStore(backend.Blob("sentinels")).GetAll());
            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hostStore,
                settingsStore.Get(), expandedSelection, out _, out var staleProofReason));
            Assert.StartsWith("current-capacity-evidence-", staleProofReason);
            Assert.NotEqual("profile-contract-unavailable", staleProofReason);
            policyStore.Update(p =>
            {
                p.Revision = policy.Revision;
                p.SensorIds = policy.SensorIds;
            });

            var firstWorker = new PrtgTrustedSamplingProfileRefreshHostedService(backend, hostStore,
                NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance);
            await firstWorker.RunSliceAsync(CancellationToken.None);
            var firstProgress = firstWorker.ReadProgress();
            Assert.Equal("full-scope-traversal-complete", firstProgress.LastOutcome);
            Assert.Equal(3, firstProgress.SelectedSensors);
            var restartedWorker = new PrtgTrustedSamplingProfileRefreshHostedService(backend, hostStore,
                NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance);
            await restartedWorker.RunSliceAsync(CancellationToken.None);
            Assert.Equal(firstProgress.CompletedAtUtc, restartedWorker.ReadProgress().CompletedAtUtc);

            AssertAdmissionBlocked(controller, new UpdatePrtgSettingsRequest
            { PrtgResourceGuardEnabled = true, PrtgResourceGuardSensorObjids = ["503"] });
            AssertAdmissionBlocked(controller, new UpdatePrtgSettingsRequest
            { PrtgFetchStrategy = PrtgFetchStrategy.Aggressive });
            AssertAdmissionBlocked(controller, new UpdatePrtgSettingsRequest
            { PrtgUrl = "https://other-prtg.example.test" });
            AssertAdmissionBlocked(controller, new UpdatePrtgSettingsRequest
            { PrtgSensorTypeWhitelist = ["ping", "cpu", "memory"] });

            controller.UpdatePrtg(new UpdatePrtgSettingsRequest { PrtgSensorTypeWhitelist = ["ping"] });
            Assert.Equal(new[] { "ping" }, settingsStore.Get().PrtgSensorTypeWhitelist);
            var narrowedSelection = PrtgSnapshotTargetResolver.Resolve(backend, hostStore,
                settingsStore.Get(), new SentinelStore(backend.Blob("sentinels")).GetAll());
            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(backend, hostStore,
                settingsStore.Get(), narrowedSelection, out _, out _));
            controller.UpdatePrtg(new UpdatePrtgSettingsRequest { PrtgEnabled = false });
            Assert.False(settingsStore.Get().PrtgEnabled);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Existing_plan_allows_only_a_fresh_same_contract_single_batch_recovery()
    {
        using var fixture = RuntimeRecoveryFixture.Create(1);
        var selection = fixture.Selection;
        var planStore = new PrtgCapacityAdmissionPlanStore(
            fixture.Backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey));
        var originalPlan = Assert.IsType<PrtgCapacityAdmissionPlan>(planStore.ReadCurrent(DateTimeOffset.UtcNow));
        fixture.RecordSnapshotFailure();

        Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
            fixture.Settings.Get(), selection, out _, out var fullyQualifiedReason));
        Assert.StartsWith("current-capacity-evidence-", fullyQualifiedReason);

        Assert.True(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
            fixture.Settings.Get(), selection, out var recoveryPlan, out var recoveryReason,
            allowBoundedSingleBatchRecovery: true), recoveryReason);
        Assert.Equal(PrtgCapacityRuntimeAdmission.BoundedSingleBatchRecoveryReason, recoveryReason);
        Assert.Equal(originalPlan.Fingerprint, recoveryPlan!.Fingerprint);
        var evidence = new PrtgSnapshotCapacityStore(
            fixture.Backend.Blob(PrtgSnapshotCapacityStore.BlobKey)).Read();
        Assert.Single(evidence);
        Assert.Equal("failed", evidence[0].Outcome);

        planStore.Invalidate(originalPlan.Owner, originalPlan.Version);
        Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
            fixture.Settings.Get(), selection, out _, out _, allowBoundedSingleBatchRecovery: true));
    }

    [Fact]
    public void Bounded_recovery_fails_closed_for_source_profile_and_over_one_batch()
    {
        using (var fixture = RuntimeRecoveryFixture.Create(1))
        {
            fixture.RecordSnapshotFailure();
            fixture.Settings.Update(s => s.PrtgUrl = "https://changed-prtg.example.test");
            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
                fixture.Settings.Get(), fixture.Selection, out _, out _, allowBoundedSingleBatchRecovery: true));
        }

        using (var fixture = RuntimeRecoveryFixture.Create(1))
        {
            fixture.RecordSnapshotFailure();
            var profileStore = new PrtgProfileTransportCapacityStore(
                fixture.Backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
            var profileSample = Assert.IsType<PrtgProfileTransportSample>(profileStore.Read().LastOrDefault());
            profileStore.Record(profileSample with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Outcome = "failed",
                RequestsSent = 0
            });
            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
                fixture.Settings.Get(), fixture.Selection, out _, out _, allowBoundedSingleBatchRecovery: true));
        }

        using (var fixture = RuntimeRecoveryFixture.Create(51))
        {
            fixture.RecordSnapshotFailure();
            Assert.Equal(51, fixture.Selection.SensorObjids.Count);
            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
                fixture.Settings.Get(), fixture.Selection, out _, out _, allowBoundedSingleBatchRecovery: true));
        }
    }

    [Fact]
    public void Bounded_recovery_requires_a_fresh_real_snapshot_sample_and_cannot_bootstrap_from_absence()
    {
        using (var fixture = RuntimeRecoveryFixture.Create(1))
        {
            var evidence = fixture.Backend.Blob(PrtgSnapshotCapacityStore.BlobKey);
            evidence.Mutate(_ => ("[]", true));

            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
                fixture.Settings.Get(), fixture.Selection, out _, out _, allowBoundedSingleBatchRecovery: true));
        }

        using (var fixture = RuntimeRecoveryFixture.Create(1))
        {
            var selection = fixture.Selection;
            var capacityStore = new PrtgSnapshotCapacityStore(
                fixture.Backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
            var sample = Assert.IsType<PrtgSnapshotCapacitySample>(capacityStore.Read().LastOrDefault());
            capacityStore.Record(sample with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow.AddHours(-25),
                RequestedSensorCount = selection.CapacitySampleBatchSize,
                Outcome = "failed"
            });

            Assert.False(PrtgCapacityRuntimeAdmission.TryGetCurrent(fixture.Backend, fixture.Hosts,
                fixture.Settings.Get(), selection, out _, out _, allowBoundedSingleBatchRecovery: true));
        }
    }

    private sealed class RuntimeRecoveryFixture : IDisposable
    {
        private readonly string _directory;
        public StorageBackend Backend { get; }
        public SystemSettingsStore Settings { get; }
        public HostStore Hosts { get; }
        public long HostId { get; }
        public PrtgSnapshotTargetSelection Selection => PrtgSnapshotTargetResolver.Resolve(
            Backend, Hosts, Settings.Get(), Array.Empty<Sentinel>());

        private RuntimeRecoveryFixture(int sensorCount)
        {
            _directory = Path.Combine(Path.GetTempPath(), "lf-snapshot-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
            }, _directory);
            Settings = new SystemSettingsStore(Backend.Blob("system_settings"));
            Hosts = new HostStore(Backend.Blob("hosts"));
            var host = Hosts.Upsert(new WebHost { HostName = "recovery-host", IpAddress = "192.0.2.51", Active = true });
            HostId = host.HostId;
            Settings.Update(settings =>
            {
                settings.PrtgEnabled = true;
                settings.PrtgUrl = "https://prtg-recovery.example.test";
                settings.PrtgAuthMode = PrtgAuthModes.Token;
                settings.PrtgApiTokenEnc = CryptoHelper.Encrypt("recovery-token");
                settings.PrtgTimeoutSeconds = 30;
                settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
                settings.PrtgSensorTypeWhitelist = ["ping"];
            });
            var sensorIds = Enumerable.Range(0, sensorCount).Select(index => 10_000L + index).ToArray();
            var today = DateTime.Today;
            var prtg = Backend.PrtgStore();
            prtg.ReplaceHostMapForDate(today, new[]
            {
                new PrtgHostMapRow { MapDate = today, DeviceObjid = 10, HostId = HostId,
                    HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok }
            });
            prtg.UpsertSensors(sensorIds.Select(id => Sensor(id, 10, "ping")).ToArray(), DateTime.Now);
            SnapshotAdmissionTestFixture.Seed(Backend, Hosts, Settings, HostId, 10,
                sensorIds.Select(id => (SensorObjid: id, SensorType: "ping")).ToArray());
        }

        public static RuntimeRecoveryFixture Create(int sensorCount) => new(sensorCount);

        public void RecordSnapshotFailure()
        {
            var selection = Selection;
            var store = new PrtgSnapshotCapacityStore(Backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
            var sample = Assert.IsType<PrtgSnapshotCapacitySample>(store.Read().LastOrDefault());
            store.Record(sample with
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                RequestedSensorCount = selection.CapacitySampleBatchSize,
                Outcome = "failed"
            });
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task RunFiveProfileCapacitySamples(StorageBackend backend, IHostStore hosts,
        SystemSettingsStore settings)
    {
        for (var sample = 0; sample < 5; sample++)
        {
            var result = await new PrtgProfileTransportCapacityPilot(backend,
                value => PrtgClientFactory.Create(value, new CapacityProfileTableHandler(), new PrtgRequestBudget()), hosts)
                .RunAsync([501, 502, 503], CancellationToken.None);
            Assert.Equal(sample == 4 ? "capacity-qualified" : "capacity-unverified", result.Status);
            if (sample < 4)
                Assert.Equal("insufficient_fresh_matching_profile_samples", result.Reason);
            Assert.Equal(6, result.RequestsSent);
            Assert.False(settings.Get().PrtgEnabled);
        }
    }

    private static PrtgSensorRow Sensor(long id, long deviceId, string type) => new()
    {
        Objid = id, DeviceObjid = deviceId, Name = $"Sensor-{id}", SensorType = type,
        Status = "Up", Paused = false
    };

    private static SystemSettingsService CreateSettingsService(StorageBackend backend, ISystemSettingsStore store)
    {
        var users = new FakeUserStore();
        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(backend.Blob("schedule_options")));
        var mail = new MailNotificationService(store, new FakeSmtpMailSender(), new FakeHostStore(), users,
            new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(backend.Blob("mail_notify_state")), freshness);
        return new SystemSettingsService(store, FakeCurrentUser.WithCapabilities(Capability.Maintain),
            new RecordingAuditService(), users, mail, new FakeReportUsageQuery());
    }

    private static void AssertAdmissionBlocked(SettingsController controller, UpdatePrtgSettingsRequest request)
    {
        var error = Assert.Throws<DomainException>(() => controller.UpdatePrtg(request));
        Assert.Equal("capacity-unverified", error.Code);
    }

    private sealed class CapacityTableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ids = request.RequestUri!.Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.StartsWith("filter_objid=", StringComparison.Ordinal))
                .Select(part => long.Parse(Uri.UnescapeDataString(part["filter_objid=".Length..]),
                    System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                sensors = ids.Select(id => new { objid = id, status = "Up", lastcheck = "2026-10-06T00:00:00Z" })
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class CapacityProfileTableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var query = request.RequestUri!.Query;
            var idPart = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(part => part.StartsWith("id=", StringComparison.Ordinal));
            var id = idPart is not null && long.TryParse(Uri.UnescapeDataString(idPart[3..]), out var parsed) ? parsed : 501;
            var body = query.Contains("content=sensors", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { sensors = new[] { new { objid = id } } })
                : JsonSerializer.Serialize(new { channels = new[] { new { objid = 3 } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RacingSettingsService(ISystemSettingsService inner, ISystemSettingsStore store) : ISystemSettingsService
    {
        public bool MutateBeforeNextPrtgUpdate { get; set; }
        public SystemSettingsDto Get() => inner.Get();
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => inner.Update(request);
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request)
        {
            if (MutateBeforeNextPrtgUpdate)
            {
                MutateBeforeNextPrtgUpdate = false;
                store.Update(s => s.PrtgTimeoutSeconds++);
            }
            return inner.UpdatePrtg(request);
        }
        public HashSet<string>? GetVisibleSeverities() => inner.GetVisibleSeverities();
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => inner.GetVisibleDayRiskLevels();
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => inner.TestAdConnection(request);
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => inner.TestMail(request);
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) =>
            inner.TestPrtgAsync(request, ct);
    }
}
