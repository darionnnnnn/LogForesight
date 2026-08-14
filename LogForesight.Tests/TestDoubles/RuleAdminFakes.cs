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

    public List<IssueAggregate> Aggregate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null)
    {
        LastCall = (from, to, hostIds);
        // 與真正的 EfIssueAggregateQuery 同一個既有慣例：空集合＝可見範圍為空，零結果
        if (hostIds != null && hostIds.Count == 0) return new List<IssueAggregate>();
        return Result;
    }

    /// <summary>回饋十八輪批次F：測試直接塞好要回傳的主機集合，不需要真的連 SQL。</summary>
    public HashSet<long> HostIdsForResult { get; set; } = new();

    public (IReadOnlyCollection<(string Source, int EventId)> Issues, DateTime From, DateTime To)? LastHostIdsForCall { get; private set; }

    public HashSet<long> HostIdsFor(IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to)
    {
        LastHostIdsForCall = (issues, from, to);
        return HostIdsForResult;
    }

    public List<HostIssueOccurrence> LatestOccurrencesResult { get; set; } = new();

    public List<HostIssueOccurrence> LatestOccurrences(
        IReadOnlyCollection<(string Source, int EventId)> issues, DateTime from, DateTime to,
        IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? visibleSeverities = null) =>
        LatestOccurrencesResult;

    public List<CategoryAggregate> AggregateByCategoryResult { get; set; } = new();

    public List<CategoryAggregate> AggregateByCategory(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds, IReadOnlySet<IssueSeverity>? allowedSeverities) =>
        AggregateByCategoryResult;

    public List<HostIssueOccurrence> ActionableOccurrencesResult { get; set; } = new();

    public List<HostIssueOccurrence> ActionableOccurrences(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) =>
        ActionableOccurrencesResult;

    public List<DateRiskAggregate> AggregateByDateResult { get; set; } = new();

    public List<DateRiskAggregate> AggregateByDate(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) =>
        AggregateByDateResult;

    public List<HostRiskAggregate> AggregateByHostResult { get; set; } = new();

    public List<HostRiskAggregate> AggregateByHost(
        DateTime from, DateTime to, IReadOnlyCollection<long>? hostIds,
        IReadOnlySet<string>? riskLevels = null, IReadOnlySet<IssueCategory>? categories = null,
        int? eventId = null, string? source = null, IssueSeverity? minSeverity = null,
        IReadOnlySet<IssueSeverity>? visibleSeverities = null) =>
        AggregateByHostResult;
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
