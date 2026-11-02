using LogForesight.Core.Models;
using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>
/// 重新分析候選日挑選器：依指定重新分析模式（<see cref="RerunMode"/>）挑出窗口內已有紀錄、且符合過濾條件的主機日。
/// </summary>
internal static class RerunDateFinder
{
    public static List<DateTime> Find(
        IAnalysisRecordReader store, DateTime today,
        IIssueHandlingStore handlingStore,
        string hostName,
        int lookbackDays,
        RerunMode mode)
    {
        if (mode == RerunMode.None || lookbackDays <= 0)
        {
            return new List<DateTime>();
        }

        var candidateDates = Enumerable.Range(1, lookbackDays)
            .Select(offset => today.AddDays(-offset))
            .Where(date => store.HasRecord(date))
            .ToList();

        if (candidateDates.Count == 0 || mode == RerunMode.All)
        {
            return candidateDates.OrderBy(date => date).ToList();
        }

        var from = today.AddDays(-lookbackDays);
        var to = today.AddDays(-1);
        var handlings = handlingStore.GetMany(new[] { hostName }, from, to);

        var matchingHandlings = handlings
            .Where(h => string.Equals(h.HostName, hostName, StringComparison.OrdinalIgnoreCase))
            .ToLookup(h => h.Date.Date);

        return candidateDates
            .Where(date =>
            {
                var dayHandlings = matchingHandlings[date.Date];
                return mode switch
                {
                    RerunMode.Unhandled => !dayHandlings.Any(),
                    RerunMode.UnhandledAndAssigned => !dayHandlings.Any(h => IssueHandlingStatuses.IsClosed(h.Status)),
                    _ => true
                };
            })
            .OrderBy(date => date)
            .ToList();
    }
}
