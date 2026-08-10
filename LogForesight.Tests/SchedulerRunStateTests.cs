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
        state.ReportProgress("netiq-ai", 2, 8);
        Assert.Equal(10, state.ProgressTotal);
        Assert.Equal(8, state.SubProgressTotal);

        state.EndRun();

        Assert.Null(state.ProgressPhase);
        Assert.Equal(0, state.ProgressDone);
        Assert.Equal(0, state.ProgressTotal);
        Assert.Null(state.SubProgressPhase);
        Assert.Equal(0, state.SubProgressDone);
        Assert.Equal(0, state.SubProgressTotal);
    }

    // ── 主／子進度分離（回饋十四輪 UI-6）─────────────────────────────────────

    /// <summary>
    /// 核心場景：netiq（主進度，搜尋仍在往下一台主機推進）與 netiq-ai（子進度，AI 佇列在背景
    /// 消化）在同一次執行內同時在跑。修復前兩者共用一組欄位，後回報的會直接覆蓋先回報的，
    /// 使用者看到的症狀是「進度卡住不動」。
    /// </summary>
    [Fact]
    public void 主進度與子進度分開存放_互不覆蓋()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq", 3, 10);
        state.ReportProgress("netiq-ai", 1, 4);
        // 主進度繼續往前推進，不該影響已經回報過的子進度
        state.ReportProgress("netiq", 4, 10);

        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(4, state.ProgressDone);
        Assert.Equal(10, state.ProgressTotal);
        Assert.Equal("netiq-ai", state.SubProgressPhase);
        Assert.Equal(1, state.SubProgressDone);
        Assert.Equal(4, state.SubProgressTotal);
    }

    /// <summary>netiq-backpressure 與 netiq-ai 是同一條「AI 背景消化」軌的兩種狀態（暫停中／消化中），
    /// 共用同一組子進度欄位，切換時互相覆蓋是正確行為（不是兩條各自獨立的子軌）。</summary>
    [Fact]
    public void netiq背壓與netiqAi共用同一組子進度欄位()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq-ai", 2, 5);
        state.ReportProgress("netiq-backpressure", 2, 5);

        Assert.Equal("netiq-backpressure", state.SubProgressPhase);
        Assert.Null(state.ProgressPhase); // 主進度欄位完全不受子進度回報影響
    }

    /// <summary>local／netiq 兩個主階段依序不重疊（同一次執行先跑完本機才進 NetIQ），
    /// 沿用舊語意共用同一組主進度欄位——後回報的直接取代前一個是預期行為。</summary>
    [Fact]
    public void local與netiq共用同一組主進度欄位()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 5, 5);
        state.ReportProgress("netiq", 1, 20);

        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(1, state.ProgressDone);
        Assert.Equal(20, state.ProgressTotal);
        Assert.Null(state.SubProgressPhase);
    }

    /// <summary>
    /// 一般使用者的「執行中告示」（<c>/api/run-activity</c>，RunActivityController）只有單一條
    /// 進度可顯示，主／子拆開後必須自己決定顯示哪一條——選子進度（較晚、較貼近「現在卡在哪」
    /// 的階段）。這條測試釘住 SchedulerRunState 這一側的前提：子進度有值時 SubProgressPhase
    /// 非 null 且主進度仍各自保有自己的值，控制器才有辦法做這個優先序判斷。
    /// 體檢時抓到的實際缺口：拆欄位後該控制器仍只讀主進度，AI 消化階段畫面會停在搜尋階段的數字。
    /// </summary>
    [Fact]
    public void 子進度有值時主子兩組欄位同時可讀_供單一告示決定優先序()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq", 100, 100);   // 搜尋主線已跑完
        state.ReportProgress("netiq-ai", 12, 80);  // AI 佇列還在背景消化

        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal("netiq-ai", state.SubProgressPhase);
        Assert.Equal(12, state.SubProgressDone);
        Assert.Equal(80, state.SubProgressTotal);
    }

    /// <summary>
    /// 二次體檢後的單點化（LatestActivity）：單一告示的「子進度優先」取捨改由 SchedulerRunState
    /// 本身提供，兩個讀取端（/api/run-activity、健康診斷 AnalysisPhase）共用——「漏改讀取端」
    /// 這個坑已在兩處各踩一次（RunActivityController、HealthService），選擇邏輯不再散落各處。
    /// </summary>
    [Fact]
    public void LatestActivity_子進度有值時優先回子進度_否則回主進度()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq", 40, 100);
        Assert.Equal(("netiq", 40, 100), state.LatestActivity()); // 尚無子進度：回主進度

        state.ReportProgress("netiq-ai", 12, 80);
        Assert.Equal(("netiq-ai", 12, 80), state.LatestActivity()); // 子進度出現後優先回它

        state.ReportProgress("netiq", 60, 100);
        Assert.Equal(("netiq-ai", 12, 80), state.LatestActivity()); // 主進度再推進也不搶回單一告示
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
