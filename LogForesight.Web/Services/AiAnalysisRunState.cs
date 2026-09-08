using LogForesight.Web.Services;

namespace LogForesight.Web.Services;

/// <summary>AI 分析執行的狀態快照，供狀態 API 一次性讀出，避免分次讀取看到不一致的中間狀態</summary>
public record AiAnalysisSnapshot(
    bool IsRunning,
    string? Trigger,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int ProgressDone,
    int ProgressTotal,
    string? LatestMessage,
    RunOutcome? LastOutcome,
    bool CanStop,
    string? IdleReason);

/// <summary>
/// AI 排程閒置原因的字面值（與前端 runs.js 的文案對照表約定）。
/// 每個值對應 <c>AiAnalysisHostedService.TickAsync</c> 的一個提前返回條件。
/// </summary>
public static class AiIdleReasons
{
    /// <summary>AI 排程未啟用</summary>
    public const string Disabled = "disabled";
    /// <summary>存量校正回填尚未完成（完成前搶跑等於把整庫重跑一遍）</summary>
    public const string BackfillPending = "backfill-pending";
    /// <summary>不在 AI 執行窗口內</summary>
    public const string OutsideWindow = "outside-window";
    /// <summary>沒有待補的主機日</summary>
    public const string NoPending = "no-pending";
}

/// <summary>
/// AI 分析排程的行程內單例執行狀態＋**自成一個併發 1 的 gate**——
/// 照 <see cref="NetiqProbeRunState"/> 的形狀做，與取數排程的
/// <see cref="SchedulerRunState"/> 完全分開（兩者互不互斥、可同時執行）。
/// </summary>
public class AiAnalysisRunState
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _runCompletionTcs;
    private int? _cachedPendingCount;
    private DateTime _pendingCacheExpiresAt;
    // 世代號：fetcher 在鎖外跑 7~15 秒，期間若 AI 排程結束並使快取失效，
    // 寫回時必須發現世代已變、丟棄這次結果，否則「執行前的舊件數」會連同全新 30 秒有效期被寫回。
    private int _pendingCacheGeneration;
    private static readonly TimeSpan PendingCacheDuration = TimeSpan.FromSeconds(30);

    public bool IsRunning { get; private set; }
    public string? Trigger { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public int ProgressDone { get; private set; }
    public int ProgressTotal { get; private set; }
    public string? LatestMessage { get; private set; }
    public RunOutcome? LastOutcome { get; private set; }

    /// <summary>
    /// 閒置原因（非執行中時才有值）：AI 排程每輪輪詢有五個前置條件，任一不成立就整輪不跑，
    /// 而畫面只看得到「閒置 + N 件待補」——使用者無從分辨是設定沒開、不在窗口、還是真的沒事做。
    /// 值由 <c>AiAnalysisHostedService.TickAsync</c> 在每個提前返回處寫入，前端有對應文案。
    /// </summary>
    public string? IdleReason { get; private set; }

    /// <summary>寫入閒置原因（執行中一律為 null，由 TryBeginRun 清空）。</summary>
    public void SetIdleReason(string? reason)
    {
        lock (_lock)
        {
            if (!IsRunning) IdleReason = reason;
        }
    }

    /// <summary>gate 本體：已在跑就回 false，呼叫端不得再開一個；成功開始時回 true 並給出可用於停止的 cts</summary>
    public bool TryBeginRun(string trigger, int total, out CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                cts = null!;
                return false;
            }

            IsRunning = true;
            IdleReason = null;
            Trigger = trigger;
            StartedAt = DateTime.Now;
            CompletedAt = null;
            ProgressDone = 0;
            ProgressTotal = total;
            LatestMessage = null;
            _cts = new CancellationTokenSource();
            _runCompletionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cts = _cts;
            return true;
        }
    }

    /// <summary>對進行中的 AI 執行發出停止信號（優雅停止，停在單筆邊界）</summary>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (!IsRunning || _cts == null || _cts.IsCancellationRequested)
                return false;

            _cts.Cancel();
            return true;
        }
    }

    public void ReportProgress(int done, int total, string? message = null)
    {
        lock (_lock)
        {
            if (!IsRunning) return;
            ProgressDone = done;
            ProgressTotal = total;
            if (message != null) LatestMessage = message;
        }
    }

    public void EndRun(bool success, string? message = null)
    {
        TaskCompletionSource<bool>? tcsToComplete;
        lock (_lock)
        {
            IsRunning = false;
            CompletedAt = DateTime.Now;
            LastOutcome = new RunOutcome(success, message, Trigger ?? "schedule", DateTime.Now);
            _cts?.Dispose();
            _cts = null;
            tcsToComplete = _runCompletionTcs;
            _runCompletionTcs = null;
            InvalidatePendingAiCacheLocked();
        }
        tcsToComplete?.TrySetResult(success);
    }

    /// <summary>
    /// 取得待補件數（行程內快取 30 秒，避免排程頁輪詢頻繁掃庫造成慢 SQL）。
    /// 快取過期或未初始化時以 fetcher 重新查詢；執行緒安全。
    /// </summary>
    public int GetPendingAiCount(Func<int> fetcher)
    {
        int generation;
        lock (_lock)
        {
            if (_cachedPendingCount.HasValue && DateTime.UtcNow < _pendingCacheExpiresAt)
            {
                return _cachedPendingCount.Value;
            }
            generation = _pendingCacheGeneration;
        }

        // 查詢刻意放在鎖外：這支查詢在實機上要 7~15 秒，若在鎖內執行，排程頁的輪詢
        // 會連帶卡住 AI 排程自己的 TryBeginRun／EndRun（共用同一把鎖）。
        // 代價是冷快取時可能有兩個併發呼叫各查一次，遠優於阻塞狀態機。
        var count = fetcher();

        lock (_lock)
        {
            // 查詢期間快取被使失效（AI 排程結束／整批重標）→ 這筆是過期的，不寫回
            if (generation == _pendingCacheGeneration)
            {
                _cachedPendingCount = count;
                _pendingCacheExpiresAt = DateTime.UtcNow.Add(PendingCacheDuration);
            }
        }

        return count;
    }

    /// <summary>
    /// 使待補件數快取失效（AI 排程每輪收尾或強制重標時呼叫，確保介面即時反映最新件數）。
    /// </summary>
    public void InvalidatePendingAiCache()
    {
        lock (_lock) InvalidatePendingAiCacheLocked();
    }

    /// <summary>失效的唯一實作（EndRun 與公開方法共用）；呼叫端須已持有 _lock。</summary>
    private void InvalidatePendingAiCacheLocked()
    {
        _cachedPendingCount = null;
        _pendingCacheGeneration++;
    }

    /// <summary>等待當前執行完成（用於強制重新分析時的「優雅停止當前執行 → 整批重標」）</summary>
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_lock)
        {
            if (!IsRunning || _runCompletionTcs == null) return true;
            tcs = _runCompletionTcs;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public AiAnalysisSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new AiAnalysisSnapshot(
                IsRunning, Trigger, StartedAt, CompletedAt,
                ProgressDone, ProgressTotal, LatestMessage,
                LastOutcome, IsRunning, IdleReason);
        }
    }
}
