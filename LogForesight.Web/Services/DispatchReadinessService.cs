using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 派工就緒檢查：把「派工會靜默失效」的八種狀況彙整成一份清單（健康頁「派工就緒」卡），
/// 並提供主機群組「看得到的派工池群組」的數量（群組清單與批次設定群組的提示共用）。
///
/// **不寫第二份判定**——每一項都呼叫既有的判定：
/// 派工池成員與可見範圍取自 <see cref="IDispatchCandidateSource.Build"/> 的快照，
/// 「這個人接不接得到這台主機」只問 <c>WorkOrderDispatcher.IneligibleReason</c>（夜間派工與待派試跑用的同一個函式）；
/// AI 窗口問 <see cref="ScheduleCalculator.HasAnyWindow"/>；email 資格問 <see cref="MailNotificationService.EligibleEmail"/>；
/// 管理員成員問 <c>AdminMembersResolver</c>。本檔不含可見範圍或處理能力的規則
/// （<c>DispatchReadinessGuardTests</c> 守門）。
///
/// 郵件通知（<see cref="SystemSettings.MailEnabled"/>）未開啟時，兩個 email 項不列入：沒有寄信就沒有「收不到信」。
/// </summary>
public sealed class DispatchReadinessService
{
    private const int ExampleLimit = 5;

    /// <summary>
    /// 在健康頁「派工就緒」卡照常顯示不通過與修正連結、但<b>不併入</b>整體狀態（<c>HealthAttention.Evaluate</c>）的項目：
    /// 自動派工開關與排程啟用是管理員可以刻意關閉的營運選擇，併入會讓導覽告示列常駐。其餘項目照舊併入。
    /// </summary>
    public static readonly IReadOnlySet<string> NotInHealthStatusIds =
        new HashSet<string>(StringComparer.Ordinal) { "auto-dispatch-off", "schedule-off" };

    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IDispatchCandidateSource _candidates;
    private readonly ISystemSettingsStore _settings;
    private readonly ScheduleOptionsStore _schedule;

    public DispatchReadinessService(
        IHostStore hosts, IHostGroupStore hostGroups, IUserStore users, IUserGroupStore userGroups,
        IDispatchCandidateSource candidates, ISystemSettingsStore settings, ScheduleOptionsStore schedule)
    {
        _hosts = hosts;
        _hostGroups = hostGroups;
        _users = users;
        _userGroups = userGroups;
        _candidates = candidates;
        _settings = settings;
        _schedule = schedule;
    }

    /// <summary>
    /// 一個主機群組被派工池「看見」的狀況。<see cref="PoolGroups"/> 只在 <see cref="Evaluated"/> 為 true 時有意義：
    /// 群組停用、沒有啟用主機（空群組），或派工池沒有任何可接單的成員（沒有東西可比對）時為 false。
    /// </summary>
    public readonly record struct HostGroupSight(long GroupId, string GroupName, bool Evaluated, int PoolGroups);

    /// <summary>
    /// 每個主機群組有幾個啟用中的派工池群組看得到它。「看得到」＝該派工池群組有一位可接單（在池、未暫停）的成員
    /// 看得到這個群組的**全部**啟用主機（群組授權是整群授權；只靠主機負責人看到其中幾台不算授權）。
    /// 以成員為單位歸屬：同時在兩個派工池群組的成員，會讓兩個群組都算看得到。
    /// </summary>
    public IReadOnlyList<HostGroupSight> GetHostGroupSights() => HostGroupSights(_candidates.Build());

    private IReadOnlyList<HostGroupSight> HostGroupSights(DispatchCandidatePool pool)
    {
        var hostIdsByGroup = new Dictionary<long, List<long>>();
        foreach (var host in ActiveHosts())
            foreach (var gid in host.GroupIds)
            {
                if (!hostIdsByGroup.TryGetValue(gid, out var list)) hostIdsByGroup[gid] = list = new List<long>();
                list.Add(host.HostId);
            }

        var poolGroupIds = _userGroups.GetAll().Where(g => g.Active && g.DispatchPool).Select(g => g.GroupId).ToList();
        // 可接單的派工池成員（在池、未暫停）依所屬派工池群組歸類
        var membersByPoolGroup = poolGroupIds.ToDictionary(id => id, _ => new List<long>());
        foreach (var user in _users.GetAll().Where(u => u.Active))
        {
            if (WorkOrderDispatcher.IneligibleReason(pool, user.UserId, null, requirePool: true) != null) continue;
            foreach (var gid in user.GroupIds.Where(membersByPoolGroup.ContainsKey))
                membersByPoolGroup[gid].Add(user.UserId);
        }
        var anyActiveMember = membersByPoolGroup.Values.Any(m => m.Count > 0);

        return _hostGroups.GetAll()
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupId)
            .Select(g =>
            {
                var hostIds = hostIdsByGroup.GetValueOrDefault(g.GroupId);
                if (!g.Active || hostIds == null || hostIds.Count == 0 || !anyActiveMember)
                    return new HostGroupSight(g.GroupId, g.GroupName, false, 0);
                var seeing = membersByPoolGroup.Values.Count(members => members.Any(userId =>
                    hostIds.All(hostId => WorkOrderDispatcher.IneligibleReason(pool, userId, hostId, requirePool: true) == null)));
                return new HostGroupSight(g.GroupId, g.GroupName, true, seeing);
            })
            .ToList();
    }

    private List<WebHost> ActiveHosts() => _hosts.GetAll().Where(h => h.Active && h.MergedInto == null).ToList();

    public DispatchReadinessDto Evaluate()
    {
        var settings = _settings.Get();
        var pool = _candidates.Build();
        var users = _users.GetAll();
        var hosts = ActiveHosts();
        var items = new List<DispatchReadinessItemDto>();

        // 1. 主機沒有群組：沒有群組就沒有任何授權路徑（只剩主機負責人），派工池看不到
        var ungrouped = NetiqHostList.Ungrouped(hosts).OrderBy(h => h.HostName, StringComparer.OrdinalIgnoreCase).ToList();
        items.Add(Item("host-no-group", "主機都有群組", ungrouped.Count == 0, ungrouped.Count,
            ungrouped.Count == 0 ? "所有啟用的主機都已加入主機群組。"
                : $"有 {ungrouped.Count} 台啟用的主機沒有加入任何主機群組，派工池看不到它們。",
            ungrouped.Select(h => h.HostName), "/admin/hosts", "前往主機頁"));

        // 2. 主機群組沒授權給任何派工池群組（啟用且有啟用主機的群組才計；空群組不算）
        var poolGroupExists = _userGroups.GetAll().Any(g => g.Active && g.DispatchPool);
        var sights = HostGroupSights(pool);
        var unseen = sights.Where(s => s.Evaluated && s.PoolGroups == 0).ToList();
        if (pool.ActivePoolMemberCount == 0)
            items.Add(Skipped("host-group-no-pool", "主機群組都有派工池看得到",
                "派工池目前沒有可接單的成員，無法判定主機群組有沒有授權給派工池；先處理上一項。", "/admin/groups", "前往群組與授權"));
        else
            items.Add(Item("host-group-no-pool", "主機群組都有派工池看得到", unseen.Count == 0, unseen.Count,
                unseen.Count == 0 ? "所有啟用的主機群組都至少有一個派工池群組看得到。"
                    : $"有 {unseen.Count} 個主機群組沒有授權給任何派工池群組，群組內的主機不會被自動派工。",
                unseen.Select(s => s.GroupName), "/admin/groups", "去授權"));

        // 3. 派工池：沒有啟用的派工池群組，或群組裡沒有可接單的人（無人、全部停用或全部暫停接單）
        var noPool = pool.ActivePoolMemberCount == 0;
        items.Add(Item("no-pool", "有可接單的派工池", !noPool, noPool ? 1 : 0,
            !noPool ? $"派工池有 {pool.ActivePoolMemberCount} 位可接單的成員。"
                : !poolGroupExists ? "沒有任何啟用中的派工池群組，自動派工不會派出任何單。"
                : "派工池群組裡沒有可接單的成員（沒有人、成員停用或全部暫停接單），自動派工不會派出任何單。",
            Array.Empty<string>(), "/admin/groups", "前往群組與授權"));

        // 4. 自動派工開關
        items.Add(Item("auto-dispatch-off", "自動派工已開啟", settings.AutoDispatchEnabled, settings.AutoDispatchEnabled ? 0 : 1,
            settings.AutoDispatchEnabled ? "夜間分析後會自動派工。" : "自動派工開關沒有開，夜間分析後不會自動派單（續掛既有單與問題負責人規則不受影響）。",
            Array.Empty<string>(), "/admin/settings#dispatch", "前往派工設定"));

        // 5. AI 執行窗口
        var schedule = _schedule.Get();
        var hasAiWindow = ScheduleCalculator.HasAnyWindow(schedule.AiWindows);
        // AI 未設定＝統計模式，是合法運作（與健康頁 AiUnreachable 排除「未設定」同一個口徑）：沒有 AI 要補跑，不需要執行窗口
        if (!AIService.BuildSettingsFromDb(settings).IsConfigured)
            items.Add(Skipped("ai-window", "AI 執行窗口已設定",
                "AI 尚未設定（統計模式），不需要 AI 執行窗口。", "/runs#settings", "前往排程設定"));
        else
            items.Add(Item("ai-window", "AI 執行窗口已設定", hasAiWindow, hasAiWindow ? 0 : 1,
                hasAiWindow ? "AI 背景補跑有設定執行窗口。" : "沒有設定任何 AI 執行窗口，AI 不會自動補跑，白話摘要會一直停在待補。",
                Array.Empty<string>(), "/runs#settings", "前往排程設定"));

        // 6. 排程啟用
        items.Add(Item("schedule-off", "排程已啟用", schedule.Enabled, schedule.Enabled ? 0 : 1,
            schedule.Enabled ? "排程已啟用，會定時取數與分析。" : "排程沒有啟用，不會定時取數與分析，也就不會有夜間派工。",
            Array.Empty<string>(), "/runs#settings", "前往排程設定"));

        var mailChecked = settings.MailEnabled;
        if (mailChecked)
        {
            // 7. 管理員 email：上報（無法處理）與管理員提醒信的收件人
            var admins = AdminMembersResolver.GetAdminMembers(_userGroups, _users);
            var adminsNoEmail = admins.Where(a => MailNotificationService.EligibleEmail(a) == null).ToList();
            var anyAdminEmail = admins.Count > adminsNoEmail.Count;
            items.Add(Item("admin-no-email", "管理員有可用的 email", anyAdminEmail, anyAdminEmail ? 0 : adminsNoEmail.Count,
                anyAdminEmail ? "至少有一位管理員有可用的 email，上報與提醒信寄得出去。"
                    : admins.Count == 0 ? "沒有任何啟用中的管理員，上報信與管理員提醒信沒有收件人。"
                    : "所有管理員都沒有可用的 email，上報信與管理員提醒信寄不出去。",
                adminsNoEmail.Select(a => a.Account), "/admin/users", "前往使用者"));

            // 8. 派工池成員 email：被派到單時的通知與每日摘要
            var userById = users.ToDictionary(u => u.UserId);
            var poolNoEmail = pool.ByUserId.Values
                .Where(c => c.InPool)
                .Where(c => !userById.TryGetValue(c.UserId, out var u) || MailNotificationService.EligibleEmail(u) == null)
                .Select(c => c.Account)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
            items.Add(Item("pool-no-email", "派工池成員都有可用的 email", poolNoEmail.Count == 0, poolNoEmail.Count,
                poolNoEmail.Count == 0 ? "派工池成員都有可用的 email。"
                    : $"有 {poolNoEmail.Count} 位派工池成員沒有可用的 email，被派到單時收不到通知。",
                poolNoEmail, "/admin/users", "前往使用者"));
        }

        return new DispatchReadinessDto
        {
            Items = items,
            FailedCount = items.Count(i => !i.Passed),
            MailChecked = mailChecked
        };
    }

    private static DispatchReadinessItemDto Item(string id, string title, bool passed, int affected, string detail,
        IEnumerable<string> examples, string fixUrl, string fixLabel) => new()
    {
        Id = id, Title = title, Passed = passed, Affected = affected, Detail = detail,
        Examples = examples.Take(ExampleLimit).ToList(), FixUrl = fixUrl, FixLabel = fixLabel
    };

    private static DispatchReadinessItemDto Skipped(string id, string title, string detail, string fixUrl, string fixLabel) => new()
    {
        Id = id, Title = title, Passed = true, Skipped = true, Detail = detail, FixUrl = fixUrl, FixLabel = fixLabel
    };
}
