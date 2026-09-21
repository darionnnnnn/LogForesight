namespace LogForesight.Web.Services;

/// <summary>讀取側靜音排除條件的來源（回饋第 47 輪批次 B-2a）。</summary>
public interface IIssueExclusionSource
{
    /// <summary>
    /// 目前生效的靜音排除條件。呼叫端在同一次請求／同一個方法呼叫內**只取一次**，再傳給該次的所有查詢——
    /// 各查詢各取一次的話，跨午夜或設定變更時同一頁會用到兩份規則。
    /// </summary>
    IssueExclusion Current();
}

/// <summary>
/// <see cref="IIssueExclusionSource"/> 的實作（Singleton）：以 (資料版本戳, 今天) 為鍵快取一份
/// <see cref="IssueExclusion"/>，鍵變了才重讀問題負責與靜音。
///
/// **內容與使用者無關**：靜音是全站同一份設定（問題負責與靜音），不含任何授權維度，
/// 所以快取鍵不需要可見主機集合或 userId。設定靜音／解除走非 GET API，
/// 由 <c>DataVersionStampPolicy</c> 推進版本戳；換日由 <c>today</c> 維度涵蓋。
/// </summary>
public sealed class IssueExclusionProvider : IIssueExclusionSource
{
    private sealed record Snapshot(long Version, DateTime Day, IssueExclusion Exclusion);

    private readonly IIssueOwnerStore _issueOwners;
    private readonly DataVersionStamp _stamp;
    private readonly Func<DateTime> _today;
    private readonly object _lock = new();
    private volatile Snapshot? _snapshot;

    public IssueExclusionProvider(IIssueOwnerStore issueOwners, DataVersionStamp stamp, Func<DateTime> today)
    {
        _issueOwners = issueOwners;
        _stamp = stamp;
        _today = today;
    }

    public IssueExclusion Current()
    {
        var version = _stamp.Current;
        var day = _today().Date;

        // 命中路徑不進鎖（同 EfIssueAggregateQuery.AliasIndex 的理由）
        var snapshot = _snapshot;
        if (snapshot != null && snapshot.Version == version && snapshot.Day == day) return snapshot.Exclusion;

        lock (_lock)
        {
            var current = _snapshot;
            if (current == null || current.Version != version || current.Day != day)
            {
                // 配上去的是進來時讀到的版本：讀取與建立之間若又被推進，下次比對不相等、多重建一次，
                // 是安全的失敗方向（反過來會讓舊內容一路命中到下次寫入）
                current = new Snapshot(version, day, IssueExclusion.From(_issueOwners.GetAll(), day));
                _snapshot = current;
            }
            return current.Exclusion;
        }
    }
}
