namespace LogForesight.Web.Services;

/// <summary>
/// 排程執行的行程內狀態（docs/WEB-SCHEDULER-PLAN.md §1.4.4）：單例，供
/// <see cref="SchedulerHostedService"/> 與狀態／停止 API 共用同一份「現在是否在跑、誰觸發的、
/// 最新進度」。<see cref="TryBeginRun"/> 內建原子檢查——這就是「行程內單一執行 gate」，
/// 手動觸發撞上排程執行中時直接拒絕（回 false），不用另外設一把鎖。
/// </summary>
public class SchedulerRunState
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public string? Trigger { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LatestMessage { get; private set; }

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

    public void EndRun()
    {
        lock (_lock)
        {
            IsRunning = false;
            Trigger = null;
            StartedAt = null;
            _cts?.Dispose();
            _cts = null;
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
