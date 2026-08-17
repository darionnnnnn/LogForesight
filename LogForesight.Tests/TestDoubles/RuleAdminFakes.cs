using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：規則維護（RuleAdminService）相關 ───────────────────────────────
// 原本定義在 RuleAdminServiceTests.cs 檔尾，搬到這裡是因為 FakeRuleStore 已被
// HostDetailIssueSummaryTests／RecordQueryServiceIssueHistoryTests／
// RecordQueryServiceSearchTests／RuleBootstrapWebIntegrationTests 等多個測試檔共用。

internal class FakeRuleStore : IKnownIssueRuleStore
{
    public RuleFileContent Content { get; set; } = new();

    public string Location => "(fake)";
    public bool Exists => true;

    public RuleLoadOutcome Load() => RuleLoadOutcome.Ok(Content);

    public void Save(RuleFileContent content) => Content = content;
}

internal class FakeRuleSeedStore : IRuleSeedStore
{
    private readonly List<RuleSeedSnapshot> _snapshots = new();

    public RuleSeedSnapshot? Get(string ruleId) =>
        _snapshots.FirstOrDefault(s => string.Equals(s.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

    public List<RuleSeedSnapshot> GetAll() => _snapshots.ToList();

    public void Sync(IEnumerable<KnownIssueRule> seedRules, int seedVersion)
    {
        _snapshots.Clear();
        foreach (var rule in seedRules)
        {
            _snapshots.Add(new RuleSeedSnapshot
            {
                RuleId = rule.Id,
                SeedVersion = seedVersion,
                ContentJson = System.Text.Json.JsonSerializer.Serialize(rule, new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                })
            });
        }
    }
}

/// <summary>回饋十四輪 C1：抑制影響面預覽（RuleAdminService.PreviewSuppression）用的假聚合查詢——
/// 測試直接塞好要回傳的 IssueAggregate 清單，不需要真的連 SQL。</summary>
internal sealed class FakeIssueAggregateQuery : IIssueAggregateQuery
{
    public List<IssueAggregate> Result { get; set; } = new();

    /// <summary>最後一次呼叫的參數，供測試斷言查詢範圍（期間／hostIds）有沒有傳對。</summary>
    public (DateTime From, DateTime To, IReadOnlyCollection<long>? HostIds)? LastCall { get; private set; }

    /// <summary>Aggregate 累計呼叫次數（回饋十九輪批次H3）：供測試斷言「同一批次內相同
    /// 可見範圍集合有沒有真的共用結果、沒有重複查詢」。</summary>
    public int AggregateCallCount { get; private set; }

    /// <summary>依 (from,to) 回傳不同結果（回饋十九輪批次H）：預設所有呼叫共用同一份
    /// <see cref="Result"/>，測試若要區分「本期」與「前期」聚合（如驗證新出現／擴散中判定）
    /// 才需要設這個委派——不設時維持既有的單一 Result 慣例，其餘既有測試不受影響。</summary>
    public Func<DateTime, DateTime, List<IssueAggregate>>? AggregateOverride { get; set; }

    public List<IssueAggregate> Aggregate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        LastCall = (from, to, hostIds);
        AggregateCallCount++;
        // 與真正的 EfIssueAggregateQuery 同一個既有慣例：空集合＝可見範圍為空，零結果
        if (hostIds != null && hostIds.Count == 0) return new List<IssueAggregate>();
        return AggregateOverride?.Invoke(from, to) ?? Result;
    }

    /// <summary>回饋十八輪批次F：測試直接塞好要回傳的主機集合，不需要真的連 SQL。</summary>
    public HashSet<long> HostIdsForResult { get; set; } = new();

    public (IReadOnlyCollection<(string Source, int EventId)> Issues, DateTime From, DateTime To)? LastHostIdsForCall { get; private set; }

    public HashSet<long> HostIdsFor(IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to)
    {
        LastHostIdsForCall = (issues, from, to);
        return HostIdsForResult;
    }

    public List<HostIssueOccurrence> LatestOccurrences(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? visibleSeverities = null) => new();

    public List<CategoryAggregate> AggregateByCategory(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? allowedSeverities) => new();

    public List<HostIssueOccurrence> ActionableOccurrencesResult { get; set; } = new();

    public List<HostIssueOccurrence> ActionableOccurrences(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null,
        IReadOnlySet<string>? riskLevels = null) =>
        ActionableOccurrencesResult;

    public List<DateRiskAggregate> AggregateByDate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) => new();

    public List<HostRiskAggregate> AggregateByHost(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) => new();

    public List<IssueDailyHostCount> DailyHostCounts(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds) => new();

    public Dictionary<(string SourceKey, int EventId), DateTime> FirstSeenFor(
        IReadOnlyCollection<(string Source, int EventId)> issues) => new();

    public List<DayHandlingProjection> DeriveDayHandling(
        DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        IReadOnlyCollection<long> excludedHostIds) => new();

    public Dictionary<(string SourceKey, int EventId), HashSet<long>> HostIdsByIssue(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds) => new();

    public ReportKpiAggregate AggregateReportKpi(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<string>? riskLevels, IReadOnlySet<IssueSeverity>? visibleSeverities) => new(0, 0, 0, 0, 0);

    public List<TrendAggregate> AggregateReportTrend(DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<string>? riskLevels, IReadOnlySet<IssueSeverity>? visibleSeverities) => new();


    public DayTodoAggregate AggregateDayTodo(
        DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity> unhandledSeverities,
        IReadOnlyCollection<long> excludedHostIds,
        DateTime today,
        IReadOnlySet<string>? riskLevels = null) => new(0, 0, 0, 0, 0);
}

internal class FakeSuppressionStore : ISuppressionStore
{
    private List<RuleSuppression> _suppressions = new();

    public string Location => "(fake)";

    /// <summary>回饋十四輪 A3：驗證 per-run 快取有沒有真的把 LoadAll 收斂成呼叫一次用的計數器。</summary>
    public int LoadAllCallCount { get; private set; }

    public List<RuleSuppression> LoadAll()
    {
        LoadAllCallCount++;
        return _suppressions.ToList();
    }

    public void SaveAll(List<RuleSuppression> suppressions) => _suppressions = suppressions.ToList();
}
