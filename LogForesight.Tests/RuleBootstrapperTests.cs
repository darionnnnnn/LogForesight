using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 啟動流程的規則載入編排（見 docs/RULES-PLAN.md）：不存在時寫入種子（僅此一次）、
/// 存在但損毀時降級用內建種子且不覆寫壞檔、驗證後只有 Enabled 的規則生效。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class RuleBootstrapperTests : IDisposable
{
    private readonly string _path;

    public RuleBootstrapperTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"logforesight-bootstrap-test-{Guid.NewGuid():N}.json");
    }

    // Run() 呼叫 KnownIssueCatalog.Initialize 覆寫共用靜態狀態；每個測試後重置回完整種子，
    // 避免影響同 collection 內其他測試類別（如 RiskReportServiceTests 依賴預設完整規則表）。
    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

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

    [Fact]
    public void 檔案不存在時寫入內建種子且回傳全部啟用()
    {
        var store = new JsonKnownIssueRuleStore(_path);

        var result = RuleBootstrapper.Run(store);

        Assert.True(store.Exists);
        Assert.False(result.UsedFallbackSeed);
        Assert.Equal(KnownIssueSeed.CreateRules().Count, result.EnabledCount);
        Assert.Equal(0, result.DisabledCount);
        Assert.Equal(KnownIssueSeed.Version, result.SeedVersion);
        Assert.Null(result.UpdateHint); // 剛寫入的就是最新種子，不該提示有更新可匯入
    }

    [Fact]
    public void 檔案損毀時降級用內建種子且不覆寫原檔()
    {
        const string corrupted = "{ not valid json at all";
        File.WriteAllText(_path, corrupted);
        var store = new JsonKnownIssueRuleStore(_path);

        var result = RuleBootstrapper.Run(store);

        Assert.True(result.UsedFallbackSeed);
        Assert.Equal("內建種子", result.Source);
        Assert.Equal(corrupted, File.ReadAllText(_path)); // 原檔完全沒被動過
    }

    [Fact]
    public void 停用的規則不計入EnabledCount且不參與Classify()
    {
        var rules = KnownIssueSeed.CreateRules();
        rules[0] = new KnownIssueRule
        {
            Id = rules[0].Id,
            Origin = rules[0].Origin,
            Enabled = false, // 停用第一條規則
            Scope = rules[0].Scope,
            MatchAllEventIds = rules[0].MatchAllEventIds,
            SourcePattern = rules[0].SourcePattern,
            EventIds = rules[0].EventIds,
            Category = rules[0].Category,
            Severity = rules[0].Severity,
            Description = rules[0].Description,
            CountThreshold = rules[0].CountThreshold,
            PlainExplanation = rules[0].PlainExplanation,
            Impact = rules[0].Impact,
            LikelyCauses = rules[0].LikelyCauses,
            NextSteps = rules[0].NextSteps
        };
        var disabledRuleSourcePattern = rules[0].SourcePattern;
        var disabledRuleEventId = rules[0].EventIds.Length > 0 ? rules[0].EventIds[0] : 0;

        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = KnownIssueSeed.Version, Rules = rules });

        var result = RuleBootstrapper.Run(store);

        Assert.Equal(rules.Count - 1, result.EnabledCount);
        Assert.Equal(1, result.DisabledCount);
        Assert.Null(KnownIssueCatalog.FindRule(disabledRuleSourcePattern, disabledRuleEventId));
    }

    [Fact]
    public void 內建種子版本較新時提示可匯入()
    {
        var store = new JsonKnownIssueRuleStore(_path);
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = KnownIssueSeed.Version - 1, Rules = KnownIssueSeed.CreateRules() });

        var result = RuleBootstrapper.Run(store);

        Assert.NotNull(result.UpdateHint);
        Assert.Contains("--import-rules", result.UpdateHint);
    }
}
