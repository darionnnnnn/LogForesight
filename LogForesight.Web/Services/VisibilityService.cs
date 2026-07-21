using LogForesight.Web.Auth;
using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 資料可見範圍的解析（docs/WEB-SPEC.md §7.1 三層授權的**第 3 層**）。
///
/// 這是不可繞過的最後防線：即使某個 API 忘了掛 <c>[Permission]</c>，
/// 只要查詢有先過這裡，使用者就拿不到未授權主機的資料。
/// 所有查詢型 Service **必須**先呼叫 <see cref="GetVisibleHostIds"/> 取得範圍再查資料。
///
/// 授權鏈：使用者 → 使用者群組 → lf_group_access → 主機群組 → 主機。
/// 範圍**不進 JWT**：調部門後不該還看得到前部門的主機，所以每次請求即時解析
/// （能力可以接受 token 效期內的延遲，範圍不行）。
/// </summary>
public interface IVisibilityService
{
    /// <summary>目前登入者可見的主機 ID。持有 ViewAll 能力者為全部主機</summary>
    IReadOnlySet<long> GetVisibleHostIds();

    /// <summary>目前登入者可見的主機（已依名稱排序）</summary>
    List<WebHost> GetVisibleHosts();

    /// <summary>確認可見，否則拋 404——**刻意不回 403**，理由見實作註解</summary>
    void EnsureVisible(long hostId);
}

public class VisibilityService : IVisibilityService
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IGroupAccessStore _access;
    private readonly IHostStore _hosts;

    // 每請求快取：一次請求內可能被多個 Service 呼叫（查詢＋計數＋明細），
    // Scoped 生命週期下重複解析同一份資料是白費工
    private IReadOnlySet<long>? _cached;

    public VisibilityService(
        ICurrentUser currentUser,
        IUserStore users,
        IUserGroupStore userGroups,
        IGroupAccessStore access,
        IHostStore hosts)
    {
        _currentUser = currentUser;
        _users = users;
        _userGroups = userGroups;
        _access = access;
        _hosts = hosts;
    }

    public IReadOnlySet<long> GetVisibleHostIds()
    {
        if (_cached != null) return _cached;

        var allHosts = _hosts.GetAll();

        // ViewAll（dev / manager / admin）：全部主機
        if (_currentUser.Has(Capability.ViewAll))
        {
            _cached = allHosts.Select(h => h.HostId).ToHashSet();
            return _cached;
        }

        // serverAdmin 沒有 ViewAll，也沒有任何主機——它是維護帳號，不看業務資料
        if (_currentUser.IsServerAdmin || !_currentUser.IsAuthenticated)
        {
            _cached = new HashSet<long>();
            return _cached;
        }

        var user = _users.Get(_currentUser.UserId);
        if (user == null || !user.Active)
        {
            _cached = new HashSet<long>();
            return _cached;
        }

        // 停用的使用者群組不帶來授權（與能力解析的規則一致）
        var activeUserGroupIds = _userGroups.GetAll()
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .Select(g => g.GroupId)
            .ToHashSet();

        var hostGroupIds = _access.GetAll()
            .Where(a => activeUserGroupIds.Contains(a.UserGroupId))
            .Select(a => a.HostGroupId)
            .ToHashSet();

        _cached = allHosts
            .Where(h => h.GroupIds.Any(hostGroupIds.Contains))
            .Select(h => h.HostId)
            .ToHashSet();

        return _cached;
    }

    public List<WebHost> GetVisibleHosts()
    {
        var visible = GetVisibleHostIds();
        return _hosts.GetAll()
            .Where(h => visible.Contains(h.HostId))
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void EnsureVisible(long hostId)
    {
        if (GetVisibleHostIds().Contains(hostId)) return;

        // 回 404 而不是 403：403 等於告訴對方「這台主機存在，只是你沒權限」，
        // 那本身就是資訊洩漏（可以用來列舉機房裡有哪些主機）。
        // 對沒有權限的人來說，「不存在」與「看不到」本來就該是同一件事。
        throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
    }
}
