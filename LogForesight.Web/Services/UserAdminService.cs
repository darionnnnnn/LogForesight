using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 使用者與群組維護（docs/WEB-SPEC.md §9.8）。
///
/// 業務規則集中在這裡，不在 Controller 也不在 Repository：
/// builtin 群組的保護、稽核寫入、群組存在性驗證。
/// </summary>
public interface IUserAdminService
{
    List<UserDto> GetUsers();

    List<UserGroupDto> GetGroups();

    UserDto SaveUser(SaveUserRequest request);

    UserDto SetUserGroups(long userId, IEnumerable<long> groupIds);
}

public class UserAdminService : IUserAdminService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;
    private readonly IAuditService _audit;

    public UserAdminService(IUserStore users, IUserGroupStore groups, IAuditService audit)
    {
        _users = users;
        _groups = groups;
        _audit = audit;
    }

    public List<UserDto> GetUsers()
    {
        var groupsById = _groups.GetAll().ToDictionary(g => g.GroupId);

        return _users.GetAll()
            .OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase)
            .Select(u => ToDto(u, groupsById))
            .ToList();
    }

    public List<UserGroupDto> GetGroups()
    {
        var users = _users.GetAll();

        return _groups.GetAll()
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

    public UserDto SaveUser(SaveUserRequest request)
    {
        var account = request.Account.Trim();
        if (string.IsNullOrWhiteSpace(account))
            throw DomainException.Validation("帳號不可為空。");

        var existing = _users.FindByAccount(account);
        var isNew = existing == null;

        var user = _users.Upsert(new WebUser
        {
            Account = account,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? account : request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Active = request.Active,
            // 群組成員資格由 SetUserGroups 專責維護：如果這裡也能改，
            // 「更新顯示名稱」這種操作就有機會意外清掉某人的所有權限
            GroupIds = existing?.GroupIds ?? new List<long>()
        });

        _audit.Record(
            action: isNew ? AuditActions.UserCreate : AuditActions.UserUpdate,
            summary: isNew
                ? $"新增使用者 {user.Account}（{user.DisplayName}）"
                : $"更新使用者 {user.Account}：顯示名稱「{user.DisplayName}」、狀態「{(user.Active ? "啟用" : "停用")}」",
            targetKind: "user",
            targetId: user.UserId.ToString(),
            detail: new { user.Account, user.DisplayName, user.Email, user.Active });

        return ToDto(user, _groups.GetAll().ToDictionary(g => g.GroupId));
    }

    public UserDto SetUserGroups(long userId, IEnumerable<long> groupIds)
    {
        var user = _users.Get(userId)
                   ?? throw DomainException.NotFound("找不到這個使用者，可能已被刪除。");

        var allGroups = _groups.GetAll().ToDictionary(g => g.GroupId);
        var requested = groupIds.Distinct().ToList();

        var unknown = requested.Where(id => !allGroups.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw DomainException.Validation($"指定的群組不存在（ID：{string.Join("、", unknown)}）。");

        var before = user.GroupIds.Select(id => allGroups.TryGetValue(id, out var g) ? g.GroupName : id.ToString()).ToList();
        var after = requested.Select(id => allGroups[id].GroupName).ToList();

        _users.SetGroups(userId, requested);

        _audit.Record(
            action: AuditActions.UserUpdate,
            summary: $"變更使用者 {user.Account} 的群組：由「{FormatGroups(before)}」改為「{FormatGroups(after)}」",
            targetKind: "user",
            targetId: userId.ToString(),
            detail: new { Before = before, After = after });

        return ToDto(_users.Get(userId)!, allGroups);
    }

    private static string FormatGroups(List<string> names) => names.Count == 0 ? "（無）" : string.Join("、", names);

    private static UserDto ToDto(WebUser user, IReadOnlyDictionary<long, UserGroup> groupsById) => new()
    {
        UserId = user.UserId,
        Account = user.Account,
        DisplayName = user.DisplayName,
        Email = user.Email,
        Active = user.Active,
        GroupIds = user.GroupIds,
        GroupNames = user.GroupIds
            .Select(id => groupsById.TryGetValue(id, out var g) ? g.GroupName : $"(已刪除:{id})")
            .ToList()
    };
}
