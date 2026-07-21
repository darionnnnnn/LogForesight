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
