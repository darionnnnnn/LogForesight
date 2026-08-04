using System.Threading;
using System.Threading.Tasks;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：NetIQ 主機清單／掃描／匯入相關 ─────────────────────────────────
// FakeNetiqServerCatalog 原本是 NetiqHostServiceTests 裡的巢狀類別，因為
// HostAdminServiceTests／NetiqLifecycleTests／SentinelEventFetchServiceTests 都要用到，
// 之前只能用 NetiqHostServiceTests.FakeNetiqServerCatalog 這種跨類別限定名稱呼叫；
// 搬出來後不再需要限定名稱。FakeClient／FakeImportLogStore／FakeNetiqHostServiceForAdmin
// 目前雖只有各自的測試檔在用，但同屬 NetIQ 領域，一併集中在這裡。

internal sealed class FakeNetiqServerCatalog : INetiqServerCatalog
{
    private readonly List<SentinelServer> _servers;

    public FakeNetiqServerCatalog(params string[] names) =>
        _servers = names.Select((n, i) => new SentinelServer { Id = i + 1, Name = n }).ToList();

    public FakeNetiqServerCatalog(params SentinelServer[] servers) => _servers = servers.ToList();

    public SentinelServer? GetServer(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _servers.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public List<string> GetServerNames() => _servers.Select(s => s.Name).ToList();

    public bool IsKnownServer(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        _servers.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>HostAdminService 只需要 GetOverview()（IP 衝突偵測，§5.4 D-4 的 conflict 篩選）——
/// 這裡的測試不涉及 conflict 狀態篩選，回空清單即可</summary>
internal class FakeNetiqHostServiceForAdmin : INetiqHostService
{
    public NetiqOverviewDto GetOverview() => new();
    public HostDto AddHost(AddNetiqHostRequest request) => throw new NotSupportedException();
    public BulkAddResultDto BulkAddHosts(BulkAddNetiqHostsRequest request) => throw new NotSupportedException();
    public HostDto SetActive(long hostId, bool active) => throw new NotSupportedException();
}

internal sealed class FakeClient : INetiqDirectoryClient
{
    private readonly List<NetiqDiscoveredHost> _hosts;

    public FakeClient(params (string name, string ip)[] hosts) =>
        _hosts = hosts.Select(h => new NetiqDiscoveredHost(h.name, h.ip)).ToList();

    public Task<NetiqDiscoveryResult> ListHostsAsync(SentinelServer s, string subnetPrefix, CancellationToken ct) =>
        Task.FromResult(new NetiqDiscoveryResult { Hosts = _hosts, CoverageNote = "test" });
}

internal class FakeImportLogStore : IImportLogStore
{
    public readonly List<ImportLogEntry> Entries = new();

    public void Append(ImportLogEntry entry) => Entries.Add(entry);

    public List<ImportLogEntry> GetRecent(int count) =>
        Entries.OrderByDescending(e => e.CreatedAt).Take(count).ToList();

    public int Prune(int retentionDays)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var removed = Entries.RemoveAll(e => e.CreatedAt < cutoff);
        return removed;
    }
}
