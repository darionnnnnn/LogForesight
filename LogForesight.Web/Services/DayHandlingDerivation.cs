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
    /// <param name="unhandledSeverities">
    /// 未處理計算納入哪些嚴重度（「系統管理 > 設定」頁維護，見 <see cref="SystemSettings.UnhandledSeverities"/>）。
    /// 未列在此集合的嚴重度，若從未被明確標記過，視同「不處理（預設）」——不計入 Total/Closed，
    /// 該問題本身也不會阻擋這一天推導成 resolved。**明確標記過的問題（含 open）一律尊重使用者的判斷，
    /// 不受這個集合影響**：曾標成結案的照樣算結案，曾被「調回未處理」的照樣算未結案。
    /// </param>
    public static DayProgress Derive(
        IReadOnlyCollection<LogIssueSignature> issues,
        IReadOnlyCollection<IssueHandling> issueHandlings,
        string? dayLevelStatus,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var handledKeys = issueHandlings.Select(h => h.IssueKey).ToHashSet(StringComparer.Ordinal);
        var counted = issues
            .Where(i => unhandledSeverities.Contains(i.Severity) || handledKeys.Contains(IssueSignatureKey.For(i)))
            .ToList();

        var closedKeys = issueHandlings
            .Where(h => IssueHandlingStatuses.IsClosed(h.Status))
            .Select(h => h.IssueKey)
            .ToHashSet(StringComparer.Ordinal);

        var total = counted.Count;
        var closed = counted.Count(i => closedKeys.Contains(IssueSignatureKey.For(i)));

        string status;
        if (total > 0 && closed == total) status = HandlingStatuses.Resolved;
        else if (closed > 0) status = HandlingStatuses.InProgress;
        else status = string.IsNullOrEmpty(dayLevelStatus) ? HandlingStatuses.Open : dayLevelStatus;

        return new DayProgress(total, closed, status);
    }
}
