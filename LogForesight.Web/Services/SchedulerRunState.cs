namespace LogForesight.Web.Services;

/// <summary>
/// 排程執行的行程內狀態（docs/archive/WEB-SCHEDULER-PLAN.md §1.4.4）：單例，供
/// <see cref="SchedulerHostedService"/> 與狀態／停止 API 共用同一份「現在是否在跑、誰觸發的、
/// 最新進度」。<see cref="TryBeginRun"/> 內建原子檢查——這就是「行程內單一執行 gate」，
/// 手動觸發撞上排程執行中時直接拒絕（回 false），不用另外設一把鎖。
/// </summary>
/// <summary>
/// 上一次執行的結局（docs/archive/FEEDBACK-7-PLAN.md）：立即執行／排程觸發失敗時，原本只寫 NLog，
/// 使用者在「已開始執行」的 toast 之後就再也看不到失敗訊息，只能翻 log 檔。狀態卡改顯示
/// 這筆紀錄，讓失敗在畫面上「看得見」。
/// </summary>
/// <param name="AnyRecordsWritten">
/// 這次執行是否有任何主機日成功寫入分析結果（回饋十八輪批次B）：本機出問題會讓整趟
/// <see cref="Success"/> 判 false（維持批次E既有的嚴格語意，見 AnalysisOrchestrator 的說明），
/// 但 NetIQ 那一路可能已經對數百上千台主機完成分析並寫入——用這個欄位讓通知閘門
/// （<see cref="SchedulerHostedService"/>）能區分「整趟失敗、什麼都沒做」與「這一路失敗、
/// 另一路已有真實產出」，後者不該讓已完成的通知一起靜音。預設 false，供既有呼叫端
/// （測試／例外路徑「orchestrator 環境層級炸掉、result 不可信」）零改動沿用舊行為。
/// </param>
public sealed record RunOutcome(bool Success, string? Message, string Trigger, DateTime EndedAt, bool AnyRecordsWritten = false)
{
    /// <summary>通知閘門（回饋十八輪批次B）：成功**或**有產出就通知——本機出問題讓整趟
    /// Success=false 時，NetIQ 那一路已寫入的高風險通知不該一起被靜音。抽成屬性讓
    /// SchedulerHostedService 的閘門與測試釘住同一份判定，不在呼叫端內嵌第二份。</summary>
    public bool ShouldNotify => Success || AnyRecordsWritten;

    /// <summary>從 orchestrator 結果推導「這次執行有沒有任何主機日真的寫入分析結果」
    /// （回饋十八輪批次B）：本機路徑有結果，或 NetIQ 管線有分析完成的主機日，任一成立即可。
    /// 抽成靜態函式供 SchedulerHostedService 與測試共用同一份推導。</summary>
    public static bool ComputeAnyRecordsWritten(OrchestratorResult result) =>
        result.LocalResults.Count > 0 || (result.NetiqResult?.HostDaysAnalyzed ?? 0) > 0;
}

public class SchedulerRunState
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public string? Trigger { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LatestMessage { get; private set; }

    /// <summary>執行進度（docs/archive/FEEDBACK-8-PLAN.md #2）：netiq；done/total 為主機日粒度。
    /// ProgressTotal=0 代表尚未進到有量化進度的階段（清理／掃描中），狀態卡改顯示不定進度。
    /// **回饋十七輪批次E 起這裡專屬 NetIQ**——本機的進度改回報到 <see cref="LocalProgressPhase"/>
    /// （見其說明：本機與 NetIQ 改並行執行後，兩者不能再共用一組欄位）。</summary>
    /// <summary>
    /// 一條進度軌（phase／分子／分母／是否完工）。三軌互不覆蓋，各自持有自己最後一次回報的值。
    /// 收成一個型別是為了**重設只寫一次**：原本 TryBeginRun 與 EndRun 各自逐欄重設 12 個欄位，
    /// 兩份清單得手動保持同步，漏一個就是「上一趟的進度殘留在畫面上」。
    /// </summary>
    private struct ProgressTrack
    {
        public ProgressTrack(string? phase, int done, int total)
        {
            Phase = phase;
            Done = done;
            Total = total;
            Completed = false;   // 有新進度就代表這一軌又在跑了
        }

        public string? Phase { get; }
        public int Done { get; }
        public int Total { get; }
        public bool Completed { get; set; }
    }

    /// <summary>三軌一起歸零（開始與結束共用同一份，不再兩處各寫一份重設清單）。</summary>
    private void ResetTracks()
    {
        _netiq = default;
        _local = default;
        _prtg = default;
    }

    private ProgressTrack _netiq;
    private ProgressTrack _local;
    private ProgressTrack _prtg;

    public string? ProgressPhase => _netiq.Phase;
    public int ProgressDone => _netiq.Done;
    public int ProgressTotal => _netiq.Total;

    /// <summary>
    /// 本機分析進度（回饋十七輪批次E）：本機與 NetIQ 機房分析改成並行執行（AnalysisOrchestrator
    /// 的 Task.WhenAll），不再是「先跑完本機才進 NetIQ」的依序關係——local／netiq 兩個 phase
    /// 現在會**同時**回報進度，若仍共用上面那組主進度欄位，交錯的 ReportProgress("local",...)／
    /// ReportProgress("netiq",...) 會互相蓋掉對方，畫面症狀跟當初 netiq／netiq-ai 共用一組欄位時
    /// 一模一樣（進度卡住不動／跳來跳去）。獨立成第三組欄位，語意上與上面的主／子進度對稱：
    /// 三條軌互不覆蓋，各自持有自己最後一次回報的值。
    /// </summary>
    public string? LocalProgressPhase => _local.Phase;
    public int LocalProgressDone => _local.Done;
    public int LocalProgressTotal => _local.Total;

    /// <summary>
    /// PRTG 擷取進度（docs/archive/FEEDBACK-37-PLAN.md 批次G1）：與 NetIQ／本機互不覆蓋的第三條進度軌，phase 包含
    /// prtg-sync（結構同步）、prtg-values（每日數值）、prtg-triggered（觸發式數值）。
    /// </summary>
    public string? PrtgProgressPhase => _prtg.Phase;
    public int PrtgProgressDone => _prtg.Done;
    public int PrtgProgressTotal => _prtg.Total;

    /// <summary>
    /// 當日 PRTG finding 是否已全部算完並追加（docs/PRTG-SPEC.md §9）。
    /// AI 分析排程據此判斷「當日待補現在可不可以判讀」——PRTG finding 會影響風險與敘述，
    /// 太早判讀等於讓 AI 看缺了 PRTG 訊號的半份資料。PRTG 停用或評估失敗時也會被設為 true
    /// （「算不出東西」不等於「還沒算完」），否則 AI 會一路等到整趟取數結束。
    /// </summary>
    public bool PrtgFindingsReady { get; private set; }

    public bool LocalCompleted => _local.Completed;
    public bool NetiqCompleted => _netiq.Completed;
    public bool PrtgCompleted => _prtg.Completed;

    /// <summary>
    /// 被進行中的手動執行佔用掉的排程窗口起始時刻（null＝沒有）。
    /// 手動觸發不受窗口 End 停止，一趟大回填可以吃掉整段窗口，而畫面上原本只會顯示
    /// 「排程設了卻沒跑」，看不出原因。
    /// </summary>
    public DateTime? SkippedScheduleAt { get; private set; }

    /// <summary>
    /// 記錄一個被佔用的窗口；同一個窗口實例只記一次（回 true 代表這次是新的，呼叫端才寫訊息）。
    /// </summary>
    public bool NoteSkippedSchedule(DateTime windowStart)
    {
        lock (_lock)
        {
            if (SkippedScheduleAt == windowStart) return false;
            SkippedScheduleAt = windowStart;
            return true;
        }
    }

    /// <summary>資源守門暫停原因（null 代表未暫停）。</summary>
    public string? PausedReason { get; private set; }

    /// <summary>最近一次執行完畢（成功/失敗/停止）的結果；站台重啟後歸零（行程內狀態，
    /// 持久紀錄請看執行總表——那裡有完整歷史，這裡只回答「剛剛那次到底成不成功」）。</summary>
    public RunOutcome? LastOutcome { get; private set; }

    /// <summary>已在執行中時回 false（呼叫端據此直接拒絕，不排隊）；成功開始時回 true 並給出可用於停止的 CancellationTokenSource</summary>
    public bool TryBeginRun(string trigger, out CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                cts = null!;
                return false;
            }

            IsRunning = true;
            Trigger = trigger;
            StartedAt = DateTime.Now;
            LatestMessage = null;
            ResetTracks();
            PrtgFindingsReady = false;
            SkippedScheduleAt = null;
            PausedReason = null;
            _cts = new CancellationTokenSource();
            cts = _cts;
            return true;
        }
    }

    /// <summary>IRunConsole 每次輸出時回報，供狀態 API 顯示「目前進度到哪」</summary>
    public void ReportMessage(string message)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            LatestMessage = message;
        }
    }

    /// <summary>
    /// 「單一告示」讀取端專用的進度快照（回饋十四輪 UI-6 體檢）：只有一行可顯示的讀取端
    /// （一般使用者的執行中告示 <c>/api/run-activity</c>、健康診斷的 AnalysisPhase）要在
    /// 主／子兩條軌之間挑一條——**子進度優先**，它代表較晚、較貼近「現在到底卡在哪」的階段
    /// （搜尋主線常常已跑完，只剩 AI 佇列在背景消化）。選擇邏輯放在這裡單點化，
    /// 不讓每個讀取端各自記得（主／子欄位拆開後，「漏改讀取端」這個坑已經踩過兩次）。
    /// 能同時畫兩條進度條的讀取端（排程作業頁的狀態 API）直接讀兩組欄位，不走這裡。
    /// </summary>
    /// <summary>
    /// 優先序：主進度（netiq） &gt; 本機 &gt; PRTG。原第一順位的子進度軌已隨 AI 拆離移除
    /// （AI 補寫的告示由 RunActivityController 讀 AiAnalysisRunState 另行提供）。
    /// 本機排在 NetIQ 之後——Full 範圍下 NetIQ 通常是規模較大、較慢的一路，單一告示優先顯示它更有
    /// 代表性；LocalOnly 範圍或 NetIQ 尚未開始回報時，自然落回本機進度，不會顯示空白。
    /// PRTG 排最後：它是附屬流程，不該蓋掉主要分析的活動訊息。
    /// </summary>
    public (string? Phase, int Done, int Total) LatestActivity()
    {
        lock (_lock)
        {
            if (ProgressPhase != null && !NetiqCompleted) return (ProgressPhase, ProgressDone, ProgressTotal);
            if (LocalProgressPhase != null && !LocalCompleted) return (LocalProgressPhase, LocalProgressDone, LocalProgressTotal);
            return PrtgProgressPhase != null && !PrtgCompleted
                ? (PrtgProgressPhase, PrtgProgressDone, PrtgProgressTotal)
                : (null, 0, 0);
        }
    }

    /// <summary>IRunProgress 的落地點（docs/archive/FEEDBACK-8-PLAN.md #2）：本機／NetIQ／PRTG 三階段各自的
    /// 進度，供狀態 API 畫進度條。phase=="local" 寫入本機專屬欄位；phase 以 "prtg-" 開頭寫入 PRTG 專屬欄位；
    /// 其餘（netiq）寫入主進度欄位——三組欄位互不覆蓋。</summary>
    public void ReportProgress(string phase, int done, int total)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            if (phase == LocalDonePhase)
            {
                _local.Completed = true;
            }
            else if (phase == RunPhases.Local)
            {
                _local = new ProgressTrack(phase, done, total);
            }
            else if (phase == NetiqDonePhase)
            {
                _netiq.Completed = true;
            }
            else if (phase == PrtgDonePhase)
            {
                // 完工訊號帶「取了幾台主機／幾個 sensor」，讓畫面說得出結果而不只是「已完成」。
                // total 為 0 代表這一路沒有可回報的量（PRTG 停用、初始化失敗、取消），
                // 這時不覆蓋軌上已累積的數字——既有規範是完工後保留最後的數字，不清空。
                if (total > 0)
                {
                    _prtg = new ProgressTrack(_prtg.Phase ?? phase, done, total);
                }
                _prtg.Completed = true;
            }
            else if (phase == RunPhases.GuardPaused)
            {
                PausedReason = "資源緊張，暫停中";
            }
            else if (phase == RunPhases.GuardResumed)
            {
                PausedReason = null;
            }
            else if (phase == RunPhases.PrtgFindingsReady)
            {
                // 訊號不是進度：**必須排在下面的 "prtg-" 前綴分支之前**，
                // 否則它會被當成 PRTG 進度軌的回報，把結構同步／取數的進度數字蓋成 0/0。
                PrtgFindingsReady = true;
            }
            else if (phase.StartsWith("prtg-", StringComparison.OrdinalIgnoreCase))
            {
                _prtg = new ProgressTrack(phase, done, total);
            }
            else
            {
                _netiq = new ProgressTrack(phase, done, total);
            }
        }
    }

    /// <summary>本機分析路徑收尾時的完工訊號——見 <see cref="ReportProgress"/> 對這個分支的說明。</summary>
    public const string LocalDonePhase = RunPhases.LocalDone;

    /// <summary>NetIQ 路徑收尾時的完工訊號（<see cref="AnalysisOrchestrator.RunNetiqAnalysisAsync"/>
    /// 的 finally，成功／失敗都會送）——見 <see cref="ReportProgress"/> 對這個分支的說明。</summary>
    public const string NetiqDonePhase = RunPhases.NetiqDone;

    /// <summary>PRTG 路徑收尾時的完工訊號（<see cref="AnalysisOrchestrator.RunPrtgFetchAsync"/>
    /// 的 finally，成功／失敗都會送）——見 <see cref="ReportProgress"/> 對這個分支的說明。</summary>
    public const string PrtgDonePhase = RunPhases.PrtgDone;

    /// <param name="outcome">這次執行的結局；null＝沒有真的開始過（例如跨行程 Mutex 逾時），
    /// 維持上一筆 LastOutcome 不變，不用「沒開始」蓋掉「上次真的跑過的結果」。</param>
    public void EndRun(RunOutcome? outcome = null)
    {
        lock (_lock)
        {
            IsRunning = false;
            Trigger = null;
            StartedAt = null;
            ResetTracks();
            PrtgFindingsReady = false;
            SkippedScheduleAt = null;
            PausedReason = null;
            _cts?.Dispose();
            _cts = null;
            if (outcome != null) LastOutcome = outcome;
        }
    }

    /// <summary>要求停止進行中的執行（優雅停止，停在主機日邊界）；沒有執行中時回 false</summary>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (!IsRunning || _cts == null) return false;
            _cts.Cancel();
            return true;
        }
    }
}
