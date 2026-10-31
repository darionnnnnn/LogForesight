using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Configuration;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.Extensions.Hosting;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 維護類控制器的測試組裝：這些控制器的建構式相依一律必填，
/// 測試只關心的相依由案例自己給，其餘用這裡以同一個後端組出的真實服務補齊（皆不啟動背景工作）。
/// </summary>
internal static class PrtgControllerFactory
{
    public static ICurrentUser FullUser() => FakeCurrentUser.WithCapabilities(Capability.Maintain, Capability.ViewAll);

    public static ISystemSettingsStore SettingsStore(StorageBackend backend) =>
        new SystemSettingsStore(backend.Blob("system_settings"));

    public static IHostStore Hosts(StorageBackend backend) => new HostStore(backend.Blob("hosts"));

    public static IVisibilityService AllVisible(IHostStore hosts) => new AllHostsVisibility(hosts);

    public static IPrtgHostMapRefresher NoRefresh() => new NoRefresher();

    public static PrtgStructureSyncService Sync(StorageBackend backend, IHostStore? hosts = null,
        SchedulerRunState? scheduler = null, PrtgStructureSyncRunState? state = null) =>
        new(SettingsStore(backend), backend, state ?? new PrtgStructureSyncRunState(), scheduler ?? new SchedulerRunState(),
            hosts ?? Hosts(backend), new PrtgStructureSyncStatusStore(backend.Blob(PrtgStructureSyncStatusStore.BlobKey)),
            new PrtgBackfillRunState(), new FakeSentinelStore(), new DataVersionStamp());

    public static PrtgBackfillService Backfill(StorageBackend backend, IHostStore? hosts = null,
        SchedulerRunState? scheduler = null, PrtgStructureSyncService? sync = null)
    {
        hosts ??= Hosts(backend);
        scheduler ??= new SchedulerRunState();
        return new PrtgBackfillService(SettingsStore(backend), backend, new PrtgBackfillRunState(), new PrtgProbeRunState(),
            hosts, scheduler, new PrtgStructureSyncRunState(), new FakeSentinelStore(),
            sync ?? Sync(backend, hosts, scheduler));
    }

    public static PrtgProbeService Probe(StorageBackend backend, IHostStore? hosts = null,
        PrtgBackfillRunState? backfillState = null) =>
        new(SettingsStore(backend), new PrtgProbeRunState(), backfillState ?? new PrtgBackfillRunState(),
            backend, hosts ?? Hosts(backend), new FakeSentinelStore(), new SchedulerRunState(), new PrtgStructureSyncRunState());

    public static PrtgSnapshotHostedService Snapshot(StorageBackend backend, IHostStore? hosts = null)
    {
        hosts ??= Hosts(backend);
        var scheduler = new SchedulerRunState();
        var sync = Sync(backend, hosts, scheduler);
        return new PrtgSnapshotHostedService(SettingsStore(backend), backend, scheduler, sync,
            Backfill(backend, hosts, scheduler, sync), hosts, new FakeSentinelStore(), new PrtgProbeRunState(),
            new IdleLifetime());
    }

    public static PrtgScopePurgeAuthorizationService PurgeAuthorization(StorageBackend backend, IHostStore? hosts = null,
        ICurrentUser? user = null)
    {
        var tag = Guid.NewGuid().ToString("N");
        var users = new UserStore(backend.Blob("factory-users-" + tag), backend.Blob("factory-user-last-login-" + tag));
        var groups = new UserGroupStore(backend.Blob("factory-groups-" + tag));
        return new PrtgScopePurgeAuthorizationService(users, new UserCapabilityResolver(groups, hosts ?? Hosts(backend)),
            user ?? FullUser(), new WebAppSettings(), backend);
    }

    public static PrtgConflictQueryService Conflict(StorageBackend backend, IHostStore? hosts = null,
        PrtgDeviceIndexCache? cache = null) => new(backend, hosts ?? Hosts(backend), cache);

    public static PrtgScopeEstimateController ScopeEstimate(ISystemSettingsService settings, StorageBackend backend,
        IHostStore? hosts = null, PrtgSnapshotHostedService? snapshot = null, IVisibilityService? visibility = null,
        ICurrentUser? currentUser = null, PrtgScopePurgeAuthorizationService? scopePurgeAuthorization = null)
    {
        hosts ??= Hosts(backend);
        currentUser ??= FullUser();
        return new PrtgScopeEstimateController(settings, backend, hosts, snapshot ?? Snapshot(backend, hosts),
            visibility ?? AllVisible(hosts), currentUser,
            scopePurgeAuthorization ?? PurgeAuthorization(backend, hosts, currentUser));
    }

    public static PrtgBackfillAdminController BackfillAdmin(StorageBackend backend, IAuditService audit,
        PrtgProbeService? probe = null, PrtgBackfillService? backfill = null, PrtgStructureSyncService? structureSync = null,
        IHostStore? hosts = null, IVisibilityService? visibility = null, ICurrentUser? currentUser = null)
    {
        hosts ??= Hosts(backend);
        return new PrtgBackfillAdminController(audit, probe ?? Probe(backend, hosts),
            backfill ?? Backfill(backend, hosts), structureSync ?? Sync(backend, hosts), hosts,
            visibility ?? AllVisible(hosts), currentUser ?? FullUser());
    }

    public static PrtgHostMappingAdminController HostMapping(StorageBackend backend, IAuditService audit,
        IHostStore? hosts = null, IPrtgHostMapRefresher? mapRefresher = null, PrtgConflictQueryService? conflictQuery = null)
    {
        hosts ??= Hosts(backend);
        return new PrtgHostMappingAdminController(audit, backend, hosts, mapRefresher ?? NoRefresh(),
            conflictQuery ?? Conflict(backend, hosts));
    }

    private sealed class NoRefresher : IPrtgHostMapRefresher
    {
        public string? TryRefreshToday() => null;
        public string? TryApplyRuleUpgrade() => null;
    }

    private sealed class IdleLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class AllHostsVisibility(IHostStore hosts) : IVisibilityService
    {
        public IReadOnlySet<long> GetVisibleHostIds() => hosts.GetAll().Select(h => h.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long id) => GetVisibleHostIds();
        public IReadOnlyList<string> GetCaseGrantHostNames() => [];
        public bool IsCaseGrantOnly(long id) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long id) => null;
        public List<WebHost> GetVisibleHosts() => hosts.GetAll();
        public void EnsureVisible(long id) { }
    }
}
