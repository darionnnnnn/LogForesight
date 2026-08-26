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
    private readonly Func<SentinelServer, string, CancellationToken, IReadOnlyCollection<string>?, Action<string, int>?, int?, Task<NetiqDiscoveryResult>>? _handler;

    public FakeClient(params (string name, string ip)[] hosts) =>
        _hosts = hosts.Select(h => new NetiqDiscoveredHost(h.name, h.ip)).ToList();

    public FakeClient(Func<SentinelServer, string, CancellationToken, IReadOnlyCollection<string>?, Action<string, int>?, int?, Task<NetiqDiscoveryResult>> handler)
    {
        _hosts = new List<NetiqDiscoveredHost>();
        _handler = handler;
    }

    public FakeClient(Exception exceptionToThrow)
    {
        _hosts = new List<NetiqDiscoveredHost>();
        _handler = (_, _, _, _, _, _) => Task.FromException<NetiqDiscoveryResult>(exceptionToThrow);
    }

    /// <summary>最後一次收到的已登錄 IP——驗證重掃時 Service 有把它們傳下去（§3.3）</summary>
    public IReadOnlyCollection<string>? LastKnownIps { get; private set; }
    public Action<string, int>? LastProgressCallback { get; private set; }
    public int? LastTotalBudgetSecondsOverride { get; private set; }

    public Task<NetiqDiscoveryResult> ListHostsAsync(
        SentinelServer s, string subnetPrefix, CancellationToken ct,
        IReadOnlyCollection<string>? knownIps = null,
        Action<string, int>? onProgress = null,
        int? totalBudgetSecondsOverride = null)
    {
        LastKnownIps = knownIps;
        LastProgressCallback = onProgress;
        LastTotalBudgetSecondsOverride = totalBudgetSecondsOverride;

        if (_handler != null)
            return _handler(s, subnetPrefix, ct, knownIps, onProgress, totalBudgetSecondsOverride);

        // 真實 client 在 server 端就排除已登錄主機，回傳裡不含它們——替身照做。
        // 不然「Service 端合成已登錄主機回結果」的行為會被替身的寬鬆回傳掩蓋，
        // 測試就驗不到那段。
        var hosts = knownIps is { Count: > 0 }
            ? _hosts.Where(h => !knownIps.Contains(h.IpAddress, StringComparer.OrdinalIgnoreCase)).ToList()
            : _hosts;

        return Task.FromResult(new NetiqDiscoveryResult { Hosts = hosts, CoverageNote = "test" });
    }
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
