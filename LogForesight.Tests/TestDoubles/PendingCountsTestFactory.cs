using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

/// <summary>
/// 測試組裝待派計數服務：以「完整待派清單」的替身推出計數（<see cref="PendingDispatchSummaryProvider"/> 替身），
/// 其餘（快取、可見範圍、靜音條件）用正式的 <see cref="PendingDispatchCountsService"/>。
/// </summary>
internal static class PendingCountsTestFactory
{
    public static PendingDispatchCountsService FromGaps(PendingDispatchGapsProvider gaps, SummaryCache cache,
        IVisibilityService visibility, IIssueExclusionSource exclusions) =>
        new(SummaryFromGaps(gaps), cache, visibility, exclusions);

    public static PendingDispatchSummaryProvider SummaryFromGaps(PendingDispatchGapsProvider gaps) => (from, to) =>
    {
        // 與試跑的預設期間一致（昨天往前 7 天）；替身清單的取得方式需要明確期間
        var periodTo = (to ?? DateTime.Today.AddDays(-1)).Date;
        var periodFrom = (from ?? periodTo.AddDays(-6)).Date;
        var g = gaps(periodFrom, periodTo, 1);
        return g.TooLarge
            ? new PendingDispatchSummary(true, g.From, g.To, 0, 0)
            : new PendingDispatchSummary(false, g.From, g.To, g.Dispatchable.Issues, g.UnassignableSection.Issues);
    };
}
