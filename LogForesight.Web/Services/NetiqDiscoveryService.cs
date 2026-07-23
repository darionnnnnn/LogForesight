using System.Collections.Concurrent;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 主動探索匯入（docs/SCALE-2000-PLAN.md §1、§5.3 D-3）：掃描 Sentinel → 網段分組 → 勾選 → 排入匯入。
/// 掃描結果暫存 token（30 分鐘），Enqueue 只接受掃描過的 IP，避免前端硬塞任意主機。
///
/// **不在這裡直接落盤主機異動**（§5.3 D-3 定案）：勾選送出只是把請求寫進佇列，
/// 實際的主機新增/更新/孤兒復活由批次執行開頭處理（見 <see cref="NetiqImportApplier"/>
/// 與批次 Program.cs 的 --apply-netiq-imports）。兩千台量級下主機異動集中在批次時段
/// 一次落盤，避免上班時間 Web 端操作與正在跑的批次互踩。
/// </summary>
public interface INetiqDiscoveryService
{
    List<NetiqScanTargetDto> GetScanTargets();
    Task<NetiqScanResultDto> ScanAsync(string serverName, CancellationToken ct);

    /// <summary>把使用者勾選的主機排入匯入佇列（不落盤主機異動）</summary>
    NetiqQueueEntryDto Enqueue(NetiqImportRequest request);

    List<NetiqQueueEntryDto> GetQueue();

    /// <summary>取消一筆排程中的請求；已套用/失敗/已取消的請求不可再取消</summary>
    void CancelQueueEntry(string queueId);
}

public class NetiqDiscoveryService : INetiqDiscoveryService
{
    private readonly INetiqServerCatalog _catalog;
    private readonly INetiqDirectoryClient _client;
    private readonly IHostStore _hosts;
    private readonly INetiqImportQueueStore _queue;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    private static readonly ConcurrentDictionary<string, PendingScan> Pending = new();
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(30);

    public NetiqDiscoveryService(
        INetiqServerCatalog catalog,
        INetiqDirectoryClient client,
        IHostStore hosts,
        INetiqImportQueueStore queue,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _catalog = catalog;
        _client = client;
        _hosts = hosts;
        _queue = queue;
        _currentUser = currentUser;
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

    public NetiqQueueEntryDto Enqueue(NetiqImportRequest request)
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

        if (wanted.Count == 0)
            throw DomainException.Validation("請至少勾選一台主機。");

        var entry = new NetiqImportQueueEntry
        {
            ServerName = scan.ServerName,
            SelectedIps = wanted,
            RequestedByAccount = _currentUser.Account,
            RequestedAt = DateTime.Now,
            Status = NetiqImportQueueStatuses.Pending
        };
        _queue.Save(entry);

        // 排入當下只記「排入」，不記「已匯入」——實際落盤發生在批次執行時，
        // 稽核措辭必須誠實反映「這件事現在還沒發生」
        _audit.Record(
            action: AuditActions.NetiqImportEnqueue,
            summary: $"排入 NetIQ 匯入佇列（Sentinel：{scan.ServerName}，{wanted.Count} 台），將於下次批次執行時套用",
            targetKind: "netiq_import_queue",
            targetId: entry.QueueId,
            detail: new { entry.QueueId, scan.ServerName, Count = wanted.Count });

        return ToDto(entry);
    }

    public List<NetiqQueueEntryDto> GetQueue() => _queue.GetAll().Select(ToDto).ToList();

    public void CancelQueueEntry(string queueId)
    {
        var entry = _queue.Get(queueId) ?? throw DomainException.NotFound("找不到這筆匯入請求。");
        if (entry.Status != NetiqImportQueueStatuses.Pending)
            throw DomainException.Validation("這筆請求已經處理過，無法取消。");

        entry.Status = NetiqImportQueueStatuses.Cancelled;
        _queue.Save(entry);

        _audit.Record(
            action: AuditActions.NetiqImportCancel,
            summary: $"取消 NetIQ 匯入佇列請求（Sentinel：{entry.ServerName}，{entry.SelectedIps.Count} 台）",
            targetKind: "netiq_import_queue",
            targetId: entry.QueueId,
            detail: new { entry.QueueId, entry.ServerName });
    }

    private static NetiqQueueEntryDto ToDto(NetiqImportQueueEntry entry) => new()
    {
        QueueId = entry.QueueId,
        ServerName = entry.ServerName,
        HostCount = entry.SelectedIps.Count,
        RequestedByAccount = entry.RequestedByAccount,
        RequestedAt = entry.RequestedAt,
        Status = entry.Status,
        StatusText = entry.Status switch
        {
            NetiqImportQueueStatuses.Pending => "排程中，將於下次批次執行時套用",
            NetiqImportQueueStatuses.Applied => "已套用",
            NetiqImportQueueStatuses.Failed => "套用失敗",
            NetiqImportQueueStatuses.Cancelled => "已取消",
            _ => entry.Status
        },
        AppliedAt = entry.AppliedAt,
        Added = entry.Added,
        Updated = entry.Updated,
        Revived = entry.Revived,
        FailureReason = entry.FailureReason
    };

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
