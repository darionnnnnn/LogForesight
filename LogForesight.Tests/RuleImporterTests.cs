using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// `--import-rules` 的合併邏輯（見 docs/RULES-PLAN.md「初次部署寫入、後續手動匯入」）：
/// builtin 預設只補缺，內容有異動需要 --overwrite-builtin 才覆蓋且保留使用者的 Enabled 選擇，
/// custom 規則一律不受匯入影響，Id 相同但 Origin 不一致視為衝突不處理。
/// <see cref="RuleImporter.BuildPlan"/> 是純函數，與儲存後端無關。
/// </summary>
public class RuleImporterTests
{
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

    // ── Linux 規則（docs/LINUX-RULES-PLAN.md §1.5：probe 後的 v5 pattern 校正走同一條路）──

    private static KnownIssueRule LinuxRule(string id, string programPattern = "sshd",
        string[]? messagePatterns = null, bool enabled = true) => new()
        {
            Id = id,
            Origin = "builtin",
            Enabled = enabled,
            Scope = "all",
            Platform = "linux",
            ProgramPattern = programPattern,
            MessagePatterns = messagePatterns ?? new[] { "Failed password" },
            Category = IssueCategory.Security,
            Severity = IssueSeverity.Medium,
            Description = "d1",
            CountThreshold = 1,
            PlainExplanation = "p",
            Impact = "i",
            LikelyCauses = new[] { "c" },
            NextSteps = new[] { "s" }
        };

    [Fact]
    public void Linux規則的MessagePatterns改變會被認出是內容變更()
    {
        // probe 之後修訂 pattern 字串（seed v4→v5）就是這條路：比對若漏看 MessagePatterns，
        // 匯入會回報「內容相同、略過」，使用者照做也拿不到修正
        var existing = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", messagePatterns: new[] { "舊 pattern" }) };
        var seed = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", messagePatterns: new[] { "新 pattern" }) };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(RuleImportAction.SkippedModifiedBuiltin, plan.Items[0].Action);
    }

    [Fact]
    public void Linux規則覆蓋後保留Linux比對欄位而不是退化成Windows規則()
    {
        var existing = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", programPattern: "sshd", enabled: false) };
        var seed = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", programPattern: "sudo") };

        var plan = RuleImporter.BuildPlan(existing, seed, overwriteBuiltin: true);

        var updated = Assert.Single(plan.ResultingRules);
        Assert.Equal("linux", updated.Platform);
        Assert.Equal("sudo", updated.ProgramPattern);
        Assert.NotEmpty(updated.MessagePatterns);
        Assert.False(updated.Enabled);
    }

    /// <summary>
    /// 覆蓋路徑的欄位涵蓋率（反射逐欄比對）：這是防止「新增規則欄位卻忘了同步複製邏輯」的網子。
    /// 曾經漏過 ElevatesDayRisk（覆蓋後「重大」旗標被清成 false，該規則從此不再讓當天判高風險日）
    /// 與 Platform／Linux 比對欄位（Linux 規則退化成無效的 Windows 規則，驗證期被靜默跳過）。
    /// 逐欄斷言而不是列舉已知欄位，未來新增欄位漏抄時這裡會直接紅燈。
    /// </summary>
    [Fact]
    public void 覆蓋builtin時除Enabled與修改追蹤外每一個欄位都取自種子()
    {
        // 兩條規則的每個欄位都刻意給不同值，漏抄的欄位才會留著 existing 的值而被抓到
        var existing = new KnownIssueRule
        {
            Id = "builtin-a", Origin = "builtin", Enabled = false, Scope = "all", Platform = "windows",
            MatchAllEventIds = true, MatchFilter = null, SourcePattern = "舊來源", EventIds = Array.Empty<int>(),
            ProgramPattern = "", EventNamePattern = "", MessagePatterns = Array.Empty<string>(),
            Category = IssueCategory.Other, Severity = IssueSeverity.Low, ElevatesDayRisk = false,
            Description = "舊", CountThreshold = 1, PlainExplanation = "舊p", Impact = "舊i",
            LikelyCauses = new[] { "舊c" }, NextSteps = new[] { "舊s" },
            ModifiedBy = 99, ModifiedAt = new DateTime(2020, 1, 1)
        };
        var seed = new KnownIssueRule
        {
            Id = "builtin-a", Origin = "builtin", Enabled = true, Scope = "all", Platform = "linux",
            MatchAllEventIds = false, MatchFilter = null, SourcePattern = "", EventIds = Array.Empty<int>(),
            ProgramPattern = "sshd", EventNamePattern = "AUTH_FAIL", MessagePatterns = new[] { "Failed password" },
            Category = IssueCategory.Security, Severity = IssueSeverity.High, ElevatesDayRisk = true,
            Description = "新", CountThreshold = 7, PlainExplanation = "新p", Impact = "新i",
            LikelyCauses = new[] { "新c" }, NextSteps = new[] { "新s" }
        };

        var plan = RuleImporter.BuildPlan(
            new List<KnownIssueRule> { existing }, new List<KnownIssueRule> { seed }, overwriteBuiltin: true);
        var updated = Assert.Single(plan.ResultingRules);

        // Enabled 保留使用者選擇；修改追蹤刻意清空（覆蓋後就等於原廠內容，不再是「已修改的 builtin」）
        var exemptions = new HashSet<string> { nameof(KnownIssueRule.Enabled), nameof(KnownIssueRule.ModifiedBy), nameof(KnownIssueRule.ModifiedAt) };

        foreach (var property in typeof(KnownIssueRule).GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            if (exemptions.Contains(property.Name)) continue;

            var fromSeed = property.GetValue(seed);
            var fromUpdated = property.GetValue(updated);

            if (fromSeed is System.Collections.IEnumerable seedItems and not string)
            {
                Assert.True(seedItems.Cast<object>().SequenceEqual(((System.Collections.IEnumerable)fromUpdated!).Cast<object>()),
                    $"欄位 {property.Name} 未自種子複製——請同步 KnownIssueRule.CloneForSeedOverwrite");
                continue;
            }

            Assert.True(Equals(fromSeed, fromUpdated),
                $"欄位 {property.Name} 未自種子複製（種子={fromSeed}、實際={fromUpdated}）——請同步 KnownIssueRule.CloneForSeedOverwrite");
        }

        Assert.False(updated.Enabled);
        Assert.Null(updated.ModifiedBy);
        Assert.Null(updated.ModifiedAt);
    }
}

/// <summary>
/// `RuleImporter.Run` 的編排（預覽 vs --apply、既有檔案套用）跑在儲存 store 上——
/// 邏輯本身與 blob 底層無關，跑在 SQLite（EF）上驗證。
/// </summary>
public class RuleImporterRunContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private IJsonBlobStore? _blob;
    private JsonKnownIssueRuleStore Store() => new(_blob ??= _fx.Blob("rules"));

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Run_檔案不存在時預覽不寫檔_Apply才真正寫入完整種子()
    {
        var store = Store();

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
        var store = Store();
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
        var store = Store();
        store.Save(new RuleFileContent { SchemaVersion = 1, SeedVersion = 0, Rules = new List<KnownIssueRule>() });

        RuleImporter.Run(store, apply: false, overwriteBuiltin: false);

        var outcome = store.Load();
        Assert.True(outcome.Success);
        Assert.Empty(outcome.Content!.Rules); // 預覽模式：規則清單仍是空的，SeedVersion 仍是舊的
        Assert.Equal(0, outcome.Content.SeedVersion);
    }
}
