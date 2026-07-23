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

    private readonly FakeNetiqImportQueueStore _queue = new();

    private NetiqDiscoveryService Create(FakeClient client, params SentinelServer[] servers) =>
        new(new NetiqHostServiceTests.FakeNetiqServerCatalog(servers), client, _hosts, _queue, new FakeCurrentUser(), new FakeAuditService());

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

    /// <summary>§5.3 D-3：套用改為兩階段——排入佇列（不落盤主機異動）＋批次套用（實際落盤）</summary>
    [Fact]
    public async System.Threading.Tasks.Task 排入佇列_不立即落盤主機異動()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);

        var entry = svc.Enqueue(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        Assert.Equal(NetiqImportQueueStatuses.Pending, entry.Status);
        Assert.Equal(1, entry.HostCount);
        Assert.Equal("test", entry.RequestedByAccount);
        Assert.Null(_hosts.FindByName("10.1.2.50"));   // 主機還沒被建立——落盤要等批次套用
    }

    [Fact]
    public async System.Threading.Tasks.Task 排入佇列_只接受掃描過的IP()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);

        // 塞一個沒掃描到的 IP，應被忽略；若因此變成空清單則直接擋下
        Assert.Throws<DomainException>(() =>
            svc.Enqueue(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.9.9.9" } }));
    }

    [Fact]
    public async System.Threading.Tasks.Task 取消排程中的請求_狀態變更為已取消()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);
        var entry = svc.Enqueue(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        svc.CancelQueueEntry(entry.QueueId);

        var updated = svc.GetQueue().First(e => e.QueueId == entry.QueueId);
        Assert.Equal(NetiqImportQueueStatuses.Cancelled, updated.Status);
    }

    [Fact]
    public async System.Threading.Tasks.Task 已套用的請求不可再取消()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", default);
        var entry = svc.Enqueue(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        var stored = _queue.Get(entry.QueueId)!;
        stored.Status = NetiqImportQueueStatuses.Applied;
        _queue.Save(stored);

        Assert.Throws<DomainException>(() => svc.CancelQueueEntry(entry.QueueId));
    }

    /// <summary>批次套用邏輯（NetiqImportApplier）：重疊主機復活保留 HostId 與關聯，與舊版 Import() 行為逐位相同</summary>
    [Fact]
    public void 套用_重疊主機復活保留HostId與關聯()
    {
        var orphan = _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            Active = false, OrphanedFromSentinel = "SENTINEL-OLD",
            GroupIds = new List<long> { 5 }, OwnerUserIds = new List<long> { 9 }
        });

        var outcome = NetiqImportApplier.Apply(
            new NetiqImportQueueEntry { ServerName = "SENTINEL-NEW", SelectedIps = new() { "10.1.2.11" } },
            _hosts);

        Assert.Equal(1, outcome.Revived);
        var revived = _hosts.FindByName("10.1.2.11")!;
        Assert.Equal(orphan.HostId, revived.HostId);       // 同 HostId
        Assert.True(revived.Active);
        Assert.Null(revived.OrphanedFromSentinel);
        Assert.Equal("SENTINEL-NEW", revived.NetiqServer);
        Assert.Equal(new[] { 5L }, revived.GroupIds);       // 群組保留
        Assert.Equal(new[] { 9L }, revived.OwnerUserIds);   // 負責人保留
    }

    [Fact]
    public void 套用_新主機以IP為HostName登錄()
    {
        var outcome = NetiqImportApplier.Apply(
            new NetiqImportQueueEntry { ServerName = "S1", SelectedIps = new() { "10.1.2.50" } },
            _hosts);

        Assert.Equal(1, outcome.Added);
        var added = _hosts.FindByName("10.1.2.50")!;
        Assert.Equal("netiq", added.Source);
        Assert.Equal("S1", added.NetiqServer);
    }

    [Fact]
    public void 套用_既有主機更新Sentinel歸屬()
    {
        _hosts.Upsert(new WebHost { HostName = "10.1.2.50", IpAddress = "10.1.2.50", Source = "netiq", NetiqServer = "OLD", Active = false });

        var outcome = NetiqImportApplier.Apply(
            new NetiqImportQueueEntry { ServerName = "NEW", SelectedIps = new() { "10.1.2.50" } },
            _hosts);

        Assert.Equal(1, outcome.Updated);
        var updated = _hosts.FindByName("10.1.2.50")!;
        Assert.Equal("NEW", updated.NetiqServer);
        Assert.True(updated.Active);
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

internal class FakeNetiqImportQueueStore : INetiqImportQueueStore
{
    private readonly List<NetiqImportQueueEntry> _items = new();

    public List<NetiqImportQueueEntry> GetAll() => _items.OrderByDescending(e => e.RequestedAt).ToList();

    public NetiqImportQueueEntry? Get(string queueId) =>
        _items.FirstOrDefault(e => e.QueueId == queueId);

    public void Save(NetiqImportQueueEntry entry)
    {
        var existing = _items.FirstOrDefault(e => e.QueueId == entry.QueueId);
        if (existing == null) { _items.Add(entry); return; }
        existing.Status = entry.Status;
        existing.AppliedAt = entry.AppliedAt;
        existing.Added = entry.Added;
        existing.Updated = entry.Updated;
        existing.Revived = entry.Revived;
        existing.FailureReason = entry.FailureReason;
    }
}

internal class FakeAuditLogStore : IAuditLogStore
{
    public readonly List<AuditEntry> Entries = new();

    public void Append(AuditEntry entry) => Entries.Add(entry);

    public PagedResult<AuditEntry> Query(AuditQuery query) => new() { Items = Entries, Total = Entries.Count };

    public int Count(DateTime from, DateTime to, string action) =>
        Entries.Count(e => e.Action == action && e.OccurredAt >= from && e.OccurredAt <= to);
}

/// <summary>批次端套用佇列（docs/SCALE-2000-PLAN.md §5.3 D-3）：與 Web 的 Enqueue 各自獨立測試，
/// 確保兩階段（排入／套用）拆開後行為仍逐位對得上舊版一次到位的 Import()。</summary>
public class NetiqImportQueueCliTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeNetiqImportQueueStore _queue = new();
    private readonly FakeAuditLogStore _audit = new();

    [Fact]
    public void 套用所有排程中項目_狀態變更為已套用並記錄筆數()
    {
        _queue.Save(new NetiqImportQueueEntry
        {
            ServerName = "S1", SelectedIps = new() { "10.1.2.60" },
            RequestedByAccount = "DOMAIN\\alice", RequestedAt = DateTime.Now
        });

        var count = NetiqImportQueueCli.ApplyPending(_queue, _hosts, _audit);

        Assert.Equal(1, count);
        var entry = _queue.GetAll().Single();
        Assert.Equal(NetiqImportQueueStatuses.Applied, entry.Status);
        Assert.Equal(1, entry.Added);
        Assert.NotNull(entry.AppliedAt);
        Assert.NotNull(_hosts.FindByName("10.1.2.60"));

        var audited = _audit.Entries.Single();
        Assert.Equal("DOMAIN\\alice", audited.Account);   // 稽核歸戶到排入當下的操作人，不是批次身分
        Assert.Equal(AuditResult.Ok, audited.Result);
    }

    [Fact]
    public void 已取消或已套用的項目不會被重複處理()
    {
        _queue.Save(new NetiqImportQueueEntry { ServerName = "S1", SelectedIps = new() { "10.1.2.61" }, Status = NetiqImportQueueStatuses.Cancelled });
        _queue.Save(new NetiqImportQueueEntry { ServerName = "S1", SelectedIps = new() { "10.1.2.62" }, Status = NetiqImportQueueStatuses.Applied });

        var count = NetiqImportQueueCli.ApplyPending(_queue, _hosts, _audit);

        Assert.Equal(0, count);
        Assert.Null(_hosts.FindByName("10.1.2.61"));
        Assert.Null(_hosts.FindByName("10.1.2.62"));
    }

    [Fact]
    public void 沒有排程中項目時回傳零()
    {
        Assert.Equal(0, NetiqImportQueueCli.ApplyPending(_queue, _hosts, _audit));
        Assert.Empty(_audit.Entries);
    }
}
