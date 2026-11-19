namespace LogForesight.Web.Filters;

/// <summary>登入端點並行上限的政策名稱與數值（Program 註冊、AuthController 套用）</summary>
public static class LoginRateLimit
{
    public const string PolicyName = "login-concurrency";

    /// <summary>同時進行中的登入驗證數：AD bind 是同步呼叫，超過這個數量就排隊</summary>
    public const int ConcurrentLogins = 8;

    /// <summary>排隊上限：再多就直接回 429，不讓請求無限堆積</summary>
    public const int QueuedLogins = 32;
}
