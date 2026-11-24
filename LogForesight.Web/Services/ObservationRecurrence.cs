using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Web.Services;

/// <summary>
/// 觀察期間再發生日數 K 的唯一計算點（交辦單成員清單與單頭的「到期且沒有再發生」共用）。
///
/// **K**＝觀察開始日（不含）到今天（含）之間，該主機該問題有出現紀錄的日數（以成員的完整問題簽章比對）。
/// **觀察開始日**＝該成員最近一次被回覆為觀察中的日期，取自逐日處理列（<see cref="IIssueHandlingStore.GetObservationStarts"/>）；
/// 逐日列還沒寫（待背景同步）或沒有時退回案件最後更新時間（回覆時案件的更新時間就是回覆時間）。
/// **無資料**（回傳 null）：觀察開始日不早於今天（還沒有可觀察的日子）、主機已不存在，或這段期間該主機沒有任何分析紀錄
/// （主機日都不存在，不能當成「沒有再發生」）。
/// 查詢形狀固定：觀察開始日一次、問題出現日一次、有零命中者才另查一次主機日存在與否——不逐成員查。
/// </summary>
internal static class ObservationRecurrence
{
    /// <summary>
    /// 對 <paramref name="members"/> 中「進行中且觀察中」的成員逐件計算；其他成員不在結果裡。
    /// <paramref name="handlings"/> 為 null（未注入）時無法取得觀察開始日，所有成員回無資料。
    /// </summary>
    public static Dictionary<string, int?> Compute(
        IReadOnlyCollection<IssueCase> members, DateTime today, IIssueHandlingStore? handlings,
        IAnalysisRecordQuery records, IReadOnlyCollection<WebHost> hosts)
    {
        var observing = members
            .Where(c => c.ClosedAt == null && c.Status == IssueHandlingStatuses.Observing)
            .ToList();
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        if (observing.Count == 0) return result;
        if (handlings == null)
        {
            foreach (var c in observing) result[c.CaseId] = null;
            return result;
        }

        var starts = handlings.GetObservationStarts(observing.Select(c => c.CaseId).ToList());
        DateTime StartOf(IssueCase c) => (starts.TryGetValue(c.CaseId, out var s) ? s : c.UpdatedAt).Date;

        var hostByKey = new Dictionary<string, HostKey>(StringComparer.Ordinal);
        foreach (var h in hosts) hostByKey.TryAdd(HostNameKey.Of(h.HostName), HostKey.Of(h));

        // 能計算的成員：主機還在、觀察開始日早於今天（窗口 (開始日, 今天] 至少有一天）
        var computable = new List<(IssueCase Case, HostKey Host, DateTime Start)>();
        foreach (var c in observing)
        {
            result[c.CaseId] = null;
            if (hostByKey.TryGetValue(HostNameKey.Of(c.HostName), out var host) && StartOf(c) < today.Date)
                computable.Add((c, host, StartOf(c)));
        }
        if (computable.Count == 0) return result;

        var hostKeys = computable.Select(x => x.Host).GroupBy(h => h.HostId).Select(g => g.First()).ToList();
        var issueKeys = computable.Select(x => x.Case.IssueKey).Distinct(StringComparer.Ordinal).ToList();
        var daysByHostIssue = records.IssueDaysFor(hostKeys, issueKeys)
            .GroupBy(h => (h.HostId, h.IssueKey))
            .ToDictionary(g => g.Key, g => g.Select(h => h.Date.Date).ToHashSet());

        var zero = new List<(IssueCase Case, HostKey Host, DateTime Start)>();
        foreach (var x in computable)
        {
            var k = daysByHostIssue.TryGetValue((x.Host.HostId, x.Case.IssueKey), out var days)
                ? days.Count(d => d > x.Start && d <= today.Date)
                : 0;
            if (k > 0) result[x.Case.CaseId] = k;
            else zero.Add(x);
        }
        if (zero.Count == 0) return result;

        // 零命中者：窗口內該主機有任何分析紀錄才算「沒有再發生」，否則維持無資料
        var recordDays = records.QueryDayStatusLightweight(new RecordQueryFilter
            {
                Hosts = zero.Select(x => x.Host).GroupBy(h => h.HostId).Select(g => g.First()).ToList(),
                From = zero.Min(x => x.Start).AddDays(1),
                To = today.Date
            })
            .Where(r => r.HostId != 0)
            .GroupBy(r => r.HostId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Date.Date).ToHashSet());
        foreach (var x in zero)
        {
            if (recordDays.TryGetValue(x.Host.HostId, out var days) && days.Any(d => d > x.Start && d <= today.Date))
                result[x.Case.CaseId] = 0;
        }
        return result;
    }
}
