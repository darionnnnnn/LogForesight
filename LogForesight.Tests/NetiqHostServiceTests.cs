using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// NetIQ 主機清單維護（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 A、IP 衝突軟處理）。
/// </summary>
public class NetiqHostServiceTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeNetiqServerCatalog _servers = new("SENTINEL-A", "SENTINEL-B");

    private NetiqHostService Create() =>
        new(_hosts, new FakeHostGroupStore(), new FakeUserStore(), _servers, new FakeAuditService());

    // ── 單筆登錄 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddHost_以IP為登錄名稱且來源標記為netiq()
    {
        var result = Create().AddHost(new AddNetiqHostRequest
        {
            IpAddress = "10.1.2.12",
            NetiqServer = "SENTINEL-A",
            RoleDesc = "OO部門資料庫"
        });

        Assert.Equal("10.1.2.12", result.HostName);
        Assert.Equal("10.1.2.12", result.IpAddress);
        Assert.Equal("netiq", result.Source);
        Assert.Equal("SENTINEL-A", result.NetiqServer);
    }

    [Fact]
    public void AddHost_IP格式不合法_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().AddHost(new AddNetiqHostRequest { IpAddress = "SRV-01" }));

        Assert.Contains("SRV-01", ex.Message);
    }

    /// <summary>
    /// Sentinel 打錯字的後果是這台主機永遠不會被任何一輪查詢帶到——
    /// 擋在輸入端比事後查「為什麼這台沒資料」便宜得多。
    /// </summary>
    [Fact]
    public void AddHost_Sentinel不在設定名單中_擋下並列出可選值()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12", NetiqServer = "SENTINEL-X" }));

        Assert.Contains("SENTINEL-A", ex.Message);
    }

    /// <summary>不填 Sentinel 是允許的：登錄的人未必知道主機在哪一台上，由批次自動確認</summary>
    [Fact]
    public void AddHost_未填Sentinel_登錄為待歸屬()
    {
        var result = Create().AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12" });

        Assert.Null(result.NetiqServer);
        Assert.Single(NetiqHostList.PendingAssignment(_hosts.GetAll()));
    }

    /// <summary>
    /// **IP 重複不擋**（軟處理）：汰換交接期間新舊兩台短暫共用同一個 IP 紀錄是真實情境，
    /// 擋下來會逼使用者先破壞既有資料才能繼續。改由衝突佇列讓人處置。
    /// </summary>
    [Fact]
    public void AddHost_IP與既有主機重複_照樣登錄並成為衝突組()
    {
        var service = Create();
        _hosts.Upsert(new WebHost { HostName = "SRV-OLD", IpAddress = "10.1.2.12", Source = "netiq" });

        service.AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12", NetiqServer = "SENTINEL-A" });

        var overview = service.GetOverview();
        Assert.Equal(1, overview.IpConflictCount);
        Assert.Equal(2, overview.IpConflicts[0].Hosts.Count);
    }

    // ── 批次貼上 ──────────────────────────────────────────────────────────────

    [Fact]
    public void BulkAdd_逐行登錄_註解與空行略過()
    {
        var result = Create().BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-A",
            Lines = "# 機房 A\n10.1.2.12,DB 主機\n\n10.1.2.13\n"
        });

        Assert.Equal(2, result.AddedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal("DB 主機", _hosts.FindByName("10.1.2.12")!.RoleDesc);
    }

    /// <summary>不合法的行不擋下整批（沿用 txt 清單「警告並略過」語意），但要說得出是哪一行、為什麼</summary>
    [Fact]
    public void BulkAdd_不合法的行_略過並回報行號與原因()
    {
        var result = Create().BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-A",
            Lines = "10.1.2.12\nSRV-01\n10.1.2.13"
        });

        Assert.Equal(2, result.AddedCount);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(2, skipped.LineNumber);
        Assert.Contains("SRV-01", skipped.Reason);
    }

    [Fact]
    public void BulkAdd_同一批內重複IP_只登錄一次並回報()
    {
        var result = Create().BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-A",
            Lines = "10.1.2.12\n10.1.2.12"
        });

        Assert.Equal(1, result.AddedCount);
        Assert.Single(result.Skipped);
    }

    /// <summary>重貼一次清單不該把已經填好的角色描述洗掉</summary>
    [Fact]
    public void BulkAdd_既有主機且該行未帶描述_保留原描述()
    {
        var service = Create();
        service.BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-A",
            Lines = "10.1.2.12,DB 主機"
        });

        var result = service.BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-A",
            Lines = "10.1.2.12"
        });

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal("DB 主機", _hosts.FindByName("10.1.2.12")!.RoleDesc);
    }

    [Fact]
    public void BulkAdd_Sentinel不在名單中_整批擋下()
    {
        Assert.Throws<DomainException>(() => Create().BulkAddHosts(new BulkAddNetiqHostsRequest
        {
            NetiqServer = "SENTINEL-X",
            Lines = "10.1.2.12"
        }));

        Assert.Empty(_hosts.GetAll());
    }

    // ── 停用／啟用與佇列 ─────────────────────────────────────────────────────

    [Fact]
    public void SetActive_停用後不再列入輪巡_但主機仍在()
    {
        var service = Create();
        var host = service.AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12", NetiqServer = "SENTINEL-A" });

        service.SetActive(host.HostId, false);

        Assert.Empty(NetiqHostList.Pollable(_hosts.GetAll()));
        Assert.Single(_hosts.GetAll());
    }

    [Fact]
    public void GetOverview_未分組主機列入待辦()
    {
        var service = Create();
        service.AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12", NetiqServer = "SENTINEL-A" });

        Assert.Equal(1, service.GetOverview().UngroupedCount);
    }

    [Fact]
    public void GetOverview_衝突組標示出實際被輪巡的那台()
    {
        var service = Create();
        service.AddHost(new AddNetiqHostRequest { IpAddress = "10.1.2.12", NetiqServer = "SENTINEL-A" });
        _hosts.Upsert(new WebHost { HostName = "SRV-DUP", IpAddress = "10.1.2.12", Source = "netiq" });

        var group = service.GetOverview().IpConflicts[0];

        Assert.True(group.Hosts[0].IsPolled);
        Assert.False(group.Hosts[1].IsPolled);
    }

    internal sealed class FakeNetiqServerCatalog : INetiqServerCatalog
    {
        private readonly List<SentinelServer> _servers;

        public FakeNetiqServerCatalog(params string[] names) =>
            _servers = names.Select(n => new SentinelServer { Name = n }).ToList();

        public FakeNetiqServerCatalog(params SentinelServer[] servers) => _servers = servers.ToList();

        public List<SentinelServer> GetServers() => _servers.ToList();

        public SentinelServer? GetServer(string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? null
                : _servers.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        public List<string> GetServerNames() => _servers.Select(s => s.Name).ToList();

        public bool IsKnownServer(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            _servers.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
