namespace LogForesight.Web.Services;

/// <summary>
/// 批次載入處理狀態並逐筆用 <see cref="IssueGroupStatusResolver"/> 判定狀態的共用骨架
/// （回饋十九輪批次D）：<see cref="IssueHandlingRollupQuery"/>／<see cref="IssueTodoQuery"/>
/// 與批次H 的逾期摘要共用同一套「批次撈 handling／case → 逐筆判定」流程，不各自重寫一份
/// 批次載入的樣板碼——那正是 E0 抽 <see cref="IssueGroupStatusResolver"/> 之前踩過的教訓。
/// </summary>
public class OccurrenceStatusResolver
{
    private readonly IHostStore _hosts;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IIssueCaseStore _cases;
    private readonly ISystemSettingsStore _settings;

    public OccurrenceStatusResolver(
        IHostStore hosts, IIssueHandlingStore issueHandlings, IIssueCaseStore cases, ISystemSettingsStore settings)
    {
        _hosts = hosts;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _settings = settings;
    }

    /// <summary>
    /// <paramref name="from"/>／<paramref name="to"/> 只用來限縮 <c>lf_issue_handling</c> 的批次查詢範圍
    /// （逐日標記天然落在這個區間內），不是狀態判定本身的依據。
    /// </summary>
    public List<ResolvedOccurrence> Resolve(
        IReadOnlyCollection<HostIssueOccurrence> occurrences, DateTime from, DateTime to)
    {
        if (occurrences.Count == 0) return new List<ResolvedOccurrence>();

        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var hostNames = occurrences
            .Select(o => hostsById.TryGetValue(o.HostId, out var h) ? h.HostName : null)
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hostNames.Count == 0) return new List<ResolvedOccurrence>();

        var handlingsByHost = _issueHandlings.GetMany(hostNames, from, to)
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var openCasesByHost = _cases.GetMany(hostNames)
            .Where(c => c.ClosedAt == null)
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();
        var today = DateTime.Today;   // 觀察到期／逾期比對真實時鐘，不是分析錨點（見批次C 的既有教訓）

        var result = new List<ResolvedOccurrence>();
        foreach (var occurrence in occurrences)
        {
            if (!hostsById.TryGetValue(occurrence.HostId, out var host)) continue;

            var openCase = openCasesByHost.TryGetValue(host.HostName, out var cases)
                ? cases.FirstOrDefault(c => string.Equals(c.IssueKey, occurrence.IssueKey, StringComparison.Ordinal))
                : null;
            var handling = openCase == null && handlingsByHost.TryGetValue(host.HostName, out var rows)
                ? rows.FirstOrDefault(h =>
                    h.Date.Date == occurrence.LastSeen.Date && string.Equals(h.IssueKey, occurrence.IssueKey, StringComparison.Ordinal))
                : null;

            var status = IssueGroupStatusResolver.Resolve(
                openCase, handling, (IssueSeverity)occurrence.SeverityRank, unhandledSeverities, today);

            result.Add(new ResolvedOccurrence(occurrence, host, status, openCase, handling));
        }

        return result;
    }
}

/// <summary>單一 (存活主機, 完整簽章) 出現快照的處理概況判定結果</summary>
public sealed record ResolvedOccurrence(
    HostIssueOccurrence Occurrence, WebHost Host, HostIssueStatus Status, IssueCase? OpenCase, IssueHandling? Handling);
