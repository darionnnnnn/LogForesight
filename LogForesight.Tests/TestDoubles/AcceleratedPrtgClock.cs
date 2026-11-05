using LogForesight.Core.Service;

namespace LogForesight.Tests.TestDoubles;

/// <summary>
/// 時間加速 1000 倍的 PRTG 配額時鐘：共享配額規則（historicdata 60 秒 5 次、table 1 秒 2 次）照常生效，
/// 但測試不必真的等——用真時鐘的 <c>new LogForesight.Core.Service.PrtgRequestBudget(new LogForesight.Tests.TestDoubles.AcceleratedPrtgClock())</c> 會讓送 10 顆 historicdata 的案例真等 60 秒以上。
/// </summary>
internal sealed class AcceleratedPrtgClock : IPrtgClock
{
    private const int Factor = 1000;
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private long _requestedTicks;

    /// <summary>配額邏輯累計要求等待的（未加速）時間：證明限速真的走過，不必守牆鐘。</summary>
    public TimeSpan TotalRequestedDelay => TimeSpan.FromTicks(Interlocked.Read(ref _requestedTicks));
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => TimeSpan.FromTicks(_watch.Elapsed.Ticks * Factor);
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref _requestedTicks, delay.Ticks);
        return Task.Delay(TimeSpan.FromTicks(Math.Max(1, delay.Ticks / Factor)), cancellationToken);
    }
}
