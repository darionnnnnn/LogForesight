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

    /// <summary>
    /// 強制格式（response_format）的啟用狀態記憶（第 70 輪 C-1）：跨 <see cref="AIService"/> 實例與跨呼叫共用，
    /// 掛在閘道上是因為閘道本來就是全站唯一、每個 <see cref="AIService"/> 建構點都必須帶的物件。
    /// </summary>
    AiGrammarMemory GrammarMemory { get; }

    /// <summary>
    /// 互動車道是否在冷卻中（D 批 A1／CU-4）：AI 網路層失敗（連線失敗、逾時）後的 <see cref="AiRequestGate.NetworkFailureCooldown"/> 內為 true。
    /// 冷卻期間互動入口（今日焦點、查詢歸納、判讀、整理說明、詢問 AI）不送請求、直接回 <see cref="AiRequestGate.CooldownMessage"/>，
    /// 不必每個人各等一次逾時；背景車道不看這個值（排程有自己的重試與退避）。預設實作恆為 false，測試替身不必跟著改。
    /// </summary>
    bool InteractiveCoolingDown => false;

    /// <summary>
    /// 回報一次網路層失敗。只有互動車道的失敗會開啟（或延長）冷卻：背景請求逾時多半是「這個 prompt 就是跑不完」，
    /// 不代表 AI 掛了。呼叫端負責分辨失敗種類（<see cref="AIService.IsNetworkFailure"/>），使用者取消不得回報。
    /// </summary>
    void ReportNetworkFailure(AiLane lane) { }

    /// <summary>回報一次成功：解除冷卻（任一車道的成功都證明 AI 通了）。</summary>
    void ReportSuccess() { }
}

/// <summary>不節流（僅供測試；正式碼的閘道參數一律必填）：行為等同只有各實例自己的序列化。</summary>
public sealed class NullAiRequestGate : IAiRequestGate
{
    public static readonly NullAiRequestGate Instance = new();

    private static readonly Task<IDisposable> NoLease = Task.FromResult<IDisposable>(new Nothing());

    public Task<IDisposable> EnterAsync(AiLane lane, CancellationToken ct) => NoLease;

    public TimeSpan InteractiveMaxWait => Timeout.InfiniteTimeSpan;

    /// <summary>每次存取回傳新實例：本類別是測試用的全域單例，不能讓測試彼此共用記憶狀態。</summary>
    public AiGrammarMemory GrammarMemory => new();

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

    /// <summary>網路層失敗後互動車道的冷卻時間（程式常數，不開設定）。</summary>
    public static readonly TimeSpan NetworkFailureCooldown = TimeSpan.FromSeconds(60);

    /// <summary>冷卻期間互動入口直接回的訊息。</summary>
    public const string CooldownMessage = "AI 暫時無法連線，請稍後再試。";

    /// <summary>等待中至多隔這麼久重看一次（背景上限改設定時不必等別人歸還許可）。</summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(1);

    private readonly Func<int> _backgroundLimit;
    private readonly TimeSpan _interactiveMaxWait;
    private readonly TimeSpan _backgroundMaxYield;
    private readonly object _lock = new();
    private int _interactive;
    private int _background;
    private TaskCompletionSource _changed = NewSignal();
    private readonly TimeProvider _time;
    // 冷卻到期時刻（UTC ticks；0＝沒有冷卻）。單一個時間戳，不是集合，沒有成長問題
    private long _cooldownUntilTicks;

    /// <param name="backgroundLimit">背景併發上限（AI 併發數設定），夾在 1～8</param>
    /// <param name="timeProvider">冷卻判定用的時鐘（「現在」一律走注入的 TimeProvider）；預設系統時鐘</param>
    public AiRequestGate(Func<int> backgroundLimit, TimeSpan? interactiveMaxWait = null, TimeSpan? backgroundMaxYield = null,
        TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _backgroundLimit = backgroundLimit;
        _interactiveMaxWait = interactiveMaxWait ?? DefaultInteractiveMaxWait;
        _backgroundMaxYield = backgroundMaxYield ?? DefaultBackgroundMaxYield;
    }

    public TimeSpan InteractiveMaxWait => _interactiveMaxWait;

    public bool InteractiveCoolingDown => _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _cooldownUntilTicks);

    public void ReportNetworkFailure(AiLane lane)
    {
        if (lane != AiLane.Interactive) return;
        var until = _time.GetUtcNow().UtcTicks + NetworkFailureCooldown.Ticks;
        long seen;
        // 只往後延、不往前縮：並發的失敗回報以最晚的到期時刻為準
        while ((seen = Interlocked.Read(ref _cooldownUntilTicks)) < until &&
               Interlocked.CompareExchange(ref _cooldownUntilTicks, until, seen) != seen) { }
        if (seen < until) Log.Warn("AI 互動請求網路層失敗，互動入口冷卻 {Seconds} 秒（冷卻期間不送請求、直接回 AI 暫時無法連線）", NetworkFailureCooldown.TotalSeconds);
    }

    public void ReportSuccess()
    {
        if (Interlocked.Exchange(ref _cooldownUntilTicks, 0) != 0) Log.Info("AI 請求成功，互動入口冷卻解除");
    }

    /// <summary>全站共用的強制格式記憶（本閘道是全站唯一實例）。</summary>
    public AiGrammarMemory GrammarMemory { get; } = new();

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
