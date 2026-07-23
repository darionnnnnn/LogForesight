namespace LogForesight.Web.Services;

/// <summary>
/// 由問題層級的結案狀態推導風險日的處理狀態（方案 B 的核心規則，單點定義）。
///
/// 規則刻意在這裡寫一次，讓詳情頁、問題清單、儀表板待辦算出同一個答案——
/// 這正是先前「清單全是未處理、儀表板待辦卻 0」那類 bug 的防線：語意分散就會漂移。
/// </summary>
public static class DayHandlingDerivation
{
    public readonly record struct DayProgress(int Total, int Closed, string DayStatus)
    {
        /// <summary>日層級是否未結案（待辦與逾期的依據）</summary>
        public bool IsUnresolved => HandlingStatuses.Unresolved.Contains(DayStatus);
    }

    /// <summary>
    /// 推導單日狀態：
    ///   - 有問題且全部結案 → resolved
    ///   - 有問題結案但未全結 → in_progress（已開始處理）
    ///   - 沒有任何問題被標記 → 退回日層級狀態（沒有問題可標時的 fallback，也相容舊資料）
    /// </summary>
    public static DayProgress Derive(
        IReadOnlyCollection<LogIssueSignature> issues,
        IReadOnlyCollection<IssueHandling> issueHandlings,
        string? dayLevelStatus)
    {
        var closedKeys = issueHandlings
            .Where(h => IssueHandlingStatuses.IsClosed(h.Status))
            .Select(h => h.IssueKey)
            .ToHashSet(StringComparer.Ordinal);

        var total = issues.Count;
        var closed = issues.Count(i => closedKeys.Contains(IssueSignatureKey.For(i)));

        string status;
        if (total > 0 && closed == total) status = HandlingStatuses.Resolved;
        else if (closed > 0) status = HandlingStatuses.InProgress;
        else status = string.IsNullOrEmpty(dayLevelStatus) ? HandlingStatuses.Open : dayLevelStatus;

        return new DayProgress(total, closed, status);
    }
}
