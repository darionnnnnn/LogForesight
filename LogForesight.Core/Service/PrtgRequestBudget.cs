using System.Diagnostics;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 端點分類：
/// - HistoricData: 滾動 60 秒最多 5 次
/// - Table: 滾動 1 秒最多 2 次
/// - Other: 其他端點（如 getpasshash.htm 或未知路徑），僅受在途上限限制
/// </summary>
public enum PrtgEndpointCategory
{
    HistoricData,
    Table,
    Other
}

/// <summary>
/// PRTG 預算時鐘介面，供正式環境真實時間與測試可控時鐘共用。
/// 支援單調經過時間（Elapsed），避免系統 UTC 時間跳動影響限流基準。
/// </summary>
public interface IPrtgClock
{
    DateTimeOffset UtcNow { get; }
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// 正式環境時鐘：使用 DateTimeOffset.UtcNow、單調 Stopwatch 與 Task.Delay。
/// </summary>
public sealed class RealPrtgClock : IPrtgClock
{
    public static readonly RealPrtgClock Instance = new();
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartTimestamp);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

/// <summary>
/// 取得預算後的在途租約。
/// 在 SendAsync 前取得；HTTP 回應讀完或請求失敗／取消後呼叫 Dispose 釋放在途名額。
/// </summary>
public sealed class PrtgBudgetLease : IDisposable
{
    private readonly Action<bool>? _onRelease;
    private readonly Action? _onMarkSent;
    private int _disposed;
    private int _markedSent;

    internal PrtgBudgetLease(Action<bool>? onRelease, Action? onMarkSent = null)
    {
        _onRelease = onRelease;
        _onMarkSent = onMarkSent;
    }

    /// <summary>
    /// 標記 HTTP 請求已發送至傳輸層。以單調時鐘記下實際發送時刻，開啟滑動窗口。
    /// 重複呼叫不得重置時間戳；已 Dispose 之租約不得再標記。已發之請求即使後續失敗或例外，亦計入已發配額，不因 HTTP 失敗退回。
    /// </summary>
    public void MarkRequestSent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _markedSent, 1) != 0) return;

        _onMarkSent?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var wasSent = Volatile.Read(ref _markedSent) != 0;
        _onRelease?.Invoke(wasSent);
    }
}

/// <summary>
/// PRTG 請求預算控制（PRTG 第 53 輪 A1.2）。
/// 契約：
/// 1. historicdata 任一滾動 60 秒最多 5 次。
/// 2. table 任一滾動 1 秒最多 2 次。
/// 3. 所有 HTTP 在途上限 4（包括 getpasshash.htm 與其他端點）。
/// 4. 單程序共享單一配額池，不隨 client 建立／釋放重置。
/// 5. 等待 historicdata 不占滿在途名額，不阻塞仍有配額之 table。
/// 6. 在途與速率在同一個可發送之 admission 點決定。未送出的 pending 預留永不過期，
///    在 MarkRequestSent 實際發送時才依單調時鐘起算滑動窗口。
/// </summary>
public class PrtgRequestBudget
{
    private readonly object _lock = new();
    private int _inFlightCount;
    private long _nextReservationId;
    private readonly List<Reservation> _reservations = new();
    private readonly List<Waiter> _waiters = new();
    private CancellationTokenSource? _delayCts;
    private TimeSpan _scheduledWakeup = TimeSpan.MaxValue;

    /// <summary>
    /// 全程序共享的預算實例。正式 client 建立入口（PrtgClientFactory 與 SystemSettingsService）皆使用此實例。
    /// 不提供 public setter，避免測試污染全域實例。
    /// </summary>
    public static PrtgRequestBudget Shared { get; } = new();

    public IPrtgClock Clock { get; }

    public int InFlightCount
    {
        get { lock (_lock) return _inFlightCount; }
    }

    internal int WaiterCount
    {
        get { lock (_lock) return _waiters.Count; }
    }

    public PrtgRequestBudget(IPrtgClock? clock = null)
    {
        Clock = clock ?? RealPrtgClock.Instance;
    }

    /// <summary>
    /// 取得指定端點的發送名額及在途租約。
    /// 統一協調在途上限（最多 4）與滑動窗口限制，確保取得名額時兩者皆滿足。
    /// 取得的名額為 pending 狀態，於 MarkRequestSent 時才起算滑動窗口。
    /// </summary>
    public virtual async Task<PrtgBudgetLease> AcquireAsync(PrtgEndpointCategory category, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter? waiter = null;
        lock (_lock)
        {
            var now = Clock.Elapsed;
            CleanExpiredReservationsUnderLock(now);

            // 若目前無排隊者，且在途名額與窗口額度皆足夠，立即准許
            if (_waiters.Count == 0 && _inFlightCount < 4 && HasCapacityUnderLock(category))
            {
                _inFlightCount++;
                var resId = Interlocked.Increment(ref _nextReservationId);
                if (category != PrtgEndpointCategory.Other)
                {
                    _reservations.Add(new Reservation(resId, category));
                }

                return CreateLeaseUnderLock(resId, category);
            }

            // 無法立即准許，加入排隊佇列
            var tcs = new TaskCompletionSource<PrtgBudgetLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = new Waiter(category, tcs);
            _waiters.Add(waiter);

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        if (_waiters.Remove(waiter))
                        {
                            waiter.Tcs.TrySetCanceled(cancellationToken);
                            EvaluateWaitersUnderLock();
                        }
                    }
                });
            }

            EvaluateWaitersUnderLock();
        }

        PrtgBudgetLease lease;
        try
        {
            lease = await waiter.Tcs.Task;
        }
        catch
        {
            waiter.Registration?.Dispose();
            throw;
        }

        waiter.Registration?.Dispose();

        // 若在被准許後、返回呼叫端前取消，釋放租約與可能預留之額度
        if (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return lease;
    }

    private PrtgBudgetLease CreateLeaseUnderLock(long reservationId, PrtgEndpointCategory category)
    {
        return new PrtgBudgetLease(
            onRelease: wasSent =>
            {
                lock (_lock)
                {
                    _inFlightCount--;
                    if (!wasSent && reservationId != 0)
                    {
                        _reservations.RemoveAll(r => r.Id == reservationId);
                    }
                    EvaluateWaitersUnderLock();
                }
            },
            onMarkSent: () =>
            {
                lock (_lock)
                {
                    var res = _reservations.FirstOrDefault(r => r.Id == reservationId);
                    if (res != null && !res.IsSent)
                    {
                        res.IsSent = true;
                        res.Timestamp = Clock.Elapsed;
                        EvaluateWaitersUnderLock();
                    }
                }
            });
    }

    private void EvaluateWaitersUnderLock()
    {
        var now = Clock.Elapsed;
        CleanExpiredReservationsUnderLock(now);

        // 依 FIFO 順序准許合資格的等待者（Table 不受 HistoricData 窗口等待阻礙）
        for (int i = 0; i < _waiters.Count; i++)
        {
            if (_inFlightCount >= 4)
            {
                break;
            }

            var waiter = _waiters[i];
            if (HasCapacityUnderLock(waiter.Category))
            {
                _waiters.RemoveAt(i);
                i--;

                _inFlightCount++;
                var resId = Interlocked.Increment(ref _nextReservationId);
                if (waiter.Category != PrtgEndpointCategory.Other)
                {
                    _reservations.Add(new Reservation(resId, waiter.Category));
                }

                var lease = CreateLeaseUnderLock(resId, waiter.Category);
                waiter.Tcs.TrySetResult(lease);
            }
        }

        ScheduleNextWakeupUnderLock(now);
    }

    private void ScheduleNextWakeupUnderLock(TimeSpan now)
    {
        if (_waiters.Count == 0 || _inFlightCount >= 4)
        {
            return;
        }

        TimeSpan? earliestWakeup = null;

        bool hasHistoricWaiter = false;
        bool hasTableWaiter = false;
        foreach (var w in _waiters)
        {
            if (w.Category == PrtgEndpointCategory.HistoricData) hasHistoricWaiter = true;
            if (w.Category == PrtgEndpointCategory.Table) hasTableWaiter = true;
        }

        if (hasHistoricWaiter)
        {
            var wakeup = GetEarliestCapacityTimeUnderLock(PrtgEndpointCategory.HistoricData, 5, TimeSpan.FromSeconds(60));
            if (wakeup.HasValue && (!earliestWakeup.HasValue || wakeup.Value < earliestWakeup.Value))
            {
                earliestWakeup = wakeup.Value;
            }
        }

        if (hasTableWaiter)
        {
            var wakeup = GetEarliestCapacityTimeUnderLock(PrtgEndpointCategory.Table, 2, TimeSpan.FromSeconds(1));
            if (wakeup.HasValue && (!earliestWakeup.HasValue || wakeup.Value < earliestWakeup.Value))
            {
                earliestWakeup = wakeup.Value;
            }
        }

        if (!earliestWakeup.HasValue)
        {
            return;
        }

        if (earliestWakeup.Value < _scheduledWakeup)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            var cts = new CancellationTokenSource();
            _delayCts = cts;
            _scheduledWakeup = earliestWakeup.Value;

            var delay = earliestWakeup.Value - now;
            if (delay <= TimeSpan.Zero)
            {
                _scheduledWakeup = TimeSpan.MaxValue;
                return;
            }

            var delayTask = Clock.DelayAsync(delay, cts.Token);
            delayTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !cts.IsCancellationRequested)
                {
                    lock (_lock)
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            _scheduledWakeup = TimeSpan.MaxValue;
                            EvaluateWaitersUnderLock();
                        }
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private TimeSpan? GetEarliestCapacityTimeUnderLock(PrtgEndpointCategory category, int limit, TimeSpan window)
    {
        var categoryReservations = _reservations
            .Where(r => r.Category == category)
            .ToList();

        if (categoryReservations.Count < limit)
        {
            return Clock.Elapsed;
        }

        var sentReservations = categoryReservations
            .Where(r => r.IsSent)
            .OrderBy(r => r.Timestamp)
            .ToList();

        int pendingCount = categoryReservations.Count - sentReservations.Count;
        if (pendingCount >= limit)
        {
            return null;
        }

        int sentIndex = categoryReservations.Count - limit;
        if (sentIndex >= 0 && sentIndex < sentReservations.Count)
        {
            return sentReservations[sentIndex].Timestamp + window;
        }

        return null;
    }

    private bool HasCapacityUnderLock(PrtgEndpointCategory category)
    {
        if (category == PrtgEndpointCategory.Other)
            return true;

        int count = 0;
        foreach (var r in _reservations)
        {
            if (r.Category == category)
            {
                count++;
            }
        }

        if (category == PrtgEndpointCategory.Table)
        {
            return count < 2;
        }

        if (category == PrtgEndpointCategory.HistoricData)
        {
            return count < 5;
        }

        return true;
    }

    private void CleanExpiredReservationsUnderLock(TimeSpan now)
    {
        var historicCutoff = now - TimeSpan.FromSeconds(60);
        var tableCutoff = now - TimeSpan.FromSeconds(1);

        _reservations.RemoveAll(r =>
            r.IsSent &&
            ((r.Category == PrtgEndpointCategory.HistoricData && r.Timestamp <= historicCutoff) ||
             (r.Category == PrtgEndpointCategory.Table && r.Timestamp <= tableCutoff)));
    }

    /// <summary>
    /// 依相對或絕對路徑分類 PRTG 端點。
    /// 支援大小寫不敏感、前綴多餘斜線與 query string 變更。比對 endpoint 檔名而非模糊子字串。
    /// </summary>
    public static PrtgEndpointCategory Classify(string? relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            return PrtgEndpointCategory.Other;

        var path = relativeOrAbsolutePath;
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return PrtgEndpointCategory.Other;

        var lastSegment = segments[^1];

        if (lastSegment.StartsWith("historicdata.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastSegment, "historicdata", StringComparison.OrdinalIgnoreCase))
        {
            return PrtgEndpointCategory.HistoricData;
        }

        if (lastSegment.StartsWith("table.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastSegment, "table", StringComparison.OrdinalIgnoreCase))
        {
            return PrtgEndpointCategory.Table;
        }

        return PrtgEndpointCategory.Other;
    }

    /// <summary>
    /// 依絕對 URI 的 AbsolutePath 分類端點。
    /// </summary>
    public static PrtgEndpointCategory Classify(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Classify(uri.AbsolutePath);
    }

    private sealed class Reservation
    {
        public long Id { get; }
        public PrtgEndpointCategory Category { get; }
        public bool IsSent { get; set; }
        public TimeSpan Timestamp { get; set; }

        public Reservation(long id, PrtgEndpointCategory category)
        {
            Id = id;
            Category = category;
            IsSent = false;
            Timestamp = TimeSpan.Zero;
        }
    }

    private sealed class Waiter
    {
        public PrtgEndpointCategory Category { get; }
        public TaskCompletionSource<PrtgBudgetLease> Tcs { get; }
        public IDisposable? Registration { get; set; }

        public Waiter(PrtgEndpointCategory category, TaskCompletionSource<PrtgBudgetLease> tcs)
        {
            Category = category;
            Tcs = tcs;
        }
    }
}
