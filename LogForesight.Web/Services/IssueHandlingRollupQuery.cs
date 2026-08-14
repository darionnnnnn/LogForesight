namespace LogForesight.Web.Services;

/// <summary>
/// 問題排行卡「處理概況」彙總（回饋十九輪批次A）：把 <see cref="IssueRankingBuilder.LookupRollup"/>
/// 需要的 <c>handlingByIssue</c> 接上——這個參數在 <see cref="IssueRankingBuilder.Build"/>
/// 早就備妥（§10.6：排除全部主機都已有結論的問題），但兩個正式呼叫端
/// （DashboardService／ReportService）從未真正傳過，一直是死碼，
/// <c>OpenHostCount</c>／<c>ResolvedHostCount</c> 因此恆為 0。
///
/// **每主機的三態判定與 <see cref="RecordListQueryService"/> 的依問題視角同一套規則**
/// （案件優先於逐日標記／觀察到期算未處理／未標記時依嚴重度預設是否需要處理）——
/// 這裡刻意複用同一份規則描述而非另外發明一套，否則儀表板與問題查詢頁對「這個問題
/// 有沒有結論」的判斷會對不上，正是外部審查點名的「三個畫面數字對不起來」那種缺陷。
///
/// 資料來源全部走真表：<see cref="IIssueAggregateQuery.LatestOccurrences"/> 取代整段期間的
/// blob 撈取，只查「這幾個問題、這些主機」的最近一次出現快照，處理狀態批次載入
/// （同 <see cref="RecordListQueryService"/> 既有的 N3 教訓：逐主機查會在大規模環境下
/// 退化成數千次個別查詢）。
/// </summary>
public class IssueHandlingRollupQuery
{
    private readonly IIssueAggregateQuery _aggregates;
    private readonly IHostStore _hosts;
    private readonly IIssueHandlingStore _issueHandlings;
    private readonly IIssueCaseStore _cases;
    private readonly ISystemSettingsStore _settings;

    public IssueHandlingRollupQuery(
        IIssueAggregateQuery aggregates, IHostStore hosts, IIssueHandlingStore issueHandlings,
        IIssueCaseStore cases, ISystemSettingsStore settings)
    {
        _aggregates = aggregates;
        _hosts = hosts;
        _issueHandlings = issueHandlings;
        _cases = cases;
        _settings = settings;
    }

    /// <summary>
    /// <paramref name="aggregates"/> 是呼叫端已經算好的問題排行（同一次查詢的結果，不重算）。
    /// 回傳以完整簽章為鍵，供 <see cref="IssueRankingBuilder.Build"/> 的內部彙總用。
    /// </summary>
    public IReadOnlyDictionary<string, IssueHandlingRollup> Build(
        IReadOnlyCollection<IssueAggregate> aggregates, DateTime from, DateTime to,
        IReadOnlyCollection<long>? visibleHostIds)
    {
        if (aggregates.Count == 0) return new Dictionary<string, IssueHandlingRollup>();

        var issues = aggregates.Select(a => (a.Source, a.EventId)).Distinct().ToList();
        var occurrences = _aggregates.LatestOccurrences(issues, from, to, visibleHostIds);
        if (occurrences.Count == 0) return new Dictionary<string, IssueHandlingRollup>();

        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);
        var hostNames = occurrences
            .Select(o => hostsById.TryGetValue(o.HostId, out var h) ? h.HostName : null)
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hostNames.Count == 0) return new Dictionary<string, IssueHandlingRollup>();

        var handlingsByHost = _issueHandlings.GetMany(hostNames, from, to)
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var openCasesByHost = _cases.GetMany(hostNames)
            .Where(c => c.ClosedAt == null)
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var openCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolvedCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var occurrence in occurrences)
        {
            if (!hostsById.TryGetValue(occurrence.HostId, out var host)) continue;

            var (isOpen, isResolved) = ResolveHostStatus(occurrence, host.HostName, openCasesByHost, handlingsByHost, unhandledSeverities);

            if (isOpen) openCount[occurrence.IssueKey] = openCount.GetValueOrDefault(occurrence.IssueKey) + 1;
            if (isResolved) resolvedCount[occurrence.IssueKey] = resolvedCount.GetValueOrDefault(occurrence.IssueKey) + 1;
        }

        return occurrences
            .Select(o => o.IssueKey)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                key => key,
                key => new IssueHandlingRollup(openCount.GetValueOrDefault(key), resolvedCount.GetValueOrDefault(key)),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// 單一主機對單一問題的處理概況——與 <c>RecordListQueryService.BuildIssueGroup</c>
    /// 逐主機迴圈內的判定逐位相同：案件優先；否則看最近一次出現當天的問題層級標記；
    /// 未標記時依嚴重度是否在「不處理」名單內預設為已處理。
    /// </summary>
    private static (bool IsOpen, bool IsResolved) ResolveHostStatus(
        HostIssueOccurrence occurrence, string hostName,
        IReadOnlyDictionary<string, List<IssueCase>> openCasesByHost,
        IReadOnlyDictionary<string, List<IssueHandling>> handlingsByHost,
        IReadOnlySet<IssueSeverity> unhandledSeverities)
    {
        var openCase = openCasesByHost.TryGetValue(hostName, out var cases)
            ? cases.FirstOrDefault(c => string.Equals(c.IssueKey, occurrence.IssueKey, StringComparison.Ordinal))
            : null;

        if (openCase != null)
        {
            // 觀察到期：問題仍在發生，計入未處理；否則案件進行中，既非未處理也非已處理
            var expired = IssueHandlingStatuses.IsObservationExpired(openCase.Status, openCase.DueDate, DateTime.Today);
            return (IsOpen: expired, IsResolved: false);
        }

        var handling = handlingsByHost.TryGetValue(hostName, out var rows)
            ? rows.FirstOrDefault(h =>
                h.Date.Date == occurrence.LastSeen.Date && string.Equals(h.IssueKey, occurrence.IssueKey, StringComparison.Ordinal))
            : null;

        if (handling != null && IssueHandlingStatuses.IsClosed(handling.Status)) return (false, true);
        if (handling != null && handling.Status is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Escalated) return (false, false);
        if (handling != null && IssueHandlingStatuses.IsObservationActive(handling.Status, handling.DueDate, DateTime.Today)) return (false, false);
        if (handling != null && IssueHandlingStatuses.IsObservationExpired(handling.Status, handling.DueDate, DateTime.Today)) return (true, false);
        if (!unhandledSeverities.Contains((IssueSeverity)occurrence.SeverityRank)) return (false, true);
        return (true, false);
    }
}
