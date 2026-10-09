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
    public async Task CompleteTypedSourceFactsPersistProfileThatTheLiveSnapshotResolverConsumes()
    {
        var row = Assert.Single(await Service("complete").ProbeAsync([11], CancellationToken.None));
        Assert.True(row.ProfileRecorded, string.Join(",", row.MissingAuthorityFields));
        Assert.Equal("ready", row.Status);
        Assert.Empty(row.MissingAuthorityFields);
        var profile = backend.PrtgStore().GetTrustedSamplingProfiles([11])[11];
        Assert.Equal("3", profile.PrimaryChannelId);
        Assert.Equal(TimeSpan.FromSeconds(60), profile.ConfirmedScanInterval);
        var authority = new PrtgTrustedSamplingStrategyStateStore(backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey))
            .GetCurrent(policy.Get(), PrtgFetchStrategy.Conservative, 15, DateTime.UtcNow);
        Assert.True(PrtgTrustedSamplingProfileResolver.Resolve(profile, backend.PrtgStore().GetResourceIdentity(11),
            policy.Get(), 11, "CPU", authority, DateTime.UtcNow, DateTime.UtcNow).Ready);
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

        var profileRows = await service.ProbeAsync([11], CancellationToken.None);
        Assert.True(Assert.Single(profileRows).ProfileRecorded);
        Assert.Equal(2, counter.RequestCount);

        var snapshot = snapshotClient.GetJsonAsync("/api/table.json?content=sensors&columns=objid&count=1");
        await WaitForWaiterAsync(budget, 1, TimeSpan.FromSeconds(5));
        Assert.Equal(2, counter.RequestCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        await snapshot.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, counter.RequestCount);
        Assert.Equal(0, budget.InFlightCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task Joint_profile_lane_plan_allows_real_bounded_probe_groups_to_progress(int count)
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
        Assert.All(rows, row => Assert.True(row.ProfileRecorded, string.Join(",", row.MissingAuthorityFields)));
        Assert.Equal(count * 2, counter.RequestCount);
        Assert.Equal(0, budget.InFlightCount);
        Assert.True(clock.Elapsed <= TimeSpan.FromSeconds(30),
            $"The bounded {count}-sensor profile request group took {clock.Elapsed}.");
        var afterProbe = PrtgProfileTransportCapacityPilot.BuildContract(backend, null,
            settings.Get(), policy.Get(), ids);
        Assert.Equal(contract.ScopeFingerprint, afterProbe.ScopeFingerprint);
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
        var service = Service("complete");

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ProbeAsync([11], CancellationToken.None,
            profilePublisher: profile =>
            {
                leaseBlob.Mutate(_ => (JsonSerializer.Serialize(new
                {
                    LeaseOwner = "takeover", LeaseVersion = 2, LeaseUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                }), true));
                return new PrtgTrustedSamplingProfileStore(backend).RecordProbeResultUnderLease(profile, "worker", 1);
            }, leaseOwner: "worker", leaseVersion: 1));

        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    private PrtgTrustedSamplingProbeService Service(string scenario) => new(backend, value =>
        PrtgClientFactory.Create(value, new Handler(scenario, policy), new PrtgRequestBudget()));

    private sealed class RequestCounter
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);
        public void Increment() => Interlocked.Increment(ref requestCount);
    }

    private sealed class Handler(string scenario, PrtgMonitoringPolicyStore policy, RequestCounter? counter = null) : HttpMessageHandler
    {
        private readonly double measured = DateTime.UtcNow.AddSeconds(-2).ToOADate();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            counter?.Increment();
            var id = ReadRequestedId(request.RequestUri!.Query);
            var deviceId = 22 + id - 11;
            var sourceTime = scenario == "stale-sample" ? measured - 1 : scenario == "future-sample" ? measured + 1 : measured;
            object response;
            if (request.RequestUri!.Query.Contains("content=sensors"))
                response = new { sensors = new[] { new { objid = id, parentid = scenario == "different-parent" ? deviceId + 1 : deviceId,
                    type = "CPU", cumsince_raw = scenario == "changed-creation" ? "recreated" : "created",
                    status_raw = scenario == "unknown-status" ? 1 : 3, lastvalue_raw = 93.0,
                    lastcheck_raw = sourceTime, interval_raw = scenario == "bad-interval" ? 0 : 60,
                    raw_timestamp_timezone_id = "UTC" } } };
            else if (scenario == "native-baseline")
                response = new { channels = new[] { new { objid = 3, name = "Load", lastvalue_raw = 93.0 } } };
            else
            {
                if (scenario == "scope-changed") policy.Update(value => value.Revision = "r2");
                response = new { channels = new[] { new { objid = 3, name = "Load", unit = "%",
                    lastvalue_raw = scenario == "different-sample-value" ? 92 : 93,
                    isprimary = scenario != "no-primary", quantity = "CpuLoadPercent", scale = scenario == "bad-scale" ? 2 : 1,
                    direction = "direct", lastcheck_raw = sourceTime + (scenario == "different-sample-time" ? 1.0 / 1440 : 0),
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
