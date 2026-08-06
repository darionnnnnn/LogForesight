using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Sentinel 孤兒主機停用（docs/archive/HISTORY.md §1.7）。</summary>
public class NetiqOrphanSweeperTests
{
    private readonly FakeHostStore _hosts = new();

    private WebHost AddNetiq(string ip, long? sentinelId, string? netiqServer = null, bool active = true, string? orphanedFrom = null) =>
        _hosts.Upsert(new WebHost
        {
            HostName = ip, IpAddress = ip, Source = "netiq",
            SentinelId = sentinelId, NetiqServer = netiqServer, Active = active, OrphanedFromSentinel = orphanedFrom
        });

    [Fact]
    public void Sentinel被刪除_停用所屬主機並記原名()
    {
        AddNetiq("10.1.2.11", sentinelId: 99, netiqServer: "SENTINEL-OLD");   // 99 已不存在於現存名單
        AddNetiq("10.1.2.12", sentinelId: 1, netiqServer: "SENTINEL-A");

        var result = NetiqOrphanSweeper.Sweep(_hosts, new long[] { 1 });

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
        AddNetiq("10.1.2.11", sentinelId: 1, netiqServer: "SENTINEL-A");

        var result = NetiqOrphanSweeper.Sweep(_hosts, System.Array.Empty<long>());

        Assert.True(result.SkippedForSafety);
        Assert.Equal(0, result.OrphanedCount);
        Assert.True(_hosts.FindByName("10.1.2.11")!.Active);   // 沒被停用
    }

    [Fact]
    public void 待歸屬主機_不受任何Sentinel移除影響()
    {
        AddNetiq("10.1.2.11", sentinelId: null);   // SentinelId = null（待歸屬）

        var result = NetiqOrphanSweeper.Sweep(_hosts, new long[] { 1 });

        Assert.Equal(0, result.OrphanedCount);
        Assert.True(_hosts.FindByName("10.1.2.11")!.Active);
    }

    [Fact]
    public void 人工停用_不被覆寫孤兒標記()
    {
        // Active=false 但沒有孤兒標記＝人工停用；sweeper 不碰
        AddNetiq("10.1.2.11", sentinelId: 99, netiqServer: "SENTINEL-OLD", active: false);

        NetiqOrphanSweeper.Sweep(_hosts, new long[] { 1 });

        Assert.Null(_hosts.FindByName("10.1.2.11")!.OrphanedFromSentinel);
    }

    [Fact]
    public void local來源主機_不受影響()
    {
        _hosts.Upsert(new WebHost { HostName = "MYPC", Source = "local", Active = true });

        NetiqOrphanSweeper.Sweep(_hosts, new long[] { 1 });

        Assert.True(_hosts.FindByName("MYPC")!.Active);
    }
}

/// <summary>
/// NetIQ 主動探索匯入（docs/archive/HISTORY.md §1；docs/archive/HISTORY.md 定案 7）。
/// 勾選送出即立即落盤，不再有排入佇列/取消的中間狀態。
/// </summary>
public class NetiqDiscoveryServiceTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeSentinelStore _sentinels = new();
    private readonly FakeImportLogStore _importLogs = new();
    private readonly RecordingAuditService _audit = new();

    // FakeClient 已搬到 TestDoubles\NetiqFakes.cs。

    private NetiqDiscoveryService Create(FakeClient client, params SentinelServer[] servers) =>
        new(new FakeNetiqServerCatalog(servers), client, _hosts, _hostGroups, _sentinels,
            _importLogs, new FakeCurrentUser(), _audit);

    private const string AnySubnet = "10.1.2";

    private static SentinelServer Discoverable(string name) =>
        new() { Name = name, BaseUrl = "https://x", Username = "u", Password = "p" };

    [Fact]
    public async System.Threading.Tasks.Task 掃描_依網段分組並標記已登錄()
    {
        _hosts.Upsert(new WebHost { HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq", NetiqServer = "S1" });

        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11"), ("SRV-B", "10.1.2.12"), ("AP-1", "10.9.9.5")),
            Discoverable("S1"));
        var result = await svc.ScanAsync("S1", AnySubnet, default);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.Subnets.Count);   // 10.1.2.0/24 與 10.9.9.0/24
        var sub = result.Subnets.First(s => s.Cidr == "10.1.2.0/24");
        Assert.Equal(1, sub.ExistingCount);
        Assert.True(sub.Hosts.First(h => h.IpAddress == "10.1.2.11").Exists);
        Assert.False(sub.Hosts.First(h => h.IpAddress == "10.1.2.12").Exists);
    }

    // ── 重掃增量：已登錄主機在 Sentinel 端就排除（docs/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.3）──

    /// <summary>
    /// 重掃時已登錄主機不必重新「發現」——在 server 端排除掉，沒有新主機時一次查詢就結束。
    /// 它們由 Service 合成回結果，所以精靈畫面的「既有／新發現」分組完全不變。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task 重掃_已登錄主機不重查但仍出現在結果中()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://x", Active = true });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", DisplayName = "SRV-A",
            Source = "netiq", NetiqServer = "S1", SentinelId = sentinel.SentinelId
        });

        // client 只回新主機（真實 client 因 NOT 子句本來就查不到已登錄的那台）
        var client = new FakeClient(("SRV-A", "10.1.2.11"), ("SRV-B", "10.1.2.12"));
        var result = await Create(client, Discoverable("S1")).ScanAsync("S1", AnySubnet, default);

        // 已登錄的那台被當成 knownIps 傳下去
        Assert.Equal(new[] { "10.1.2.11" }, client.LastKnownIps);

        // 但畫面上兩台都在，且已登錄的那台標為既有
        var sub = Assert.Single(result.Subnets);
        Assert.Equal(2, sub.Hosts.Count);
        Assert.True(sub.Hosts.First(h => h.IpAddress == "10.1.2.11").Exists);
        Assert.False(sub.Hosts.First(h => h.IpAddress == "10.1.2.12").Exists);
        // 合成回來的顯示名稱取主機清單的 DisplayName（比事件裡的 sn 可靠）
        Assert.Equal("SRV-A", sub.Hosts.First(h => h.IpAddress == "10.1.2.11").HostName);
    }

    /// <summary>
    /// **孤兒主機不算已登錄**：它出現在掃描結果裡是有意義的訊號（「被標為孤兒的主機
    /// 又在回報事件了」正是「重疊復活」要抓的）。把它排除掉再無條件合成回去，
    /// 等於宣稱它還活著——那是假訊息。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task 重掃_孤兒主機不列入排除清單()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://x", Active = true });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            SentinelId = sentinel.SentinelId, Active = false, OrphanedFromSentinel = "SENTINEL-OLD"
        });

        var client = new FakeClient(("SRV-A", "10.1.2.11"));
        var result = await Create(client, Discoverable("S1")).ScanAsync("S1", AnySubnet, default);

        Assert.Empty(client.LastKnownIps!);
        // 真的掃到了才標重疊復活
        Assert.True(result.Subnets[0].Hosts[0].OrphanOverlap);
    }

    /// <summary>其他 Sentinel 轄下的主機不該被排除——它們對這台而言就是未知主機</summary>
    [Fact]
    public async System.Threading.Tasks.Task 重掃_別台Sentinel的主機不列入排除清單()
    {
        _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://x", Active = true });
        var s2 = _sentinels.Upsert(new Sentinel { Name = "S2", BaseUrl = "https://y", Active = true });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq", SentinelId = s2.SentinelId
        });

        var client = new FakeClient(("SRV-A", "10.1.2.11"));
        await Create(client, Discoverable("S1")).ScanAsync("S1", AnySubnet, default);

        Assert.Empty(client.LastKnownIps!);
    }

    /// <summary>不同網段的已登錄主機不列入排除——排除清單只該涵蓋本次掃描的網段</summary>
    [Fact]
    public async System.Threading.Tasks.Task 重掃_網段外的已登錄主機不列入排除清單()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1", BaseUrl = "https://x", Active = true });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.9.9.5", IpAddress = "10.9.9.5", Source = "netiq", SentinelId = sentinel.SentinelId
        });

        var client = new FakeClient(("SRV-B", "10.1.2.12"));
        await Create(client, Discoverable("S1")).ScanAsync("S1", AnySubnet, default);

        Assert.Empty(client.LastKnownIps!);
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
        var result = await svc.ScanAsync("S1", AnySubnet, default);

        var host = result.Subnets[0].Hosts[0];
        Assert.True(host.OrphanOverlap);
        Assert.Equal("SENTINEL-OLD", host.OrphanedFrom);
        Assert.Equal(1, result.Subnets[0].OrphanOverlapCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_立即落盤主機異動()
    {
        var sentinel = _sentinels.Upsert(new Sentinel { Name = "S1" });
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        var result = svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        Assert.Equal("S1", result.ServerName);
        Assert.Equal(1, result.Added);
        var added = _hosts.FindByName("10.1.2.50");
        Assert.NotNull(added);   // 立即落盤，不用等批次
        Assert.Equal(sentinel.SentinelId, added!.SentinelId);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_只接受掃描過的IP()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        // 塞一個沒掃描到的 IP，應被忽略；若因此變成空清單則直接擋下
        Assert.Throws<DomainException>(() =>
            svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.9.9.9" } }));
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_記入匯入紀錄()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        var log = Assert.Single(_importLogs.Entries);
        Assert.Equal("Netiq", log.Kind);
        Assert.Equal("S1", log.FileName);   // 沒有檔案可言，借這個欄位顯示來源 Sentinel
        Assert.Equal(1, log.AddedCount);
    }

    /// <summary>token 用過即丟——避免使用者連按兩次「匯入」把同一批主機重複套用（雖然 Upsert 本身冪等，
    /// 但已用過的 token 理應失效，行為要跟「請重新掃描」一致，不能悄悄再套用一次）</summary>
    [Fact]
    public async System.Threading.Tasks.Task 匯入_同一個token不能重複套用()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);
        var request = new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } };

        svc.Import(request);

        Assert.Throws<DomainException>(() => svc.Import(request));
    }

    /// <summary>批次套用邏輯（NetiqImportApplier）：重疊主機復活保留 HostId 與關聯</summary>
    [Fact]
    public void 套用_重疊主機復活保留HostId與關聯()
    {
        var sentinels = new FakeSentinelStore();
        var newSentinel = sentinels.Upsert(new Sentinel { Name = "SENTINEL-NEW" });

        var orphan = _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            Active = false, OrphanedFromSentinel = "SENTINEL-OLD",
            GroupIds = new List<long> { 5 }, OwnerUserIds = new List<long> { 9 }
        });

        var outcome = NetiqImportApplier.Apply("SENTINEL-NEW", new[] { "10.1.2.11" }, _hosts, sentinels);

        Assert.Equal(1, outcome.Revived);
        var revived = _hosts.FindByName("10.1.2.11")!;
        Assert.Equal(orphan.HostId, revived.HostId);       // 同 HostId
        Assert.True(revived.Active);
        Assert.Null(revived.OrphanedFromSentinel);
        Assert.Equal("SENTINEL-NEW", revived.NetiqServer);
        Assert.Equal(newSentinel.SentinelId, revived.SentinelId);
        Assert.Equal(new[] { 5L }, revived.GroupIds);       // 群組保留
        Assert.Equal(new[] { 9L }, revived.OwnerUserIds);   // 負責人保留
    }

    // ── 掃描匯入的 OS（docs/LINUX-RULES.md §3：只套用在本次新增的主機）──────

    [Fact]
    public void 套用_新主機採用精靈選定的OS()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });

        NetiqImportApplier.Apply("S1", new[] { "10.1.2.60" }, _hosts, sentinels, os: "linux");

        Assert.Equal("linux", _hosts.FindByName("10.1.2.60")!.Os);
    }

    [Fact]
    public void 套用_未指定OS時新主機預設windows()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });

        NetiqImportApplier.Apply("S1", new[] { "10.1.2.61" }, _hosts, sentinels);

        Assert.Equal("windows", _hosts.FindByName("10.1.2.61")!.Os);
    }

    /// <summary>
    /// 既有主機的 OS 一律不動——與群組指派同一原則：匯入不是隱性改設定。
    /// 改 OS 等於把這台主機的偵測面整個換掉，靜默改掉是很難察覺的行為變更。
    /// </summary>
    [Fact]
    public void 套用_既有主機的OS不被匯入改動()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });
        _hosts.Upsert(new WebHost { HostName = "10.1.2.62", IpAddress = "10.1.2.62", Source = "netiq", Os = "linux" });

        NetiqImportApplier.Apply("S1", new[] { "10.1.2.62" }, _hosts, sentinels, os: "windows");

        Assert.Equal("linux", _hosts.FindByName("10.1.2.62")!.Os);
    }

    [Fact]
    public void 套用_復活的孤兒主機OS也不被匯入改動()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "SENTINEL-NEW" });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.63", IpAddress = "10.1.2.63", Source = "netiq",
            Active = false, OrphanedFromSentinel = "SENTINEL-OLD", Os = "linux"
        });

        var outcome = NetiqImportApplier.Apply("SENTINEL-NEW", new[] { "10.1.2.63" }, _hosts, sentinels, os: "windows");

        Assert.Equal(1, outcome.Revived);
        Assert.Equal("linux", _hosts.FindByName("10.1.2.63")!.Os);
    }

    [Fact]
    public void 套用_新主機以IP為HostName登錄()
    {
        var sentinels = new FakeSentinelStore();
        var s1 = sentinels.Upsert(new Sentinel { Name = "S1" });

        var outcome = NetiqImportApplier.Apply("S1", new[] { "10.1.2.50" }, _hosts, sentinels);

        Assert.Equal(1, outcome.Added);
        var added = _hosts.FindByName("10.1.2.50")!;
        Assert.Equal("netiq", added.Source);
        Assert.Equal("S1", added.NetiqServer);
        Assert.Equal(s1.SentinelId, added.SentinelId);
    }

    [Fact]
    public void 套用_既有主機更新Sentinel歸屬()
    {
        var sentinels = new FakeSentinelStore();
        var newSentinel = sentinels.Upsert(new Sentinel { Name = "NEW" });

        _hosts.Upsert(new WebHost { HostName = "10.1.2.50", IpAddress = "10.1.2.50", Source = "netiq", NetiqServer = "OLD", Active = false });

        var outcome = NetiqImportApplier.Apply("NEW", new[] { "10.1.2.50" }, _hosts, sentinels);

        Assert.Equal(1, outcome.Updated);
        var updated = _hosts.FindByName("10.1.2.50")!;
        Assert.Equal("NEW", updated.NetiqServer);
        Assert.Equal(newSentinel.SentinelId, updated.SentinelId);
        Assert.True(updated.Active);
    }

    /// <summary>ServerName 對不到現存的 Sentinel（已被刪除）時，SentinelId 維持 null 但不阻斷整批匯入</summary>
    [Fact]
    public void 套用_ServerName對不到現存Sentinel_SentinelId維持null不阻斷匯入()
    {
        var outcome = NetiqImportApplier.Apply("已刪除的Sentinel", new[] { "10.1.2.70" }, _hosts, new FakeSentinelStore());

        Assert.Equal(1, outcome.Added);
        var added = _hosts.FindByName("10.1.2.70")!;
        Assert.Null(added.SentinelId);
        Assert.Equal("已刪除的Sentinel", added.NetiqServer);
    }

    [Fact]
    public async System.Threading.Tasks.Task 掃描_未設定帳密的Sentinel擋下()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.50")),
            new SentinelServer { Name = "S1" });   // 無帳密

        await Assert.ThrowsAsync<DomainException>(() => svc.ScanAsync("S1", AnySubnet, default));
    }

    // ── 套用：網段群組指派（docs/archive/HISTORY.md 定案 8） ─────────────

    [Fact]
    public void 套用_新主機依網段指派套用群組()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });

        var outcome = NetiqImportApplier.Apply(
            "S1", new[] { "10.1.2.11", "10.9.9.5" }, _hosts, sentinels,
            groupByIp: new Dictionary<string, long?> { ["10.1.2.11"] = 7L });

        Assert.Equal(2, outcome.Added);
        Assert.Equal(new[] { 7L }, _hosts.FindByName("10.1.2.11")!.GroupIds);
        Assert.Empty(_hosts.FindByName("10.9.9.5")!.GroupIds);   // 未指派＝未分組
    }

    [Fact]
    public void 套用_既有主機的群組不受網段指派影響()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });

        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.11", IpAddress = "10.1.2.11", Source = "netiq",
            NetiqServer = "OLD", Active = true, GroupIds = new List<long> { 5 }
        });

        var outcome = NetiqImportApplier.Apply(
            "S1", new[] { "10.1.2.11" }, _hosts, sentinels,
            groupByIp: new Dictionary<string, long?> { ["10.1.2.11"] = 99L });

        Assert.Equal(1, outcome.Updated);
        Assert.Equal(new[] { 5L }, _hosts.FindByName("10.1.2.11")!.GroupIds);   // 群組不動
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_網段指派existing套用到新主機()
    {
        var groupId = _hostGroups.Upsert(new HostGroup { GroupName = "既有群組" }).GroupId;
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11"), ("SRV-B", "10.9.9.5")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest
        {
            Token = scan.Token,
            SelectedIps = new() { "10.1.2.11", "10.9.9.5" },
            GroupAssignments = new()
            {
                new NetiqSubnetGroupAssignment { Cidr = "10.1.2.0/24", Mode = "existing", HostGroupId = groupId },
                new NetiqSubnetGroupAssignment { Cidr = "10.9.9.0/24", Mode = "skip" }
            }
        });

        Assert.Equal(new[] { groupId }, _hosts.FindByName("10.1.2.11")!.GroupIds);
        Assert.Empty(_hosts.FindByName("10.9.9.5")!.GroupIds);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_網段指派new自動建立群組()
    {
        var svc = Create(new FakeClient(("SRV-A", "10.1.2.11")), Discoverable("S1"));
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest
        {
            Token = scan.Token,
            SelectedIps = new() { "10.1.2.11" },
            GroupAssignments = new()
            {
                new NetiqSubnetGroupAssignment { Cidr = "10.1.2.0/24", Mode = "new", NewGroupName = "新建群組" }
            }
        });

        var created = _hostGroups.FindByName("新建群組");
        Assert.NotNull(created);
        Assert.Equal(new[] { created!.GroupId }, _hosts.FindByName("10.1.2.11")!.GroupIds);
    }

    // ── 探索掃描回填真實機器名（docs/NETIQ-API-REFERENCE.md §3.4：網段範圍掃描投影 sn 欄位）──

    [Fact]
    public async System.Threading.Tasks.Task 匯入_新主機帶入掃描時取得的真實機器名()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });
        var svc = new NetiqDiscoveryService(
            new FakeNetiqServerCatalog(Discoverable("S1")),
            new FakeClient(("srv-dc01", "10.1.2.50")), _hosts, _hostGroups, sentinels,
            _importLogs, new FakeCurrentUser(), _audit);
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        Assert.Equal("srv-dc01", _hosts.FindByName("10.1.2.50")!.DisplayName);
    }

    /// <summary>
    /// 掃描沒拿到 sn 時 <c>NetiqDiscoveredHost.HostName</c> 會退回 IP，但那不是名字——寫成
    /// DisplayName 會讓主機頁顯示「10.1.2.50（10.1.2.50）」這種廢話，也讓夜間批次 TouchNetiq
    /// 之後補到的真名看起來像是被人改過。名字未知就維持 null，等批次回填。
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task 匯入_掃描未取得機器名時DisplayName維持空白而非填入IP()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });
        var svc = new NetiqDiscoveryService(
            new FakeNetiqServerCatalog(Discoverable("S1")),
            new FakeClient(("10.1.2.50", "10.1.2.50")), _hosts, _hostGroups, sentinels,
            _importLogs, new FakeCurrentUser(), _audit);
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        Assert.Null(_hosts.FindByName("10.1.2.50")!.DisplayName);
    }

    [Fact]
    public async System.Threading.Tasks.Task 匯入_既有主機的DisplayName不被掃描結果覆蓋()
    {
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { Name = "S1" });
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.50", IpAddress = "10.1.2.50", Source = "netiq",
            NetiqServer = "OLD", Active = false, DisplayName = "人工核對過的名稱"
        });
        var svc = new NetiqDiscoveryService(
            new FakeNetiqServerCatalog(Discoverable("S1")),
            new FakeClient(("掃描回報的不同名稱", "10.1.2.50")), _hosts, _hostGroups, sentinels,
            _importLogs, new FakeCurrentUser(), _audit);
        var scan = await svc.ScanAsync("S1", AnySubnet, default);

        svc.Import(new NetiqImportRequest { Token = scan.Token, SelectedIps = new() { "10.1.2.50" } });

        // 既有主機（含復活的孤兒）：更新只動 NetiqServer/SentinelId/Active，DisplayName 不動——
        // 同 groupByIp/os 一致原則，匯入不隱性改既有主機欄位
        Assert.Equal("人工核對過的名稱", _hosts.FindByName("10.1.2.50")!.DisplayName);
    }
}
