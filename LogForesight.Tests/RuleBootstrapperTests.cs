using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 啟動流程的規則載入編排（見 docs/RULES-SPEC.md）：不存在時寫入種子（僅此一次）、
/// 存在但損毀時降級用內建種子且不覆寫壞檔、驗證後只有 Enabled 的規則生效。
/// 邏輯本身與 blob 底層無關，跑在 SQLite（EF）上驗證。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class RuleBootstrapperContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private EfJsonBlobStore? _blob;
    private EfJsonBlobStore Blob => _blob ??= _fx.Blob("rules");

    private KnownIssueRuleStore Store() => new(Blob);

    private static void WriteRaw(EfJsonBlobStore blob, string text) =>
        blob.Mutate<object?>(_ => (text, null));

    // Run() 呼叫 KnownIssueCatalog.Initialize 覆寫共用靜態狀態；每個測試後重置回完整種子，
    // 避免影響同 collection 內其他測試類別（如 RiskReportServiceTests 依賴預設完整規則表）。
    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 檔案不存在時寫入內建種子且EnabledCount只計啟用規則()
    {
        var store = Store();

        var result = RuleBootstrapper.Run(store);

        Assert.True(store.Exists);
        Assert.False(result.UsedFallbackSeed);
        var seedRules = KnownIssueSeed.CreateRules();
        Assert.Equal(seedRules.Count(r => r.Enabled), result.EnabledCount);
        Assert.Equal(seedRules.Count(r => !r.Enabled), result.DisabledCount);
        Assert.Contains(seedRules, r => r.Id == "builtin-prtg-disk-free-trend"
            && r.PrtgRuleCode == PrtgRuleEvaluator.RuleDiskFreeTrend && !r.Enabled);
        Assert.Equal(KnownIssueSeed.Version, result.SeedVersion);
        Assert.Null(result.UpdateHint); // 剛寫入的就是最新種子，不該提示有更新可匯入
    }

    [Fact]
    public void 檔案損毀時降級用內建種子且不覆寫原檔()
    {
        const string corrupted = "{ not valid json at all";
        WriteRaw(Blob, corrupted);
        var store = Store();

        var result = RuleBootstrapper.Run(store);

        Assert.True(result.UsedFallbackSeed);
        Assert.Equal("內建種子", result.Source);
        Assert.Equal(corrupted, Blob.Read()); // 原檔完全沒被動過
    }

    [Fact]
    public void 停用的規則不計入EnabledCount且不參與Classify()
    {
        var rules = KnownIssueSeed.CreateRules();
        int disabledIndex = rules.FindIndex(r => r.Enabled && r.EventIds.Length > 0);
        Assert.True(disabledIndex >= 0);
        var ruleToDisable = rules[disabledIndex];
        rules[disabledIndex] = new KnownIssueRule
        {
            Id = ruleToDisable.Id,
            Origin = ruleToDisable.Origin,
            Enabled = false, // 停用第一條規則
            Scope = ruleToDisable.Scope,
            MatchAllEventIds = ruleToDisable.MatchAllEventIds,
            SourcePattern = ruleToDisable.SourcePattern,
            EventIds = ruleToDisable.EventIds,
            Category = ruleToDisable.Category,
            Severity = ruleToDisable.Severity,
            Description = ruleToDisable.Description,
            CountThreshold = ruleToDisable.CountThreshold,
            PlainExplanation = ruleToDisable.PlainExplanation,
            Impact = ruleToDisable.Impact,
            LikelyCauses = ruleToDisable.LikelyCauses,
            NextSteps = ruleToDisable.NextSteps
        };
        var disabledRuleSourcePattern = ruleToDisable.SourcePattern;
        var disabledRuleEventId = ruleToDisable.EventIds[0];

        var store = Store();
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = KnownIssueSeed.Version, Rules = rules });

        var result = RuleBootstrapper.Run(store);

        Assert.Equal(rules.Count(r => r.Enabled), result.EnabledCount);
        Assert.Equal(rules.Count(r => !r.Enabled), result.DisabledCount);
        Assert.Null(KnownIssueCatalog.FindRule(disabledRuleSourcePattern, disabledRuleEventId));

        var disabledSignature = new LogIssueSignature
        {
            LogName = "System",
            Source = disabledRuleSourcePattern,
            EventId = disabledRuleEventId,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Count = 1
        };
        KnownIssueCatalog.Classify(disabledSignature);
        Assert.Equal(IssueCategory.Other, disabledSignature.Category);

        var enabledRule = rules.First(r => r.Enabled && r.EventIds.Length > 0);
        var enabledSignature = new LogIssueSignature
        {
            LogName = "System",
            Source = enabledRule.SourcePattern,
            EventId = enabledRule.EventIds[0],
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Count = 1
        };
        KnownIssueCatalog.Classify(enabledSignature);
        Assert.Equal(enabledRule.Category, enabledSignature.Category);
    }

    [Fact]
    public void 內建種子版本較新時提示可匯入()
    {
        var store = Store();
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = KnownIssueSeed.Version - 1, Rules = KnownIssueSeed.CreateRules() });

        var result = RuleBootstrapper.Run(store);

        Assert.NotNull(result.UpdateHint);
        Assert.Contains("規則維護", result.UpdateHint); // 升級入口是 Web 規則頁（--import-rules CLI 已隨 Phase 5 退場）
    }
}
