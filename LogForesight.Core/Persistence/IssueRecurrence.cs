namespace LogForesight.Core.Persistence;

/// <summary>
/// 一件案件的復發判定結果（<see cref="IssueRecurrence.Evaluate"/>）。
/// </summary>
/// <param name="IsRecurrence">上一件以「已處理」結案且結案日落在回看窗內</param>
/// <param name="Count">復發次數 N：建立於本件之前、以「已處理」結案、結案日落在回看窗內的案件數</param>
/// <param name="Occurrence">同主機同問題的第幾次（建立在本件之前、未取消的案件數＋1，從 1 起算）</param>
/// <param name="Previous">上一件（本件之前最近建立、已結案、未取消的案件）；沒有為 null</param>
public sealed record RecurrenceInfo(bool IsRecurrence, int Count, int Occurrence, IssueCase? Previous)
{
    /// <summary>沒有任何前案（或問題欄解析不出）時的結果</summary>
    public static readonly RecurrenceInfo None = new(false, 0, 1, null);
}

/// <summary>
/// 復發的唯一判定點（讀取時推導，不存欄位）：單頭、成員、我的交辦、管理者提醒都呼叫這裡，不得另寫第二份比較。
///
/// **同一個問題**＝同主機（<see cref="HostNameKey"/>）、同 Source＋EventId，與派工延續性
/// （<see cref="DispatchContext.ContinuityHandlerFor"/>）同一個口徑；回看天數共用 <see cref="DispatchContext.ContinuityDays"/>。
///
/// **回看窗的比較方式**：以日曆日差計——(本件建立日 − 前案結案日) 介於 0 到 <see cref="WindowDays"/> 天（含兩端）。
/// 第 30 天算、第 31 天不算；前案結案日晚於本件建立日（不應發生）不算。
///
/// **取消交辦的案件不參與**（上一件、復發次數與「第 N 次」都略過）：取消不是處理人對問題的結論。
/// 不處理類（不處理、誤報、已知雜訊）結案的案件算「上一件」但不計入 <see cref="RecurrenceInfo.Count"/>，
/// 且上一件是不處理類時不算復發（即使更早有已處理）。
/// </summary>
public static class IssueRecurrence
{
    /// <summary>回看天數（與派工延續性同一個常數）</summary>
    public const int WindowDays = DispatchContext.ContinuityDays;

    /// <summary>前案結案日是否落在本件建立日往前 <see cref="WindowDays"/> 天內（日曆日差，含兩端）</summary>
    public static bool IsWithinWindow(DateTime createdAt, DateTime closedAt)
    {
        var days = (createdAt.Date - closedAt.Date).Days;
        return days >= 0 && days <= WindowDays;
    }

    /// <summary>
    /// 對 <paramref name="targets"/> 逐件判定。<paramref name="history"/> 是同主機同問題的前案候選
    /// （通常由 <c>IIssueCaseStore.GetClosedHistory</c> 一次取回，可含多件、多個問題；未結案與取消的會被略過）。
    /// 回傳以 CaseId 為鍵；每件 target 一定有結果。
    /// </summary>
    public static Dictionary<string, RecurrenceInfo> Evaluate(IEnumerable<IssueCase> targets, IEnumerable<IssueCase> history)
    {
        var byGroup = new Dictionary<(string Host, string Source, int EventId), List<IssueCase>>();
        foreach (var h in history)
        {
            if (h.ClosedAt == null || h.Cancelled || GroupOf(h) is not { } key) continue;
            if (!byGroup.TryGetValue(key, out var list)) byGroup[key] = list = new List<IssueCase>();
            list.Add(h);
        }

        var result = new Dictionary<string, RecurrenceInfo>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (GroupOf(target) is not { } group || !byGroup.TryGetValue(group, out var candidates))
            {
                result[target.CaseId] = RecurrenceInfo.None;
                continue;
            }

            var prior = candidates
                .Where(p => p.CreatedAt < target.CreatedAt && !string.Equals(p.CaseId, target.CaseId, StringComparison.Ordinal))
                .ToList();
            if (prior.Count == 0)
            {
                result[target.CaseId] = RecurrenceInfo.None;
                continue;
            }

            var previous = prior.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.CaseId, StringComparer.Ordinal).First();
            var count = prior.Count(p => p.Status == IssueHandlingStatuses.Resolved && IsWithinWindow(target.CreatedAt, p.ClosedAt!.Value));
            var isRecurrence = previous.Status == IssueHandlingStatuses.Resolved && IsWithinWindow(target.CreatedAt, previous.ClosedAt!.Value);
            result[target.CaseId] = new RecurrenceInfo(isRecurrence, count, prior.Count + 1, previous);
        }
        return result;
    }

    /// <summary>問題分組鍵：（主機鍵, 來源鍵, EventId）；問題欄解析不出的案件不分組</summary>
    private static (string Host, string Source, int EventId)? GroupOf(IssueCase c) =>
        c.SourceName == null || c.EventId == null
            ? null
            : (HostNameKey.Of(c.HostName), WorkOrderIssueKey.SourceKeyOf(c.SourceName), c.EventId.Value);
}
