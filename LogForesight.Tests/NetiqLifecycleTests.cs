using LogForesight;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel 孤兒主機停用（docs/SCALE-2000-PLAN.md §1.7）。</summary>
public class NetiqOrphanSweeperTests
{
    private readonly FakeHostStore _hosts = new();

    private WebHost AddNetiq(string ip, string? sentinel, bool active = true, string? orphanedFrom = null) =>
        _hosts.Upsert(new WebHost
        {
            HostName = ip, IpAddress = ip, Source = "netiq",
            NetiqServer = sentinel, Active = active, OrphanedFromSentinel = orphanedFrom
        });

    [Fact]
    public void Sentinel移除_停用所屬主機並記原名()
    {
        AddNetiq("10.1.2.11", "SENTINEL-OLD");
        AddNetiq("10.1.2.12", "SENTINEL-A");

        var result = NetiqOrphanSweeper.Sweep(_hosts, new[] { "SENTINEL-A" });

        Assert.Equal(1, result.OrphanedCount);
        var orphaned = _hosts.FindByName("10.1.2.11")!;
        Assert.False(orphaned.Active);
        Assert.Equal("SENTINEL-OLD", orphaned.OrphanedFromSentinel);
        // 名單內的主機不動
        Assert.True(_hosts.FindByName("10.1.2.12")!.Active);
    }

    [Fact]
    public void 空名單有NetIQ主機_安全跳過不停用()
    {
        AddNetiq("10.1.2.11", "SENTINEL-A");

        var result = NetiqOrphanSweeper.Sweep(_hosts, System.Array.Empty<string>());

        Assert.True(result.SkippedForSafety);
        Assert.Equal(0, result.OrphanedCount);
        Assert.True(_hosts.FindByName("10.1.2.11")!.Active);   // 沒被停用
    }

    [Fact]
    public void 待歸屬主機_不受任何Sentinel移除影響()
    {
        AddNetiq("10.1.2.11", null);   // NetiqServer = null（待歸屬）

        var result = NetiqOrphanSweeper.Sweep(_hosts, new[] { "SENTINEL-A" });

        Assert.Equal(0, result.OrphanedCount);
        Assert.True(_hosts.FindByName("10.1.2.11")!.Active);
    }

    [Fact]
    public void 人工停用_不被覆寫孤兒標記()
    {
        // Active=false 但沒有孤兒標記＝人工停用；sweeper 不碰
        AddNetiq("10.1.2.11", "SENTINEL-OLD", active: false);

        NetiqOrphanSweeper.Sweep(_hosts, new[] { "SENTINEL-A" });

        Assert.Null(_hosts.FindByName("10.1.2.11")!.OrphanedFromSentinel);
    }

    [Fact]
    public void local來源主機_不受影響()
    {
        _hosts.Upsert(new WebHost { HostName = "MYPC", Source = "local", Active = true });

        NetiqOrphanSweeper.Sweep(_hosts, new[] { "SENTINEL-A" });

        Assert.True(_hosts.FindByName("MYPC")!.Active);
    }
}

/// <summary>NetIQ 主動探索匯入（docs/SCALE-2000-PLAN.md §1）。</summary>
public class NetiqDiscoveryServiceTests
{
    private readonly FakeHostStore _hosts = new();

    private sealed class FakeClient : INetiqDirectoryClient
    {
        private readonly List<NetiqDiscoveredHost> _hosts;
        public FakeClient(params (string name, string ip)[] hosts) =>
            _hosts = hosts.Select(h => new NetiqDiscoveredHost(h.name, h.ip)).ToList();
        public System.Threading.Tasks.Task<List<NetiqDiscoveredHost>> ListHostsAsync(SentinelServer s, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(_hosts);
    }

    private NetiqDiscoveryService Create(FakeClient client, params SentinelServer[] servers) =>
        new(new NetiqHostServiceTests.FakeNetiqServerCatalog(servers), client, _hosts, new FakeAuditService());

    private static SentinelServer Discoverable(string name) =>
        new() { Name = name, BaseUrl = "https://x", Username = "u", Password = "p" };

    [Fact]
    public async System.Threading.Tasks.Task 掃描_依網段分組並標記已登錄()
    {
        _hosts.Upsert(new WebHost { HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq", NetiqServer = "S1" });

        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11"), ("SRV-B", "10.1.2.12"), ("AP-1", "10.9.9.5")),
            Discoverable("S1"));
        var result = await svc.ScanAsync("S1", default);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.Subnets.Count);   // 10.1.2.0/24 與 10.9.9.0/24
        var sub = result.Subnets.First(s => s.Cidr == "10.1.2.0/24");
        Assert.Equal(1, sub.ExistingCount);
        Assert.True(sub.Hosts.First(h => h.IpAddress == "10.1.2.11").Exists);
        Assert.False(sub.Hosts.First(h => h.IpAddress == "10.1.2.12").Exists);
    }

    [Fact]
    public async System.Threading.Tasks.Task 掃描_孤兒主機標記重疊()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            Active = false, OrphanedFromSentinel = "SENTINEL-OLD"
        });

        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11")), Discoverable("S1"));
        var result = await svc.ScanAsync("S1", default);

        var host = result.Subnets[0].Hosts[0];
        Assert.True(host.OrphanOverlap);
        Assert.Equal("SENTINEL-OLD", host.OrphanedFrom);
        Assert.Equal(1, result.Subnets[0].OrphanOverlapCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_重疊主機復活保留HostId與關聯()
    {
        var orphan = _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            Active = false, OrphanedFromSentinel = "SENTINEL-OLD",
            GroupIds = new List<long> { 5 }, OwnerUserIds = new List<long> { 9 }
        });

        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11")), Discoverable("SENTINEL-NEW"));
        var scan = await svc.ScanAsync("SENTINEL-NEW", default);
        var result = svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.11" } });

        Assert.Equal(1, result.Revived);
        var revived = _hosts.FindByName("10.1.2.11")!;
        Assert.Equal(orphan.HostId, revived.HostId);       // 同 HostId
        Assert.True(revived.Active);
        Assert.Null(revived.OrphanedFromSentinel);
        Assert.Equal("SENTINEL-NEW", revived.NetiqServer);
        Assert.Equal(new[] { 5L }, revived.GroupIds);       // 群組保留
        Assert.Equal(new[] { 9L }, revived.OwnerUserIds);   // 負責人保留
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_新主機以IP為HostName登錄()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);
        var result = svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        Assert.Equal(1, result.Added);
        var added = _hosts.FindByName("10.1.2.50")!;
        Assert.Equal("netiq", added.Source);
        Assert.Equal("S1", added.NetiqServer);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_只接受掃描過的IP()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);
        // 塞一個沒掃描到的 IP，應被忽略
        var result = svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.9.9.9" } });

        Assert.Equal(0, result.Added);
        Assert.Null(_hosts.FindByName("10.9.9.9"));
    }

    [Fact]
    public async System.Threading.Tasks.Task 掃描_未設定帳密的Sentinel擋下()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")),
            new SentinelServer { Name = "S1" });   // 無帳密

        await Assert.ThrowsAsync<DomainException>(() => svc.ScanAsync("S1", default));
    }

    [Fact]
    public void 掃描目標_標示可否掃描()
    {
        var svc = Create(new FakeClient(), Discoverable("S1"), new SentinelServer { Name = "S2" });
        var targets = svc.GetScanTargets();

        Assert.True(targets.First(t => t.Name == "S1").CanDiscover);
        Assert.False(targets.First(t => t.Name == "S2").CanDiscover);
        Assert.NotNull(targets.First(t => t.Name == "S2").Reason);
    }
}
