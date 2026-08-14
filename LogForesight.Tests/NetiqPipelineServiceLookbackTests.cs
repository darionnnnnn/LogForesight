using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-3-PLAN.md #1：<see cref="NetiqPipelineService.ResolveLookbackDays"/>——
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

/// <summary>
/// Sentinel 平行度的行程內上限（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）。
///
/// 分析已搬進網站行程執行（本輪定案不拆獨立 worker），平行度不再只是「分析跑多快」，
/// 而是「同時有幾條執行緒在跟使用者的請求搶 thread pool 與連線」。管理者的設定仍然有效，
/// 但被夾在一個安全上限內——真的需要更快，該做的是拆 worker，不是把這個數字調大。
/// </summary>
public class NetiqPipelineParallelismTests
{
    [Fact]
    public void 設定值在上限內時照設定執行()
    {
        Assert.Equal(2, NetiqPipelineService.ResolveParallelism(2));
    }

    [Fact]
    public void 設定值超過行程內上限時夾住()
    {
        Assert.Equal(NetiqOptions.MaxParallelServersLimit, NetiqPipelineService.ResolveParallelism(8));
    }

    /// <summary>設 1＝完全依序，是既有語意（docs/archive/FEEDBACK-3-PLAN.md #2），不該被上限邏輯改掉</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void 設定值小於一時一律視為依序(int configured, int expected)
    {
        Assert.Equal(expected, NetiqPipelineService.ResolveParallelism(configured));
    }
}

/// <summary>
/// 單一 Sentinel 內部的批次查詢平行度（回饋十三輪 D），與 <see cref="NetiqPipelineParallelismTests"/>
/// 同一個「行程架構上限，不是效能旋鈕」理由，只是維度不同——見 NetiqOptions.MaxParallelQueriesPerServer。
/// </summary>
public class NetiqPipelineQueryParallelismTests
{
    [Fact]
    public void 設定值在上限內時照設定執行()
    {
        Assert.Equal(2, NetiqPipelineService.ResolveQueryParallelism(2));
    }

    [Fact]
    public void 設定值超過行程內上限時夾住()
    {
        Assert.Equal(NetiqOptions.MaxParallelQueriesPerServerLimit, NetiqPipelineService.ResolveQueryParallelism(8));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void 設定值小於一時一律視為依序(int configured, int expected)
    {
        Assert.Equal(expected, NetiqPipelineService.ResolveQueryParallelism(configured));
    }
}
