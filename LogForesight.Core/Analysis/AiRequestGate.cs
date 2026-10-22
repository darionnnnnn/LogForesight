using System.Diagnostics;
using NLog;

namespace LogForesight.Core.Analysis;

/// <summary>AI 請求的來源：互動（畫面上有人在等）或背景（排程判讀、深析、報告重生）。</summary>
public enum AiLane
{
    Interactive,
    Background
}

/// <summary>
/// 全站 AI 請求閘道（docs/WEB-SPEC.md §9.10「AI 呼叫節流」）：地端模型一次只消化得了少數請求，
/// 每個 <see cref="AIService"/> 實例各自序列化擋不住「多個實例同時打」。所有實例共用這一道。
/// </summary>
public interface IAiRequestGate
{
    /// <summary>
    /// 取得一次 AI 請求的許可，Dispose 回傳值即歸還。取消會穿透（不佔許可）；
    /// 互動車道排不到時擲 <see cref="TimeoutException"/>，呼叫端照一般 AI 失敗降級。
    /// </summary>
    Task<IDisposable> EnterAsync(AiLane lane, CancellationToken ct);

    /// <summary>
    /// 互動車道排隊的上限：<see cref="AIService"/> 在自己的實例佇列也套同一個上限（閘道在佇列之後取，
    /// 只限閘道等於沒限——畫面上的人會在佇列裡等到前一個問題答完）。不節流時為無限。
    /// </summary>
    TimeSpan InteractiveMaxWait { get; }
}

/// <summary>不節流（測試與未接 DI 的呼叫端）：行為等同只有各實例自己的序列化。</summary>
public sealed class NullAiRequestGate : IAiRequestGate
{
    public static readonly NullAiRequestGate Instance = new();

    private static readonly Task<IDisposable> NoLease = Task.FromResult<IDisposable>(new Nothing());

    public Task<IDisposable> EnterAsync(AiLane lane, CancellationToken ct) => NoLease;

    public TimeSpan InteractiveMaxWait => Timeout.InfiniteTimeSpan;

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// 互動優先的兩車道閘道：
/// - 互動：同時最多 <see cref="InteractiveLimit"/> 條，不等背景；排隊超過上限就放棄（使用者不會一直等）。
/// - 背景：同時最多「AI 併發數」條（每次取許可時重讀設定）；有互動在途時不開新請求，
///   最多讓 <c>backgroundMaxYield</c>，之後照常放行，連續互動也不會讓背景餓死。已在途的背景請求不中斷。
/// </summary>
public sealed class AiRequestGate : IAiRequestGate
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const int InteractiveLimit = 2;
    public static readonly TimeSpan DefaultInteractiveMaxWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultBackgroundMaxYield = TimeSpan.FromSeconds(30);

    /// <summary>等待中至多隔這麼久重看一次（背景上限改設定時不必等別人歸還許可）。</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    private readonly Func<int> _backgroundLimit;
    private readonly TimeSpan _interactiveMaxWait;
    private readonly TimeSpan _backgroundMaxYield;
    private readonly object _lock = new();
    private int _interactive;
    private int _background;
    private TaskCompletionSource _changed = NewSignal();

    /// <param name="backgroundLimit">背景併發上限（AI 併發數設定），夾在 1～8</param>
    public AiRequestGate(Func<int> backgroundLimit, TimeSpan? interactiveMaxWait = null, TimeSpan? backgroundMaxYield = null)
    {
        _backgroundLimit = backgroundLimit;
        _interactiveMaxWait = interactiveMaxWait ?? DefaultInteractiveMaxWait;
        _backgroundMaxYield = backgroundMaxYield ?? DefaultBackgroundMaxYield;
    }

    public TimeSpan InteractiveMaxWait => _interactiveMaxWait;

    /// <summary>互動排隊逾上限的訊息（實例佇列與閘道共用同一句）。</summary>
    public static string InteractiveTimeoutMessage(TimeSpan maxWait) =>
        $"AI 互動請求排隊超過 {maxWait.TotalSeconds:0.#} 秒，請稍後再試。";

    public async Task<IDisposable> EnterAsync(AiLane lane, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var waitLogged = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // 設定讀取（可能碰 DB）放在鎖外
            var backgroundLimit = lane == AiLane.Background ? Math.Clamp(_backgroundLimit(), 1, 8) : 0;
            var waited = Stopwatch.GetElapsedTime(started);
            TimeSpan wait;
            Task changed;
            lock (_lock)
            {
                if (lane == AiLane.Interactive)
                {
                    if (_interactive < InteractiveLimit)
                    {
                        _interactive++;
                        return new Lease(this, lane);
                    }
                    wait = _interactiveMaxWait - waited;
                    if (wait <= TimeSpan.Zero)
                        throw new TimeoutException(InteractiveTimeoutMessage(_interactiveMaxWait));
                }
                else
                {
                    var yielding = _interactive > 0 && waited < _backgroundMaxYield;
                    if (!yielding && _background < backgroundLimit)
                    {
                        _background++;
                        return new Lease(this, lane);
                    }
                    wait = yielding ? _backgroundMaxYield - waited : RecheckInterval;
                }
                changed = _changed.Task;
            }

            if (!waitLogged)
            {
                waitLogged = true;
                Log.Info("AI 請求等待閘道（{Lane}）：互動在途或已達併發上限", lane);
            }
            await Task.WhenAny(changed, Task.Delay(wait < RecheckInterval ? wait : RecheckInterval, ct)).ConfigureAwait(false);
        }
    }

    private void Release(AiLane lane)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (lane == AiLane.Interactive) _interactive--;
            else _background--;
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lease(AiRequestGate gate, AiLane lane) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(lane);
        }
    }
}
