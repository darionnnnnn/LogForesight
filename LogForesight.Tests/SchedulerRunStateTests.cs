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
    /// 後的行為：保留最後進度並標記 NetiqCompleted 為 true，讓 LatestActivity() 跳過已完成軌，
    /// 優先序自然落回還在推進的本機。
    /// </summary>
    [Fact]
    public void NetIQ完工後保留進度並標記完成_LatestActivity落回仍在推進的本機()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 7);
        state.ReportProgress("netiq", 2, 2);
        Assert.Equal(("netiq", 2, 2), state.LatestActivity()); // NetIQ 還在跑（或剛跑完但還沒送完工訊號）時優先顯示它

        state.ReportProgress("netiq-done", 0, 0);

        // 新契約：保留最後一次回報值，只把 NetiqCompleted 設為 true
        Assert.True(state.NetiqCompleted);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(2, state.ProgressDone);
        Assert.Equal(2, state.ProgressTotal);
        // 本機的欄位完全不受影響——它還在跑，不該被 NetIQ 收尾的動作波及
        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(7, state.LocalProgressTotal);
        // 單一告示讀取端現在會落回本機，不再顯示已完工軌的舊值
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

    // ── PRTG 進度軌（docs/archive/FEEDBACK-37-PLAN.md 批次G1）───────────────────────────────────────────────

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

    /// <summary>prtg-done 保留 PRTG 數字並標記完成，且不影響 NetIQ 與本機兩組。</summary>
    [Fact]
    public void ReportProgress_prtgDone保留PRTG數字並標記完成_且不影響NetIQ與本機組()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 1, 3);
        state.ReportProgress("netiq", 10, 50);
        state.ReportProgress("prtg-values", 5, 5);

        state.ReportProgress("prtg-done", 0, 0);

        // PRTG 保留數字並標記完成
        Assert.True(state.PrtgCompleted);
        Assert.Equal("prtg-values", state.PrtgProgressPhase);
        Assert.Equal(5, state.PrtgProgressDone);
        Assert.Equal(5, state.PrtgProgressTotal);

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

    // ── 完工訊號與完成標記（批次C 階段1）───────────────────────────────────────────────

    /// <summary>1. local-done 保留數字並標記完成：先回報 ("local", 3, 5)，再送 ("local-done", 0, 0) →
    /// LocalProgressPhase 仍為 "local"、LocalProgressDone 為 3、LocalProgressTotal 為 5，LocalCompleted 為 true。</summary>
    [Fact]
    public void ReportProgress_localDone保留數字並標記完成()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 3, 5);
        state.ReportProgress(SchedulerRunState.LocalDonePhase, 0, 0);

        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(3, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.True(state.LocalCompleted);
    }

    /// <summary>2. 三軌全部完成後 LatestActivity() 回 null：三軌各回報一次進度再各送一次 done →
    /// LatestActivity().Phase 為 null。</summary>
    [Fact]
    public void LatestActivity_三軌全部完成後回null()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 1, 5);
        state.ReportProgress("netiq", 2, 10);
        state.ReportProgress("prtg-values", 3, 5);

        state.ReportProgress(SchedulerRunState.LocalDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.NetiqDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.PrtgDonePhase, 0, 0);

        Assert.Null(state.LatestActivity().Phase);
        Assert.Equal((null, 0, 0), state.LatestActivity());
    }

    /// <summary>3. 完成後又收到新進度時旗標復位：送完 local-done 之後再回報 ("local", 4, 5) →
    /// LocalCompleted 為 false 且數字更新為 4。</summary>
    [Fact]
    public void ReportProgress_完成後收到新進度時旗標復位()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 3, 5);
        state.ReportProgress(SchedulerRunState.LocalDonePhase, 0, 0);
        Assert.True(state.LocalCompleted);

        state.ReportProgress("local", 4, 5);
        Assert.False(state.LocalCompleted);
        Assert.Equal(4, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
    }

    /// <summary>4. EndRun 清空三個完成旗標：三軌都完成後呼叫 EndRun →
    /// 三個 XxxCompleted 皆為 false、三軌 phase 皆為 null。</summary>
    [Fact]
    public void EndRun_三軌完成後清空三個完成旗標與phase()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 1, 2);
        state.ReportProgress("netiq", 3, 4);
        state.ReportProgress("prtg-values", 5, 6);

        state.ReportProgress(SchedulerRunState.LocalDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.NetiqDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.PrtgDonePhase, 0, 0);

        Assert.True(state.LocalCompleted);
        Assert.True(state.NetiqCompleted);
        Assert.True(state.PrtgCompleted);

        state.EndRun();

        Assert.False(state.LocalCompleted);
        Assert.False(state.NetiqCompleted);
        Assert.False(state.PrtgCompleted);
        Assert.Null(state.LocalProgressPhase);
        Assert.Null(state.ProgressPhase);
        Assert.Null(state.PrtgProgressPhase);
    }

    /// <summary>5. 完工訊號不互相污染：只送 netiq-done 時，LocalCompleted 與 PrtgCompleted 必須仍為 false，
    /// 且 local／prtg 兩軌的數字不受影響（釘住既有的「三軌互不覆蓋」契約）。</summary>
    [Fact]
    public void ReportProgress_完工訊號不互相污染()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 5);
        state.ReportProgress("netiq", 10, 20);
        state.ReportProgress("prtg-sync", 1, 3);

        // 只送 netiq-done
        state.ReportProgress(SchedulerRunState.NetiqDonePhase, 0, 0);

        // netiq 完成
        Assert.True(state.NetiqCompleted);

        // local 與 prtg 仍未完成
        Assert.False(state.LocalCompleted);
        Assert.False(state.PrtgCompleted);

        // local 與 prtg 兩軌數字不受影響
        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.Equal("prtg-sync", state.PrtgProgressPhase);
        Assert.Equal(1, state.PrtgProgressDone);
        Assert.Equal(3, state.PrtgProgressTotal);
    }

    /// <summary>
    /// 驗證 guard-paused 設 PausedReason、guard-resumed 清空，
    /// 且兩者都不影響三軌的 phase／done／total（釘住「不落入 catch-all」）。
    /// </summary>
    [Fact]
    public void ReportProgress_GuardPaused與Resumed設定與清空PausedReason且不影響三軌進度()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 5);
        state.ReportProgress("netiq", 10, 20);
        state.ReportProgress("prtg-sync", 1, 3);

        // 1. 發送 guard-paused：設定 PausedReason，不影響三軌任何數值與 phase
        state.ReportProgress("guard-paused", 0, 0);

        Assert.NotNull(state.PausedReason);
        Assert.Equal("資源緊張，暫停中", state.PausedReason);

        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(10, state.ProgressDone);
        Assert.Equal(20, state.ProgressTotal);
        Assert.Equal("prtg-sync", state.PrtgProgressPhase);
        Assert.Equal(1, state.PrtgProgressDone);
        Assert.Equal(3, state.PrtgProgressTotal);

        // 2. 發送 guard-resumed：清空 PausedReason，三軌數值仍保持原樣
        state.ReportProgress("guard-resumed", 0, 0);

        Assert.Null(state.PausedReason);

        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(10, state.ProgressDone);
        Assert.Equal(20, state.ProgressTotal);
        Assert.Equal("prtg-sync", state.PrtgProgressPhase);
        Assert.Equal(1, state.PrtgProgressDone);
        Assert.Equal(3, state.PrtgProgressTotal);

        // 3. EndRun 與 TryBeginRun 都清為 null
        state.ReportProgress("guard-paused", 0, 0);
        Assert.NotNull(state.PausedReason);
        state.EndRun();
        Assert.Null(state.PausedReason);

        Assert.True(state.TryBeginRun("manual", out _));
        Assert.Null(state.PausedReason);
    }
    /// <summary>
    /// 批次B：prtg-findings-ready 是訊號不是進度——它也以 "prtg-" 開頭，
    /// 分支若排在前綴分支之後，PRTG 進度軌的數字會被這則訊號蓋成 0/0。
    /// </summary>
    [Fact]
    public void ReportProgress_PrtgFindingsReady設旗標且不影響三軌進度()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress("local", 2, 5);
        state.ReportProgress("netiq", 7, 9);
        state.ReportProgress("prtg-sync-devices", 40, 120);

        Assert.False(state.PrtgFindingsReady);

        state.ReportProgress(AnalysisOrchestrator.PrtgFindingsReadyPhase, 0, 0);

        Assert.True(state.PrtgFindingsReady);

        // 三軌數字一格都不能動
        Assert.Equal("local", state.LocalProgressPhase);
        Assert.Equal(2, state.LocalProgressDone);
        Assert.Equal(5, state.LocalProgressTotal);
        Assert.Equal("netiq", state.ProgressPhase);
        Assert.Equal(7, state.ProgressDone);
        Assert.Equal(9, state.ProgressTotal);
        Assert.Equal("prtg-sync-devices", state.PrtgProgressPhase);
        Assert.Equal(40, state.PrtgProgressDone);
        Assert.Equal(120, state.PrtgProgressTotal);
    }

    /// <summary>批次B：就緒旗標不得跨執行殘留——下一趟開始時 AI 會據此判斷可否處理當日待補。</summary>
    [Fact]
    public void PrtgFindingsReady_開始與結束執行時都重設()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress(AnalysisOrchestrator.PrtgFindingsReadyPhase, 0, 0);
        Assert.True(state.PrtgFindingsReady);

        state.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));
        Assert.False(state.PrtgFindingsReady);

        Assert.True(state.TryBeginRun("schedule", out _));
        Assert.False(state.PrtgFindingsReady);
    }
    /// <summary>
    /// 批次G3：三軌重設收成單一 ResetTracks。原本 TryBeginRun 與 EndRun 各自逐欄重設 12 個欄位，
    /// 兩份清單得手動保持同步，漏一個就是「上一趟的進度殘留在畫面上」。
    /// </summary>
    [Fact]
    public void 三軌進度在開始與結束時都完整歸零()
    {
        var state = new SchedulerRunState();

        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress("local", 3, 9);
        state.ReportProgress("netiq", 5, 7);
        state.ReportProgress("prtg-sync-devices", 40, 120);
        state.ReportProgress(SchedulerRunState.LocalDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.NetiqDonePhase, 0, 0);
        state.ReportProgress(SchedulerRunState.PrtgDonePhase, 0, 0);

        state.EndRun(new RunOutcome(true, null, "schedule", DateTime.Now));

        AssertTracksCleared(state);

        // 下一趟開始時同樣是乾淨的
        Assert.True(state.TryBeginRun("manual:tester", out _));
        AssertTracksCleared(state);
    }

    private static void AssertTracksCleared(SchedulerRunState state)
    {
        Assert.Null(state.ProgressPhase);
        Assert.Equal(0, state.ProgressDone);
        Assert.Equal(0, state.ProgressTotal);
        Assert.False(state.NetiqCompleted);

        Assert.Null(state.LocalProgressPhase);
        Assert.Equal(0, state.LocalProgressDone);
        Assert.Equal(0, state.LocalProgressTotal);
        Assert.False(state.LocalCompleted);

        Assert.Null(state.PrtgProgressPhase);
        Assert.Equal(0, state.PrtgProgressDone);
        Assert.Equal(0, state.PrtgProgressTotal);
        Assert.False(state.PrtgCompleted);

        Assert.False(state.PrtgFindingsReady);
        Assert.Null(state.SkippedScheduleAt);
    }

    /// <summary>
    /// 批次G3：一軌收到新進度時，該軌的完工旗標要跟著清掉——
    /// 否則「跑完又開始跑」的軌會一直畫成滿格。
    /// </summary>
    [Fact]
    public void 完工後再收到進度會清掉該軌的完工旗標()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));

        state.ReportProgress(SchedulerRunState.PrtgDonePhase, 0, 0);
        Assert.True(state.PrtgCompleted);

        state.ReportProgress("prtg-triggered", 1, 10);
        Assert.False(state.PrtgCompleted);
        Assert.Equal("prtg-triggered", state.PrtgProgressPhase);
    }

    /// <summary>
    /// 批次G4：手動執行佔住 gate 時，該窗口的自動觸發會靜默消失。
    /// 同一個窗口只記一次，避免每 60 秒輪詢就重複寫一則訊息。
    /// </summary>
    [Fact]
    public void 被佔用的排程窗口同一個實例只記一次()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("manual:tester", out _));

        var windowStart = DateTime.Today.AddHours(22);

        Assert.True(state.NoteSkippedSchedule(windowStart));
        Assert.Equal(windowStart, state.SkippedScheduleAt);

        // 同一個窗口再記一次不算新的
        Assert.False(state.NoteSkippedSchedule(windowStart));

        // 換一個窗口才算新的
        var nextWindow = windowStart.AddDays(1);
        Assert.True(state.NoteSkippedSchedule(nextWindow));
        Assert.Equal(nextWindow, state.SkippedScheduleAt);
    }
}
