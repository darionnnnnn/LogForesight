using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SchedulerRunState"/> 的 <see cref="RunOutcome"/> 記錄（docs/archive/FEEDBACK-7-PLAN.md）：
/// 立即執行／排程觸發失敗時，狀態卡要能顯示「上次執行到底成不成功」，不能只靠 log 檔。
/// </summary>
public class SchedulerRunStateTests
{
    [Fact]
    public void EndRun帶Outcome_記錄LastOutcome()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("manual:tester", out _));

        var outcome = new RunOutcome(true, null, "manual:tester", DateTime.Now);
        state.EndRun(outcome);

        Assert.False(state.IsRunning);
        Assert.Same(outcome, state.LastOutcome);
    }

    [Fact]
    public void EndRun帶失敗Outcome_記錄失敗訊息()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        var outcome = new RunOutcome(false, "Operation is not valid due to the current state of the object.", "schedule", DateTime.Now);
        state.EndRun(outcome);

        Assert.NotNull(state.LastOutcome);
        Assert.False(state.LastOutcome!.Success);
        Assert.Equal("Operation is not valid due to the current state of the object.", state.LastOutcome.Message);
    }

    [Fact]
    public void EndRun傳null_保留前一筆LastOutcome()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("manual:a", out _));
        var first = new RunOutcome(true, null, "manual:a", DateTime.Now);
        state.EndRun(first);

        // 第二次觸發因跨行程 Mutex 逾時而「沒有真的開始」——EndRun(null) 不該蓋掉上一筆真正跑過的結果
        Assert.True(state.TryBeginRun("manual:b", out _));
        state.EndRun(null);

        Assert.Same(first, state.LastOutcome);
    }

    [Fact]
    public void 尚未執行過任何一次時LastOutcome為null()
    {
        var state = new SchedulerRunState();

        Assert.Null(state.LastOutcome);
    }

    /// <summary>docs/archive/FEEDBACK-8-PLAN.md #2：EndRun 要把進度歸零，不留上一趟執行的殘留數字——
    /// 目前前端只在 isRunning 時畫進度條所以不會顯示出來，但欄位本身該是乾淨的，
    /// 不能依賴呼叫端的顯示邏輯剛好把髒資料蓋住。</summary>
    [Fact]
    public void EndRun後進度欄位歸零()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress("netiq", 5, 10);
        Assert.Equal(10, state.ProgressTotal);

        state.EndRun();

        Assert.Null(state.ProgressPhase);
        Assert.Equal(0, state.ProgressDone);
        Assert.Equal(0, state.ProgressTotal);
    }

    [Fact]
    public void EndRun不帶參數_預設等同null不覆蓋前一筆()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("manual:a", out _));
        var first = new RunOutcome(true, null, "manual:a", DateTime.Now);
        state.EndRun(first);

        Assert.True(state.TryBeginRun("manual:b", out _));
        state.EndRun(); // 呼叫端未提供 outcome 的既有呼叫型態仍要能編譯、且行為等同傳 null

        Assert.Same(first, state.LastOutcome);
    }
}
