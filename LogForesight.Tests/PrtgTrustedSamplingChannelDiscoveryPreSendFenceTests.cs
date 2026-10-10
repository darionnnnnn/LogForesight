using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogForesight.Tests;

[Collection("PrtgSnapshotSharedBudget")]
public sealed class PrtgTrustedSamplingChannelDiscoveryPreSendFenceTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lf-channel-presend-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend backend;
    private readonly HostStore hosts;
    private readonly SystemSettingsStore settings;
    private readonly PrtgMonitoringPolicyStore policy;
    private readonly LoopbackPrtg prtg = new();
    private readonly WebHost host;
    private readonly UserGroup group;
    private readonly WebUser user;
    private readonly MutableVisibility visibility;
    private readonly ServiceProvider services;
    private PrtgCapacityAdmissionPlan plan;
    private int tableSendCount;

    public PrtgTrustedSamplingChannelDiscoveryPreSendFenceTests()
    {
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        Directory.CreateDirectory(root);
        backend = new StorageBackend(new StorageSettings
        { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(root, "fence.db")}" }, root);
        hosts = new HostStore(backend.Blob("hosts"));
        settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = prtg.BaseUrl;
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = "pre-send-fence-fixture-token";
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        host = hosts.Upsert(new WebHost { HostName = "pre-send-fence-fixture", IpAddress = "192.0.2.90", Active = true });
        policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p =>
        {
            p.Revision = "pre-send-policy-r1";
            p.CoreSystemId = "pre-send-core";
            p.SourceGeneration = "pre-send-source-r1";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(prtg.BaseUrl);
            p.ValidFrom = DateTimeOffset.UtcNow.AddDays(-1);
            p.HostIds = [host.HostId];
            p.SensorIds = [11];
            p.SourceTimeZoneId = "UTC";
            p.SourceCultureName = "en-US";
        });
        backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = 22, HostId = host.HostId,
            HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok
        }]);
        backend.PrtgStore().UpsertSensors([new PrtgSensorRow { Objid = 11, DeviceObjid = 22, SensorType = "CPU" }], DateTime.UtcNow);
        backend.PrtgStore().BindObservedResource(11, host.HostId, policy.Get().SourceGeneration,
            PrtgTimelineResourceIdentity.BuildResourceFingerprint("22", "CPU", "created", 0));
        group = new UserGroupStore(backend.Blob("user_groups")).Upsert(new UserGroup
        { GroupName = "pre-send-maintainers", Role = UserRole.Admin, Active = true });
        user = new UserStore(backend.Blob("users"), backend.Blob("user_last_login")).Upsert(new WebUser
        { Account = "pre-send-user", Active = true, GroupIds = [group.GroupId] });
        visibility = new MutableVisibility(host.HostId);
        services = new ServiceCollection().AddScoped<IVisibilityService>(_ => visibility).BuildServiceProvider();
        plan = FixturePlan(settings.Get().Revision, policy.Get().Revision);
        PrtgRequestBudget.Shared.SetAdmissionPlan(plan);
    }

    [Theory]
    [InlineData("maintain")]
    [InlineData("visibility")]
    [InlineData("source")]
    public async Task RevocationWhileGeneralQuotaIsHeldPreventsWireGetAndFormalWrites(string revoke)
    {
        var worker = NewWorker();
        var job = worker.Queue(MakeJob());
        using var held = await HoldGeneralQuotaAsync();
        var running = worker.RunOneAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(running.IsCompleted);
        Assert.Equal(0, prtg.RequestCount);

        switch (revoke)
        {
            case "maintain":
                group.Active = false;
                new UserGroupStore(backend.Blob("user_groups")).Upsert(group);
                break;
            case "visibility":
                visibility.Visible.Clear();
                break;
            case "source":
                policy.Update(p => p.SourceGeneration = "pre-send-source-revoked");
                break;
        }

        held.Dispose();
        Assert.True(await running);
        Assert.Equal("failed-stale", worker.Read(job.JobId)!.Status);
        Assert.Equal(0, prtg.RequestCount);
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task CurrentFencesAfterQuotaWaitAllowBoundedGeneralGet()
    {
        var worker = NewWorker();
        var job = worker.Queue(MakeJob());
        using var held = await HoldGeneralQuotaAsync();
        var running = worker.RunOneAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(running.IsCompleted);
        held.Dispose();

        Assert.True(await running);
        Assert.Equal("completed", worker.Read(job.JobId)!.Status);
        Assert.Equal(1, prtg.RequestCount);
        Assert.Contains("content=channels", Assert.Single(prtg.Requests));
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    [Fact]
    public async Task RevocationDuringGeneralPurposePacingWaitPreventsWireGetAndTableSendAccounting()
    {
        // Prime the General lane's last-send timestamp without any transport request, then
        // tighten its rate so the worker's lease waits several seconds before marking sent.
        plan = FixturePlan(settings.Get().Revision, policy.Get().Revision, 100);
        PrtgRequestBudget.Shared.SetAdmissionPlan(plan);
        using (var primingLease = await PrtgRequestBudget.Shared.AcquireAsync(PrtgEndpointCategory.Table,
            CancellationToken.None, PrtgRequestPurpose.General, plan.Fingerprint))
            await primingLease.MarkRequestSentAsync();
        plan = FixturePlan(settings.Get().Revision, policy.Get().Revision, .25);
        PrtgRequestBudget.Shared.SetAdmissionPlan(plan);

        var worker = NewWorker();
        var job = worker.Queue(MakeJob());
        var running = worker.RunOneAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(running.IsCompleted);
        Assert.Equal(0, prtg.RequestCount);
        Assert.Equal(0, Volatile.Read(ref tableSendCount));

        visibility.Visible.Clear();
        Assert.True(await running);
        Assert.Equal("failed-stale", worker.Read(job.JobId)!.Status);
        Assert.Equal(0, prtg.RequestCount);
        Assert.Equal(0, Volatile.Read(ref tableSendCount));
        Assert.Null(new PrtgTrustedSamplingBindingStore(backend).Get(11));
        Assert.Empty(backend.PrtgStore().GetTrustedSamplingProfiles([11]));
    }

    private async Task<LeaseGroup> HoldGeneralQuotaAsync()
    {
        var leases = new List<PrtgBudgetLease>();
        for (var i = 0; i < 4; i++)
        {
            var lease = await PrtgRequestBudget.Shared.AcquireAsync(PrtgEndpointCategory.Other,
                CancellationToken.None, PrtgRequestPurpose.General, plan.Fingerprint);
            await lease.MarkRequestSentAsync();
            leases.Add(lease);
        }
        return new LeaseGroup(leases);
    }

    private PrtgTrustedSamplingChannelDiscoveryHostedService NewWorker() =>
        new(backend, hosts, NullLogger<PrtgTrustedSamplingChannelDiscoveryHostedService>.Instance,
            value =>
            {
                value.PrtgUrl = prtg.BaseUrl;
                var client = PrtgClientFactory.Create(value, budget: PrtgRequestBudget.Shared);
                client.TableRequestSent = () => Interlocked.Increment(ref tableSendCount);
                return client;
            }, services.GetRequiredService<IServiceScopeFactory>())
        { AdmissionOverride = (_, _) => (plan, "pre-send-test-plan") };

    private PrtgChannelDiscoveryJobStore.Job MakeJob()
    {
        var identity = backend.PrtgStore().GetResourceIdentity(11);
        var now = DateTimeOffset.UtcNow;
        var job = new PrtgChannelDiscoveryJobStore.Job
        {
            JobId = Guid.NewGuid().ToString("N"), RequesterUserId = user.UserId, SensorObjid = 11,
            SettingsRevision = settings.Get().Revision, PolicyRevision = policy.Get().Revision,
            SourceGeneration = policy.Get().SourceGeneration, HostSnapshotVersion = hosts.CapturePrtgSnapshot().Version,
            ResourceGeneration = identity.Generation, IdentityEpoch = identity.Epoch,
            ChannelGeneration = identity.ChannelGeneration, IdentityChannelFingerprint = identity.ChannelFingerprint,
            BindingFingerprint = "", QueuedAtUtc = now,
            DeadlineUtc = now + PrtgTrustedSamplingChannelDiscoveryHostedService.JobDeadline
        };
        var invalid = new List<string>();
        if (job.RequesterUserId < 0) invalid.Add(nameof(job.RequesterUserId));
        if (job.SettingsRevision.Length is < 1 or > 128) invalid.Add(nameof(job.SettingsRevision));
        if (job.PolicyRevision.Length is < 1 or > 128) invalid.Add(nameof(job.PolicyRevision));
        if (job.SourceGeneration.Length is < 1 or > 128) invalid.Add(nameof(job.SourceGeneration));
        if (job.ResourceGeneration.Length is < 1 or > 128) invalid.Add(nameof(job.ResourceGeneration));
        if (job.IdentityEpoch <= 0) invalid.Add(nameof(job.IdentityEpoch));
        if (job.ChannelGeneration.Length > 128) invalid.Add(nameof(job.ChannelGeneration));
        if (job.BindingFingerprint.Length > 128) invalid.Add(nameof(job.BindingFingerprint));
        if (job.IdentityChannelFingerprint.Length > 128) invalid.Add(nameof(job.IdentityChannelFingerprint));
        if (job.DeadlineUtc - job.QueuedAtUtc > TimeSpan.FromMinutes(5)) invalid.Add("deadline");
        Assert.Empty(invalid);
        return job;
    }

    private static PrtgCapacityAdmissionPlan FixturePlan(string settingsRevision, string policyRevision,
        double generalRequestsPerSecond = 10)
    {
        var now = DateTimeOffset.UtcNow;
        var version = (PrtgRequestBudget.Shared.CurrentAdmissionPlan?.Version ?? 0) + 1;
        return new(new string('A', 64), new string('B', 64), new string('C', 64), new string('D', 64),
            new string('E', 64), new string('F', 64), new string('1', 64), new string('2', 64),
            settingsRevision, policyRevision, 10, 10, generalRequestsPerSecond, now, now.AddHours(1),
            "pre-send-fence-test", version);
    }

    private sealed class LeaseGroup(List<PrtgBudgetLease> leases) : List<PrtgBudgetLease>(leases), IDisposable
    {
        public void Dispose() { foreach (var lease in this) lease.Dispose(); }
    }

    private sealed class MutableVisibility(long hostId) : IVisibilityService
    {
        public HashSet<long> Visible { get; } = [hostId];
        public IReadOnlySet<long> GetVisibleHostIds() => Visible;
        public IReadOnlySet<long> GetVisibleHostIds(PrtgHostSnapshot snapshot) => Visible;
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => Visible;
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => Visible;
        public List<WebHost> GetVisibleHosts() => [];
        public void EnsureVisible(long id) { if (!Visible.Contains(id)) throw new UnauthorizedAccessException(); }
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => Visible;
    }

    private sealed class LoopbackPrtg : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task pump;
        private readonly ConcurrentQueue<string> requests = new();
        public string BaseUrl { get; }
        public int RequestCount => requests.Count;
        public string[] Requests => requests.ToArray();

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
                requests.Enqueue(context.Request.Url?.PathAndQuery ?? "");
                var bytes = Encoding.UTF8.GetBytes("""{"channels":[{"objid":9011,"name":"CPU","unit":"%"}]}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (listener.IsListening) listener.Stop();
            listener.Close();
            return new ValueTask(pump);
        }
    }

    public async ValueTask DisposeAsync()
    {
        PrtgRequestBudget.Shared.ClearAdmissionPlan();
        await prtg.DisposeAsync();
        await services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
