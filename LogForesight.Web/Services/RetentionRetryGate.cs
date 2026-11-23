namespace LogForesight.Web.Services;

/// <summary>
/// 單獨保留清除（排程輪詢補做）的重試判定。失敗不記成「今天已完成」，
/// 但也不能每分鐘重打：失敗後至少隔 <see cref="RetryInterval"/> 才再試，
/// 當天首次之後最多再重試 <see cref="MaxRetriesPerDay"/> 次（含首次共 4 次執行），用完等隔天。
/// 判定是不碰真時鐘的純函式；排程服務只負責呼叫並以 <see cref="RetentionRetryState"/> 記錄結果。
/// </summary>
internal static class RetentionRetryGate
{
    /// <summary>失敗後多久才可重試</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    /// <summary>首次之後當天最多再重試幾次（總執行次數＝1＋此值）</summary>
    internal const int MaxRetriesPerDay = 3;

    /// <param name="failuresToday">今天已失敗的次數</param>
    /// <param name="lastFailureAt">今天最近一次失敗的時間（沒失敗過為 null）</param>
    /// <param name="confirmedToday">今天是否已確認完成（成功，或已有其他路徑做過）</param>
    internal static bool ShouldRun(DateTime now, int failuresToday, DateTime? lastFailureAt, bool confirmedToday)
    {
        if (confirmedToday) return false;
        if (failuresToday <= 0 || lastFailureAt is null) return true;
        if (failuresToday > MaxRetriesPerDay) return false;
        return now - lastFailureAt.Value >= RetryInterval;
    }
}

/// <summary>單獨保留清除當天的重試狀態；跨日自動歸零。只在排程輪詢單一執行緒使用。</summary>
internal sealed class RetentionRetryState
{
    private DateTime _day;
    private int _failures;
    private DateTime? _lastFailureAt;
    private bool _confirmed;

    private void Roll(DateTime now)
    {
        if (_day == now.Date) return;
        _day = now.Date;
        _failures = 0;
        _lastFailureAt = null;
        _confirmed = false;
    }

    internal bool ShouldRun(DateTime now)
    {
        Roll(now);
        return RetentionRetryGate.ShouldRun(now, _failures, _lastFailureAt, _confirmed);
    }

    internal void RecordSuccess(DateTime now)
    {
        Roll(now);
        _confirmed = true;
    }

    internal void RecordFailure(DateTime now)
    {
        Roll(now);
        _failures++;
        _lastFailureAt = now;
    }
}
