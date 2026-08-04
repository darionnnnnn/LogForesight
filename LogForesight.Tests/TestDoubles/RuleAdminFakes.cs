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

internal class FakeSuppressionStore : ISuppressionStore
{
    private List<RuleSuppression> _suppressions = new();

    public string Location => "(fake)";

    public List<RuleSuppression> LoadAll() => _suppressions.ToList();

    public void SaveAll(List<RuleSuppression> suppressions) => _suppressions = suppressions.ToList();
}
