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

        var queryFrom = PrtgFalsePositiveWindow.HandlingLookbackFrom(from);
        var handlingsByHost = _issueHandlings.GetMany(hostNames, queryFrom, to)
            .GroupBy(h => h.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var openCasesByHost = _cases.GetOpenMany(hostNames)
            .GroupBy(c => c.HostName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();
        var today = DateTime.Today;   // 觀察到期／逾期比對真實時鐘，不是分析錨點（見批次C 的既有教訓）

        // 每主機的查找表（案件：問題鍵；處理列：問題鍵＋日期）。同鍵多筆只收清單順序中的第一筆，
        // 與原本 FirstOrDefault 取到的相同；問題鍵比對仍由 IssueSignatureKeyComparer 決定。
        var casesIndex = new Dictionary<string, Dictionary<string, IssueCase>>(StringComparer.OrdinalIgnoreCase);
        var rowsIndex = new Dictionary<string, Dictionary<(DateTime Day, string Key), IssueHandling>>(StringComparer.OrdinalIgnoreCase);
        var keyComparer = IssueSignatureKeyComparer.Instance;

        var result = new List<ResolvedOccurrence>();
        foreach (var occurrence in occurrences)
        {
            if (!hostsById.TryGetValue(occurrence.HostId, out var host)) continue;

            IssueCase? openCase = null;
            if (openCasesByHost.TryGetValue(host.HostName, out var cases))
            {
                if (occurrence.IssueKey == null)
                    openCase = cases.FirstOrDefault(c => keyComparer.Equals(c.IssueKey, occurrence.IssueKey));
                else
                {
                    if (!casesIndex.TryGetValue(host.HostName, out var byKey))
                    {
                        byKey = new Dictionary<string, IssueCase>(keyComparer);
                        foreach (var c in cases)
                            if (c.IssueKey != null) byKey.TryAdd(c.IssueKey, c);
                        casesIndex[host.HostName] = byKey;
                    }
                    byKey.TryGetValue(occurrence.IssueKey, out openCase);
                }
            }
            var rows = openCase == null && handlingsByHost.TryGetValue(host.HostName, out var hostRows)
                ? hostRows
                : null;
            IssueHandling? handling = null;
            if (rows != null)
            {
                if (occurrence.IssueKey == null)
                    handling = rows.FirstOrDefault(h =>
                        h.Date.Date == occurrence.LastSeen.Date && keyComparer.Equals(h.IssueKey, occurrence.IssueKey));
                else
                {
                    if (!rowsIndex.TryGetValue(host.HostName, out var byDayKey))
                    {
                        byDayKey = new Dictionary<(DateTime Day, string Key), IssueHandling>(new DayKeyComparer());
                        foreach (var h in rows)
                            if (h.IssueKey != null) byDayKey.TryAdd((h.Date.Date, h.IssueKey), h);
                        rowsIndex[host.HostName] = byDayKey;
                    }
                    byDayKey.TryGetValue((occurrence.LastSeen.Date, occurrence.IssueKey), out handling);
                }
            }

            var inheritedFalsePositive = false;
            if (handling == null && rows != null)
            {
                var covering = PrtgFalsePositiveWindow.Covering(occurrence.IssueKey, occurrence.LastSeen, rows);
                if (covering != null)
                {
                    handling = covering;
                    inheritedFalsePositive = true;
                }
            }

            var status = IssueGroupStatusResolver.Resolve(
                openCase, handling, LegacySeverityRank.ToSeverity(occurrence.SeverityRank), unhandledSeverities, today);

            result.Add(new ResolvedOccurrence(occurrence, host, status, openCase, handling, inheritedFalsePositive));
        }

        return result;
    }

    private sealed class DayKeyComparer : IEqualityComparer<(DateTime Day, string Key)>
    {
        public bool Equals((DateTime Day, string Key) a, (DateTime Day, string Key) b) =>
            a.Day == b.Day && IssueSignatureKeyComparer.Instance.Equals(a.Key, b.Key);
        public int GetHashCode((DateTime Day, string Key) k) =>
            HashCode.Combine(k.Day, IssueSignatureKeyComparer.Instance.GetHashCode(k.Key));
    }
}

/// <summary>單一 (存活主機, 完整簽章) 出現快照的處理概況判定結果</summary>
public sealed record ResolvedOccurrence(
    HostIssueOccurrence Occurrence, WebHost Host, HostIssueStatus Status, IssueCase? OpenCase, IssueHandling? Handling,
    bool InheritedFalsePositive);
