using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 群組與授權維護（docs/WEB-SPEC.md §9.8 群組頁）。
/// builtin 群組的保護規則在這一層強制——Repository 只負責存取。
/// </summary>
public class GroupAdminService
{
    private readonly IUserGroupStore _userGroups;
    private readonly IHostGroupStore _hostGroups;
    private readonly IGroupAccessStore _access;
    private readonly IUserStore _users;
    private readonly IHostStore _hosts;
    private readonly IAuditService _audit;
    private readonly PermissionVersionStamp _permissionVersion;

    public GroupAdminService(
        IUserGroupStore userGroups,
        IHostGroupStore hostGroups,
        IGroupAccessStore access,
        IUserStore users,
        IHostStore hosts,
        IAuditService audit,
        PermissionVersionStamp permissionVersion)
    {
        _permissionVersion = permissionVersion;
        _userGroups = userGroups;
        _hostGroups = hostGroups;
        _access = access;
        _users = users;
        _hosts = hosts;
        _audit = audit;
    }

    public List<UserGroupDto> GetUserGroups()
    {
        var users = _users.GetAll();

        return _userGroups.GetAll()
            .OrderByDescending(g => g.Builtin)
            .ThenBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UserGroupDto
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Role = g.Role.ToString(),
                Builtin = g.Builtin,
                Active = g.Active,
                MemberCount = users.Count(u => u.GroupIds.Contains(g.GroupId)),
                DispatchPool = g.DispatchPool
            })
            .ToList();
    }

    public UserGroupDto SaveUserGroup(SaveUserGroupRequest request)
    {
        var name = request.GroupName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw DomainException.Validation("群組名稱不可為空。");

        if (!InputParsing.TryParseDefinedEnum<UserRole>(request.Role, out var role))
            throw DomainException.Validation($"未知的角色「{request.Role}」。");

        var existing = request.GroupId == 0 ? null : _userGroups.Get(request.GroupId);
        if (request.GroupId != 0 && existing == null)
            throw DomainException.NotFound("找不到這個群組，可能已被刪除。");

        var duplicate = _userGroups.FindByName(name);
        if (duplicate != null && duplicate.GroupId != request.GroupId)
            throw DomainException.Conflict($"已有同名的使用者群組「{name}」。");

        // builtin 群組允許改名（配合公司慣例，如把 admin 改成「資訊室管理員」），
        // 但角色不可改：整套授權都建立在「這個群組是 Admin 角色」之上，
        // 改掉它等於讓權限模型失去地基
        if (existing?.Builtin == true && role != existing.Role)
            throw DomainException.Validation(
                $"「{existing.GroupName}」是系統內建群組，不可變更角色（可以改名或停用）。");

        var saved = _userGroups.Upsert(new UserGroup
        {
            GroupId = request.GroupId,
            GroupName = name,
            Role = existing?.Builtin == true ? existing.Role : role,
            Builtin = existing?.Builtin ?? false,
            Active = request.Active
        });
        // 既有群組的角色／啟停改了，成員的能力跟著變（新群組還沒有成員，不影響任何人）
        if (existing != null) _permissionVersion.Bump();

        _audit.Record(
            action: existing == null ? AuditActions.GroupCreate : AuditActions.GroupUpdate,
            summary: existing == null
                ? $"新增使用者群組「{saved.GroupName}」（角色 {saved.Role}）"
                : $"更新使用者群組「{saved.GroupName}」：角色 {saved.Role}、{(saved.Active ? "啟用" : "停用")}",
            targetKind: "group",
            targetId: saved.GroupId.ToString(),
            detail: new { saved.GroupName, Role = saved.Role.ToString(), saved.Active });

        return GetUserGroups().First(g => g.GroupId == saved.GroupId);
    }

    public void DeleteUserGroup(long groupId)
    {
        var group = _userGroups.Get(groupId)
                    ?? throw DomainException.NotFound("找不到這個群組，可能已被刪除。");

        if (group.Builtin)
            throw DomainException.Validation($"「{group.GroupName}」是系統內建群組，不可刪除（可改為停用）。");

        var memberCount = _users.GetAll().Count(u => u.GroupIds.Contains(groupId));
        if (memberCount > 0)
            throw DomainException.Conflict(
                $"「{group.GroupName}」還有 {memberCount} 位成員，請先將成員移出群組再刪除。");

        _userGroups.Delete(groupId);
        _access.SetForUserGroup(groupId, Array.Empty<long>());
        _permissionVersion.Bump();

        _audit.Record(
            action: AuditActions.GroupDelete,
            summary: $"刪除使用者群組「{group.GroupName}」",
            targetKind: "group",
            targetId: groupId.ToString());
    }

    public void SetDispatchPool(long groupId, bool inPool)
    {
        var group = _userGroups.Get(groupId)
                    ?? throw DomainException.NotFound("找不到這個群組，可能已被刪除。");

        if (group.Builtin)
            throw DomainException.Validation("系統內建群組不能設為派工池，請改用自建的維運群組。");

        _userGroups.SetDispatchPool(groupId, inPool);

        _audit.Record(
            action: AuditActions.GroupDispatchPool,
            summary: $"{group.GroupName}：{(inPool ? "設為派工池" : "取消派工池")}",
            targetKind: "group",
            targetId: groupId.ToString(),
            detail: new { InPool = inPool });
    }

    public List<HostGroupDto> GetHostGroups()
    {
        var hosts = _hosts.GetAll();

        return _hostGroups.GetAll()
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HostGroupDto
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Active = g.Active,
                HostCount = hosts.Count(h => h.GroupIds.Contains(g.GroupId))
            })
            .ToList();
    }

    public HostGroupDto SaveHostGroup(SaveHostGroupRequest request)
    {
        var name = request.GroupName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw DomainException.Validation("群組名稱不可為空。");

        var existing = request.GroupId == 0 ? null : _hostGroups.Get(request.GroupId);
        if (request.GroupId != 0 && existing == null)
            throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var duplicate = _hostGroups.FindByName(name);
        if (duplicate != null && duplicate.GroupId != request.GroupId)
            throw DomainException.Conflict($"已有同名的主機群組「{name}」。");

        var saved = _hostGroups.Upsert(new HostGroup
        {
            GroupId = request.GroupId,
            GroupName = name,
            Active = request.Active
        });

        _audit.Record(
            action: existing == null ? AuditActions.GroupCreate : AuditActions.GroupUpdate,
            summary: existing == null
                ? $"新增主機群組「{saved.GroupName}」"
                : $"更新主機群組「{saved.GroupName}」：{(saved.Active ? "啟用" : "停用")}",
            targetKind: "group",
            targetId: saved.GroupId.ToString());

        return GetHostGroups().First(g => g.GroupId == saved.GroupId);
    }

    public void DeleteHostGroup(long groupId)
    {
        var group = _hostGroups.Get(groupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var hostCount = _hosts.GetAll().Count(h => h.GroupIds.Contains(groupId));
        if (hostCount > 0)
            throw DomainException.Conflict(
                $"「{group.GroupName}」還有 {hostCount} 台主機，請先將主機移出群組再刪除。");

        _hostGroups.Delete(groupId);

        // 連同以它為目標的授權一併清掉，否則會留下指向不存在群組的孤兒授權
        var remaining = _access.GetAll().Where(a => a.HostGroupId != groupId).ToList();
        _access.ReplaceAll(remaining);

        _audit.Record(
            action: AuditActions.GroupDelete,
            summary: $"刪除主機群組「{group.GroupName}」",
            targetKind: "group",
            targetId: groupId.ToString());
    }

    public HostGroupMemberPreviewDto PreviewMembers(long hostGroupId, HostGroupMemberQueryRequest request)
    {
        var group = _hostGroups.Get(hostGroupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize);
        var matched = MatchHosts(_hosts.GetAll(), request.Pattern, request.Query);
        var groupsById = _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);

        // 排序鍵與計數都只看群組 id，不必為全部命中主機建 DTO；只有當頁才解析群組名稱
        bool InOthers(WebHost h) => h.GroupIds.Any(id => id != hostGroupId);

        var pageHosts = matched
            .OrderByDescending(InOthers)   // 已屬其他群組的排前面，需要留意的先看到
            .ThenBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var candidates = pageHosts.Select(h =>
        {
            // 現有群組排除「本目標群組」——那不是「已屬其他群組」，另以 AlreadyInTarget 表達
            var otherGroups = NameFormat.ResolveNames(
                h.GroupIds.Where(id => id != hostGroupId), groupsById, name => name);

            return new HostGroupMemberCandidateDto
            {
                HostId = h.HostId,
                HostName = h.HostName,
                IpAddress = h.IpAddress,
                CurrentGroups = otherGroups,
                InOtherGroups = otherGroups.Count > 0,
                AlreadyInTarget = h.GroupIds.Contains(hostGroupId)
            };
        }).ToList();

        return new HostGroupMemberPreviewDto
        {
            GroupId = group.GroupId,
            GroupName = group.GroupName,
            MatchCount = matched.Count,
            InOtherGroupsCount = matched.Count(InOthers),
            AlreadyInTargetCount = matched.Count(h => h.GroupIds.Contains(hostGroupId)),
            Page = page,
            PageSize = pageSize,
            Candidates = candidates
        };
    }

    /// <summary>
    /// 加入成員：一次 <see cref="IHostStore.MutateBatch{TResult}"/> 完成全部變更（3000 台也只有一次整份讀改寫）。
    /// <see cref="AddHostGroupMembersRequest.AllMatching"/> 為 true 時在同一次讀改寫內以條件重新比對。
    /// </summary>
    public AddHostGroupMembersResultDto AddMembers(long hostGroupId, AddHostGroupMembersRequest request)
    {
        var group = _hostGroups.Get(hostGroupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var wantedIds = request.HostIds.Distinct().ToHashSet();

        var (changed, addedCount) = _hosts.MutateBatch(list =>
        {
            var names = new List<string>();
            var newlyAdded = 0;
            IEnumerable<WebHost> targets = request.AllMatching
                ? MatchHosts(list, request.Pattern, request.Query)
                : list.Where(h => wantedIds.Contains(h.HostId));

            foreach (var host in targets.ToList())
            {
                if (host.MergedInto != null) continue;   // 墓碑列略過（防前端送過期資料）
                // 已在本群組：一般加入時略過；勾了「移出原群組」時仍要處理（把它從其他群組移出），勾選與全部加入同一條規則
                var wasMember = host.GroupIds.Contains(hostGroupId);
                if (wasMember && !request.RemoveFromOthers) continue;

                // removeFromOthers：只保留本目標群組；否則在既有群組上追加本群組
                var next = request.RemoveFromOthers
                    ? new List<long> { hostGroupId }
                    : host.GroupIds.Concat(new[] { hostGroupId }).Distinct().ToList();
                if (wasMember && next.Count == host.GroupIds.Count) continue;   // 本來就只在本群組：沒有變動
                host.GroupIds = next;
                names.Add(host.HostName);
                if (!wasMember) newlyAdded++;
            }

            return (names, newlyAdded);
        });
        var movedCount = changed.Count - addedCount;

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"批次加入主機群組「{group.GroupName}」：加入 {addedCount} 台" +
                     (request.RemoveFromOthers ? $"（並移出原群組；另有 {movedCount} 台原已在本群組、只移出其他群組）" : ""),
            targetKind: "group",
            targetId: group.GroupId.ToString(),
            detail: new { group.GroupName, request.RemoveFromOthers, Hosts = changed });

        return new AddHostGroupMembersResultDto { AddedCount = addedCount, MovedFromOthersCount = movedCount };
    }

    public PagedResult<HostGroupMemberDto> GetMembers(long hostGroupId, int page = 1, int pageSize = 50)
    {
        var group = _hostGroups.Get(hostGroupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        (page, pageSize) = Paging.Normalize(page, pageSize);

        var members = _hosts.GetAll()
            .Where(h => h.MergedInto == null && h.GroupIds.Contains(hostGroupId))
            .ToList();

        // 依 IP 數值排序（無 IP 或非 IPv4 的排最後），再依主機名
        var items = members
            .OrderBy(h => IpSortKey(h.IpAddress))
            .ThenBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new HostGroupMemberDto
            {
                HostId = h.HostId,
                HostName = h.HostName,
                IpAddress = h.IpAddress,
                OtherGroupCount = h.GroupIds.Count(id => id != hostGroupId)
            })
            .ToList();

        return new PagedResult<HostGroupMemberDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            Total = members.Count
        };
    }

    /// <summary>IPv4 轉可排序的數值；無法解析者排最後</summary>
    private static long IpSortKey(string? ip)
    {
        var parts = (ip ?? "").Split('.');
        if (parts.Length != 4) return long.MaxValue;
        long key = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, out var octet)) return long.MaxValue;
            key = key * 256 + octet;
        }
        return key;
    }

    public void RemoveMembers(long hostGroupId, IEnumerable<long> hostIds)
    {
        var group = _hostGroups.Get(hostGroupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var wanted = hostIds.Distinct().ToHashSet();

        // 一次 MutateBatch：查無或早已不在此群組的主機安靜略過
        var removed = _hosts.MutateBatch(list =>
        {
            var names = new List<string>();
            foreach (var host in list.Where(h => wanted.Contains(h.HostId)))
            {
                if (!host.GroupIds.Contains(hostGroupId)) continue;
                host.GroupIds = host.GroupIds.Where(id => id != hostGroupId).Distinct().ToList();
                names.Add(host.HostName);
            }
            return names;
        });

        if (removed.Count == 0) return;

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"從主機群組「{group.GroupName}」移出：{removed.Count} 台",
            targetKind: "group",
            targetId: group.GroupId.ToString(),
            detail: new { group.GroupName, Hosts = removed });
    }

    /// <summary>
    /// 依網段或關鍵字命中主機。墓碑列（已併入其他主機）一律排除——它們不該再被指派群組。
    /// 網段與 IP 欄位比對；關鍵字同時比對 HostName 與 IpAddress（NetIQ 主機名即 IP）。
    /// </summary>
    private static List<WebHost> MatchHosts(IEnumerable<WebHost> all, string? pattern, string? queryText)
    {
        var hosts = all.Where(h => h.MergedInto == null);

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var range = CidrMatcher.Parse(pattern)
                        ?? throw DomainException.Validation(
                            $"「{pattern}」不是有效的網段（可用 192.168.0.0/24、192.168.0.* 或單一 IP）。");
            return hosts.Where(h => CidrMatcher.Matches(range, h.IpAddress)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(queryText))
        {
            var q = queryText.Trim();
            return hosts
                .Where(h => h.HostName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            (h.IpAddress ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new List<WebHost>();
    }

    /// <summary>
    /// 授權矩陣：列＝使用者群組、欄＝主機群組、格子＝是否授權。
    /// ViewAll 角色（admin/manager/dev）不列入——他們本來就看得到全部主機，
    /// 放進矩陣只會讓人以為那些勾選有意義。
    /// </summary>
    public AccessMatrixDto GetAccessMatrix()
    {
        var accesses = _access.GetAll();

        // 啟用主機數：Active 且未併入其他主機（墓碑不算）；一次掃描全部主機，不逐群組查
        var hostCountByGroup = new Dictionary<long, int>();
        foreach (var host in _hosts.GetAll().Where(h => h.Active && h.MergedInto == null))
            foreach (var gid in host.GroupIds.Distinct())
                hostCountByGroup[gid] = hostCountByGroup.GetValueOrDefault(gid) + 1;

        var userGroups = _userGroups.GetAll()
            .Where(g => g.Role == UserRole.User)
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hostGroups = _hostGroups.GetAll()
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AccessMatrixDto
        {
            UserGroups = userGroups.Select(g => new AccessMatrixRowDto
            {
                UserGroupId = g.GroupId,
                UserGroupName = g.GroupName,
                Active = g.Active,
                GrantedHostGroupIds = accesses
                    .Where(a => a.UserGroupId == g.GroupId)
                    .Select(a => a.HostGroupId)
                    .ToList()
            }).ToList(),

            HostGroups = hostGroups.Select(g => new HostGroupDto
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Active = g.Active,
                HostCount = hostCountByGroup.GetValueOrDefault(g.GroupId)
            }).ToList()
        };
    }

    /// <summary>
    /// 授權增減：以當下資料庫狀態套用 grant／revoke（在 store 的單次讀改寫內完成），回傳套用後的完整清單。
    /// 驗證（重疊、不存在的主機群組）任何一項失敗都整筆不寫；兩者皆空只回目前清單、不寫稽核。
    /// </summary>
    public AccessChangeResultDto ChangeAccess(long userGroupId, IEnumerable<long>? grant, IEnumerable<long>? revoke)
    {
        var userGroup = _userGroups.Get(userGroupId)
                        ?? throw DomainException.NotFound("找不到這個使用者群組。");

        var allHostGroups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var grantIds = (grant ?? Array.Empty<long>()).Distinct().ToList();
        var revokeIds = (revoke ?? Array.Empty<long>()).Distinct().ToList();

        var overlap = grantIds.Intersect(revokeIds).ToList();
        if (overlap.Count > 0)
            throw DomainException.Validation(
                $"同一個主機群組不可同時授權又收回（ID：{string.Join("、", overlap)}）。");

        NameFormat.EnsureAllKnown(grantIds, allHostGroups, "主機群組");
        NameFormat.EnsureAllKnown(revokeIds, allHostGroups, "主機群組");

        if (grantIds.Count == 0 && revokeIds.Count == 0)
            return new AccessChangeResultDto
            {
                GrantedHostGroupIds = _access.GetAll()
                    .Where(a => a.UserGroupId == userGroupId).Select(a => a.HostGroupId).ToList()
            };

        var change = _access.ApplyChange(userGroupId, grantIds, revokeIds);

        string NamesOf(List<long> ids) => NameFormat.Join(
            ids.Select(id => allHostGroups.TryGetValue(id, out var g) ? g.GroupName : id.ToString()).OrderBy(n => n).ToList());

        if (change.Added.Count > 0)
            _audit.Record(
                action: AuditActions.AccessGrant,
                summary: $"授權「{userGroup.GroupName}」檢視主機群組：{NamesOf(change.Added)}",
                targetKind: "group",
                targetId: userGroupId.ToString(),
                detail: new { Added = change.Added.Select(id => allHostGroups[id].GroupName).OrderBy(n => n).ToList() });

        if (change.Removed.Count > 0)
            _audit.Record(
                action: AuditActions.AccessRevoke,
                summary: $"收回「{userGroup.GroupName}」檢視主機群組：{NamesOf(change.Removed)}",
                targetKind: "group",
                targetId: userGroupId.ToString(),
                detail: new { Removed = change.Removed.Select(id => allHostGroups[id].GroupName).OrderBy(n => n).ToList() });

        return new AccessChangeResultDto { GrantedHostGroupIds = change.Final };
    }
}
