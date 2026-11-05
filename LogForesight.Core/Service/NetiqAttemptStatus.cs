namespace LogForesight.Core.Service;

/// <summary>
/// NetIQ 主機日取數狀態與「要不要自動重查」的唯一判定。狀態字串寫進帳本與父紀錄，
/// 重查清單散在多處的話，新增一個狀態就會有地方漏掉（帳本拒寫、預覽回 409、總表顯示錯色）。
/// </summary>
public static class NetiqAttemptStatus
{
    public const string Running = "running";
    public const string Success = "success";
    public const string Partial = "partial";
    public const string RetainedIncomplete = "retained-incomplete";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    /// <summary>沒有舊紀錄、來源回 0 筆且未截斷：可能是真安靜，也可能是 Sentinel 暫時沒資料。不寫父紀錄、下一趟再查一次，第二次仍空才定案。</summary>
    public const string EmptyOnce = "empty-once";

    /// <summary>每天自動重查的上限（失敗、停止、空資料）：隔天歸零，服務恢復後會自動補回。</summary>
    public const int MaxAutoRetriesPerDay = 2;

    /// <summary>截斷類的自動重查總上限：持續截斷隔天重查結果也一樣，只耗 Sentinel 查詢。</summary>
    public const int MaxTruncatedAutoRetries = 2;

    internal static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        { Running, Success, Partial, RetainedIncomplete, Failed, Stopped, EmptyOnce };

    /// <summary>父紀錄上的狀態值得重查（父紀錄的停止已轉成失敗，空資料不寫父紀錄）。</summary>
    public static bool ParentNeedsRetry(string? status) => status is Failed or Running or Partial or RetainedIncomplete;

    /// <summary>帳本最新狀態值得重查（補跑預覽與排程續跑共用）。</summary>
    public static bool LedgerNeedsRetry(string? status) => status is Failed or Running or Partial or RetainedIncomplete or Stopped or EmptyOnce;

    private static bool IsTruncated(string status) => status is Partial or RetainedIncomplete;

    /// <summary>
    /// 自動重查是否已達上限（只限制排程；手動補跑不受限）。
    /// 失敗類按日計：憑證過期或服務中斷一整晚不該讓那幾天永遠補不回來；截斷類累計：重查結果不會變。
    /// </summary>
    public static bool AutoRetryExhausted(NetiqSourceAttempt.AttemptHistory history) =>
        IsTruncated(history.LatestStatus)
            ? history.TruncatedRuns >= MaxTruncatedAutoRetries
            : history.RetryRunsToday >= MaxAutoRetriesPerDay;

    /// <summary>這一趟的結果算一次「需要重查」（同一趟可能寫多筆回執，由 <see cref="NetiqSourceAttempt.Receipt"/> 以趟去重）。</summary>
    internal static bool CountsAsAttempt(string status) => status is not (Running or Success);

    internal static bool CountsAsTruncated(string status) => IsTruncated(status);
}
