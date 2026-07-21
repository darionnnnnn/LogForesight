using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>主機維護（docs/WEB-SPEC.md §9.8 主機頁）</summary>
public interface IHostAdminService
{
    List<HostDto> GetHosts();
    HostDto SaveHost(SaveHostRequest request);
    HostDto SetHostGroups(long hostId, IEnumerable<long> groupIds);
    HostDto SetHostOwners(long hostId, IEnumerable<long> userIds);
    void MergeHost(long sourceHostId, long targetHostId);
    void UnmergeHost(long hostId);
}

public class HostAdminService : IHostAdminService
{
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly INetiqServerCatalog _servers;
    private readonly IAuditService _audit;

    public HostAdminService(
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

    public List<HostDto> GetHosts()
    {
        var groups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);

        return _hosts.GetAll()
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .Select(h => ToDto(h, groups, users))
            .ToList();
    }

    public HostDto SaveHost(SaveHostRequest request)
    {
        var hostName = request.HostName.Trim();
        if (string.IsNullOrWhiteSpace(hostName))
            throw DomainException.Validation("主機名稱不可為空。");

        // 這是**同一份資料的另一條寫入路徑**（NetIQ 清單走 NetiqHostService）。
        // 驗證只掛在其中一條的話，從編輯表單就能繞過去存進不合格的值——
        // 而不合格的 IP／Sentinel 的後果是這台主機永遠查無資料，且完全沒有跡象
        if (!string.IsNullOrWhiteSpace(request.IpAddress) && !NetiqHostList.IsValidIp(request.IpAddress))
            throw DomainException.Validation($"「{request.IpAddress.Trim()}」不是有效的 IP 位址。");

        if (!string.IsNullOrWhiteSpace(request.NetiqServer) && !_servers.IsKnownServer(request.NetiqServer))
        {
            var known = _servers.GetServerNames();
            throw DomainException.Validation(
                $"「{request.NetiqServer.Trim()}」不在已設定的 Sentinel 名單中" +
                (known.Count == 0
                    ? "（批次 appsettings.json 的 NetIq.Servers 尚未設定）。"
                    : $"，可選：{string.Join("、", known)}。"));
        }

        var existing = _hosts.FindByName(hostName);
        var isNew = existing == null;

        var ipChanged = existing?.IpAddress != request.IpAddress;

        var saved = _hosts.Upsert(new WebHost
        {
            HostName = hostName,
            IpAddress = string.IsNullOrWhiteSpace(request.IpAddress) ? null : request.IpAddress.Trim(),
            IpUpdatedAt = ipChanged ? DateTime.Now : existing?.IpUpdatedAt,
            NetiqServer = string.IsNullOrWhiteSpace(request.NetiqServer) ? null : request.NetiqServer.Trim(),
            RoleDesc = request.RoleDesc?.Trim() ?? "",
            Source = existing?.Source ?? "local",
            Active = request.Active,
            // 群組與負責人由專屬端點維護，避免「更新角色描述」意外清掉它們
            GroupIds = existing?.GroupIds ?? new List<long>(),
            OwnerUserIds = existing?.OwnerUserIds ?? new List<long>()
        });

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: isNew
                ? $"新增主機 {saved.HostName}（{saved.RoleDesc}）"
                : $"更新主機 {saved.HostName}：角色描述「{saved.RoleDesc}」、狀態「{(saved.Active ? "啟用" : "停用")}」",
            targetKind: "host",
            targetId: saved.HostId.ToString(),
            detail: new { saved.HostName, saved.IpAddress, saved.NetiqServer, saved.RoleDesc, saved.Active });

        return ToDto(saved, _hostGroups.GetAll().ToDictionary(g => g.GroupId), _users.GetAll().ToDictionary(u => u.UserId));
    }

    public HostDto SetHostGroups(long hostId, IEnumerable<long> groupIds)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var allGroups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var requested = groupIds.Distinct().ToList();

        var unknown = requested.Where(id => !allGroups.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw DomainException.Validation($"指定的主機群組不存在（ID：{string.Join("、", unknown)}）。");

        var before = host.GroupIds.Select(id => allGroups.TryGetValue(id, out var g) ? g.GroupName : id.ToString()).ToList();
        var after = requested.Select(id => allGroups[id].GroupName).ToList();

        _hosts.SetGroups(hostId, requested);

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"變更主機 {host.HostName} 的群組：由「{Format(before)}」改為「{Format(after)}」" +
                     "（會影響哪些使用者看得到這台主機）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Before = before, After = after });

        return ToDto(_hosts.Get(hostId)!, allGroups, _users.GetAll().ToDictionary(u => u.UserId));
    }

    public HostDto SetHostOwners(long hostId, IEnumerable<long> userIds)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        var allUsers = _users.GetAll().ToDictionary(u => u.UserId);
        var requested = userIds.Distinct().ToList();

        var unknown = requested.Where(id => !allUsers.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw DomainException.Validation($"指定的使用者不存在（ID：{string.Join("、", unknown)}）。");

        var before = host.OwnerUserIds.Select(id => allUsers.TryGetValue(id, out var u) ? u.Account : id.ToString()).ToList();
        var after = requested.Select(id => allUsers[id].Account).ToList();

        _hosts.SetOwners(hostId, requested);

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"變更主機 {host.HostName} 的負責人：由「{Format(before)}」改為「{Format(after)}」",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Before = before, After = after });

        return ToDto(_hosts.Get(hostId)!, _hostGroups.GetAll().ToDictionary(g => g.GroupId), allUsers);
    }

    public void MergeHost(long sourceHostId, long targetHostId)
    {
        if (sourceHostId == targetHostId)
            throw DomainException.Validation("來源與目標是同一台主機。");

        var source = _hosts.Get(sourceHostId) ?? throw DomainException.NotFound("找不到來源主機。");
        var target = _hosts.Get(targetHostId) ?? throw DomainException.NotFound("找不到目標主機。");

        if (source.MergedInto != null)
            throw DomainException.Conflict($"{source.HostName} 已經併入其他主機，請先解除原本的綁定。");

        // 目標本身是墓碑就會形成 A→B→C 的鏈。查詢的別名展開認得整條鏈（歷史不會掉），
        // 但鏈對使用者是純粹的困惑——併入一台已經停用的主機，畫面上看不出資料最後去了哪。
        // 擋在這裡，要求指向最終那台
        if (target.MergedInto != null)
            throw DomainException.Conflict(
                $"{target.HostName} 本身已併入其他主機，不能作為併入目標；請改以最終的那台主機為目標。");

        _hosts.Merge(sourceHostId, targetHostId);

        _audit.Record(
            action: AuditActions.HostMerge,
            summary: $"將主機 {source.HostName} 併入 {target.HostName}" +
                     $"（{source.HostName} 保留為墓碑紀錄並停用，綁錯可反向修復）",
            targetKind: "host",
            targetId: sourceHostId.ToString(),
            detail: new { Source = source.HostName, Target = target.HostName });
    }

    public void UnmergeHost(long hostId)
    {
        var host = _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");

        if (host.MergedInto == null)
            throw DomainException.Validation($"{host.HostName} 沒有併入任何主機，不需要解除。");

        var target = _hosts.Get(host.MergedInto.Value);

        _hosts.Unmerge(hostId);

        _audit.Record(
            action: AuditActions.HostUnmerge,
            summary: $"解除主機 {host.HostName} 與 {target?.HostName ?? $"(已刪除:{host.MergedInto})"} 的綁定" +
                     $"（{host.HostName} 恢復啟用；合併時帶入對方的群組/負責人等設定不會自動收回，請一併確認）",
            targetKind: "host",
            targetId: hostId.ToString(),
            detail: new { Source = host.HostName, Target = target?.HostName });
    }

    private static string Format(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);

    private static HostDto ToDto(
        WebHost host,
        IReadOnlyDictionary<long, HostGroup> groupsById,
        IReadOnlyDictionary<long, WebUser> usersById) => new()
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
        GroupIds = host.GroupIds,
        GroupNames = host.GroupIds
            .Select(id => groupsById.TryGetValue(id, out var g) ? g.GroupName : $"(已刪除:{id})")
            .ToList(),
        OwnerUserIds = host.OwnerUserIds,
        OwnerNames = host.OwnerUserIds
            .Select(id => usersById.TryGetValue(id, out var u) ? u.DisplayName : $"(已刪除:{id})")
            .ToList()
    };
}
