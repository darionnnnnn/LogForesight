using LogForesight.Core.Persistence;
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

/// <summary>Identifies the bounded workload sharing the single process-wide PRTG request budget.</summary>
public enum PrtgRequestPurpose
{
    General,
    ProfileRefresh,
    Snapshot,
    CapacityPilot
}

public sealed record PrtgRequestBudgetUsage(int InFlight, int ProfileInFlight, int SnapshotInFlight,
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
    private readonly Func<CancellationToken, Task>? _beforeMarkSent;
    private int _disposed;
    private int _markedSent;

    internal PrtgBudgetLease(Action<bool>? onRelease, Action? onMarkSent = null,
        Func<CancellationToken, Task>? beforeMarkSent = null)
    {
        _onRelease = onRelease;
        _onMarkSent = onMarkSent;
        _beforeMarkSent = beforeMarkSent;
    }

    public async Task MarkRequestSentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _markedSent) != 0) return;
        if (_beforeMarkSent is not null) await _beforeMarkSent(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        MarkRequestSent();
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
    private readonly Dictionary<PrtgRequestPurpose, int> _inFlightByPurpose = new();
    private readonly Dictionary<PrtgRequestPurpose, SemaphoreSlim> _purposeSendGates = new()
    {
        [PrtgRequestPurpose.General] = new(1, 1),
        [PrtgRequestPurpose.ProfileRefresh] = new(1, 1),
        [PrtgRequestPurpose.Snapshot] = new(1, 1)
    };
    private readonly Dictionary<PrtgRequestPurpose, TimeSpan> _lastPurposeTableSent = new();
    private readonly Dictionary<PrtgRequestPurpose, TimeSpan> _nextPurposeTableSlot = new();
    private PrtgCapacityAdmissionPlan? _admissionPlan;
    private CancellationTokenSource? _delayCts;
    private TimeSpan _scheduledWakeup = TimeSpan.MaxValue;
    private IPrtgHistoricRequestCoordinator? _historicCoordinator;
    private readonly AsyncLocal<string?> _historicReservationId = new();

    /// <summary>
    /// 全程序共享的預算實例。正式 client 建立入口（PrtgClientFactory 與 SystemSettingsService）皆使用此實例。
    /// 不提供 public setter，避免測試污染全域實例。
    /// </summary>
    public static PrtgRequestBudget Shared { get; } = new();

    public IPrtgClock Clock { get; }

    public void SetHistoricCoordinator(IPrtgHistoricRequestCoordinator coordinator) =>
        Volatile.Write(ref _historicCoordinator, coordinator ?? throw new ArgumentNullException(nameof(coordinator)));

    public async Task<PrtgHistoricReservation> ReserveQualificationHistoricAsync(string jobId, string owner,
        long leaseVersion, CancellationToken cancellationToken)
    {
        var coordinator = Volatile.Read(ref _historicCoordinator)
            ?? throw new InvalidOperationException("historic-sql-admission-not-installed");
        return await coordinator.ReserveQualificationAsync(jobId, owner, leaseVersion, cancellationToken);
    }

    public async Task<PrtgHistoricReservation> ReserveOtherHistoricAsync(CancellationToken cancellationToken)
    {
        var coordinator = Volatile.Read(ref _historicCoordinator)
            ?? throw new InvalidOperationException("historic-sql-admission-not-installed");
        return await coordinator.ReserveOtherAsync(cancellationToken);
    }

    public IDisposable UseHistoricReservation(PrtgHistoricReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        var prior = _historicReservationId.Value;
        _historicReservationId.Value = reservation.Id;
        return new HistoricReservationScope(_historicReservationId, prior);
    }

    private sealed class HistoricReservationScope(AsyncLocal<string?> slot, string? prior) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) slot.Value = prior;
        }
    }

    public void SetAdmissionPlan(PrtgCapacityAdmissionPlan plan)
    {
        lock (_lock)
        {
            if (_admissionPlan is not null && plan.Version < _admissionPlan.Version) return;
            if (_admissionPlan is not null && plan.Version == _admissionPlan.Version &&
                plan.Fingerprint != _admissionPlan.Fingerprint) return;
            _admissionPlan = plan;
            _nextPurposeTableSlot.Clear();
            FailStaleAdmissionWaitersUnderLock();
            EvaluateWaitersUnderLock();
        }
    }

    public void ClearAdmissionPlan()
    {
        lock (_lock)
        {
            _admissionPlan = null;
            _nextPurposeTableSlot.Clear();
            FailStaleAdmissionWaitersUnderLock();
            EvaluateWaitersUnderLock();
        }
    }

    public PrtgCapacityAdmissionPlan? CurrentAdmissionPlan
    {
        get { lock (_lock) return _admissionPlan; }
    }

    /// <summary>
    /// Returns a lower bound for Table purpose pacing under the current, exact admission plan.
    /// It includes any already reserved slot for the first send and the minimum spacing between
    /// the remaining sends. It deliberately excludes HTTP latency and shared rolling-window waits;
    /// callers may reject only when this lower bound alone exceeds their deadline budget.
    /// </summary>
    public bool TryGetMinimumTablePacingWait(PrtgRequestPurpose purpose, int requestCount,
        string admissionPlanFingerprint, out TimeSpan minimumWait)
    {
        minimumWait = TimeSpan.Zero;
        if (requestCount is < 1 or > 100_000 || string.IsNullOrWhiteSpace(admissionPlanFingerprint) ||
            purpose is not (PrtgRequestPurpose.General or PrtgRequestPurpose.ProfileRefresh or PrtgRequestPurpose.Snapshot))
            return false;

        lock (_lock)
        {
            var plan = _admissionPlan;
            if (plan is null || plan.LeaseUntilUtc <= Clock.UtcNow || plan.Fingerprint != admissionPlanFingerprint)
                return false;
            var rate = purpose switch
            {
                PrtgRequestPurpose.Snapshot => plan.SnapshotTableRequestsPerSecond,
                PrtgRequestPurpose.ProfileRefresh => plan.ProfileTableRequestsPerSecond,
                _ => plan.GeneralResidualRequestsPerSecond
            };
            if (!double.IsFinite(rate) || rate <= 0) return false;

            var intervalSeconds = 1d / rate;
            if (!double.IsFinite(intervalSeconds) || intervalSeconds > TimeSpan.MaxValue.TotalSeconds)
                return false;
            TimeSpan interval;
            try { interval = TimeSpan.FromSeconds(intervalSeconds); }
            catch (OverflowException) { return false; }
            var now = Clock.Elapsed;
            var firstDue = now;
            if (_lastPurposeTableSent.TryGetValue(purpose, out var lastSent))
            {
                if (lastSent.Ticks > TimeSpan.MaxValue.Ticks - interval.Ticks) return false;
                var lastDue = TimeSpan.FromTicks(lastSent.Ticks + interval.Ticks);
                if (lastDue > firstDue) firstDue = lastDue;
            }
            if (_nextPurposeTableSlot.TryGetValue(purpose, out var reservedSlot) && reservedSlot > firstDue)
                firstDue = reservedSlot;

            var firstDelay = firstDue > now ? firstDue - now : TimeSpan.Zero;
            var restTicks = (double)interval.Ticks * (requestCount - 1);
            if (!double.IsFinite(restTicks) || restTicks > TimeSpan.MaxValue.Ticks) return false;
            var roundedRestTicks = (long)Math.Ceiling(restTicks);
            if (firstDelay.Ticks > TimeSpan.MaxValue.Ticks - roundedRestTicks) return false;
            minimumWait = TimeSpan.FromTicks(firstDelay.Ticks + roundedRestTicks);
            return true;
        }
    }

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
            return new(_inFlightCount,
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.ProfileRefresh) +
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.CapacityPilot),
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.Snapshot),
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
    /// 統一協調在途上限（最多 4）與滑動窗口限制，確保取得名額時兩者皆滿足。
    /// 取得的名額為 pending 狀態，於 MarkRequestSent 時才起算滑動窗口。
    /// </summary>
    /// <summary>Compatibility-preserving legacy entry point; existing custom budget overrides remain active.</summary>
    public virtual Task<PrtgBudgetLease> AcquireAsync(PrtgEndpointCategory category,
        CancellationToken cancellationToken = default) =>
        AcquireCoreAsync(category, cancellationToken, PrtgRequestPurpose.General, null);

    /// <summary>Purpose-aware runtime entry point used by the bounded snapshot/profile lanes.</summary>
    public Task<PrtgBudgetLease> AcquireAsync(PrtgEndpointCategory category, CancellationToken cancellationToken,
        PrtgRequestPurpose purpose, string? admissionPlanFingerprint = null) =>
        AcquireCoreAsync(category, cancellationToken, purpose, admissionPlanFingerprint);

    private async Task<PrtgBudgetLease> AcquireCoreAsync(PrtgEndpointCategory category,
        CancellationToken cancellationToken, PrtgRequestPurpose purpose, string? admissionPlanFingerprint)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var coordinator = category == PrtgEndpointCategory.HistoricData ? Volatile.Read(ref _historicCoordinator) : null;
        var historicTicketId = category == PrtgEndpointCategory.HistoricData ? _historicReservationId.Value : null;
        if (category == PrtgEndpointCategory.HistoricData && ReferenceEquals(this, Shared) && coordinator is null)
            throw new InvalidOperationException("historic-sql-admission-not-installed");
        // Acquire the durable rolling-window ticket before requesting any local in-flight slot.
        // A caller waiting for the global historic quota must not consume one of four HTTP permits.
        if (category == PrtgEndpointCategory.HistoricData && coordinator is not null && historicTicketId is null)
        {
            using var ticket = await coordinator.ReserveOtherAsync(cancellationToken);
            historicTicketId = ticket.Id;
            return await AcquireWithHistoricTicketAsync(category, cancellationToken, purpose,
                admissionPlanFingerprint, coordinator, historicTicketId, transferTicket: ticket);
        }
        return await AcquireWithHistoricTicketAsync(category, cancellationToken, purpose,
            admissionPlanFingerprint, coordinator, historicTicketId, transferTicket: null);
    }

    private async Task<PrtgBudgetLease> AcquireWithHistoricTicketAsync(PrtgEndpointCategory category,
        CancellationToken cancellationToken, PrtgRequestPurpose purpose, string? admissionPlanFingerprint,
        IPrtgHistoricRequestCoordinator? coordinator, string? historicTicketId, PrtgHistoricReservation? transferTicket)
    {
        try
        {
        await ReservePurposeTableSlotBeforeAcquireAsync(category, purpose, admissionPlanFingerprint, cancellationToken);

        Waiter? waiter = null;
        lock (_lock)
        {
            ValidateAdmissionPlanUnderLock(purpose, admissionPlanFingerprint, category);
            var now = Clock.Elapsed;
            CleanExpiredReservationsUnderLock(now);

            // 若目前無排隊者，且在途名額與窗口額度皆足夠，立即准許
            if (_waiters.Count == 0 && HasInFlightCapacityUnderLock(purpose) && HasCapacityUnderLock(category))
            {
                _inFlightCount++;
                IncrementPurposeUnderLock(purpose);
                var resId = Interlocked.Increment(ref _nextReservationId);
                if (category != PrtgEndpointCategory.Other)
                {
                    _reservations.Add(new Reservation(resId, category));
                }

                var immediateLease = CreateLeaseUnderLock(resId, category, purpose, admissionPlanFingerprint,
                    coordinator, historicTicketId);
                transferTicket?.TransferOwnership();
                return immediateLease;
            }

            // 無法立即准許，加入排隊佇列
            var tcs = new TaskCompletionSource<PrtgBudgetLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = new Waiter(category, purpose, admissionPlanFingerprint, historicTicketId, coordinator, tcs);
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

        transferTicket?.TransferOwnership();
        return lease;
        }
        catch
        {
            if (transferTicket is not null) transferTicket.Dispose();
            else if (historicTicketId is not null && coordinator is not null &&
                historicTicketId != _historicReservationId.Value) coordinator.ReleaseReservation(historicTicketId);
            throw;
        }
    }

    private PrtgBudgetLease CreateLeaseUnderLock(long reservationId, PrtgEndpointCategory category,
        PrtgRequestPurpose purpose, string? admissionPlanFingerprint,
        IPrtgHistoricRequestCoordinator? historicCoordinator, string? historicTicketId)
    {
        return new PrtgBudgetLease(
            onRelease: wasSent =>
            {
                lock (_lock)
                {
                    _inFlightCount--;
                    DecrementPurposeUnderLock(purpose);
                    if (!wasSent && reservationId != 0)
                    {
                        _reservations.RemoveAll(r => r.Id == reservationId);
                    }
                    if (!wasSent && category == PrtgEndpointCategory.HistoricData && historicTicketId is not null &&
                        historicCoordinator is not null && historicTicketId != _historicReservationId.Value)
                        historicCoordinator.ReleaseReservation(historicTicketId);
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
            },
            beforeMarkSent: async ct =>
            {
                await PacePurposeTableSendAsync(category, purpose, admissionPlanFingerprint, ct);
                if (category != PrtgEndpointCategory.HistoricData) return;
                if (historicCoordinator is not null && historicTicketId is not null)
                    await historicCoordinator.WaitAndRecordSendAsync(false, ct, historicTicketId);
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
            try { ValidateAdmissionPlanUnderLock(waiter.Purpose, waiter.AdmissionPlanFingerprint, waiter.Category); }
            catch (InvalidOperationException ex)
            {
                _waiters.RemoveAt(i--);
                waiter.Tcs.TrySetException(ex);
                continue;
            }
            if (HasInFlightCapacityUnderLock(waiter.Purpose) && HasCapacityUnderLock(waiter.Category))
            {
                _waiters.RemoveAt(i);
                i--;

                _inFlightCount++;
                IncrementPurposeUnderLock(waiter.Purpose);
                var resId = Interlocked.Increment(ref _nextReservationId);
                if (waiter.Category != PrtgEndpointCategory.Other)
                {
                    _reservations.Add(new Reservation(resId, waiter.Category));
                }

                var lease = CreateLeaseUnderLock(resId, waiter.Category, waiter.Purpose, waiter.AdmissionPlanFingerprint,
                    waiter.HistoricCoordinator, waiter.HistoricTicketId);
                waiter.Tcs.TrySetResult(lease);
            }
        }

        ScheduleNextWakeupUnderLock(now);
    }

    private bool HasInFlightCapacityUnderLock(PrtgRequestPurpose purpose)
    {
        if (_inFlightCount >= 4) return false;
        var activePurpose = _inFlightByPurpose.GetValueOrDefault(purpose);
        if (purpose is PrtgRequestPurpose.ProfileRefresh or PrtgRequestPurpose.CapacityPilot)
            return activePurpose == 0 &&
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.ProfileRefresh) +
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.CapacityPilot) == 0;
        // Snapshot work is bounded to three requests so one slot remains available to the
        // serialized profile lane. General retains the historic shared four-slot ceiling.
        if (purpose == PrtgRequestPurpose.Snapshot)
        {
            var profileInFlight = _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.ProfileRefresh) +
                _inFlightByPurpose.GetValueOrDefault(PrtgRequestPurpose.CapacityPilot);
            return activePurpose < 3 && _inFlightCount < (profileInFlight > 0 ? 4 : 3);
        }
        if (purpose == PrtgRequestPurpose.General && _waiters.Any(w =>
            w.Purpose is PrtgRequestPurpose.ProfileRefresh or PrtgRequestPurpose.CapacityPilot) && _inFlightCount >= 3)
            return false;
        return true;
    }

    private void ValidateAdmissionPlanUnderLock(PrtgRequestPurpose purpose, string? fingerprint,
        PrtgEndpointCategory category)
    {
        if (category != PrtgEndpointCategory.Table ||
            purpose is not (PrtgRequestPurpose.Snapshot or PrtgRequestPurpose.ProfileRefresh) &&
            !(purpose == PrtgRequestPurpose.General && fingerprint is not null))
            return;
        if (_admissionPlan is null || _admissionPlan.LeaseUntilUtc <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(fingerprint) || _admissionPlan.Fingerprint != fingerprint)
            throw new InvalidOperationException("prtg-capacity-admission-plan-missing-or-stale");
    }

    private async Task PacePurposeTableSendAsync(PrtgEndpointCategory category, PrtgRequestPurpose purpose,
        string? fingerprint, CancellationToken ct)
    {
        if (category != PrtgEndpointCategory.Table || purpose == PrtgRequestPurpose.CapacityPilot) return;
        await _purposeSendGates[purpose].WaitAsync(ct);
        try
        {
            PrtgCapacityAdmissionPlan? plan;
            double rate;
            TimeSpan? last;
            lock (_lock)
            {
                ValidateAdmissionPlanUnderLock(purpose, fingerprint, category);
                plan = _admissionPlan;
                if (plan is null) return;
                if (plan.LeaseUntilUtc <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("prtg-capacity-admission-plan-expired");
                rate = purpose switch
                {
                    PrtgRequestPurpose.Snapshot => plan.SnapshotTableRequestsPerSecond,
                    PrtgRequestPurpose.ProfileRefresh => plan.ProfileTableRequestsPerSecond,
                    PrtgRequestPurpose.General => plan.GeneralResidualRequestsPerSecond,
                    _ => double.PositiveInfinity
                };
                if (rate <= 0) throw new InvalidOperationException("prtg-capacity-general-table-lane-unavailable");
                last = _lastPurposeTableSent.TryGetValue(purpose, out var previous) ? previous : null;
            }
            if (last.HasValue)
            {
                var due = last.Value + TimeSpan.FromSeconds(1d / rate);
                var delay = due - Clock.Elapsed;
                if (delay > TimeSpan.Zero) await Clock.DelayAsync(delay, ct);
            }
            lock (_lock)
            {
                ValidateAdmissionPlanUnderLock(purpose, fingerprint, category);
                if (_admissionPlan?.Fingerprint != plan?.Fingerprint || _admissionPlan?.Version != plan?.Version)
                    throw new InvalidOperationException("prtg-capacity-admission-plan-superseded");
                _lastPurposeTableSent[purpose] = Clock.Elapsed;
            }
        }
        finally { _purposeSendGates[purpose].Release(); }
    }

    /// <summary>
    /// Purpose-rate delay happens before reserving a global in-flight permit. A slot is reserved
    /// under the purpose gate so concurrent callers cannot all wake on the same rate boundary.
    /// MarkRequestSent performs a second short check after shared quota/permit waits, protecting
    /// the actual wire rate if a reserved caller was delayed after this point.
    /// </summary>
    private async Task ReservePurposeTableSlotBeforeAcquireAsync(PrtgEndpointCategory category,
        PrtgRequestPurpose purpose, string? fingerprint, CancellationToken ct)
    {
        if (category != PrtgEndpointCategory.Table || purpose == PrtgRequestPurpose.CapacityPilot) return;
        await _purposeSendGates[purpose].WaitAsync(ct);
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (_lock)
                {
                    ValidateAdmissionPlanUnderLock(purpose, fingerprint, category);
                    var plan = _admissionPlan;
                    if (plan is null) return;
                    var rate = purpose switch
                    {
                        PrtgRequestPurpose.Snapshot => plan.SnapshotTableRequestsPerSecond,
                        PrtgRequestPurpose.ProfileRefresh => plan.ProfileTableRequestsPerSecond,
                        PrtgRequestPurpose.General => plan.GeneralResidualRequestsPerSecond,
                        _ => double.PositiveInfinity
                    };
                    if (rate <= 0) throw new InvalidOperationException("prtg-capacity-purpose-lane-unavailable");
                    var interval = TimeSpan.FromSeconds(1d / rate);
                    var now = Clock.Elapsed;
                    var due = now;
                    if (_lastPurposeTableSent.TryGetValue(purpose, out var lastSent) && lastSent + interval > due)
                        due = lastSent + interval;
                    if (_nextPurposeTableSlot.TryGetValue(purpose, out var reserved) && reserved > due)
                        due = reserved;
                    delay = due - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        _nextPurposeTableSlot[purpose] = now + interval;
                        return;
                    }
                }
                await Clock.DelayAsync(delay, ct);
            }
        }
        finally { _purposeSendGates[purpose].Release(); }
    }

    private void FailStaleAdmissionWaitersUnderLock()
    {
        for (var i = _waiters.Count - 1; i >= 0; i--)
        {
            var waiter = _waiters[i];
            try { ValidateAdmissionPlanUnderLock(waiter.Purpose, waiter.AdmissionPlanFingerprint, waiter.Category); }
            catch (InvalidOperationException ex)
            {
                _waiters.RemoveAt(i);
                waiter.Tcs.TrySetException(ex);
            }
        }
    }

    private void IncrementPurposeUnderLock(PrtgRequestPurpose purpose) =>
        _inFlightByPurpose[purpose] = _inFlightByPurpose.GetValueOrDefault(purpose) + 1;

    private void DecrementPurposeUnderLock(PrtgRequestPurpose purpose)
    {
        var active = _inFlightByPurpose.GetValueOrDefault(purpose);
        if (active <= 1) _inFlightByPurpose.Remove(purpose);
        else _inFlightByPurpose[purpose] = active - 1;
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

        // The bounded primary-channel property lookup participates in the same reserved 2/s
        // source lane as the table reads in a four-call trusted-profile refresh.
        if (string.Equals(lastSegment, "getobjectproperty.htm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastSegment, "getobjectproperty", StringComparison.OrdinalIgnoreCase))
            return PrtgEndpointCategory.Table;

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
        public PrtgRequestPurpose Purpose { get; }
        public string? AdmissionPlanFingerprint { get; }
        public string? HistoricTicketId { get; }
        public IPrtgHistoricRequestCoordinator? HistoricCoordinator { get; }
        public TaskCompletionSource<PrtgBudgetLease> Tcs { get; }
        public IDisposable? Registration { get; set; }

        public Waiter(PrtgEndpointCategory category, PrtgRequestPurpose purpose, string? admissionPlanFingerprint,
            string? historicTicketId, IPrtgHistoricRequestCoordinator? historicCoordinator,
            TaskCompletionSource<PrtgBudgetLease> tcs)
        {
            Category = category;
            Purpose = purpose;
            AdmissionPlanFingerprint = admissionPlanFingerprint;
            HistoricTicketId = historicTicketId;
            HistoricCoordinator = historicCoordinator;
            Tcs = tcs;
        }
    }
}
