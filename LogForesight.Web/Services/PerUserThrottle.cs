namespace LogForesight.Web.Services;

/// <summary>
/// 行程內滑動窗口節流的泛型基底（Singleton）：同一個鍵在視窗內最多 N 次。
/// 重啟即歸零。為避免以不重複的鍵灌爆記憶體，鍵的總數有上限（<c>maxKeys</c>，預設 10,000）：
/// 已達上限且是新鍵時拒絕並不記錄，既有鍵照常計數。
/// </summary>
public class SlidingWindowThrottle<TKey> where TKey : notnull
{
    public const int DefaultMaxKeys = 10_000;

    private readonly int _maxCalls;
    private readonly TimeSpan _window;
    private readonly int _maxKeys;
    private readonly object _lock = new();
    private readonly Dictionary<TKey, Queue<DateTime>> _calls = new();
    private DateTime _lastSweepUtc = DateTime.MinValue;

    public SlidingWindowThrottle(int maxCalls, TimeSpan window, int maxKeys = DefaultMaxKeys)
    {
        if (maxCalls < 1) throw new ArgumentOutOfRangeException(nameof(maxCalls));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        if (maxKeys < 1) throw new ArgumentOutOfRangeException(nameof(maxKeys));
        _maxCalls = maxCalls;
        _window = window;
        _maxKeys = maxKeys;
    }

    /// <summary>還在額度內就記一次並回 true；超過回 false（不記）</summary>
    public bool TryAcquire(TKey key, DateTime nowUtc) => TryAcquire(key, nowUtc, out _);

    /// <summary>
    /// 同上；被拒絕時 <paramref name="retryAfterSeconds"/> 為最舊一筆離開視窗的剩餘秒數（至少 1），通過時為 0。
    /// </summary>
    public bool TryAcquire(TKey key, DateTime nowUtc, out int retryAfterSeconds)
    {
        lock (_lock)
        {
            // 清掉整個窗口都過期的鍵，字典不隨歷來鍵數無限成長。每個視窗最多掃一次、鍵數已滿時立刻掃：
            // 匿名的 CSP 回報端點鍵數可到上限，每次呼叫都全掃會讓一個請求在鎖內做上百萬次比較
            if (_calls.Count >= _maxKeys || nowUtc - _lastSweepUtc >= _window)
            {
                foreach (var expired in _calls.Where(kv => kv.Value.Count == 0 || nowUtc - kv.Value.Last() >= _window)
                             .Select(kv => kv.Key).ToList())
                    _calls.Remove(expired);
                _lastSweepUtc = nowUtc;
            }

            if (!_calls.TryGetValue(key, out var queue))
            {
                if (_calls.Count >= _maxKeys)
                {
                    // 新鍵且鍵數已滿：拒絕且不記錄；最早能騰出位置的時間未知，保守回整個視窗
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(_window.TotalSeconds));
                    return false;
                }
                queue = new Queue<DateTime>();
                _calls[key] = queue;
            }
            while (queue.Count > 0 && nowUtc - queue.Peek() >= _window) queue.Dequeue();
            if (queue.Count >= _maxCalls)
            {
                var remaining = queue.Peek() + _window - nowUtc;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                return false;
            }
            queue.Enqueue(nowUtc);
            retryAfterSeconds = 0;
            return true;
        }
    }
}

/// <summary>
/// AI 端點的每使用者節流（Singleton）：同一使用者在視窗內最多 N 次，行程內滑動窗口。
/// 重啟即歸零——這只是防手滑連按與濫用外部 AI 額度，不是安全邊界；全站併發另由 IAiRequestGate 負責。
/// 兩個具名實例（<see cref="ManualAiThrottle"/>、<see cref="AutoAiThrottle"/>）數字寫死為常數，不進 SystemSettings。
/// </summary>
public class PerUserThrottle : SlidingWindowThrottle<long>
{
    public PerUserThrottle(int maxCalls, TimeSpan window) : base(maxCalls, window) { }
}

/// <summary>CSP 回報端點的每來源 IP 節流：每 IP 60 秒 10 次</summary>
public sealed class CspReportIpThrottle : SlidingWindowThrottle<string>
{
    public const int MaxCalls = 10;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public CspReportIpThrottle() : base(MaxCalls, Window) { }
}

/// <summary>CSP 回報去重：同一個「violated-directive＋blocked-uri」組合 60 秒只記 1 次</summary>
public sealed class CspReportDedupThrottle : SlidingWindowThrottle<string>
{
    public const int MaxCalls = 1;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public CspReportDedupThrottle() : base(MaxCalls, Window) { }
}

/// <summary>手動觸發的 AI 端點（tidy-note、interpret-issue、chat、help/ask）：每人 60 秒 6 次</summary>
public sealed class ManualAiThrottle : PerUserThrottle
{
    public const int MaxCalls = 6;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public ManualAiThrottle() : base(MaxCalls, Window) { }
}

/// <summary>進頁或換條件自動呼叫的 AI 端點（today-focus、query-summary）：每人 60 秒 30 次</summary>
public sealed class AutoAiThrottle : PerUserThrottle
{
    public const int MaxCalls = 30;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public AutoAiThrottle() : base(MaxCalls, Window) { }
}

/// <summary>AI 端點超限時的統一回應：429、碼 rate_limited、Retry-After 標頭</summary>
public static class AiRateLimit
{
    /// <summary>節流掛鉤：<see cref="BeforeCall"/> 交給服務層在「真的要呼叫 AI」時才執行</summary>
    public sealed class GateState
    {
        private readonly PerUserThrottle _throttle;
        private readonly long _userId;
        public GateState(PerUserThrottle throttle, long userId) { _throttle = throttle; _userId = userId; }

        public bool Rejected { get; private set; }
        public int RetryAfter { get; private set; }

        public bool BeforeCall()
        {
            if (_throttle.TryAcquire(_userId, DateTime.UtcNow, out var retryAfter)) return true;
            Rejected = true;
            RetryAfter = retryAfter;
            return false;
        }
    }

    public static GateState Gate(PerUserThrottle throttle, long userId) => new(throttle, userId);

    /// <summary>設定回應狀態與 Retry-After，回傳信封（動作的回傳型別不變）</summary>
    public static Models.ApiResponse<T> Reject<T>(HttpResponse? response, int retryAfterSeconds)
    {
        if (response != null)
        {
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return Models.ApiResponse<T>.Fail(Models.ApiErrorCodes.RateLimited, $"AI 使用太頻繁，請約 {retryAfterSeconds} 秒後再試");
    }
}
