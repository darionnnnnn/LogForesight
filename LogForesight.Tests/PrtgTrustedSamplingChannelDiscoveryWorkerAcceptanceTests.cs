using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

[Collection("PrtgSnapshotSharedBudget")]
public sealed class PrtgTrustedSamplingChannelDiscoveryWorkerAcceptanceTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-channel-worker-acceptance-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly HostStore hosts;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;
    private readonly LoopbackPrtg loopback = new();
    private readonly WebHost host;
    private readonly UserGroup group;
    private readonly WebUser user;
    private readonly IServiceProvider visibilityProvider;
    private readonly PrtgCapacityAdmissionPlan plan;

    public PrtgTrustedSamplingChannelDiscoveryWorkerAcceptanceTests()
    {
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        Directory.CreateDirectory(root);
        backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(root, "acceptance.db")}"
        }, root);
        hosts = new HostStore(backend.Blob("hosts"));
        settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(value =>
        {
            value.PrtgEnabled = true;
            value.PrtgUrl = loopback.BaseUrl;
            value.PrtgAuthMode = PrtgAuthModes.Token;
            value.PrtgApiTokenEnc = "loopback-only-fixture-token";
            value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            value.PrtgTimeoutSeconds = 10;
        });
        host = hosts.Upsert(new WebHost { HostName = "channel-worker-fixture", IpAddress = "192.0.2.80", Active = true });
        policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value =>
        {
            value.Revision = "channel-worker-policy-r1";
            value.CoreSystemId = "channel-worker-fixture-core";
            value.SourceGeneration = "channel-worker-source-r1";
            value.EndpointHint = EfPrtgObservationStore.SourceHintFor(loopback.BaseUrl);
            value.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            value.HostIds = [host.HostId];
            value.SensorIds = [11, 12];
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
        });
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = 22, HostId = host.HostId,
            HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok
        }]);
        backend.PrtgStore().UpsertSensors(new long[] { 11L, 12L }.Select(id => new PrtgSensorRow
        { Objid = id, DeviceObjid = 22, SensorType = "CPU" }).ToArray(), DateTime.UtcNow);
        foreach (var sensorId in new long[] { 11, 12 })
            backend.PrtgStore().BindObservedResource(sensorId, host.HostId, "channel-worker-source-r1",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));

        group = new UserGroupStore(backend.Blob("user_groups")).Upsert(new UserGroup
        { GroupName = "channel-worker-admin", Role = UserRole.Admin, Active = true });
        user = new UserStore(backend.Blob("users"), backend.Blob("user_last_login")).Upsert(new WebUser
        { Account = "channel-worker-user", Active = true, GroupIds = [group.GroupId] });

        var visibility = new ServiceCollection()
            .AddScoped<IVisibilityService>(_ => new TestVisibility([host.HostId]))
            .BuildServiceProvider();
        visibilityProvider = visibility;
        plan = FixturePlan(settings.Get().Revision, policy.Get().Revision);
        PrtgRequestBudget.Shared.SetAdmissionPlan(plan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableControllerQueueSurvivesWorkerRestartAndFeedsReadOnlyChannelDropdownWithoutBinding(bool hasObservedChannel)
    {
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        if (hasObservedChannel)
            backend.PrtgStore().SetObservedChannel(11, policy.Get().SourceGeneration, new string('A', 64));
        var queueWorker = NewWorker(withAuthorizationScope: false);
        var controller = NewController(queueWorker);
        var edit = Assert.IsType<ApiResponse<PrtgTrustedSamplingBindingEditDto>>(
            Assert.IsType<OkObjectResult>(controller.GetBinding(11)).Value);
        var accepted = Assert.IsType<AcceptedResult>(controller.QueueChannelDiscovery(11,
            new PrtgTrustedSamplingChannelDiscoveryRequest(edit.Data!.ExpectedSettingsRevision,
                edit.Data.ExpectedPolicyRevision, edit.Data.ExpectedHostSnapshotVersion,
                edit.Data.ExpectedIdentityEpoch, edit.Data.ExpectedChannelGeneration,
                edit.Data.ExpectedBindingRevision, "")));
        var queued = Assert.IsType<ApiResponse<PrtgTrustedSamplingChannelDiscoveryDto>>(accepted.Value).Data!;
        Assert.Equal("queued", queued.Status);
        Assert.True(queued.ReadOnly);
        Assert.False(queued.AuthorizesQualification);
        Assert.False(queued.AuthorizesProfile);

        var restartedWorker = NewWorker(withAuthorizationScope: true);
        restartedWorker.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        Assert.True(await restartedWorker.RunOneAsync(CancellationToken.None));
        var completed = restartedWorker.Read(queued.JobId)!;
        Assert.Equal("completed", completed.Status);
        Assert.Single(completed.Channels);
        Assert.Equal(1, loopback.RequestCount);
        Assert.Contains("content=channels", Assert.Single(loopback.Requests));
        Assert.Contains("id=11", Assert.Single(loopback.Requests));
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));

        var response = controller.ReadChannelDiscovery(queued.JobId);
        var dto = Assert.IsType<PrtgTrustedSamplingChannelDiscoveryDto>(
            Assert.IsType<ApiResponse<PrtgTrustedSamplingChannelDiscoveryDto>>(
                Assert.IsType<OkObjectResult>(response).Value).Data);
        Assert.Equal("completed", dto.Status);
        Assert.Equal("9007199254740993", dto.Channels.Single().ChannelObjectId);
        Assert.True(dto.ReadOnly);
        Assert.False(dto.AuthorizesQualification);
        Assert.False(dto.AuthorizesProfile);
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task WorkerRechecksMaintainRoleBeforeAndAfterGet()
    {
        var before = QueueDirect(NewWorker(false), 11);
        DeactivateMaintainers();
        var blocked = NewWorker(true);
        blocked.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        Assert.True(await blocked.RunOneAsync(CancellationToken.None));
        Assert.Equal("failed-stale", blocked.Read(before.JobId)!.Status);
        Assert.Equal(0, loopback.RequestCount);

        ReactivateMaintainers();
        var after = QueueDirect(NewWorker(false), 12);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loopback.OnRequest = async _ =>
        {
            loopback.RequestSeen.TrySetResult();
            await gate.Task;
            return LoopbackResponse.Json("""{"channels":[{"objid":9002,"name":"Memory","lastvalue_raw":"4","unit":"%"}]}""");
        };
        var worker = NewWorker(true);
        worker.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        var running = worker.RunOneAsync(CancellationToken.None);
        await loopback.RequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DeactivateMaintainers();
        gate.SetResult();
        Assert.True(await running);
        var failed = worker.Read(after.JobId)!;
        Assert.Equal("failed-stale", failed.Status);
        Assert.Empty(failed.Channels);
        Assert.Equal(1, loopback.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observed_channel_metadata_changes_before_or_during_get_are_rejected(bool duringGet)
    {
        backend.PrtgStore().SetObservedChannel(11, policy.Get().SourceGeneration, new string('A', 64));
        var queued = QueueDirect(NewWorker(false), 11);
        void ChangeChannel() => backend.PrtgStore().SetObservedChannel(11,
            policy.Get().SourceGeneration, new string('B', 64));
        if (duringGet)
            loopback.OnRequest = _ =>
            {
                ChangeChannel();
                return Task.FromResult(LoopbackResponse.Json("""{"channels":[]}"""));
            };
        else ChangeChannel();
        var worker = NewWorker(true);
        worker.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        var result = worker.Read(queued.JobId)!;
        Assert.Equal("failed-stale", result.Status);
        Assert.Empty(result.Channels);
        Assert.Equal(duringGet ? 1 : 0, loopback.RequestCount);
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task TwoWorkersShareOneDatabaseLeaseAndOnlyOneCanSendGeneralGet()
    {
        var first = QueueDirect(NewWorker(false), 11);
        QueueDirect(NewWorker(false), 12);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loopback.OnRequest = async _ =>
        {
            loopback.RequestSeen.TrySetResult();
            await gate.Task;
            return LoopbackResponse.Json("""{"channels":[]}""");
        };
        var workerA = NewWorker(true);
        var workerB = NewWorker(true);
        workerA.AdmissionOverride = workerB.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        var taskA = workerA.RunOneAsync(CancellationToken.None);
        await loopback.RequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await workerB.RunOneAsync(CancellationToken.None));
        gate.SetResult();
        Assert.True(await taskA);
        Assert.Equal(1, loopback.RequestCount);
        Assert.Equal("completed", workerA.Read(first.JobId)!.Status);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("oversize")]
    public async Task RedirectAndOversizeResponsesFailClosed(string mode)
    {
        var queued = QueueDirect(NewWorker(false), 11);
        loopback.OnRequest = _ => Task.FromResult(mode == "redirect"
            ? new LoopbackResponse(HttpStatusCode.Redirect, "", loopback.BaseUrl + "redirect-target")
            : LoopbackResponse.Json("{" + new string(' ', 128 * 1024) + "}"));
        var worker = NewWorker(true);
        worker.AdmissionOverride = (_, _) => (plan, "fixture-only-capacity-plan");
        Assert.True(await worker.RunOneAsync(CancellationToken.None));
        var failed = worker.Read(queued.JobId)!;
        Assert.Equal("failed", failed.Status);
        Assert.Empty(failed.Channels);
        Assert.Equal(1, loopback.RequestCount);
        Assert.DoesNotContain(loopback.Requests, request => request.Contains("redirect-target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredDeadlineNeverSendsGet()
    {
        var firstWorker = NewWorker(false);
        var now = DateTimeOffset.UtcNow.AddMinutes(-6);
        var job = MakeJob(11, now, user.UserId);
        firstWorker.Queue(job);
        var restarted = NewWorker(true);
        Assert.False(await restarted.RunOneAsync(CancellationToken.None));
        Assert.Equal("expired", restarted.Read(job.JobId)!.Status);
        Assert.Equal(0, loopback.RequestCount);
    }

    private PrtgChannelDiscoveryJobStore.Job QueueDirect(PrtgTrustedSamplingChannelDiscoveryHostedService service, long sensorId)
    {
        var now = DateTimeOffset.UtcNow;
        return service.Queue(MakeJob(sensorId, now, user.UserId));
    }

    private PrtgChannelDiscoveryJobStore.Job MakeJob(long sensorId, DateTimeOffset queuedAt, long requesterId)
    {
        var identity = backend.PrtgStore().GetResourceIdentity(sensorId);
        var binding = new PrtgTrustedSamplingBindingStore(backend).Get(sensorId);
        return new PrtgChannelDiscoveryJobStore.Job
        {
            JobId = Guid.NewGuid().ToString("N"), RequesterUserId = requesterId, SensorObjid = sensorId,
            SettingsRevision = settings.Get().Revision, PolicyRevision = policy.Get().Revision,
            SourceGeneration = policy.Get().SourceGeneration, HostSnapshotVersion = hosts.CapturePrtgSnapshot().Version,
            ResourceGeneration = identity.Generation, IdentityEpoch = identity.Epoch,
            ChannelGeneration = identity.ChannelGeneration, BindingRevision = binding?.BindingRevision ?? 0,
            IdentityChannelFingerprint = identity.ChannelFingerprint,
            BindingFingerprint = binding?.BindingFingerprint ?? "", QueuedAtUtc = queuedAt,
            DeadlineUtc = queuedAt + PrtgTrustedSamplingChannelDiscoveryHostedService.JobDeadline
        };
    }

    private PrtgTrustedSamplingChannelDiscoveryHostedService NewWorker(bool withAuthorizationScope)
    {
        var scopes = withAuthorizationScope ? visibilityProvider.GetRequiredService<IServiceScopeFactory>() : null;
        return new PrtgTrustedSamplingChannelDiscoveryHostedService(backend, hosts,
            NullLogger<PrtgTrustedSamplingChannelDiscoveryHostedService>.Instance,
            value =>
            {
                value.PrtgUrl = loopback.BaseUrl;
                return PrtgClientFactory.Create(value, budget: PrtgRequestBudget.Shared);
            }, scopes);
    }

    private PrtgTrustedSamplingProfilesController NewController(PrtgTrustedSamplingChannelDiscoveryHostedService channelWorker) =>
        new(backend, hosts, new TestVisibility([host.HostId]), new TestUser(user.UserId),
            new PrtgTrustedSamplingProfileRefreshHostedService(backend, hosts,
                NullLogger<PrtgTrustedSamplingProfileRefreshHostedService>.Instance), channelWorker);

    private void DeactivateMaintainers()
    {
        group.Active = false;
        new UserGroupStore(backend.Blob("user_groups")).Upsert(group);
    }

    private void ReactivateMaintainers()
    {
        group.Active = true;
        new UserGroupStore(backend.Blob("user_groups")).Upsert(group);
    }

    private static PrtgCapacityAdmissionPlan FixturePlan(string settingsRevision, string policyRevision)
    {
        var now = DateTimeOffset.UtcNow;
        var version = (PrtgRequestBudget.Shared.CurrentAdmissionPlan?.Version ?? 0) + 1;
        return new(new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
            new string('E', 64), new string('F', 64), new string('1', 64), new string('2', 64),
            settingsRevision, policyRevision, .5, .5, .5, now, now.AddHours(1), "channel-discovery-fixture", version);
    }

    private sealed class TestUser(long id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public long UserId => id;
        public string Account => "channel-worker-user";
        public string DisplayName => "Channel Worker User";
        public IReadOnlySet<Capability> Capabilities => new HashSet<Capability> { Capability.Maintain, Capability.ViewAll };
        public bool IsServerAdmin => false;
        public bool Has(Capability capability) => Capabilities.Contains(capability);
    }

    private sealed class TestVisibility(IEnumerable<long> visible) : IVisibilityService
    {
        private readonly HashSet<long> ids = visible.ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIds() => ids;
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot) => ids;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => ids;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => ids;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long hostId) { if (!ids.Contains(hostId)) throw new UnauthorizedAccessException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => ids;
    }

    private sealed record LoopbackResponse(HttpStatusCode Status, string Body, string? Location = null)
    {
        public static LoopbackResponse Json(string body) => new(HttpStatusCode.OK, body);
    }

    private sealed class LoopbackPrtg : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task pump;
        private readonly ConcurrentQueue<string> requests = new();
        private readonly SemaphoreSlim observed = new(0);
        public string BaseUrl { get; }
        public int RequestCount => requests.Count;
        public string[] Requests => requests.ToArray();
        public Func<HttpListenerRequest, Task<LoopbackResponse>> OnRequest { get; set; } = _ =>
            Task.FromResult(LoopbackResponse.Json("""{"channels":[{"objid":9007199254740993,"name":"CPU Load","lastvalue_raw":"17.5","unit":"%"}]}"""));
        public TaskCompletionSource RequestSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoopbackPrtg()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(BaseUrl);
            listener.Start();
            pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                var path = context.Request.Url?.PathAndQuery ?? "";
                requests.Enqueue(path);
                observed.Release();
                RequestSeen.TrySetResult();
                try
                {
                    var reply = await OnRequest(context.Request);
                    context.Response.StatusCode = (int)reply.Status;
                    if (reply.Location is not null) context.Response.RedirectLocation = reply.Location;
                    var bytes = Encoding.UTF8.GetBytes(reply.Body);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    if (bytes.Length > 0) await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (Exception)
                { try { context.Response.Abort(); } catch { } }
            }
        }

        public async Task<string> WaitForRequestAsync()
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            return requests.Last();
        }

        public ValueTask DisposeAsync()
        {
            if (listener.IsListening) listener.Stop();
            listener.Close();
            observed.Dispose();
            return new ValueTask(pump);
        }
    }

    public async ValueTask DisposeAsync()
    {
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        await loopback.DisposeAsync();
        if (visibilityProvider is IDisposable disposable) disposable.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
