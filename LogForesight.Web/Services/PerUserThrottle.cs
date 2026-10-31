namespace LogForesight.Web.Services;

/// <summary>
/// AI 端點的每使用者節流（Singleton）：同一使用者在視窗內最多 N 次，行程內滑動窗口。
/// 重啟即歸零——這只是防手滑連按與濫用外部 AI 額度，不是安全邊界；全站併發另由 IAiRequestGate 負責。
/// 兩個具名實例（<see cref="ManualAiThrottle"/>、<see cref="AutoAiThrottle"/>）數字寫死為常數，不進 SystemSettings。
/// </summary>
public class PerUserThrottle
{
    private readonly int _maxCalls;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private readonly Dictionary<long, Queue<DateTime>> _calls = new();

    public PerUserThrottle(int maxCalls, TimeSpan window)
    {
        if (maxCalls < 1) throw new ArgumentOutOfRangeException(nameof(maxCalls));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _maxCalls = maxCalls;
        _window = window;
    }

    /// <summary>還在額度內就記一次並回 true；超過回 false（不記）</summary>
    public bool TryAcquire(long userId, DateTime nowUtc) => TryAcquire(userId, nowUtc, out _);

    /// <summary>
    /// 同上；被拒絕時 <paramref name="retryAfterSeconds"/> 為最舊一筆離開視窗的剩餘秒數（至少 1），通過時為 0。
    /// </summary>
    public bool TryAcquire(long userId, DateTime nowUtc, out int retryAfterSeconds)
    {
        lock (_lock)
        {
            // 順手清掉整個窗口都過期的使用者，字典不隨歷來使用者數無限成長
            foreach (var key in _calls.Where(kv => kv.Value.Count == 0 || nowUtc - kv.Value.Last() >= _window)
                         .Select(kv => kv.Key).ToList())
                _calls.Remove(key);

            if (!_calls.TryGetValue(userId, out var queue))
            {
                queue = new Queue<DateTime>();
                _calls[userId] = queue;
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
