using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Models;
using LogForesight.Web.Models.Dto;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="NetiqPipelineService.ResolveLookbackDays"/>——首次執行與缺漏日回補統一套用
/// BackfillDays，只夾在 <see cref="NetiqOptions.MaxBackfillDaysLimit"/>（回望窗口與趨勢基線
/// 窗口是兩件不同的事，不互相夾住）。
/// </summary>
public class NetiqPipelineServiceLookbackTests
{
    [Fact]
    public void 預設值1只回補一天()
    {
        Assert.Equal(1, NetiqPipelineService.ResolveLookbackDays(backfillDays: 1));
    }

    [Fact]
    public void BackfillDays設為3時回補三天內缺漏()
    {
        Assert.Equal(3, NetiqPipelineService.ResolveLookbackDays(backfillDays: 3));
    }

    /// <summary>回望天數不再被趨勢窗口（14）夾住——設 30 就回望 30 天</summary>
    [Fact]
    public void BackfillDays設為30時不被趨勢窗口夾住()
    {
        Assert.Equal(30, NetiqPipelineService.ResolveLookbackDays(backfillDays: 30));
    }

    [Fact]
    public void BackfillDays超過上限時夾在上限()
    {
        Assert.Equal(NetiqOptions.MaxBackfillDaysLimit, NetiqPipelineService.ResolveLookbackDays(backfillDays: 45));
    }

    [Fact]
    public void BackfillDays小於一時一律視為一天()
    {
        Assert.Equal(1, NetiqPipelineService.ResolveLookbackDays(backfillDays: 0));
    }

    /// <summary>DTO 驗證邊界：30 通過、31 被拒（與 pipeline 夾值共用同一個上限常數）</summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(31, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void BackfillDays的DTO驗證以上限常數為界(int value, bool expectValid)
    {
        var dto = new UpdateNetiqOptionsRequest { BackfillDays = value };
        var ctx = new ValidationContext(dto) { MemberName = nameof(UpdateNetiqOptionsRequest.BackfillDays) };
        var ok = Validator.TryValidateProperty(value, ctx, new List<ValidationResult>());
        Assert.Equal(expectValid, ok);
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
