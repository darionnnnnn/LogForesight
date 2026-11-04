namespace LogForesight.Core.Service;

// 逐顆 historicdata 的失敗處理放在這個檔案，原因只有一個：PRTG 掛掉或限流時，數值取數是「數量大、逐顆、
// 沒有任何節制」的那一路（表格分頁已有 PrtgTablePager 的 2 次重試與請求配額，不在這裡處理）。
// 這裡拆成兩個彼此獨立、可單測的小類：
//   PrtgValueFetchRetryPolicy：只管「數字」——暫停序列、誤差、連續失敗與總暫停的停損門檻、冷卻長度；
//   PauseGate：只管「機制」——一次拉起、所有工作在下一顆送出前等它放行，不疊加。
// 兩者都不碰 PrtgClient、資料庫與 console，PrtgFetchService.FetchValuesAsync 負責把它們串起來。
// 門檻全是常數而不是設定鍵：沒有人需要在畫面上調它，新增沒有消費端的設定是本專案紅線。

/// <summary>
/// 數值取數的重試政策：暫停序列（15→60→180 秒，第 4 次起維持 180）、0～20% 隨機誤差、
/// <c>Retry-After</c> 取較大者、成功一顆序列歸零，以及兩道停損（連續 10 顆可重試失敗、總暫停 15 分鐘）。
/// 非執行緒安全：同一次 <c>FetchValuesAsync</c> 的並行工作要用同一把鎖序列化對它的呼叫。
/// </summary>
internal sealed class PrtgValueFetchRetryPolicy
{
    /// <summary>暫停序列（暫定值）；第 4 次起沿用最後一個。</summary>
    internal static readonly IReadOnlyList<TimeSpan> PauseSequence =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180)];

    /// <summary>隨機誤差的上限比例：實際暫停＝序列值 ×（1＋0～0.2），避免多條工作同時恢復又同時打 PRTG。</summary>
    internal const double JitterFraction = 0.2;

    /// <summary>連續可重試失敗的次數（同一個閘內的只算一次，成功歸零）達此數就停損。正常情況總暫停上限會先到（約 20 分鐘）；這一道保險給暫停被短路或時鐘不走的情況。</summary>
    internal const int MaxConsecutiveFailures = 10;

    /// <summary>一次 <c>FetchValuesAsync</c> 的總暫停時間上限；加上下一次暫停會達到它就停損。</summary>
    internal static readonly TimeSpan MaxTotalPause = TimeSpan.FromMinutes(15);

    /// <summary>停損後同一個服務實例的冷卻時間：期間不送任何數值請求。</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly Func<double> _jitter;
    private int _streak;

    /// <param name="jitter">回傳 [0,1) 的亂數來源；測試注入固定值以取得確定的暫停長度，null＝<see cref="Random.Shared"/>。</param>
    internal PrtgValueFetchRetryPolicy(Func<double>? jitter = null)
    {
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    /// <summary>目前連續可重試失敗的顆數（成功一顆歸零；其他 4xx 與寫入失敗既不加也不歸零）。</summary>
    internal int ConsecutiveFailures { get; private set; }

    /// <summary>連續失敗是否已達停損門檻。</summary>
    internal bool ConsecutiveLimitReached => ConsecutiveFailures >= MaxConsecutiveFailures;

    /// <summary>
    /// 失敗值不值得等一下再試——唯一的判定來源是表格分頁的 <see cref="PrtgTablePager.IsRetryable"/>
    /// （逾時、連線失敗、5xx、429），兩條路徑不各寫一份。401／403／其他 4xx、憑證失敗不在其內。
    /// </summary>
    internal bool ShouldRetry(PrtgClientException ex) => PrtgTablePager.IsRetryable(ex);

    /// <summary>成功取到一顆：序列與連續失敗數歸零。</summary>
    internal void RecordSuccess()
    {
        _streak = 0;
        ConsecutiveFailures = 0;
    }

    /// <summary>記一次可重試失敗（閘已拉起時的失敗屬同一波，呼叫端不會記）。</summary>
    internal void RecordRetryableFailure() => ConsecutiveFailures++;

    /// <summary>
    /// 取得下一次暫停的長度並讓序列前進一格：序列值加 0～20% 誤差，與 <paramref name="retryAfter"/> 取較大者。
    /// <paramref name="retryAfter"/> 由 <c>PrtgClient</c> 從回應標頭讀出，這裡不解析字串。
    /// </summary>
    internal TimeSpan NextDelay(TimeSpan? retryAfter)
    {
        var baseDelay = PauseSequence[Math.Min(_streak, PauseSequence.Count - 1)];
        _streak++;
        var jitter = Math.Clamp(_jitter(), 0d, 1d);
        var delay = baseDelay + TimeSpan.FromTicks((long)Math.Round(baseDelay.Ticks * JitterFraction * jitter));
        return retryAfter is { } ra && ra > delay ? ra : delay;
    }

    /// <summary>
    /// 已經暫停了 <paramref name="alreadyPaused"/>（<see cref="PauseGate.TotalPaused"/>，以時鐘量得的實際長度），
    /// 再暫停 <paramref name="delay"/> 會不會讓總暫停達到上限。
    /// </summary>
    internal bool WouldReachTotalPauseLimit(TimeSpan alreadyPaused, TimeSpan delay) => alreadyPaused + delay >= MaxTotalPause;
}

/// <summary>
/// 共用暫停閘：任何一顆碰到可重試失敗就拉起，所有工作在下一顆送出前都要等它放行。
/// 閘已拉起時再拉起不會疊加（回傳進行中的那一個），所以多條並行工作同時失敗只會暫停一次。
/// 等待用注入的延遲函式——測試注入零等待或可控的等待，不必真的等 15 秒。
/// </summary>
internal sealed class PauseGate
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private TaskCompletionSource? _open;
    private CancellationTokenSource? _cts;
    private TimeSpan _totalPaused;

    /// <param name="delay">等待函式；測試注入零等待或可控的等待。</param>
    /// <param name="clock">量測實際暫停長度的時鐘（總暫停停損用）；測試注入的等待函式若要模擬時間流逝，就推進這個時鐘。</param>
    internal PauseGate(Func<TimeSpan, CancellationToken, Task> delay, TimeProvider? clock = null)
    {
        _delay = delay;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 到目前為止實際暫停的總長（每次暫停結束時以時鐘量得，不是排定的長度）。
    /// 量實際長度的原因：停損要看「真的被 PRTG 拖了多久」，而不是「打算等多久」。
    /// </summary>
    internal TimeSpan TotalPaused
    {
        get { lock (_sync) return _totalPaused; }
    }

    /// <summary>閘目前是否拉起（暫停進行中）。</summary>
    internal bool IsRaised
    {
        get { lock (_sync) return _open is { Task.IsCompleted: false }; }
    }

    /// <summary>等到閘放行；沒有暫停時立刻返回。<paramref name="ct"/> 取消時擲 <see cref="OperationCanceledException"/>。</summary>
    internal Task WaitAsync(CancellationToken ct)
    {
        Task pending;
        lock (_sync)
        {
            if (_open == null || _open.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
            pending = _open.Task;
        }
        return pending.WaitAsync(ct);
    }

    /// <summary>
    /// 拉起閘並開始等 <paramref name="delay"/>；不需要等待它的呼叫端可以直接丟掉回傳的工作，
    /// 下一顆送出前的 <see cref="WaitAsync"/> 會等。閘已拉起時回傳進行中的工作而不重新計時。
    /// </summary>
    internal Task RaiseAsync(TimeSpan delay, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_open is { Task.IsCompleted: false }) return _open.Task;

            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _open = open;
            _cts = cts;
            _ = RunPauseAsync(delay, open, cts);
            return open.Task;
        }
    }

    /// <summary>立刻放行並中止進行中的等待（停損時用：等待中的工作不必再睡完剩下的時間）。</summary>
    internal void Reset()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _open?.TrySetResult();
        }
    }

    private async Task RunPauseAsync(TimeSpan delay, TaskCompletionSource open, CancellationTokenSource cts)
    {
        var startedAt = _clock.GetUtcNow();
        try
        {
            await _delay(delay, cts.Token);
        }
        catch
        {
            // 取消（呼叫端取消或 Reset）與延遲函式的其他失敗都視為「暫停結束」：
            // 取消由等待者自己的令牌擲出，這裡不能讓一個壞掉的延遲函式把整批卡死在閘前。
        }
        finally
        {
            lock (_sync)
            {
                // 先記總長再放行：放行後第一個失敗的工作就會來問「已經暫停多久」
                var elapsed = _clock.GetUtcNow() - startedAt;
                if (elapsed > TimeSpan.Zero) _totalPaused += elapsed;
                if (ReferenceEquals(_cts, cts)) _cts = null;
            }
            open.TrySetResult();
            cts.Dispose();
        }
    }
}
