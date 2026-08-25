using LogForesight.Core.Analysis;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 待辦的問題口徑（回饋十九輪批次D2，外部審查§一-2）：舊版
/// <see cref="HandlingHistoryQueryService.GetTodo"/> 的計數單位是「風險日數」，
/// 2000 台環境下「未處理 1,340」對使用者等於沒有資訊——這裡改成「未處理問題 X 個」，
/// 答的是「有幾個不同的問題要處理」而不是「有幾個主機日還沒結案」。
///
/// 母體與 <see cref="HandlingHistoryQueryService.GetTodo"/> 一致（<see
/// cref="IIssueAggregateQuery.ActionableOccurrences"/> 已下推同一個定義：日層級 RiskLevel
/// 為高或中），狀態判定共用 <see cref="OccurrenceStatusResolver"/>／<see
/// cref="IssueGroupStatusResolver"/>，不是另一套規則——否則儀表板的「未處理問題」跟
/// 依問題視角的處理概況又會對不上。
/// </summary>
public class IssueTodoQuery
{
    private static readonly IssueTupleComparer IssueComparer = new();

    private readonly IIssueAggregateQuery _aggregates;
    private readonly OccurrenceStatusResolver _statusResolver;

    public IssueTodoQuery(IIssueAggregateQuery aggregates, OccurrenceStatusResolver statusResolver)
    {
        _aggregates = aggregates;
        _statusResolver = statusResolver;
    }

    public IssueTodoDto Build(
        DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds,
        IReadOnlySet<string>? riskLevels = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) =>
        Aggregate(ResolveActionable(from, to, visibleHostIds, riskLevels, visibleSeverities));

    /// <summary>
    /// 解析一次、彙總多次（回饋十九輪批次I 體檢修正）：儀表板的全站 KPI 與逐群組的
    /// UnhandledCount 需要同一份可行動快照的不同子集彙總——原本逐群組各自呼叫
    /// <see cref="Build"/>，每個群組都是一趟 SQL＋一輪批次載入（<see cref="OccurrenceStatusResolver"/>
    /// 內含 hosts/handling/case 三次查詢），群組數不設上限時就是 4×N 次查詢，正是
    /// <c>IssueHandlingRollupQuery</c> 註解裡點名要避免的那種 N+1。呼叫端改為：
    /// 對全站可見範圍解析一次，再用 <see cref="Aggregate"/> 對各群組的主機子集各自彙總。
    /// </summary>
    public List<ResolvedOccurrence> ResolveActionable(
        DateTime from, DateTime to, IReadOnlyCollection<long>? visibleHostIds,
        IReadOnlySet<string>? riskLevels = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        var occurrences = _aggregates.ActionableOccurrences(
            from, to, visibleHostIds, riskLevels: riskLevels, visibleSeverities: visibleSeverities);
        if (occurrences.Count == 0) return new List<ResolvedOccurrence>();

        return _statusResolver.Resolve(occurrences, from, to);
    }

    /// <summary>純彙總（無 I/O）：問題計數以 (Source, EventId) 去重——同一個問題在多台主機
    /// 都未處理只算一個問題，「影響幾台」交給 AffectedHostCount 另外回答，
    /// 兩件事不要混在同一個數字裡。</summary>
    public static IssueTodoDto Aggregate(IEnumerable<ResolvedOccurrence> resolved)
    {
        var openIssues = new HashSet<(string Source, int EventId)>(IssueComparer);
        var inProgressIssues = new HashSet<(string, int)>(IssueComparer);
        var overdueIssues = new HashSet<(string, int)>(IssueComparer);
        var openHosts = new HashSet<long>();

        foreach (var r in resolved)
        {
            var signature = IssueSignatureKey.TryParseSignature(r.Occurrence.IssueKey);
            if (signature == null) continue;

            switch (r.Status)
            {
                case HostIssueStatus.Open:
                    openIssues.Add(signature.Value);
                    openHosts.Add(r.Occurrence.HostId);
                    break;
                case HostIssueStatus.Processing:
                    inProgressIssues.Add(signature.Value);
                    break;
            }

            // 逾期是「處理中但預計完成日已過」，獨立於三態之外——in_progress 本身不分「按時」
            // 與「拖過頭」，這裡補上這一層，供批次H 郵件的逾期區塊共用同一個口徑
            if (IsOverdueInProgress(r)) overdueIssues.Add(signature.Value);
        }

        return new IssueTodoDto
        {
            OpenIssueCount = openIssues.Count,
            InProgressIssueCount = inProgressIssues.Count,
            OverdueIssueCount = overdueIssues.Count,
            AffectedHostCount = openHosts.Count
        };
    }

    /// <summary>逾期＝處理中但預計完成日已過。internal 供 <c>MailIssueDigest</c>（回饋十九輪批次H）
    /// 共用同一個口徑，不是各自訂一套逾期規則（見上方呼叫處註解）。</summary>
    internal static bool IsOverdueInProgress(ResolvedOccurrence r)
    {
        var today = DateTime.Today;
        if (r.OpenCase != null)
            return r.OpenCase.Status == IssueHandlingStatuses.InProgress && r.OpenCase.DueDate?.Date < today;

        return r.Handling is { Status: IssueHandlingStatuses.InProgress } && r.Handling.DueDate?.Date < today;
    }

    private sealed class IssueTupleComparer : IEqualityComparer<(string Source, int EventId)>
    {
        public bool Equals((string Source, int EventId) x, (string Source, int EventId) y) =>
            x.EventId == y.EventId && StringComparer.OrdinalIgnoreCase.Equals(x.Source, y.Source);

        public int GetHashCode((string Source, int EventId) obj) =>
            HashCode.Combine(obj.Source != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source) : 0, obj.EventId);
    }
}
