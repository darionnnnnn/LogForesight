namespace LogForesight.Core.Service;

/// <summary>
/// 把 PRTG 的時鐘（<see cref="IPrtgClock"/>）接成 <see cref="TimeProvider"/>，讓 Polly 的重試等待與請求配額走同一個時間來源：
/// 正式環境是 <see cref="RealPrtgClock"/>（真的等），測試的共用配額池換成加速時鐘後，分頁重試的 15／60 秒也一併加速，
/// 不必另開靜態掛鉤。只實作等待用得到的部分（一次性計時器）；週期計時器與到期時間變更也支援，但 Polly 不會用到。
/// </summary>
internal sealed class PrtgClockTimeProvider(IPrtgClock clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => clock.UtcNow;

    public override long GetTimestamp() => clock.Elapsed.Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new ClockTimer(clock, callback, state, dueTime, period);

    private sealed class ClockTimer : ITimer
    {
        private readonly IPrtgClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _sync = new();
        private CancellationTokenSource? _cts;
        private bool _disposed;

        internal ClockTimer(IPrtgClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                if (_disposed) return false;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                if (dueTime == Timeout.InfiniteTimeSpan) return true;

                var cts = _cts = new CancellationTokenSource();
                _ = RunAsync(dueTime, period, cts.Token);
                return true;
            }
        }

        private async Task RunAsync(TimeSpan dueTime, TimeSpan period, CancellationToken token)
        {
            try
            {
                await _clock.DelayAsync(dueTime, token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    _callback(_state);
                    if (period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero) return;
                    await _clock.DelayAsync(period, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 計時器被取消或重設：不觸發回呼
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
