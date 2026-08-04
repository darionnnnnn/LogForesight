namespace LogForesight.Web.Services;

/// <summary>
/// 排程執行的行程內狀態（docs/WEB-SCHEDULER-PLAN.md §1.4.4）：單例，供
/// <see cref="SchedulerHostedService"/> 與狀態／停止 API 共用同一份「現在是否在跑、誰觸發的、
/// 最新進度」。<see cref="TryBeginRun"/> 內建原子檢查——這就是「行程內單一執行 gate」，
/// 手動觸發撞上排程執行中時直接拒絕（回 false），不用另外設一把鎖。
/// </summary>
/// <summary>
/// 上一次執行的結局（docs/FEEDBACK-7-PLAN.md）：立即執行／排程觸發失敗時，原本只寫 NLog，
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

    /// <param name="outcome">這次執行的結局；null＝沒有真的開始過（例如跨行程 Mutex 逾時），
    /// 維持上一筆 LastOutcome 不變，不用「沒開始」蓋掉「上次真的跑過的結果」。</param>
    public void EndRun(RunOutcome? outcome = null)
    {
        lock (_lock)
        {
            IsRunning = false;
            Trigger = null;
            StartedAt = null;
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
