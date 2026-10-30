using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// NetIQ 主機清單維護（docs/archive/HISTORY.md 決策 A）。
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
    private readonly IUserDisplayNameService _userDisplayNames;
    private readonly PermissionVersionStamp _permissionVersion;

    public NetiqHostService(
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IUserStore users,
        INetiqServerCatalog servers,
        IAuditService audit,
        IUserDisplayNameService userDisplayNames,
        IPrtgHostMapRefresher mapRefresher,
        PermissionVersionStamp permissionVersion)
    {
        _mapRefresher = mapRefresher;
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _servers = servers;
        _audit = audit;
        _userDisplayNames = userDisplayNames;
        _permissionVersion = permissionVersion;
    }

    /// <summary>停用／啟用會改變主機是否參與 PRTG 對應（docs/PRTG-SPEC.md §4）。</summary>
    private readonly IPrtgHostMapRefresher _mapRefresher;

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

        var os = WebHost.NormalizeOs(request.Os)
                 ?? throw DomainException.Validation($"作業系統類型「{request.Os}」不合法，僅接受 windows 或 linux。");

        // 分級選填：留空時新主機退回一般、既有主機沿用原分級（同 SentinelId 省略＝待歸屬的寬鬆慣例，
        // 不像 Os 沒有合法空值可退——分級的預設本來就是「一般」，不需要強制輸入）
        string? tier = null;
        if (!string.IsNullOrWhiteSpace(request.Tier))
        {
            tier = WebHost.NormalizeTier(request.Tier)
                   ?? throw DomainException.Validation($"主機分級「{request.Tier}」不合法，僅接受 core、standard 或 test。");
        }

        var (sentinelId, sentinel) = ResolveSentinel(request.NetiqServer);

        // IP 重複刻意**不擋**：改用衝突佇列處理（決策：軟處理）。
        // 擋下來的話，汰換交接期間「新舊兩台短暫共用同一個 IP 紀錄」就無法登錄，
        // 反而逼使用者先破壞既有資料才能繼續。
        var existing = _hosts.FindByName(ip);
        var permissionChanged = existing != null && !existing.Active && existing.OwnerUserIds.Count > 0;

        // 省略（null 或空白）＝沿用既有值，與 BulkAddHosts 同一判定（F4 契約 1）；明確提供才覆寫
        var roleDesc = request.RoleDesc?.Trim() ?? "";

        var saved = _hosts.Upsert(new WebHost
        {
            HostName = ip,
            IpAddress = ip,
            IpUpdatedAt = existing?.IpAddress == ip ? existing.IpUpdatedAt : DateTime.Now,
            SentinelId = sentinelId ?? existing?.SentinelId,
            NetiqServer = sentinel ?? existing?.NetiqServer,
            RoleDesc = string.IsNullOrWhiteSpace(roleDesc) ? existing?.RoleDesc ?? "" : roleDesc,
            Source = NetiqHostList.NetiqSource,
            Os = os,
            Tier = tier ?? existing?.Tier ?? WebHost.TierStandard,
            Active = true,
            GroupIds = existing?.GroupIds ?? new List<long>(),
            OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
        });

        if (permissionChanged)
            _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: existing == null
                ? $"新增 NetIQ 主機 {ip}（Sentinel：{saved.NetiqServer ?? "待歸屬"}、OS：{os}）"
                : $"更新 NetIQ 主機 {ip}（Sentinel：{saved.NetiqServer ?? "待歸屬"}、OS：{os}）",
            targetKind: "host",
            targetId: saved.HostId.ToString(),
            detail: new { saved.HostName, saved.NetiqServer, saved.RoleDesc });

        return ToDto(saved);
    }

    public BulkAddResultDto BulkAddHosts(BulkAddNetiqHostsRequest request)
    {
        var os = WebHost.NormalizeOs(request.Os)
                 ?? throw DomainException.Validation($"作業系統類型「{request.Os}」不合法，僅接受 windows 或 linux。");

        string? tier = null;
        if (!string.IsNullOrWhiteSpace(request.Tier))
        {
            tier = WebHost.NormalizeTier(request.Tier)
                   ?? throw DomainException.Validation($"主機分級「{request.Tier}」不合法，僅接受 core、standard 或 test。");
        }

        var (sentinelId, sentinel) = ResolveSentinel(request.NetiqServer);

        var result = new BulkAddResultDto();
        var permissionChanged = false;
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
            permissionChanged |= existing != null && !existing.Active && existing.OwnerUserIds.Count > 0;

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
                Os = os,
                // 分級只套用在**新增**的主機（回饋十九輪批次I 體檢修正，與掃描精靈
                // NetiqImportApplier 同一原則、也是 bulk modal 說明文字的承諾）：前端下拉
                // 恆送具體值，寫成 tier ?? existing 的話「重貼一次清單」就會把既有主機
                // 已設好的分級靜默洗成這次下拉選的值
                Tier = existing?.Tier ?? tier ?? WebHost.TierStandard,
                Active = true,
                GroupIds = existing?.GroupIds ?? new List<long>(),
                OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
            });

            if (existing == null) result.AddedCount++;
            else result.UpdatedCount++;
        }

        if (permissionChanged)
            _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"批次登錄 NetIQ 主機（Sentinel：{sentinel ?? "待歸屬"}）：" +
                     $"新增 {result.AddedCount} 台、更新 {result.UpdatedCount} 台、略過 {result.Skipped.Count} 行",
            targetKind: "host",
            targetId: null,
            detail: new { Sentinel = sentinel, result.AddedCount, result.UpdatedCount, result.Skipped });

        return result;
    }

    /// <summary>
    /// 人工啟用／停用主機。以既有主機的複本改 <c>Active</c> 後 Upsert，其餘欄位（含
    /// <c>OrphanedFromSentinel</c>）原樣保留（F4 契約 2）。
    ///
    /// **孤兒標記只在人工重新啟用時清除**：停用時保留（仍是原屬某台已移除 Sentinel 的孤兒），
    /// 重新啟用代表人已表態、標記使命結束（設計定案，見 docs/archive/HISTORY.md「2026-07-23」段 §1.7）。
    /// 孤兒標記的用途只是讓汰換 Sentinel 時能在掃描精靈裡認出「這台原屬某台已移除的 Sentinel」。
    /// </summary>
    public HostDto SetActive(long hostId, bool active)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");
        var permissionChanged = host.Active != active && host.OwnerUserIds.Count > 0;

        var copy = CopyHost(host);
        copy.Active = active;
        // 停用保留孤兒標記；人工重新啟用才清除（見上方 XML 註解）
        if (active) copy.OrphanedFromSentinel = null;
        _hosts.Upsert(copy);

        if (permissionChanged)
            _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: active
                ? $"啟用主機 {host.HostName}（恢復每日分析）"
                : $"停用主機 {host.HostName}（停止分析，既有歷史紀錄保留）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { host.HostName, Active = active });

        // 已停用的主機不參與 PRTG 對應，今天那筆要跟著變（docs/PRTG-SPEC.md §4）。
        // 重算失敗不影響停用本身，但要帶進回應——與改 IP 那條路徑同一種失敗、同一個 DTO 欄位。
        var remapWarning = _mapRefresher.TryRefreshToday();

        var dto = ToDto(_hosts.Get(hostId)!);
        dto.RemapWarning = remapWarning;
        return dto;
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

    /// <summary>
    /// 主機複本：<c>IHostStore.Get</c> 回傳的是共用快照，不可原地修改，且 <c>Upsert</c> 逐欄覆寫，
    /// 所以寫回前一律先複製。欄位在這裡列全，避免像手刻 <c>WebHost</c> 那樣漏抄
    /// （<c>OrphanedFromSentinel</c> 即曾因此被靜默清掉）；新增模型欄位時記得同步加一行。
    /// </summary>
    private static WebHost CopyHost(WebHost h) => new()
    {
        HostId = h.HostId,
        HostName = h.HostName,
        DisplayName = h.DisplayName,
        IpAddress = h.IpAddress,
        IpUpdatedAt = h.IpUpdatedAt,
        SentinelId = h.SentinelId,
        NetiqServer = h.NetiqServer,
        RoleDesc = h.RoleDesc,
        Source = h.Source,
        Os = h.Os,
        Active = h.Active,
        MergedInto = h.MergedInto,
        LastReportAt = h.LastReportAt,
        CreatedAt = h.CreatedAt,
        GroupIds = new List<long>(h.GroupIds),
        OwnerUserIds = new List<long>(h.OwnerUserIds),
        OrphanedFromSentinel = h.OrphanedFromSentinel,
        IsHighVolume = h.IsHighVolume,
        Tier = h.Tier
    };

    private static BulkAddLineDto Skip(NetiqHostLine line, string reason) => new()
    {
        LineNumber = line.LineNumber,
        RawLine = line.RawLine,
        Reason = reason
    };

    private HostDto ToDto(WebHost host)
    {
        // 每次呼叫都重查 store（既有效能議題，記入 BACKLOG，取數策略此次不動）
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);
        return HostDtoMapper.ToDto(host, groups, users, _userDisplayNames);
    }
}
