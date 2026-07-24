using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 主機清單維護（docs/NETIQ-HOSTLIST-WEB-PLAN.md 決策 A）。
///
/// **清單項目就是 <see cref="WebHost"/> 列**（`Source='netiq'`），不另建實體：
/// 群組、負責人、合併、CSV 匯入、授權全部直接重用，也不會多出一層
/// 「清單項目 ↔ 主機」的配對狀態機要維護。
/// </summary>
public interface INetiqHostService
{
    NetiqOverviewDto GetOverview();

    HostDto AddHost(AddNetiqHostRequest request);

    BulkAddResultDto BulkAddHosts(BulkAddNetiqHostsRequest request);

    HostDto SetActive(long hostId, bool active);
}

public class NetiqHostService : INetiqHostService
{
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly INetiqServerCatalog _servers;
    private readonly IAuditService _audit;

    public NetiqHostService(
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IUserStore users,
        INetiqServerCatalog servers,
        IAuditService audit)
    {
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _servers = servers;
        _audit = audit;
    }

    public NetiqOverviewDto GetOverview()
    {
        var allHosts = _hosts.GetAll();
        var conflicts = NetiqHostList.IpConflicts(allHosts);

        return new NetiqOverviewDto
        {
            SentinelNames = _servers.GetServerNames(),
            PendingAssignmentCount = NetiqHostList.PendingAssignment(allHosts).Count(),
            IpConflictCount = conflicts.Count,
            UngroupedCount = NetiqHostList.Ungrouped(allHosts).Count(),
            IpConflicts = conflicts.Select(group => new IpConflictGroupDto
            {
                IpAddress = group[0].IpAddress ?? "",
                Hosts = group.Select((h, index) => new IpConflictHostDto
                {
                    HostId = h.HostId,
                    HostName = h.HostName,
                    NetiqServer = h.NetiqServer,
                    RoleDesc = h.RoleDesc,
                    // 每組只有最早建立的那台會被輪巡（NetiqHostList.Pollable 的規則）
                    IsPolled = index == 0
                }).ToList()
            }).ToList()
        };
    }

    public HostDto AddHost(AddNetiqHostRequest request)
    {
        var ip = request.IpAddress?.Trim() ?? "";
        if (!NetiqHostList.IsValidIp(ip))
            throw DomainException.Validation($"「{ip}」不是有效的 IP 位址。");

        var (sentinelId, sentinel) = ResolveSentinel(request.NetiqServer);

        // IP 重複刻意**不擋**：改用衝突佇列處理（決策：軟處理）。
        // 擋下來的話，汰換交接期間「新舊兩台短暫共用同一個 IP 紀錄」就無法登錄，
        // 反而逼使用者先破壞既有資料才能繼續。
        var existing = _hosts.FindByName(ip);

        var saved = _hosts.Upsert(new WebHost
        {
            HostName = ip,
            IpAddress = ip,
            IpUpdatedAt = existing?.IpAddress == ip ? existing.IpUpdatedAt : DateTime.Now,
            SentinelId = sentinelId,
            NetiqServer = sentinel,
            RoleDesc = request.RoleDesc?.Trim() ?? "",
            Source = NetiqHostList.NetiqSource,
            Active = true,
            GroupIds = existing?.GroupIds ?? new List<long>(),
            OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
        });

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: existing == null
                ? $"新增 NetIQ 主機 {ip}（Sentinel：{sentinel ?? "待歸屬"}）"
                : $"更新 NetIQ 主機 {ip}（Sentinel：{sentinel ?? "待歸屬"}）",
            targetKind: "host",
            targetId: saved.HostId.ToString(),
            detail: new { saved.HostName, saved.NetiqServer, saved.RoleDesc });

        return ToDto(saved);
    }

    public BulkAddResultDto BulkAddHosts(BulkAddNetiqHostsRequest request)
    {
        var (sentinelId, sentinel) = ResolveSentinel(request.NetiqServer);

        var result = new BulkAddResultDto();
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = (request.Lines ?? "").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var parsed = NetiqHostList.ParseLine(lines[i], i + 1);
            if (parsed == null) continue;   // 空行或註解

            if (parsed.Error != null)
            {
                result.Skipped.Add(Skip(parsed, parsed.Error));
                continue;
            }

            // 同一批貼上內部的重複：靜默匯入兩次會讓使用者以為兩台都建立了
            if (!seenIps.Add(parsed.IpAddress))
            {
                result.Skipped.Add(Skip(parsed, "這一批貼上的內容中已出現過同一個 IP"));
                continue;
            }

            var existing = _hosts.FindByName(parsed.IpAddress);

            _hosts.Upsert(new WebHost
            {
                HostName = parsed.IpAddress,
                IpAddress = parsed.IpAddress,
                IpUpdatedAt = existing?.IpAddress == parsed.IpAddress ? existing.IpUpdatedAt : DateTime.Now,
                SentinelId = sentinelId ?? existing?.SentinelId,
                NetiqServer = sentinel ?? existing?.NetiqServer,
                // 角色描述留空時保留既有值：重貼一次清單不該把已經填好的描述洗掉
                RoleDesc = parsed.RoleDesc.Length > 0 ? parsed.RoleDesc : existing?.RoleDesc ?? "",
                Source = NetiqHostList.NetiqSource,
                Active = true,
                GroupIds = existing?.GroupIds ?? new List<long>(),
                OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
            });

            if (existing == null) result.AddedCount++;
            else result.UpdatedCount++;
        }

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"批次登錄 NetIQ 主機（Sentinel：{sentinel ?? "待歸屬"}）：" +
                     $"新增 {result.AddedCount} 台、更新 {result.UpdatedCount} 台、略過 {result.Skipped.Count} 行",
            targetKind: "host",
            targetId: null,
            detail: new { Sentinel = sentinel, result.AddedCount, result.UpdatedCount, result.Skipped });

        return result;
    }

    public HostDto SetActive(long hostId, bool active)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        _hosts.Upsert(new WebHost
        {
            HostName = host.HostName,
            IpAddress = host.IpAddress,
            IpUpdatedAt = host.IpUpdatedAt,
            SentinelId = host.SentinelId,
            NetiqServer = host.NetiqServer,
            RoleDesc = host.RoleDesc,
            Source = host.Source,
            Active = active,
            GroupIds = host.GroupIds,
            OwnerUserIds = host.OwnerUserIds
        });

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: active
                ? $"啟用主機 {host.HostName}（恢復每日分析）"
                : $"停用主機 {host.HostName}（停止分析，既有歷史紀錄保留）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { host.HostName, Active = active });

        return ToDto(_hosts.Get(hostId)!);
    }

    /// <summary>
    /// 解析 Sentinel 名稱成 (SentinelId, 正規化名稱)：空白＝待歸屬（允許），有填則必須是
    /// 已存在的 Sentinel。識別鍵是 PK（定案 4），這裡順便把名稱換成 Sentinel 現存的正確大小寫。
    /// 打錯名字的後果是這台主機永遠不會被任何一輪查詢帶到——擋在輸入端比事後查「為什麼沒資料」便宜得多。
    /// </summary>
    private (long? SentinelId, string? Name) ResolveSentinel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);

        var name = value.Trim();
        var server = _servers.GetServer(name);
        if (server == null)
        {
            var known = _servers.GetServerNames();
            throw DomainException.Validation(
                $"「{name}」不在已設定的 Sentinel 名單中" +
                (known.Count == 0
                    ? "（尚未於 Sentinel 管理頁新增任何 Sentinel）。"
                    : $"，可選：{string.Join("、", known)}。"));
        }

        return (server.Id, server.Name);
    }

    private static BulkAddLineDto Skip(NetiqHostLine line, string reason) => new()
    {
        LineNumber = line.LineNumber,
        RawLine = line.RawLine,
        Reason = reason
    };

    private HostDto ToDto(WebHost host)
    {
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        return new HostDto
        {
            HostId = host.HostId,
            HostName = host.HostName,
            DisplayName = host.DisplayName,
            IpAddress = host.IpAddress,
            NetiqServer = host.NetiqServer,
            RoleDesc = host.RoleDesc,
            Source = host.Source,
            Active = host.Active,
            MergedInto = host.MergedInto,
            LastReportAt = host.LastReportAt,
            CreatedAt = host.CreatedAt,
            GroupIds = host.GroupIds,
            GroupNames = host.GroupIds
                .Select(id => groups.TryGetValue(id, out var g) ? g.GroupName : $"(已刪除:{id})")
                .ToList(),
            OwnerUserIds = host.OwnerUserIds,
            OwnerNames = host.OwnerUserIds
                .Select(id => users.TryGetValue(id, out var u) ? u.DisplayName : $"(已刪除:{id})")
                .ToList()
        };
    }
}
