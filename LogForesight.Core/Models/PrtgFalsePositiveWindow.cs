namespace LogForesight.Core.Models;

/// <summary>
/// PRTG 問題「標記誤報」30 日效期判定窗口。
/// 只對 PRTG 問題（LogName == "PRTG"）生效。
/// </summary>
public static class PrtgFalsePositiveWindow
{
    public const int Days = 30;

    /// <summary>
    /// 該主機該問題的逐日標記中，找「日期 ≤ occurrenceDate」的最近一筆明確標記；
    /// 若它是 FalsePositive 且 occurrenceDate ≤ 該標記日期 + Days → 回傳那筆標記（視同誤報），否則 null。
    /// 只對 PRTG 問題生效（issueKey 的 LogName 為 PrtgFindingMapper.PrtgLogName）；非 PRTG 一律 null。
    /// </summary>
    public static IssueHandling? Covering(string? issueKey, DateTime occurrenceDate, IEnumerable<IssueHandling> handlingsOfThisHostIssue)
    {
        if (string.IsNullOrEmpty(issueKey)) return null;

        var parsed = IssueSignatureKey.TryParseFull(issueKey);
        if (parsed == null || !string.Equals(parsed.Value.LogName, Service.PrtgFindingMapper.PrtgLogName, StringComparison.OrdinalIgnoreCase))
            return null;

        var occDate = occurrenceDate.Date;
        var latest = handlingsOfThisHostIssue
            .Where(h => !string.IsNullOrWhiteSpace(h.Status)
                     && h.Date.Date <= occDate
                     && IssueSignatureKeyComparer.Instance.Equals(h.IssueKey, issueKey))
            .OrderByDescending(h => h.Date.Date)
            .ThenByDescending(h => h.UpdatedAt)
            .FirstOrDefault();

        if (latest == null) return null;

        if (string.Equals(latest.Status, IssueHandlingStatuses.FalsePositive, StringComparison.OrdinalIgnoreCase))
        {
            if (occDate <= latest.Date.Date.AddDays(Days))
                return latest;
        }

        return null;
    }
}
