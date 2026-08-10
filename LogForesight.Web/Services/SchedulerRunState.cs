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
public sealed record RunOutcome(bool Success, string? Message, string Trigger, DateTime EndedAt);

public class SchedulerRunState
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public string? Trigger { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LatestMessage { get; private set; }

    /// <summary>執行進度（docs/archive/FEEDBACK-8-PLAN.md #2）：local｜netiq；done/total 為主機日粒度。
    /// ProgressTotal=0 代表尚未進到有量化進度的階段（清理／掃描中），狀態卡改顯示不定進度。</summary>
    public string? ProgressPhase { get; private set; }
    public int ProgressDone { get; private set; }
    public int ProgressTotal { get; private set; }

    /// <summary>
    /// 子進度（回饋十四輪 UI-6）：netiq-ai／netiq-backpressure 是 AI 佇列在背景消化的同一條軌，
    /// 與上面的主進度（netiq，搜尋＋統計仍在往下一台主機推進）在同一次執行內是**同時**在跑的
    /// 兩件事——原本共用一組 ProgressPhase/Done/Total 欄位，後回報的會直接覆蓋先回報的，
    /// 使用者看到的症狀是「進度卡住不動」，其實是另一條軌把畫面接管走了。分成獨立欄位後
    /// 兩條軌各自持有最後回報值，狀態 API 兩者都給，前端就能同時畫出主／子兩條進度條。
    /// local／netiq 兩個主階段彼此不重疊（同一次執行依序跑完本機才進 NetIQ），沿用舊語意
    /// 共用同一組主進度欄位即可，不需要比照子進度拆開。
    /// </summary>
    private static readonly HashSet<string> SubProgressPhases = new() { "netiq-ai", "netiq-backpressure" };

    public string? SubProgressPhase { get; private set; }
    public int SubProgressDone { get; private set; }
    public int SubProgressTotal { get; private set; }

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
            ProgressPhase = null;
            ProgressDone = 0;
            ProgressTotal = 0;
            SubProgressPhase = null;
            SubProgressDone = 0;
            SubProgressTotal = 0;
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

    /// <summary>IRunProgress 的落地點（docs/archive/FEEDBACK-8-PLAN.md #2）：本機／NetIQ 兩階段各自的
    /// 主機日進度，供狀態 API 畫進度條。phase 落在 <see cref="SubProgressPhases"/> 時寫入子進度欄位，
    /// 其餘（local／netiq）寫入主進度欄位——兩組欄位互不覆蓋（回饋十四輪 UI-6）。</summary>
    public void ReportProgress(string phase, int done, int total)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            if (SubProgressPhases.Contains(phase))
            {
                SubProgressPhase = phase;
                SubProgressDone = done;
                SubProgressTotal = total;
            }
            else
            {
                ProgressPhase = phase;
                ProgressDone = done;
                ProgressTotal = total;
            }
        }
    }

    /// <param name="outcome">這次執行的結局；null＝沒有真的開始過（例如跨行程 Mutex 逾時），
    /// 維持上一筆 LastOutcome 不變，不用「沒開始」蓋掉「上次真的跑過的結果」。</param>
    public void EndRun(RunOutcome? outcome = null)
    {
        lock (_lock)
        {
            IsRunning = false;
            Trigger = null;
            StartedAt = null;
            ProgressPhase = null;
            ProgressDone = 0;
            ProgressTotal = 0;
            SubProgressPhase = null;
            SubProgressDone = 0;
            SubProgressTotal = 0;
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
