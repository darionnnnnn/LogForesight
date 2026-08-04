using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/FEEDBACK-3-PLAN.md #2：<see cref="NetiqPipelineResult"/> 的計數與 Warnings
/// 在多台 Sentinel 平行處理下會被多個 Task 同時寫入——驗證內部方法（Interlocked／lock）
/// 併發呼叫不掉計數、不遺失 Warning。
/// </summary>
public class NetiqPipelineResultConcurrencyTests
{
    [Fact]
    public void 三個計數方法並發呼叫不掉數()
    {
        var result = new NetiqPipelineResult();
        const int iterations = 1000;

        Parallel.Invoke(
            () => Parallel.For(0, iterations, _ => result.AddSkipped()),
            () => Parallel.For(0, iterations, _ => result.AddAnalyzed()),
            () => Parallel.For(0, iterations, _ => result.AddFailed()));

        Assert.Equal(iterations, result.HostsSkippedUpToDate);
        Assert.Equal(iterations, result.HostDaysAnalyzed);
        Assert.Equal(iterations, result.HostsFailed);
    }

    [Fact]
    public void AddFailed可傳入批次數量()
    {
        var result = new NetiqPipelineResult();

        result.AddFailed(50);
        result.AddFailed(30);

        Assert.Equal(80, result.HostsFailed);
    }

    [Fact]
    public void AddWarning並發呼叫不遺失任何一筆()
    {
        var result = new NetiqPipelineResult();
        const int iterations = 500;

        Parallel.For(0, iterations, i => result.AddWarning($"warning-{i}"));

        Assert.Equal(iterations, result.Warnings.Count);
        Assert.Equal(iterations, result.Warnings.Distinct().Count());
    }

    /// <summary>docs/archive/FEEDBACK-8-PLAN.md #2：進度條分母（AddToTotal）與分子
    /// （HostDaysDone = HostDaysAnalyzed + HostsFailed）——各 Sentinel 平行掃描時累加不掉數。</summary>
    [Fact]
    public void AddToTotal並發呼叫不掉數且HostDaysDone等於成功加失敗()
    {
        var result = new NetiqPipelineResult();
        const int iterations = 300;

        Parallel.Invoke(
            () => Parallel.For(0, iterations, _ => result.AddToTotal(1)),
            () => Parallel.For(0, iterations, _ => result.AddAnalyzed()),
            () => Parallel.For(0, iterations, _ => result.AddFailed()));

        Assert.Equal(iterations, result.HostDaysTotal);
        Assert.Equal(iterations * 2, result.HostDaysDone);
    }
}
