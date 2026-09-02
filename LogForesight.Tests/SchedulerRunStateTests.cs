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

    /// <summary>AnyRecordsWritten（回饋十八輪批次B）：預設 false，既有呼叫端（不帶這個參數的
    /// 建構）零改動維持舊行為——通知閘門只在明確算出「有寫入」時才放行。</summary>
    [Fact]
    public void RunOutcome_未指定AnyRecordsWritten時預設false()
    {
        var outcome = new RunOutcome(true, null, "manual:tester", DateTime.Now);

        Assert.False(outcome.AnyRecordsWritten);
    }

    /// <summary>整趟失敗（本機環境性問題）但 NetIQ 那一路已有真實產出——閘門要能區分
    /// 「整趟真的什麼都沒做」與「一路失敗、另一路已完成」，見 SchedulerHostedService 的說明。</summary>
    [Fact]
    public void RunOutcome_失敗但有寫入時AnyRecordsWritten為true()
    {
        var outcome = new RunOutcome(false, "本機分析失敗", "schedule", DateTime.Now, AnyRecordsWritten: true);

        Assert.False(outcome.Success);
        Assert.True(outcome.AnyRecordsWritten);
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

    // ── 主／子進度分離（回饋十四輪 UI-6）─────────────────────────────────────

    // 兩個 phase 現在會同時回報進度，不能再共用一組主進度欄位——否則會重演 netiq／netiq-ai
    // 當初共用一組欄位時「進度卡住不動」的症狀。

    /// <summary>核心場景：local 與 netiq 交錯回報時互不覆蓋，各自持有自己最後一次回報的值
    /// （取代舊版「local／netiq 依序不重疊、共用一組欄位」的假設——並行後這個假設不再成立）。</summary>
    [Fact]
    public void local與netiq並行時各自獨立進度欄位_互不覆蓋()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 5);
        state.ReportProgress("netiq", 1, 20);
        // 兩軌交錯繼續推進，不該互相蓋掉對方
        state.ReportProgress("local", 3, 5);
        state.ReportProgress("netiq", 4, 20);

        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(3, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(4, state.ProgressDone);
        Assert.Equal(20, state.ProgressTotal);
    }

    /// <summary>
    /// 體檢輪抓到的真實缺口：NetIQ 主機少、本機在回補多天缺漏時，NetIQ 通常會比本機早跑完。
    /// 修復前 ProgressPhase 只在整趟執行的 TryBeginRun/EndRun 才會被清空，NetIQ 內部跑完後
    /// 不會主動清掉自己的欄位——LatestActivity() 因此會一路顯示 netiq 跑完當下的最後一次
    /// 回報值（凍結不動），即使本機明明還在推進，外觀上與「卡住」無法區分。
    /// <see cref="AnalysisOrchestrator.RunNetiqAnalysisAsync"/> 收尾時會送一個特殊 phase
    /// （"netiq-done"）通知 NetIQ 這一路真的結束了，這裡直接驗證 SchedulerRunState 這一側收到
    /// 後的行為：清空主／子進度欄位，讓 LatestActivity() 的優先序自然落回還在推進的本機。
    /// </summary>
    [Fact]
    public void NetIQ完工後清空主進度欄位_LatestActivity落回仍在推進的本機()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 7);
        state.ReportProgress("netiq", 2, 2);
        Assert.Equal(("netiq", 2, 2), state.LatestActivity()); // NetIQ 還在跑（或剛跑完但還沒送完工訊號）時優先顯示它

        state.ReportProgress("netiq-done", 0, 0);

        Assert.Null(state.ProgressPhase);
        Assert.Equal(0, state.ProgressTotal);
        // 本機的欄位完全不受影響——它還在跑，不該被 NetIQ 收尾的動作波及
        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(7, state.LocalProgressTotal);
        // 單一告示讀取端現在會落回本機，不再顯示 netiq 凍結的 2/2 舊值
        Assert.Equal(("local", 2, 7), state.LatestActivity());
    }

    [Fact]
    public void EndRun後本機進度欄位也歸零()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress("local", 3, 5);
        Assert.Equal(5, state.LocalProgressTotal);

        state.EndRun();

        Assert.Null(state.LocalProgressPhase);
        Assert.Equal(0, state.LocalProgressDone);
        Assert.Equal(0, state.LocalProgressTotal);
    }

    /// <summary>LatestActivity 的第三順位：LocalOnly 範圍（netiq 從未回報）或 NetIQ 尚未開始
    /// 回報時，單一告示要能落回本機進度，不能顯示空白——這是 LocalOnly 手動觸發最常見的情境。</summary>
    [Fact]
    public void LatestActivity_netiq未回報時落回本機進度()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("manual:tester", out _));

        state.ReportProgress("local", 2, 5);

        Assert.Equal(("local", 2, 5), state.LatestActivity());
    }

    /// <summary>本機與 NetIQ 同時在跑時，單一告示優先顯示 NetIQ（Full 範圍下通常規模較大、
    /// 較具代表性）——本機進度仍照常累積在自己的欄位，只是不搶單一告示的顯示權。</summary>
    [Fact]
    public void LatestActivity_本機與NetIQ同時在跑時優先顯示NetIQ()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 1, 5);
        state.ReportProgress("netiq", 10, 100);

        Assert.Equal(("netiq", 10, 100), state.LatestActivity());
    }

    /// <summary>
    /// 單一告示的選擇邏輯（LatestActivity）由 SchedulerRunState 本身提供，兩個讀取端
    /// （/api/run-activity、健康診斷 AnalysisPhase）共用——「漏改讀取端」這個坑已在兩處
    /// 各踩一次，選擇邏輯不再散落各處。子進度軌已隨 AI 拆離移除（體檢輪）：未知 phase
    /// 一律落主進度欄位，優先序剩 netiq > 本機。
    /// </summary>
    [Fact]
    public void LatestActivity_只剩主與本機兩軌_未知phase落主進度()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq", 40, 100);
        Assert.Equal(("netiq", 40, 100), state.LatestActivity());

        // 舊子軌 phase（netiq-ai）已無特殊路由：當成一般主進度值寫入
        state.ReportProgress("netiq-ai", 12, 80);
        Assert.Equal(("netiq-ai", 12, 80), state.ProgressPhase != null ? (state.ProgressPhase, state.ProgressDone, state.ProgressTotal) : (null, 0, 0));
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

    // ── 通知閘門「成功或有產出就通知」（回饋十八輪批次B，終檢輪補測試）──────────
    // 核心語意：本機拋例外讓整趟 Success=false，但 NetIQ 已對大量主機寫入時，通知不能被靜音。

    /// <summary>本機失敗（LocalResults 空）＋NetIQ 有寫入 → 判定有產出、閘門放行。</summary>
    [Fact]
    public void ComputeAnyRecordsWritten_本機失敗但NetIQ有寫入時為true()
    {
        var netiq = new NetiqPipelineResult();
        netiq.AddAnalyzed();   // NetIQ 這一路完成了至少一個主機日

        var result = new OrchestratorResult { Success = false, FailureMessage = "本機分析失敗", NetiqResult = netiq };

        Assert.True(RunOutcome.ComputeAnyRecordsWritten(result));
        Assert.True(new RunOutcome(false, "本機分析失敗", "schedule", DateTime.Now,
            RunOutcome.ComputeAnyRecordsWritten(result)).ShouldNotify);
    }

    /// <summary>整趟真的什麼都沒做（兩路皆空）→ 不通知，維持批次E的嚴格語意。</summary>
    [Fact]
    public void ComputeAnyRecordsWritten_兩路皆無產出時為false且不通知()
    {
        var result = new OrchestratorResult { Success = false, FailureMessage = "環境層級失敗" };

        Assert.False(RunOutcome.ComputeAnyRecordsWritten(result));
        Assert.False(new RunOutcome(false, "環境層級失敗", "schedule", DateTime.Now,
            RunOutcome.ComputeAnyRecordsWritten(result)).ShouldNotify);
    }

    /// <summary>本機有結果（NetIQ 未跑）也算有產出——閘門看的是「任一路有寫入」。</summary>
    [Fact]
    public void ComputeAnyRecordsWritten_本機有結果時為true()
    {
        var result = new OrchestratorResult { Success = false, FailureMessage = "NetIQ 失敗" };
        result.LocalResults.Add(new LocalDaySummary(DateTime.Today.AddDays(-1), RiskLevels.Low, HasReport: false));

        Assert.True(RunOutcome.ComputeAnyRecordsWritten(result));
    }

    /// <summary>成功一律通知，不看 AnyRecordsWritten（例如全部主機都已是最新、零新寫入的成功執行）。</summary>
    [Fact]
    public void ShouldNotify_成功時不論有無寫入皆通知()
    {
        Assert.True(new RunOutcome(true, null, "schedule", DateTime.Now, AnyRecordsWritten: false).ShouldNotify);
    }

    // ── PRTG 進度軌（任務 G1）───────────────────────────────────────────────

    /// <summary>
    /// 反例測試（最重要）：先收到 netiq 進度回報，再收到 prtg-values 回報 →
    /// 斷言 NetIQ 那組欄位完全不變，且 PRTG 那組是新值。
    /// （防止 prtg-* phase 落入 catch-all else 分支蓋掉 NetIQ 主組）。
    /// </summary>
    [Fact]
    public void ReportProgress_收到prtg回報不覆蓋NetIQ欄位()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("netiq", 10, 50);
        state.ReportProgress("prtg-values", 2, 6);

        // 斷言 NetIQ 那組欄位完全不變
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(10, state.ProgressDone);
        Assert.Equal(50, state.ProgressTotal);

        // 斷言 PRTG 那組是新值
        Assert.Equal("prtg-values", state.PrtgProgressPhase);
        Assert.Equal(2, state.PrtgProgressDone);
        Assert.Equal(6, state.PrtgProgressTotal);
    }

    /// <summary>prtg-done 清空 PRTG 那組，且不影響 NetIQ 與本機兩組。</summary>
    [Fact]
    public void ReportProgress_prtgDone清空PRTG組且不影響NetIQ與本機組()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 1, 3);
        state.ReportProgress("netiq", 10, 50);
        state.ReportProgress("prtg-values", 5, 5);

        state.ReportProgress("prtg-done", 0, 0);

        // PRTG 那組清空
        Assert.Null(state.PrtgProgressPhase);
        Assert.Equal(0, state.PrtgProgressDone);
        Assert.Equal(0, state.PrtgProgressTotal);

        // 不影響 NetIQ 與本機兩組
        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(1, state.LocalProgressDone);
        Assert.Equal(3, state.LocalProgressTotal);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(10, state.ProgressDone);
        Assert.Equal(50, state.ProgressTotal);
    }

    /// <summary>LatestActivity 優先序：NetIQ ＞ 本機 ＞ PRTG</summary>
    [Fact]
    public void LatestActivity_優先序為NetIQ大於本機大於PRTG()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("prtg-values", 1, 6);
        Assert.Equal(("prtg-values", 1, 6), state.LatestActivity());

        state.ReportProgress("local", 2, 5);
        Assert.Equal(("local", 2, 5), state.LatestActivity());

        state.ReportProgress("netiq", 10, 100);
        Assert.Equal(("netiq", 10, 100), state.LatestActivity());
    }

    /// <summary>EndRun 清空 PRTG 進度欄位</summary>
    [Fact]
    public void EndRun後PRTG進度欄位也歸零()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress("prtg-values", 3, 5);
        Assert.Equal(5, state.PrtgProgressTotal);

        state.EndRun();

        Assert.Null(state.PrtgProgressPhase);
        Assert.Equal(0, state.PrtgProgressDone);
        Assert.Equal(0, state.PrtgProgressTotal);
    }
}
