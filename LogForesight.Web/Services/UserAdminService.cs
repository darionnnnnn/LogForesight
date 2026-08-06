using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 使用者與群組維護（docs/WEB-SPEC.md §9.8、§9.8a 使用者詳細）。
///
/// 業務規則集中在這裡，不在 Controller 也不在 Repository：
/// builtin 群組的保護、稽核寫入、群組存在性驗證。
/// </summary>
public class UserAdminService
{
    private readonly IUserStore _users;
    private readonly IUserGroupStore _groups;
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IIssueCaseStore _cases;
    private readonly IVisibilityService _visibility;
    private readonly IAuditService _audit;

    public UserAdminService(
        IUserStore users,
        IUserGroupStore groups,
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IIssueCaseStore cases,
        IVisibilityService visibility,
        IAuditService audit)
    {
        _users = users;
        _groups = groups;
        _hosts = hosts;
        _hostGroups = hostGroups;
        _cases = cases;
        _visibility = visibility;
        _audit = audit;
    }

    public List<UserDto> GetUsers()
    {
        var groupsById = _groups.GetAll().ToDictionary(g => g.GroupId);

        // 負責主機數（體檢 H3）：一次掃過主機清單建索引，不要逐使用者呼叫
        // GetOwnedHostIdsFor——那是 N 次整份 hosts blob 讀取（規模化規劃根因 A）
        var ownedCounts = new Dictionary<long, int>();
        foreach (var host in _hosts.GetAll().Where(h => h.Active))
        {
            foreach (var ownerId in host.OwnerUserIds)
                ownedCounts[ownerId] = ownedCounts.GetValueOrDefault(ownerId) + 1;
        }

        return _users.GetAll()
            .OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase)
            .Select(u => ToDto(u, groupsById, ownedCounts.GetValueOrDefault(u.UserId)))
            .ToList();
    }

    /// <summary>
    /// 使用者詳細（docs/archive/FEEDBACK-11-PLAN.md §3）：基本資料＋所屬群組＋**此人**的可見主機
    /// （含「為什麼看得到」）＋被指派歷程。
    ///
    /// 可見範圍以**被查看者**為準（`GetVisibleHostIdsFor`），與處理人工作頁「以檢視者為準」
    /// 的規則刻意不同——這頁要回答的是「這個人看得到什麼」，用檢視者的範圍算會答非所問。
    /// 能檢視本頁的前提是 Maintain（頁面與 API 皆標註），不會因此讓一般角色查到別人的授權面。
    /// </summary>
    public UserDetailDto GetUserDetail(long userId)
    {
        var user = _users.Get(userId)
                   ?? throw DomainException.NotFound("找不到這個使用者，可能已被刪除。");

        var allGroups = _groups.GetAll();
        var groupsById = allGroups.ToDictionary(g => g.GroupId);
        var memberGroups = allGroups.Where(g => user.GroupIds.Contains(g.GroupId)).ToList();

        // 能力＝啟用中群組的角色聯集（停用群組不給能力，同 IdentityService.ResolveCapabilities）
        // ＋負責人隱含的 User 角色（§2b）。
        // **停用帳號一律顯示為無能力**：它連登入都進不來（IdentityService 擋、
        // ActiveUserMiddleware 逐請求 401），列出「他可以標記處理狀態」是誤導；
        // 與下方可見主機為空同一個判斷，畫面上兩者要一致。
        var ownedHostIds = _visibility.GetOwnedHostIdsFor(userId);
        var roles = new List<UserRole>();
        if (user.Active)
        {
            roles.AddRange(memberGroups.Where(g => g.Active).Select(g => g.Role));
            if (ownedHostIds.Count > 0) roles.Add(UserRole.User);
        }

        // 兩條路徑分開問（§2b）：一台主機可能既是他負責的、部門群組也被授權，
        // 「為什麼看得到」因此是兩個獨立的是非題，不是二選一
        var groupVisibleHostIds = _visibility.GetGroupVisibleHostIdsFor(userId);
        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var hostGroupsById = _hostGroups.GetAll().ToDictionary(g => g.GroupId);

        var visibleHosts = ownedHostIds.Union(groupVisibleHostIds)
            .Where(hostsById.ContainsKey)
            .Select(id => hostsById[id])
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .Select(h => new UserVisibleHostDto
            {
                HostId = h.HostId,
                HostName = h.HostName,
                IpAddress = h.IpAddress,
                Os = h.Os,
                GroupNames = NameFormat.ResolveNames(h.GroupIds, hostGroupsById, g => g.GroupName),
                ViaOwner = ownedHostIds.Contains(h.HostId),
                ViaGroup = groupVisibleHostIds.Contains(h.HostId)
            })
            .ToList();

        return new UserDetailDto
        {
            User = ToDto(user, groupsById),
            Groups = memberGroups.Select(g => new UserGroupDto
            {
                GroupId = g.GroupId,
                GroupName = g.GroupName,
                Role = g.Role.ToString(),
                Builtin = g.Builtin,
                Active = g.Active,
                MemberCount = _users.GetAll().Count(u => u.GroupIds.Contains(g.GroupId))
            }).ToList(),
            Capabilities = RoleCapabilityMap.For(roles).Select(c => c.ToString()).OrderBy(c => c).ToList(),
            VisibleHosts = visibleHosts,
            AssignmentHistory = BuildAssignmentHistory(userId, hostsById)
        };
    }

    private List<UserAssignmentHistoryDto> BuildAssignmentHistory(long userId, IReadOnlyDictionary<long, WebHost> hostsById)
    {
        var hostIdByName = hostsById.Values
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().HostId, StringComparer.OrdinalIgnoreCase);

        return _cases.GetByHandler(userId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new UserAssignmentHistoryDto
            {
                CaseId = c.CaseId,
                HostId = hostIdByName.TryGetValue(c.HostName, out var id) ? id : null,
                HostName = c.HostName,
                IssueLabel = c.IssueLabel,
                Status = c.Status,
                StatusText = HandlingTextHelpers.IssueStatusText(c.Status),
                Closed = c.ClosedAt != null,
                CreatedAt = c.CreatedAt,
                CreatedByAccount = c.CreatedByAccount,
                ClosedAt = c.ClosedAt,
                FirstLinkedDate = c.FirstLinkedDate.ToString("yyyy-MM-dd"),
                LastLinkedDate = c.LastLinkedDate.ToString("yyyy-MM-dd")
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

        NameFormat.EnsureAllKnown(requested, allGroups, "群組");

        var before = user.GroupIds.Select(id => allGroups.TryGetValue(id, out var g) ? g.GroupName : id.ToString()).ToList();
        var after = requested.Select(id => allGroups[id].GroupName).ToList();

        _users.SetGroups(userId, requested);

        _audit.Record(
            action: AuditActions.UserUpdate,
            summary: $"變更使用者 {user.Account} 的群組：由「{NameFormat.Join(before)}」改為「{NameFormat.Join(after)}」",
            targetKind: "user",
            targetId: userId.ToString(),
            detail: new { Before = before, After = after });

        return ToDto(_users.Get(userId)!, allGroups);
    }

    /// <summary>一次新增的帳號上限，防手滑貼整份名冊——超過請分批處理</summary>
    public const int BatchCreateMaxAccounts = 100;

    public BatchCreateUsersResultDto BatchCreateUsers(BatchCreateUsersRequest request)
    {
        var allGroups = _groups.GetAll().ToDictionary(g => g.GroupId);
        var requestedGroupIds = (request.GroupIds ?? new List<long>()).Distinct().ToList();

        var unknownGroups = requestedGroupIds.Where(id => !allGroups.ContainsKey(id)).ToList();
        if (unknownGroups.Count > 0)
            throw DomainException.Validation($"指定的群組不存在（ID：{string.Join("、", unknownGroups)}）。");

        var (accounts, invalidCount) = NormalizeBatchAccounts(request.Accounts);
        if (accounts.Count == 0)
            throw DomainException.Validation("請至少輸入一個有效帳號。");
        if (accounts.Count > BatchCreateMaxAccounts)
            throw DomainException.Validation($"一次最多新增 {BatchCreateMaxAccounts} 筆帳號，請分批處理。");

        var result = new BatchCreateUsersResultDto { InvalidCount = invalidCount };

        foreach (var account in accounts)
        {
            var existing = _users.FindByAccount(account);
            if (existing != null)
            {
                if (request.OverwriteExisting)
                {
                    // 既有帳號的群組覆蓋走既有 SetUserGroups：同一套驗證＋Before/After 稽核，
                    // 與手動編輯使用者的群組是同一條稽核軌跡，不另外自訂一套
                    SetUserGroups(existing.UserId, requestedGroupIds);
                    result.Overwritten.Add(account);
                }
                else
                {
                    result.Skipped.Add(account);
                }
                continue;
            }

            var user = _users.Upsert(new WebUser
            {
                Account = account,
                DisplayName = account,   // 批次新增只填帳號，顯示名稱預設＝帳號（AD 登入時可能補上真正的顯示名稱，見 #8）
                Email = null,
                Active = request.Active,
                GroupIds = new List<long>()
            });
            _users.SetGroups(user.UserId, requestedGroupIds);

            _audit.Record(
                action: AuditActions.UserCreate,
                summary: $"批次新增使用者 {user.Account}",
                targetKind: "user",
                targetId: user.UserId.ToString(),
                detail: new { user.Account, user.DisplayName, user.Active, GroupIds = requestedGroupIds });

            result.Created.Add(account);
        }

        _audit.Record(
            action: AuditActions.UserCreate,
            summary: $"批次新增使用者摘要：新增 {result.Created.Count} 筆、覆蓋 {result.Overwritten.Count} 筆、跳過 {result.Skipped.Count} 筆",
            targetKind: "user_batch",
            targetId: "batch",
            detail: new { result.Created, result.Overwritten, result.Skipped, result.InvalidCount });

        return result;
    }

    /// <summary>trim、去除空白行、去重（不分大小寫，帳號比對規則同 FindByAccount）、篩掉超長帳號</summary>
    private static (List<string> Accounts, int InvalidCount) NormalizeBatchAccounts(List<string>? raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accounts = new List<string>();
        var invalidCount = 0;

        foreach (var line in raw ?? new List<string>())
        {
            var trimmed = line?.Trim() ?? "";
            if (trimmed.Length == 0) continue;   // 空白行是輸入排版，不是壞資料，不計入 invalid

            if (trimmed.Length > 255)
            {
                invalidCount++;
                continue;
            }

            if (seen.Add(trimmed)) accounts.Add(trimmed);
        }

        return (accounts, invalidCount);
    }

    private static UserDto ToDto(
        WebUser user, IReadOnlyDictionary<long, UserGroup> groupsById, int ownedHostCount = 0) => new()
    {
        UserId = user.UserId,
        Account = user.Account,
        DisplayName = user.DisplayName,
        Email = user.Email,
        Active = user.Active,
        GroupIds = user.GroupIds,
        GroupNames = NameFormat.ResolveNames(user.GroupIds, groupsById, g => g.GroupName),
        LastLoginAt = user.LastLoginAt,
        OwnedHostCount = ownedHostCount
    };
}
