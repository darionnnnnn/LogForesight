using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 群組與授權維護（docs/WEB-SPEC.md §9.8 群組頁）。
/// builtin 群組的保護規則在這一層強制——Repository 只負責存取。
/// </summary>
public interface IGroupAdminService
{
    List<UserGroupDto> GetUserGroups();
    UserGroupDto SaveUserGroup(SaveUserGroupRequest request);
    void DeleteUserGroup(long groupId);

    List<HostGroupDto> GetHostGroups();
    HostGroupDto SaveHostGroup(SaveHostGroupRequest request);
    void DeleteHostGroup(long groupId);

    /// <summary>批次加入群組成員的預覽：以網段或關鍵字查命中主機（含現有群組）</summary>
    HostGroupMemberPreviewDto PreviewMembers(long hostGroupId, HostGroupMemberQueryRequest request);

    /// <summary>把選定主機加入群組（可選同時移出原群組）</summary>
    HostGroupMemberPreviewDto AddMembers(long hostGroupId, AddHostGroupMembersRequest request);

    AccessMatrixDto GetAccessMatrix();
    void SetAccess(long userGroupId, IEnumerable<long> hostGroupIds);
}

public class GroupAdminService : IGroupAdminService
{
    private readonly IUserGroupStore _userGroups;
    private readonly IHostGroupStore _hostGroups;
    private readonly IGroupAccessStore _access;
    private readonly IUserStore _users;
    private readonly IHostStore _hosts;
    private readonly IAuditService _audit;

    public GroupAdminService(
        IUserGroupStore userGroups,
        IHostGroupStore hostGroups,
        IGroupAccessStore access,
        IUserStore users,
        IHostStore hosts,
        IAuditService audit)
    {
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
                MemberCount = users.Count(u => u.GroupIds.Contains(g.GroupId))
            })
            .ToList();
    }

    public UserGroupDto SaveUserGroup(SaveUserGroupRequest request)
    {
        var name = request.GroupName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw DomainException.Validation("群組名稱不可為空。");

        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
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

        _audit.Record(
            action: AuditActions.GroupDelete,
            summary: $"刪除使用者群組「{group.GroupName}」",
            targetKind: "group",
            targetId: groupId.ToString());
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

        var matched = MatchHosts(request);
        var groupsById = _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);

        var candidates = matched
            .Select(h =>
            {
                // 現有群組排除「本目標群組」——那不是「已屬其他群組」，另以 AlreadyInTarget 表達
                var otherGroups = h.GroupIds
                    .Where(id => id != hostGroupId)
                    .Select(id => groupsById.TryGetValue(id, out var name) ? name : $"(已刪除:{id})")
                    .ToList();

                return new HostGroupMemberCandidateDto
                {
                    HostId = h.HostId,
                    HostName = h.HostName,
                    IpAddress = h.IpAddress,
                    CurrentGroups = otherGroups,
                    InOtherGroups = otherGroups.Count > 0,
                    AlreadyInTarget = h.GroupIds.Contains(hostGroupId)
                };
            })
            .OrderByDescending(c => c.InOtherGroups)   // 已屬其他群組的排前面，需要留意的先看到
            .ThenBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new HostGroupMemberPreviewDto
        {
            GroupId = group.GroupId,
            GroupName = group.GroupName,
            MatchCount = candidates.Count,
            InOtherGroupsCount = candidates.Count(c => c.InOtherGroups),
            Candidates = candidates
        };
    }

    public HostGroupMemberPreviewDto AddMembers(long hostGroupId, AddHostGroupMembersRequest request)
    {
        var group = _hostGroups.Get(hostGroupId)
                    ?? throw DomainException.NotFound("找不到這個主機群組，可能已被刪除。");

        var wanted = request.HostIds.Distinct().ToList();
        var added = new List<string>();

        foreach (var hostId in wanted)
        {
            var host = _hosts.Get(hostId);
            if (host == null || host.MergedInto != null) continue;   // 查無或墓碑列略過（防前端送過期資料）
            if (host.GroupIds.Contains(hostGroupId) && !request.RemoveFromOthers) continue;

            // removeFromOthers：只保留本目標群組；否則在既有群組上追加本群組
            var newGroups = request.RemoveFromOthers
                ? new List<long> { hostGroupId }
                : host.GroupIds.Concat(new[] { hostGroupId }).Distinct().ToList();

            _hosts.SetGroups(hostId, newGroups);
            added.Add(host.HostName);
        }

        _audit.Record(
            action: AuditActions.HostUpdate,
            summary: $"批次加入主機群組「{group.GroupName}」：{added.Count} 台" +
                     (request.RemoveFromOthers ? "（並移出原群組）" : ""),
            targetKind: "group",
            targetId: group.GroupId.ToString(),
            detail: new { group.GroupName, request.RemoveFromOthers, Hosts = added });

        // 回傳套用後的最新預覽（同一組主機），讓前端就地反映結果
        return PreviewMembers(hostGroupId, new HostGroupMemberQueryRequest());
    }

    /// <summary>
    /// 依網段或關鍵字命中主機。墓碑列（已併入其他主機）一律排除——它們不該再被指派群組。
    /// 網段與 IP 欄位比對；關鍵字同時比對 HostName 與 IpAddress（NetIQ 主機名即 IP）。
    /// </summary>
    private List<WebHost> MatchHosts(HostGroupMemberQueryRequest request)
    {
        var hosts = _hosts.GetAll().Where(h => h.MergedInto == null);

        if (!string.IsNullOrWhiteSpace(request.Pattern))
        {
            var range = CidrMatcher.Parse(request.Pattern)
                        ?? throw DomainException.Validation(
                            $"「{request.Pattern}」不是有效的網段（可用 10.1.2.0/24、10.1.2.* 或單一 IP）。");
            return hosts.Where(h => CidrMatcher.Matches(range, h.IpAddress)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.Trim();
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
                Active = g.Active
            }).ToList()
        };
    }

    public void SetAccess(long userGroupId, IEnumerable<long> hostGroupIds)
    {
        var userGroup = _userGroups.Get(userGroupId)
                        ?? throw DomainException.NotFound("找不到這個使用者群組。");

        var allHostGroups = _hostGroups.GetAll().ToDictionary(g => g.GroupId);
        var requested = hostGroupIds.Distinct().ToList();

        var unknown = requested.Where(id => !allHostGroups.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw DomainException.Validation($"指定的主機群組不存在（ID：{string.Join("、", unknown)}）。");

        var before = _access.GetAll()
            .Where(a => a.UserGroupId == userGroupId)
            .Select(a => allHostGroups.TryGetValue(a.HostGroupId, out var g) ? g.GroupName : a.HostGroupId.ToString())
            .OrderBy(n => n)
            .ToList();
        var after = requested.Select(id => allHostGroups[id].GroupName).OrderBy(n => n).ToList();

        _access.SetForUserGroup(userGroupId, requested);

        var isGrant = after.Count >= before.Count;
        _audit.Record(
            action: isGrant ? AuditActions.AccessGrant : AuditActions.AccessRevoke,
            summary: $"變更「{userGroup.GroupName}」可存取的主機群組：" +
                     $"由「{Format(before)}」改為「{Format(after)}」",
            targetKind: "group",
            targetId: userGroupId.ToString(),
            detail: new { Before = before, After = after });
    }

    private static string Format(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);
}
