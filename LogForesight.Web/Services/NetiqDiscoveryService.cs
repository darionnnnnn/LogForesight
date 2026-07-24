using System.Collections.Concurrent;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 主動探索匯入（docs/SCALE-2000-PLAN.md §1；docs/NETIQ-WEB-CONFIG-PLAN.md 定案 6、7、8）：
/// 新增 Sentinel（可選自動掃描）或選既有 Sentinel 掃描 → 網段分組 → 勾選 → （可選）指派群組 → 立即套用。
/// 掃描結果暫存 token（30 分鐘），套用只接受掃描過的 IP，避免前端硬塞任意主機。
///
/// **勾選送出即落盤**（定案 7，修訂 SCALE-2000-PLAN §5.3 原本的排入佇列設計）：
/// 主機列新增/更新/孤兒復活直接透過 <see cref="NetiqImportApplier"/> 套用，不再等批次執行。
/// 兩千台量級下這一步本身很輕量（純粹是 upsert 幾十到幾百列），真正重的規則檢查本來就
/// 要等下次批次才有——即時落盤只是讓「這台主機被收進清單」這件事本身不用等到隔天。
/// </summary>
public interface INetiqDiscoveryService
{
    Task<NetiqScanResultDto> ScanAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// 新增 Sentinel 精靈步驟 1（定案 6）：以尚未存檔的帳密直接掃描——掃描本身就是連線驗證，
    /// 掃描成功才建立 Sentinel；失敗則什麼都不留下，帳密只過境不落地。
    /// </summary>
    Task<NetiqScanResultDto> CreateAndScanAsync(CreateAndScanSentinelRequest request, CancellationToken ct);

    /// <summary>套用使用者勾選的主機：立即新增/更新/孤兒復活，並記入匯入紀錄</summary>
    NetiqImportResultDto Import(NetiqImportRequest request);
}

public class NetiqDiscoveryService : INetiqDiscoveryService
{
    private readonly INetiqServerCatalog _catalog;
    private readonly INetiqDirectoryClient _client;
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly ISentinelStore _sentinels;
    private readonly ISentinelAdminService _sentinelAdmin;
    private readonly IImportLogStore _importLogs;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;

    private static readonly ConcurrentDictionary<string, PendingScan> Pending = new();
    private static readonly TimeSpan ScanLifetime = TimeSpan.FromMinutes(30);

    public NetiqDiscoveryService(
        INetiqServerCatalog catalog,
        INetiqDirectoryClient client,
        IHostStore hosts,
        IHostGroupStore hostGroups,
        ISentinelStore sentinels,
        ISentinelAdminService sentinelAdmin,
        IImportLogStore importLogs,
        ICurrentUser currentUser,
        IAuditService audit)
    {
        _catalog = catalog;
        _client = client;
        _hosts = hosts;
        _hostGroups = hostGroups;
        _sentinels = sentinels;
        _sentinelAdmin = sentinelAdmin;
        _importLogs = importLogs;
        _currentUser = currentUser;
        _audit = audit;
    }

    public async Task<NetiqScanResultDto> ScanAsync(string serverName, CancellationToken ct)
    {
        var server = _catalog.GetServer(serverName)
                     ?? throw DomainException.Validation($"找不到 Sentinel「{serverName}」。");
        if (!server.CanDiscover)
            throw DomainException.Validation($"Sentinel「{serverName}」尚未設定探索帳密，無法主動掃描。");

        var discovered = await DiscoverAsync(server, ct);
        return BuildScanResult(server.Name, discovered);
    }

    public async Task<NetiqScanResultDto> CreateAndScanAsync(CreateAndScanSentinelRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            throw DomainException.Validation("請輸入 Sentinel 名稱。");
        if (_sentinels.FindByName(name) != null)
            throw DomainException.Conflict($"已有名稱為「{name}」的 Sentinel。");
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            throw DomainException.Validation("請輸入探索帳號與密碼才能掃描。");

        // 用尚未存檔的帳密直接掃描——掃描本身就是連線驗證，失敗不留下任何東西
        var probe = new SentinelServer
        {
            Name = name,
            BaseUrl = request.BaseUrl?.Trim() ?? "",
            Username = request.Username.Trim(),
            Password = request.Password
        };
        var discovered = await DiscoverAsync(probe, ct);

        // 掃描成功才建立（定案 6：中途取消＝沒建立，不留半成品）。
        // 重用 SentinelAdminService 而不是在這裡另寫一份加密/驗證邏輯，兩條寫入路徑才不會漂移
        var sentinel = _sentinelAdmin.SaveSentinel(new SaveSentinelRequest
        {
            Name = name,
            BaseUrl = probe.BaseUrl,
            Username = probe.Username,
            Password = request.Password
        });

        return BuildScanResult(sentinel.Name, discovered);
    }

    private async Task<List<NetiqDiscoveredHost>> DiscoverAsync(SentinelServer server, CancellationToken ct)
    {
        try
        {
            return await _client.ListHostsAsync(server, ct);
        }
        catch (NetiqDiscoveryException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
    }

    /// <summary>掃描結果 → 網段分組 DTO，並暫存 token 供匯入時核對。掃描/新增精靈共用同一份，避免兩邊漂移</summary>
    private NetiqScanResultDto BuildScanResult(string serverName, List<NetiqDiscoveredHost> discovered)
    {
        // 同 IP 去重（保留第一筆）
        discovered = discovered
            .GroupBy(h => h.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        CleanupExpired();
        var token = Guid.NewGuid().ToString("N");
        Pending[token] = new PendingScan(serverName, discovered, DateTime.Now);

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
            Server = serverName,
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

        if (wanted.Count == 0)
            throw DomainException.Validation("請至少勾選一台主機。");

        var groupByIp = ResolveGroupAssignments(scan, request.GroupAssignments);
        var outcome = NetiqImportApplier.Apply(scan.ServerName, wanted, _hosts, _sentinels, groupByIp);

        // 用過即丟：token 對應的掃描快照已經落盤，同一個 token 不該被重複套用第二次
        Pending.TryRemove(request.Token, out _);

        _importLogs.Append(new ImportLogEntry
        {
            UserId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            Account = _currentUser.Account,
            Kind = "Netiq",
            FileName = scan.ServerName,
            AddedCount = outcome.Added,
            UpdatedCount = outcome.Updated,
            RevivedCount = outcome.Revived,
            CreatedAt = DateTime.Now
        });

        _audit.Record(
            action: AuditActions.NetiqImportApplied,
            summary: $"從 NetIQ 匯入（Sentinel：{scan.ServerName}）：新增 {outcome.Added}、更新 {outcome.Updated}" +
                     (outcome.Revived > 0 ? $"、復活 {outcome.Revived}" : ""),
            targetKind: "netiq_import",
            targetId: scan.ServerName,
            detail: new { scan.ServerName, outcome.Added, outcome.Updated, outcome.Revived, HostCount = wanted.Count });

        return new NetiqImportResultDto
        {
            ServerName = scan.ServerName,
            Added = outcome.Added,
            Updated = outcome.Updated,
            Revived = outcome.Revived
        };
    }

    /// <summary>
    /// 依網段指派解析出 IP → 群組 id（定案 8）。新群組在這裡就地建立（送出當下即建，
    /// 即使匯入本身之後失敗，空群組也無害，可於群組頁直接看到並沿用或刪除）。
    /// 沒有指派清單時回傳空字典——Apply 端沒對到的 IP 視為未分組，維持 Phase 3 行為。
    /// </summary>
    private Dictionary<string, long?> ResolveGroupAssignments(PendingScan scan, List<NetiqSubnetGroupAssignment> assignments)
    {
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        if (assignments.Count == 0) return result;

        var cidrByIp = scan.Hosts.ToDictionary(h => h.IpAddress, h => Slash24(h.IpAddress), StringComparer.OrdinalIgnoreCase);

        var groupIdByCidr = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            groupIdByCidr[assignment.Cidr] = assignment.Mode switch
            {
                "existing" => assignment.HostGroupId,
                "new" => ResolveOrCreateGroup(assignment.NewGroupName),
                _ => null   // skip
            };
        }

        foreach (var (ip, cidr) in cidrByIp)
        {
            if (groupIdByCidr.TryGetValue(cidr, out var groupId))
                result[ip] = groupId;
        }

        return result;
    }

    private long? ResolveOrCreateGroup(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var existing = _hostGroups.FindByName(trimmed);
        if (existing != null) return existing.GroupId;

        var created = _hostGroups.Upsert(new HostGroup { GroupName = trimmed, Active = true });
        _audit.Record(
            action: AuditActions.GroupCreate,
            summary: $"新增主機群組「{trimmed}」（NetIQ 匯入精靈建立）",
            targetKind: "host_group",
            targetId: created.GroupId.ToString(),
            detail: new { created.GroupName });

        return created.GroupId;
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
