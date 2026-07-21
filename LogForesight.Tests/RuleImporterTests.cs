using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// `--import-rules` 的合併邏輯（見 docs/RULES-PLAN.md「初次部署寫入、後續手動匯入」）：
/// builtin 預設只補缺，內容有異動需要 --overwrite-builtin 才覆蓋且保留使用者的 Enabled 選擇，
/// custom 規則一律不受匯入影響，Id 相同但 Origin 不一致視為衝突不處理。
/// </summary>
public class RuleImporterTests : IDisposable
{
    private readonly string _path;

    public RuleImporterTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"logforesight-import-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        var tmp = _path + ".tmp";
        if (File.Exists(tmp))
        {
            File.Delete(tmp);
        }
    }

    private static KnownIssueRule Rule(string id, string origin = "builtin", bool enabled = true,
        string sourcePattern = "A", int[]? eventIds = null, string description = "d1") => new()
        {
            Id = id,
            Origin = origin,
            Enabled = enabled,
            Scope = "all",
            MatchAllEventIds = false,
            MatchFilter = null,
            SourcePattern = sourcePattern,
            EventIds = eventIds ?? new[] { 1 },
            Category = IssueCategory.Other,
            Severity = IssueSeverity.Medium,
            Description = description,
            CountThreshold = 1,
            PlainExplanation = "p",
            Impact = "i",
            LikelyCauses = new[] { "c" },
            NextSteps = new[] { "s" }
        };

    // ── BuildPlan（純函數）────────────────────────────────────────

    [Fact]
    public void 種子中existing沒有的builtin規則被新增()
    {
        var plan = RuleImporter.BuildPlan(new List<KnownIssueRule>(), new List<KnownIssueRule> { Rule("builtin-new") }, overwriteBuiltin: false);

        Assert.Equal(1, plan.Added);
        Assert.Equal(0, plan.Updated);
        Assert.Single(plan.ResultingRules);
        Assert.Equal(RuleImportAction.Added, plan.Items[0].Action);
    }

    [Fact]
    public void 內容相同的builtin規則略過()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a") };
        var seed = new List<KnownIssueRule> { Rule("builtin-a") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(0, plan.Added);
        Assert.Equal(0, plan.Updated);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(RuleImportAction.SkippedUnchanged, plan.Items[0].Action);
    }

    [Fact]
    public void 內容不同的builtin規則沒有overwrite參數時略過且不覆蓋()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", description: "舊內容") };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", description: "新內容") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(0, plan.Updated);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(RuleImportAction.SkippedModifiedBuiltin, plan.Items[0].Action);
        Assert.Equal("舊內容", plan.ResultingRules[0].Description);
    }

    [Fact]
    public void 內容不同的builtin規則有overwrite參數時覆蓋且保留使用者的Enabled設定()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", enabled: false, description: "舊內容") };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", enabled: true, description: "新內容") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(1, plan.Updated);
        var updated = Assert.Single(plan.ResultingRules);
        Assert.Equal("新內容", updated.Description);
        Assert.False(updated.Enabled); // 使用者停用的選擇被保留，不因匯入而悄悄打開
    }

    [Fact]
    public void Id相同但Origin為custom時視為衝突不處理()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", origin: "custom", description: "使用者的版本") };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", origin: "builtin", description: "種子版本") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(1, plan.Conflicts);
        Assert.Equal(RuleImportAction.Conflict, plan.Items[0].Action);
        Assert.Equal("使用者的版本", plan.ResultingRules[0].Description); // 完全不動
    }

    [Fact]
    public void 使用者自訂的custom規則不受匯入影響()
    {
        var existing = new List<KnownIssueRule> { Rule("custom-mine", origin: "custom") };
        var seed = new List<KnownIssueRule> { Rule("builtin-new") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(2, plan.ResultingRules.Count);
        Assert.Contains(plan.ResultingRules, r => r.Id == "custom-mine");
        Assert.Contains(plan.ResultingRules, r => r.Id == "builtin-new");
    }

    // ── Run（含檔案 I/O）─────────────────────────────────────────

    [Fact]
    public void Run_檔案不存在時預覽不寫檔_Apply才真正寫入完整種子()
    {
        var store = new JsonKnownIssueRuleStore(_path);

        RuleImporter.Run(store, apply: false, overwriteBuiltin: false);
        Assert.False(store.Exists);

        RuleImporter.Run(store, apply: true, overwriteBuiltin: false);
        Assert.True(store.Exists);

        var outcome = store.Load();
        Assert.True(outcome.Success);
        Assert.Equal(KnownIssueSeed.CreateRules().Count, outcome.Content!.Rules.Count);
        Assert.Equal(KnownIssueSeed.Version, outcome.Content.SeedVersion);
    }

    [Fact]
    public void Run_既有檔案套用後更新SeedVersion且補上缺少的builtin規則()
    {
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = 0, Rules = new List<KnownIssueRule>() });

        RuleImporter.Run(store, apply: true, overwriteBuiltin: false);

        var outcome = store.Load();
        Assert.True(outcome.Success);
        Assert.Equal(KnownIssueSeed.Version, outcome.Content!.SeedVersion);
        Assert.Equal(KnownIssueSeed.CreateRules().Count, outcome.Content.Rules.Count);
    }

    [Fact]
    public void Run_預覽模式不寫入任何檔案內容變更()
    {
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = 0, Rules = new List<KnownIssueRule>() });

        RuleImporter.Run(store, apply: false, overwriteBuiltin: false);

        var outcome = store.Load();
        Assert.True(outcome.Success);
        Assert.Empty(outcome.Content!.Rules); // 預覽模式：規則清單仍是空的，SeedVersion 仍是舊的
        Assert.Equal(0, outcome.Content.SeedVersion);
    }
}
