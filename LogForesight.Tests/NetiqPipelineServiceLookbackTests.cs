using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/FEEDBACK-3-PLAN.md #1：<see cref="NetiqPipelineService.ResolveLookbackDays"/>——
/// 首次執行與缺漏日回補統一套用 BackfillDays，取代原本「首次深度回補 14 天」的例外路徑。
/// </summary>
public class NetiqPipelineServiceLookbackTests
{
    [Fact]
    public void 預設值1只回補一天()
    {
        Assert.Equal(1, NetiqPipelineService.ResolveLookbackDays(backfillDays: 1, trendWindowDays: 14));
    }

    [Fact]
    public void BackfillDays設為3時回補三天內缺漏()
    {
        Assert.Equal(3, NetiqPipelineService.ResolveLookbackDays(backfillDays: 3, trendWindowDays: 14));
    }

    /// <summary>不再有「首次執行深度回補 14 天」的例外——即使 BackfillDays 設定值較大，
    /// 也不會超過趨勢窗口本身，回補比趨勢分析用得到的更多天沒有意義</summary>
    [Fact]
    public void BackfillDays大於趨勢窗口時以趨勢窗口為準()
    {
        Assert.Equal(14, NetiqPipelineService.ResolveLookbackDays(backfillDays: 20, trendWindowDays: 14));
    }

    [Fact]
    public void BackfillDays等於趨勢窗口時取該值()
    {
        Assert.Equal(14, NetiqPipelineService.ResolveLookbackDays(backfillDays: 14, trendWindowDays: 14));
    }
}
