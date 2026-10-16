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

/// <summary>共用同一個程序內配額池的工作類別；快照另有在途上限，保留一個名額給其他工作。</summary>
public enum PrtgRequestPurpose
{
    General,
    Snapshot
}

public sealed record PrtgRequestBudgetUsage(int InFlight, int SnapshotInFlight,
    int TableRequestsInLastSecond, int HistoricRequestsInLastMinute,
    TimeSpan? UntilNextTableToken, TimeSpan? UntilNextHistoricToken);

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

    public Task MarkRequestSentAsync(CancellationToken cancellationToken = default,
        Action? beforeMarkRequestSent = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _markedSent) != 0) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        beforeMarkRequestSent?.Invoke();
        MarkRequestSent();
        return Task.CompletedTask;
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
/// PRTG 請求預算控制（docs/PRTG-SPEC.md §3b）。
/// 契約：
/// 1. historicdata 任一滾動 60 秒最多 5 次。
/// 2. table 任一滾動 1 秒最多 2 次。
/// 3. 所有 HTTP 在途上限 4（包括 getpasshash.htm 與其他端點）；快照最多佔 3 個，保留一個給其他工作。
/// 4. 單程序共享單一配額池，不隨 client 建立／釋放重置。
/// 5. 等待 historicdata 不占滿在途名額，不阻塞仍有配額之 table。
/// 6. 在途與速率在同一個准許點決定。未送出的 pending 預留永不過期，
///    在 MarkRequestSent 實際發送時才依單調時鐘起算滑動窗口。
/// </summary>
public class PrtgRequestBudget
{
    private const int MaximumInFlight = 4;
    private const int MaximumSnapshotInFlight = 3;

    private readonly object _lock = new();
    private int _inFlightCount;
    private long _nextReservationId;
    private readonly List<Reservation> _reservations = new();
    private readonly List<Waiter> _waiters = new();
    private readonly Dictionary<PrtgRequestPurpose, int> _inFlightByPurpose = new();
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

    public PrtgRequestBudgetUsage ReadUsage()
    {
        lock (_lock)
        {
            var now = Clock.Elapsed;
            CleanExpiredReservationsUnderLock(now);
            var table = _reservations.Where(x => x.Category == PrtgEndpointCategory.Table && x.IsSent)
                .OrderBy(x => x.Timestamp).ToArray();
            var historic = _reservations.Where(x => x.Category == PrtgEndpointCategory.HistoricData && x.IsSent)
                .OrderBy(x => x.Timestamp).ToArray();
            TimeSpan? tableDelay = table.Length < 2 ? TimeSpan.Zero :
                Positive(table[table.Length - 2].Timestamp + TimeSpan.FromSeconds(1) - now);
            TimeSpan? historicDelay = historic.Length < 5 ? TimeSpan.Zero :
                Positive(historic[historic.Length - 5].Timestamp + TimeSpan.FromSeconds(60) - now);
            return new(_inFlightCount, _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.Snapshot),
                table.Length, historic.Length, tableDelay, historicDelay);
        }
    }

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

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
    /// 統一協調在途上限與滑動窗口限制，確保取得名額時兩者皆滿足。
    /// 取得的名額為 pending 狀態，於 MarkRequestSent 時才起算滑動窗口。
    /// </summary>
    public virtual Task<PrtgBudgetLease> AcquireAsync(PrtgEndpointCategory category,
        CancellationToken cancellationToken = default) =>
        AcquireAsync(category, cancellationToken, PrtgRequestPurpose.General);

    public async Task<PrtgBudgetLease> AcquireAsync(PrtgEndpointCategory category, CancellationToken cancellationToken,
        PrtgRequestPurpose purpose)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            CleanExpiredReservationsUnderLock(Clock.Elapsed);

            // 若目前無排隊者，且在途名額與窗口額度皆足夠，立即准許
            if (_waiters.Count == 0 && HasInFlightCapacityUnderLock(purpose) && HasCapacityUnderLock(category))
            {
                return GrantUnderLock(category, purpose);
            }

            // 無法立即准許，加入排隊佇列
            waiter = new Waiter(category, purpose,
                new TaskCompletionSource<PrtgBudgetLease>(TaskCreationOptions.RunContinuationsAsynchronously));
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
        finally
        {
            waiter.Registration?.Dispose();
        }

        // 若在被准許後、返回呼叫端前取消，釋放租約與可能預留之額度
        if (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return lease;
    }

    private PrtgBudgetLease GrantUnderLock(PrtgEndpointCategory category, PrtgRequestPurpose purpose)
    {
        _inFlightCount++;
        _inFlightByPurpose[purpose] = _inFlightByPurpose.GetValueOrDefault(purpose) + 1;
        var reservationId = Interlocked.Increment(ref _nextReservationId);
        if (category != PrtgEndpointCategory.Other)
        {
            _reservations.Add(new Reservation(reservationId, category));
        }

        return new PrtgBudgetLease(
            onRelease: wasSent =>
            {
                lock (_lock)
                {
                    _inFlightCount--;
                    var active = _inFlightByPurpose.GetValueOrDefault(purpose);
                    if (active <= 1) _inFlightByPurpose.Remove(purpose);
                    else _inFlightByPurpose[purpose] = active - 1;
                    if (!wasSent)
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
            var waiter = _waiters[i];
            if (HasInFlightCapacityUnderLock(waiter.Purpose) && HasCapacityUnderLock(waiter.Category))
            {
                _waiters.RemoveAt(i);
                i--;
                waiter.Tcs.TrySetResult(GrantUnderLock(waiter.Category, waiter.Purpose));
            }
        }

        ScheduleNextWakeupUnderLock(now);
    }

    private bool HasInFlightCapacityUnderLock(PrtgRequestPurpose purpose)
    {
        if (_inFlightCount >= MaximumInFlight) return false;
        return purpose != PrtgRequestPurpose.Snapshot ||
            _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.Snapshot) < MaximumSnapshotInFlight;
    }

    private void ScheduleNextWakeupUnderLock(TimeSpan now)
    {
        if (_waiters.Count == 0 || _inFlightCount >= MaximumInFlight)
        {
            return;
        }

        TimeSpan? earliestWakeup = null;
        foreach (var (category, limit, window) in new[]
                 {
                     (PrtgEndpointCategory.HistoricData, 5, TimeSpan.FromSeconds(60)),
                     (PrtgEndpointCategory.Table, 2, TimeSpan.FromSeconds(1))
                 })
        {
            if (!_waiters.Any(w => w.Category == category)) continue;
            var wakeup = GetEarliestCapacityTimeUnderLock(category, limit, window);
            if (wakeup.HasValue && (!earliestWakeup.HasValue || wakeup.Value < earliestWakeup.Value))
            {
                earliestWakeup = wakeup.Value;
            }
        }

        if (!earliestWakeup.HasValue || earliestWakeup.Value >= _scheduledWakeup)
        {
            return;
        }

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

        Clock.DelayAsync(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || cts.IsCancellationRequested) return;
            lock (_lock)
            {
                if (cts.IsCancellationRequested) return;
                _scheduledWakeup = TimeSpan.MaxValue;
                EvaluateWaitersUnderLock();
            }
        }, TaskScheduler.Default);
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
        var count = _reservations.Count(r => r.Category == category);
        return category switch
        {
            PrtgEndpointCategory.Table => count < 2,
            PrtgEndpointCategory.HistoricData => count < 5,
            _ => true
        };
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
        }
    }

    private sealed class Waiter
    {
        public PrtgEndpointCategory Category { get; }
        public PrtgRequestPurpose Purpose { get; }
        public TaskCompletionSource<PrtgBudgetLease> Tcs { get; }
        public IDisposable? Registration { get; set; }

        public Waiter(PrtgEndpointCategory category, PrtgRequestPurpose purpose,
            TaskCompletionSource<PrtgBudgetLease> tcs)
        {
            Category = category;
            Purpose = purpose;
            Tcs = tcs;
        }
    }
}
