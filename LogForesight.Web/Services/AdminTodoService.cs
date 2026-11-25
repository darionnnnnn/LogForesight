using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 管理員待辦（儀表板「需要你處理」列與交辦總覽提醒區的共同入口）：只把「呼叫者能不能看待辦類別」與「可派未派的數字從哪來」
/// 兩件使用者相關的事接到單一彙整函式 <see cref="AdminReminderService.Compute"/>，不另寫任何計算。
/// 具 Assign 才帶待辦類別；Manager、Dev（只有 ViewAll）拿到的是只含既有提醒類別的結果。
/// </summary>
public class AdminTodoService
{
    private readonly AdminReminderService _reminders;
    private readonly PendingDispatchGapsProvider _gaps;
    private readonly ICurrentUser _currentUser;
    private readonly SummaryCache _cache;
    private readonly IVisibilityService _visibility;
    private readonly IIssueExclusionSource _exclusions;

    public AdminTodoService(AdminReminderService reminders, PendingDispatchGapsProvider gaps, ICurrentUser currentUser,
        SummaryCache cache, IVisibilityService visibility, IIssueExclusionSource exclusions)
    {
        _reminders = reminders;
        _gaps = gaps;
        _currentUser = currentUser;
        _cache = cache;
        _visibility = visibility;
        _exclusions = exclusions;
    }

    /// <summary>目前使用者的提醒與待辦；沒有 Assign 時不含待辦類別，也不跑待派試跑</summary>
    public AdminReminderDto GetForCurrentUser()
    {
        if (!_currentUser.Has(Capability.Assign)) return _reminders.Compute();
        return _reminders.Compute(includeTodo: true, PendingCounts());
    }

    /// <summary>
    /// 呼叫端已經有待派試跑結果時（儀表板）直接帶入，不再跑第二次試跑。
    /// <paramref name="pending"/> 為 null 時可派未派不計。
    /// </summary>
    public AdminReminderDto GetForCurrentUser(PendingDispatchCounts? pending) =>
        _currentUser.Has(Capability.Assign) ? _reminders.Compute(includeTodo: true, pending) : _reminders.Compute();

    /// <summary>
    /// 可派問題數：待派試跑結果依可見主機集合與靜音條件各存一份（鍵含授權維度；內容是計數，不含主機名）。
    /// 試跑期間用預設的「昨天往前 7 天」，與儀表板待派卡同一段。
    /// </summary>
    private PendingDispatchCounts PendingCounts() => _cache.GetOrAdd(
        SummaryCache.KeyOf("pending-dispatch-counts", _exclusions.Current().CacheToken, _visibility.GetVisibleHostIds()),
        () =>
        {
            var gaps = _gaps(null, null, 1);
            return new PendingDispatchCounts(gaps.Dispatchable.Issues, gaps.TooLarge);
        });
}
