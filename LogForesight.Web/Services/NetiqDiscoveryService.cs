using System.Collections.Concurrent;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 主動探索匯入（docs/SCALE-2000-PLAN.md §1）：掃描 Sentinel → 網段分組 → 勾選 → 匯入。
/// 掃描結果暫存 token（30 分鐘），Import 只接受掃描過的 IP，避免前端硬塞任意主機。
/// </summary>
public interface INetiqDiscoveryService
{
    List<NetiqScanTargetDto> GetScanTargets();
    Task<NetiqScanResultDto> ScanAsync(string serverName, CancellationToken ct);
    NetiqImportResultDto Import(NetiqImportRequest request);
}

public class NetiqDiscoveryService : INetiqDiscoveryService
{
    private readonly INetiqServerCatalog _catalog;
    private readonly INetiqDirectoryClient _client;
    private readonly IHostStore _hosts;
    private readonly IAuditService _audit;

    private static readonly ConcurrentDictionary<string, PendingScan> Pending = new();
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(30);

    public NetiqDiscoveryService(
        INetiqServerCatalog catalog,
        INetiqDirectoryClient client,
        IHostStore hosts,
        IAuditService audit)
    {
        _catalog = catalog;
        _client = client;
        _hosts = hosts;
        _audit = audit;
    }

    public List<NetiqScanTargetDto> GetScanTargets() =>
        _catalog.GetServers()
            .Select(s => new NetiqScanTargetDto
            {
                Name = s.Name,
                CanDiscover = s.CanDiscover,
                Reason = s.CanDiscover ? null : "尚未在批次 appsettings.json 設定探索帳號密碼"
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<NetiqScanResultDto> ScanAsync(string serverName, CancellationToken ct)
    {
        var server = _catalog.GetServer(serverName)
                     ?? throw DomainException.Validation($"找不到 Sentinel「{serverName}」。");
        if (!server.CanDiscover)
            throw DomainException.Validation($"Sentinel「{serverName}」尚未設定探索帳密，無法主動掃描。");

        List<NetiqDiscoveredHost> discovered;
        try
        {
            discovered = await _client.ListHostsAsync(server, ct);
        }
        catch (NetiqDiscoveryException ex)
        {
            throw DomainException.Validation(ex.Message);
        }

        // 同 IP 去重（保留第一筆）
        discovered = discovered
            .GroupBy(h => h.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        CleanupExpired();
        var token = Guid.NewGuid().ToString("N");
        Pending[token] = new PendingScan(server.Name, discovered, DateTime.Now);

        var byName = _hosts.GetAll().ToDictionary(h => h.HostName, StringComparer.OrdinalIgnoreCase);

        var subnets = discovered
            .GroupBy(h => Slash24(h.IpAddress))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var hosts = g.Select(h =>
                {
                    byName.TryGetValue(h.IpAddress, out var existing);
                    var orphan = existing?.OrphanedFromSentinel;
                    return new NetiqScanHostDto
                    {
                        HostName = h.HostName,
                        IpAddress = h.IpAddress,
                        Exists = existing != null && orphan == null && existing.MergedInto == null,
                        OrphanOverlap = orphan != null,
                        OrphanedFrom = orphan
                    };
                }).OrderBy(h => h.IpAddress, StringComparer.OrdinalIgnoreCase).ToList();

                return new NetiqSubnetDto
                {
                    Cidr = g.Key,
                    TotalCount = hosts.Count,
                    ExistingCount = hosts.Count(h => h.Exists),
                    OrphanOverlapCount = hosts.Count(h => h.OrphanOverlap),
                    Hosts = hosts
                };
            })
            .ToList();

        return new NetiqScanResultDto
        {
            Token = token,
            Server = server.Name,
            TotalCount = discovered.Count,
            Subnets = subnets
        };
    }

    public NetiqImportResultDto Import(NetiqImportRequest request)
    {
        CleanupExpired();
        if (!Pending.TryGetValue(request.Token, out var scan))
            throw DomainException.Validation("掃描結果已逾期或不存在，請重新掃描。");

        // 只接受掃描過的 IP（前端不能硬塞任意主機）
        var scannedIps = scan.Hosts.ToDictionary(h => h.IpAddress, StringComparer.OrdinalIgnoreCase);
        var wanted = request.SelectedIps
            .Where(ip => scannedIps.ContainsKey(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new NetiqImportResultDto();
        foreach (var ip in wanted)
        {
            var existing = _hosts.FindByName(ip);

            if (existing?.OrphanedFromSentinel != null)
            {
                // 重疊復活：同 HostId 復活，歷史/群組/負責人零斷裂
                existing.Active = true;
                existing.NetiqServer = scan.ServerName;
                existing.OrphanedFromSentinel = null;
                _hosts.Upsert(existing);
                result.Revived++;
            }
            else if (existing != null)
            {
                existing.NetiqServer = scan.ServerName;
                existing.Active = true;
                _hosts.Upsert(existing);
                result.Updated++;
            }
            else
            {
                _hosts.Upsert(new WebHost
                {
                    HostName = ip,
                    IpAddress = ip,
                    IpUpdatedAt = DateTime.Now,
                    NetiqServer = scan.ServerName,
                    Source = "netiq",
                    Active = true,
                    GroupIds = new List<long>(),
                    OwnerUserIds = new List<long>()
                });
                result.Added++;
            }
        }

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"從 NetIQ 匯入主機（Sentinel：{scan.ServerName}）：新增 {result.Added}、更新 {result.Updated}" +
                     (result.Revived > 0 ? $"、重新啟用 {result.Revived}" : ""),
            targetKind: "host",
            targetId: null,
            detail: new { scan.ServerName, result.Added, result.Updated, result.Revived, Count = wanted.Count });

        return result;
    }

    /// <summary>IP 的 /24 網段字串（10.1.2.37 → 10.1.2.0/24）。非法 IP 歸到「其他」</summary>
    private static string Slash24(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return "其他";
        return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
    }

    private static void CleanupExpired()
    {
        var cutoff = DateTime.Now - ScanLifetime;
        foreach (var entry in Pending.Where(p => p.Value.CreatedAt < cutoff).ToList())
            Pending.TryRemove(entry.Key, out _);
    }

    private record PendingScan(string ServerName, List<NetiqDiscoveredHost> Hosts, DateTime CreatedAt);
}
