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
///
/// **負責人路徑**（docs/archive/FEEDBACK-11-PLAN.md §2b）與群組授權鏈**取聯集**：主機的
/// <see cref="WebHost.OwnerUserIds"/> 含此人時直接可見那台主機。理由是負責人匯入
/// （owners.csv）是主機歸屬的第一手資料，卻與部門群組授權完全脫鉤——改版前「這台出事、
/// 負責人卻打不開」是常態，要靠管理員另外去授權矩陣補一刀才會通。負責人給的是**整台**
/// 可見（與群組授權同級），不是案件授與那種問題層級的窄授與。
///
/// **案件授與**（docs/archive/FEEDBACK-10-PLAN.md §7）是這兩條路徑之外、刻意更窄的第三條路徑：
/// 被指派為某個問題案件的處理人時，取得「那台主機的那個問題」的檢視權，其餘一律不可見。
/// 沒有這條路徑，把問題交辦給不在該主機授權範圍內的人＝對方打不開，等於白指派。
/// </summary>
public interface IVisibilityService
{
    /// <summary>
    /// 目前登入者可見的主機 ID。持有 ViewAll 能力者為全部主機；
    /// 其餘人為「群組授權 ∪ 自己是負責人的主機」（docs/archive/FEEDBACK-11-PLAN.md §2b）。
    /// </summary>
    IReadOnlySet<long> GetVisibleHostIds();

    /// <summary>
    /// 指定使用者**因負責人身分**而可見的主機 ID（docs/archive/FEEDBACK-11-PLAN.md §2b／§3）。
    /// 與 <see cref="GetGroupVisibleHostIdsFor"/> 一起回答使用者詳細頁的
    /// 「他為什麼看得到這台」——兩條路徑可同時成立，所以分成兩個集合而不是一個列舉。
    /// </summary>
    IReadOnlySet<long> GetOwnedHostIdsFor(long userId);

    /// <summary>
    /// 指定使用者**經群組授權鏈**而可見的主機 ID（含 ViewAll 角色的全部主機）。
    /// <see cref="GetVisibleHostIdsFor"/> ＝ 本集合 ∪ <see cref="GetOwnedHostIdsFor"/>。
    /// </summary>
    IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId);

    /// <summary>目前登入者可見的主機（已依名稱排序）</summary>
    List<WebHost> GetVisibleHosts();

    /// <summary>確認可見，否則拋 404——**刻意不回 403**，理由見實作註解</summary>
    void EnsureVisible(long hostId);

    /// <summary>
    /// 案件授與（docs/archive/FEEDBACK-10-PLAN.md §7）：目前登入者身為案件處理人而取得的
    /// **範圍限定**檢視權——主機名稱 → 該主機上他經手過的問題簽章集合。
    ///
    /// 這是「被交辦了就看得到那件事」的最小授權：admin 把某台沒授權給你的主機上的某個問題
    /// 指派給你，你看得到的就只有那一個問題，不是那台主機的全部資料。
    /// 授與以「現在或曾經是處理人」為準——結案後仍看得到自己處理過的東西。
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlySet<string>> GetCaseGrants();

    /// <summary>
    /// 這台主機是不是**只**經由案件授與才看得到（docs/archive/FEEDBACK-10-PLAN.md §7）。
    /// true 時查詢端要把回傳內容裁剪到被授與的問題（見 RecordDetailQueryService）。
    /// </summary>
    bool IsCaseGrantOnly(long hostId);

    /// <summary>
    /// 這台主機上允許存取的問題簽章白名單（docs/archive/FEEDBACK-10-PLAN.md §7）：
    /// **null＝不限制**（使用者對這台主機有一般檢視權）；非 null＝只能碰這些問題。
    ///
    /// 這是案件授與的**單一咽喉點**：任何「以 issueKey 為參數」的讀寫入口（標記狀態、
    /// 問題歷史、處理歷程）都要先問過這裡。沒有它，案件授與只擋得住畫面——
    /// 使用者換一個 issueKey 直接打 API，就能碰到同一天其他不屬於他的問題。
    /// </summary>
    IReadOnlySet<string>? GetIssueKeyRestriction(long hostId);

    /// <summary>
    /// **指定使用者**（不是目前登入者）的可見主機（docs/archive/FEEDBACK-10-PLAN.md §7）。
    /// 用途是指派前的檢查：「我要交辦給的這個人，看得到這台主機嗎？」——看不到仍可指派
    /// （對方會取得案件範圍限定的檢視權），但執行指派的人要被告知。
    /// **不做每請求快取**：對象是別人、且一次指派可能問到多個不同的人。
    /// </summary>
    IReadOnlySet<long> GetVisibleHostIdsFor(long userId);
}

public class VisibilityService : IVisibilityService
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IGroupAccessStore _access;
    private readonly IHostStore _hosts;
    private readonly IIssueCaseStore _cases;

    // 每請求快取：一次請求內可能被多個 Service 呼叫（查詢＋計數＋明細），
    // Scoped 生命週期下重複解析同一份資料是白費工
    private IReadOnlySet<long>? _cached;
    private IReadOnlyDictionary<string, IReadOnlySet<string>>? _cachedGrants;

    public VisibilityService(
        ICurrentUser currentUser,
        IUserStore users,
        IUserGroupStore userGroups,
        IGroupAccessStore access,
        IHostStore hosts,
        IIssueCaseStore cases)
    {
        _currentUser = currentUser;
        _users = users;
        _userGroups = userGroups;
        _access = access;
        _hosts = hosts;
        _cases = cases;
    }

    public IReadOnlySet<long> GetVisibleHostIds()
    {
        if (_cached != null) return _cached;

        // 停用主機（管理員手動停用，或 Sentinel 移除觸發的系統停用）從可見範圍整批排除——
        // 資料只留在資料庫，歷史/儀表板/報表都不再計入，重新啟用即全部復原。
        // 墓碑列（合併來源，同樣 Active=false）不受影響：它們的歷史經由存活主機的別名展開
        // 可見（RecordRepository.VisibleHostKeys），不需要自己也在這個集合裡。
        var allHosts = _hosts.GetAll().Where(h => h.Active).ToList();

        // ViewAll（dev / manager / admin）：全部啟用中的主機
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

        // 群組授權 ∪ 負責人（§2b）：負責人是主機歸屬的第一手資料，不該還要另外去授權矩陣補一刀
        _cached = allHosts
            .Where(h => h.GroupIds.Any(hostGroupIds.Contains) || h.OwnerUserIds.Contains(user.UserId))
            .Select(h => h.HostId)
            .ToHashSet();

        return _cached;
    }

    public IReadOnlySet<long> GetOwnedHostIdsFor(long userId)
    {
        // 停用的使用者不因負責人身分取得任何範圍（停用優先於一切授權路徑，同 GetVisibleHostIds）
        var user = _users.Get(userId);
        if (userId <= 0 || user == null || !user.Active) return new HashSet<long>();

        return _hosts.GetAll()
            .Where(h => h.Active && h.OwnerUserIds.Contains(userId))
            .Select(h => h.HostId)
            .ToHashSet();
    }

    public IReadOnlySet<long> GetVisibleHostIdsFor(long userId)
    {
        // 與 GetVisibleHostIds 同一套規則（群組授權 ∪ 負責人，§2b）——兩邊漂移的話，
        // 指派前的「他看得到這台嗎」提示就會說謊
        var visible = GetGroupVisibleHostIdsFor(userId).ToHashSet();
        visible.UnionWith(GetOwnedHostIdsFor(userId));
        return visible;
    }

    public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId)
    {
        var user = _users.Get(userId);
        if (user == null || !user.Active) return new HashSet<long>();

        // 對象持有 ViewAll（dev/manager/admin 群組）時看得到全部主機——能力來自所屬群組的
        // 角色聯集，與登入時解析 cap claim 的規則同一套（RoleCapabilityMap）
        var activeGroups = _userGroups.GetAll()
            .Where(g => g.Active && user.GroupIds.Contains(g.GroupId))
            .ToList();

        var allHosts = _hosts.GetAll().Where(h => h.Active).ToList();

        if (RoleCapabilityMap.For(activeGroups.Select(g => g.Role)).Contains(Capability.ViewAll))
            return allHosts.Select(h => h.HostId).ToHashSet();

        var hostGroupIds = _access.GetAll()
            .Where(a => activeGroups.Any(g => g.GroupId == a.UserGroupId))
            .Select(a => a.HostGroupId)
            .ToHashSet();

        return allHosts
            .Where(h => h.GroupIds.Any(hostGroupIds.Contains))
            .Select(h => h.HostId)
            .ToHashSet();
    }

    public List<WebHost> GetVisibleHosts()
    {
        var visible = GetVisibleHostIds();
        return _hosts.GetAll()
            .Where(h => visible.Contains(h.HostId))
            .OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> GetCaseGrants()
    {
        if (_cachedGrants != null) return _cachedGrants;

        // ServerAdmin（UserId=0）與未登入者沒有案件，不必查
        if (_currentUser.UserId <= 0)
        {
            _cachedGrants = new Dictionary<string, IReadOnlySet<string>>();
            return _cachedGrants;
        }

        _cachedGrants = _cases.GetByHandler(_currentUser.UserId)
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)g.Select(c => c.IssueKey).ToHashSet(StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

        return _cachedGrants;
    }

    public bool IsCaseGrantOnly(long hostId)
    {
        if (GetVisibleHostIds().Contains(hostId)) return false;

        var host = _hosts.Get(hostId);
        return host != null && GetCaseGrants().ContainsKey(host.HostName);
    }

    public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId)
    {
        if (!IsCaseGrantOnly(hostId)) return null;

        var host = _hosts.Get(hostId);
        return host != null && GetCaseGrants().TryGetValue(host.HostName, out var keys)
            ? keys
            : new HashSet<string>(StringComparer.Ordinal);   // 主機查無＝什麼都不給碰
    }

    public void EnsureVisible(long hostId)
    {
        if (GetVisibleHostIds().Contains(hostId)) return;

        // 案件授與（§7）：被交辦的問題所在的主機放行，但呼叫端必須用 IsCaseGrantOnly
        // 把內容裁剪到那些問題——放行的是「這件事」，不是「這台主機」
        if (IsCaseGrantOnly(hostId)) return;

        // 回 404 而不是 403：403 等於告訴對方「這台主機存在，只是你沒權限」，
        // 那本身就是資訊洩漏（可以用來列舉機房裡有哪些主機）。
        // 對沒有權限的人來說，「不存在」與「看不到」本來就該是同一件事。
        throw DomainException.NotFound("找不到這台主機，或您沒有檢視權限。");
    }
}
