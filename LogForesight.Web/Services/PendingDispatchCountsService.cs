using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>待派試跑只回計數的取得方式（對應 <c>WorkOrderBoardService.GetGapCounts</c>，DI 註冊）</summary>
public delegate PendingDispatchSummary PendingDispatchSummaryProvider(DateTime? from, DateTime? to);

/// <summary>
/// 待派試跑計數的單一取得點（儀表板待派卡與「需要你處理」共用）。
/// 結果依「可見主機集合＋靜音條件＋今天＋資料版本」存進 <see cref="SummaryCache"/>：
/// 試跑只讀呼叫者可見範圍內的資料、結果是計數（不含主機名），可見範圍相同的具派工權限者
/// （管理員與主管）共用同一份；不具 Assign 的人不會呼叫這裡（呼叫端先判能力）。
/// 鍵含日期：試跑窗口是昨天往前 7 天，跨日不沿用前一天。
/// </summary>
public class PendingDispatchCountsService
{
    private readonly PendingDispatchSummaryProvider _compute;
    private readonly SummaryCache _cache;
    private readonly IVisibilityService _visibility;
    private readonly IIssueExclusionSource _exclusions;

    public PendingDispatchCountsService(PendingDispatchSummaryProvider compute, SummaryCache cache,
        IVisibilityService visibility, IIssueExclusionSource exclusions)
    {
        _compute = compute;
        _cache = cache;
        _visibility = visibility;
        _exclusions = exclusions;
    }

    /// <summary>以完整待派清單的取得方式推出計數（只給沒有注入計數入口的組裝，例如測試替身）</summary>
    public static PendingDispatchSummaryProvider FromGaps(PendingDispatchGapsProvider gaps) => (from, to) =>
    {
        // 與試跑的預設期間一致（昨天往前 7 天）；替身清單的取得方式需要明確期間
        var periodTo = (to ?? DateTime.Today.AddDays(-1)).Date;
        var periodFrom = (from ?? periodTo.AddDays(-6)).Date;
        var g = gaps(periodFrom, periodTo, 1);
        return g.TooLarge
            ? new PendingDispatchSummary(true, g.From, g.To, 0, 0)
            : new PendingDispatchSummary(false, g.From, g.To, g.Dispatchable.Issues, g.UnassignableSection.Issues);
    };

    public PendingDispatchSummary Get()
    {
        var exclusion = _exclusions.Current();
        return _cache.GetOrAdd(
            SummaryCache.KeyOf("pending-dispatch-summary", $"{exclusion.CacheToken}|{exclusion.Today:yyyyMMdd}", _visibility.GetVisibleHostIds()),
            () => _compute(null, null));
    }
}
