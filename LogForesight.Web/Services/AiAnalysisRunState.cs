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
    bool CanStop);

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
    private static readonly TimeSpan PendingCacheDuration = TimeSpan.FromSeconds(30);

    public bool IsRunning { get; private set; }
    public string? Trigger { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public int ProgressDone { get; private set; }
    public int ProgressTotal { get; private set; }
    public string? LatestMessage { get; private set; }
    public RunOutcome? LastOutcome { get; private set; }

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
            _cachedPendingCount = null;
        }
        tcsToComplete?.TrySetResult(success);
    }

    /// <summary>
    /// 取得待補件數（行程內快取 30 秒，避免排程頁輪詢頻繁掃庫造成慢 SQL）。
    /// 快取過期或未初始化時以 fetcher 重新查詢；執行緒安全。
    /// </summary>
    public int GetPendingAiCount(Func<int> fetcher)
    {
        lock (_lock)
        {
            if (_cachedPendingCount.HasValue && DateTime.UtcNow < _pendingCacheExpiresAt)
            {
                return _cachedPendingCount.Value;
            }
        }

        // 查詢刻意放在鎖外：這支查詢在實機上要 7~15 秒，若在鎖內執行，排程頁的輪詢
        // 會連帶卡住 AI 排程自己的 TryBeginRun／EndRun（共用同一把鎖）。
        // 代價是冷快取時可能有兩個併發呼叫各查一次，遠優於阻塞狀態機。
        var count = fetcher();

        lock (_lock)
        {
            _cachedPendingCount = count;
            _pendingCacheExpiresAt = DateTime.UtcNow.Add(PendingCacheDuration);
        }

        return count;
    }

    /// <summary>
    /// 使待補件數快取失效（AI 排程每輪收尾或強制重標時呼叫，確保介面即時反映最新件數）。
    /// </summary>
    public void InvalidatePendingAiCache()
    {
        lock (_lock)
        {
            _cachedPendingCount = null;
        }
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
                LastOutcome, IsRunning);
        }
    }
}
