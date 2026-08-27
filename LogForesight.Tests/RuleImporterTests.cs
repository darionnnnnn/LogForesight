using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 內建規則升級的合併邏輯（見 docs/RULES-SPEC.md「初次部署寫入、後續手動匯入」；入口是
/// Web 規則維護頁的升級橫幅，早期的 `--import-rules` CLI 已隨 Phase 5 退場）：
/// 1. builtin 若內容與種子不同，依 ModifiedBy 分流：
///    - ModifiedBy == null（未曾修改）：視為原廠更新直接套用（UpdatedBuiltin），不需 overwriteBuiltin。
///    - ModifiedBy 有值（曾被使用者修改）：保護手動修改，維持 SkippedModifiedBuiltin，需勾選 overwriteBuiltin 才會覆蓋。
/// 2. 覆蓋與原廠更新時皆保留使用者的 Enabled 選擇。
/// 3. custom 規則一律不受匯入影響，Id 相同但 Origin 不一致視為衝突不處理。
/// <see cref="RuleImportPlanner.BuildPlan"/> 是純函數，與儲存後端無關。
/// </summary>
public class RuleImporterTests
{
    private static KnownIssueRule Rule(string id, string origin = "builtin", bool enabled = true,
        string sourcePattern = "A", int[]? eventIds = null, string description = "d1",
        long? modifiedBy = null, DateTime? modifiedAt = null) => new()
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
            NextSteps = new[] { "s" },
            ModifiedBy = modifiedBy,
            ModifiedAt = modifiedAt
        };

    // ── BuildPlan（純函數）────────────────────────────────────────

    [Fact]
    public void 種子中existing沒有的builtin規則被新增()
    {
        var plan = RuleImportPlanner.BuildPlan(new List<KnownIssueRule>(), new List<KnownIssueRule> { Rule("builtin-new") }, overwriteBuiltin: false);

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

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(0, plan.Added);
        Assert.Equal(0, plan.Updated);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(RuleImportAction.SkippedUnchanged, plan.Items[0].Action);
    }

    [Fact]
    public void 內容不同的builtin規則有overwrite參數時覆蓋且保留使用者的Enabled設定()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", enabled: false, description: "舊內容", modifiedBy: 5) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", enabled: true, description: "新內容") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: true);

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

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(1, plan.Conflicts);
        Assert.Equal(RuleImportAction.Conflict, plan.Items[0].Action);
        Assert.Equal("使用者的版本", plan.ResultingRules[0].Description); // 完全不動
    }

    [Fact]
    public void 使用者自訂的custom規則不受匯入影響()
    {
        var existing = new List<KnownIssueRule> { Rule("custom-mine", origin: "custom") };
        var seed = new List<KnownIssueRule> { Rule("builtin-new") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(2, plan.ResultingRules.Count);
        Assert.Contains(plan.ResultingRules, r => r.Id == "custom-mine");
        Assert.Contains(plan.ResultingRules, r => r.Id == "builtin-new");
    }

    // ── Linux 規則（docs/LINUX-RULES.md §1.5：probe 後的 v5 pattern 校正走同一條路）──

    private static KnownIssueRule LinuxRule(string id, string programPattern = "sshd",
        string[]? messagePatterns = null, bool enabled = true, long? modifiedBy = null) => new()
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
            NextSteps = new[] { "s" },
            ModifiedBy = modifiedBy
        };

    [Fact]
    public void Linux規則的MessagePatterns改變會被認出是內容變更()
    {
        // probe 之後修訂 pattern 字串（seed v4→v5）就是這條路：比對若漏看 MessagePatterns，
        // 匯入會回報「內容相同、略過」，使用者照做也拿不到修正。
        // 未修改過的 Linux 規則在內容不同時，會直接套用原廠更新（UpdatedBuiltin）。
        var existing = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", messagePatterns: new[] { "舊 pattern" }) };
        var seed = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", messagePatterns: new[] { "新 pattern" }) };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(RuleImportAction.UpdatedBuiltin, plan.Items[0].Action);
        Assert.Equal(1, plan.Updated);
    }

    [Fact]
    public void Linux規則覆蓋後保留Linux比對欄位而不是退化成Windows規則()
    {
        var existing = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", programPattern: "sshd", enabled: false) };
        var seed = new List<KnownIssueRule> { LinuxRule("builtin-linux-a", programPattern: "sudo") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: true);

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

        var plan = RuleImportPlanner.BuildPlan(
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

    // ── C2: 區分使用者改過與程式更新（ModifiedBy 分流）──────────────────

    [Fact]
    public void 未修改過的builtin規則內容有異時判定為UpdatedBuiltin且直接套用()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", description: "舊版說明", modifiedBy: null) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", description: "新版原廠修正說明") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(1, plan.Updated);
        Assert.Equal(0, plan.Skipped);
        Assert.Equal(RuleImportAction.UpdatedBuiltin, plan.Items[0].Action);
        Assert.Contains("未被修改過，直接套用", plan.Items[0].Detail);
        var resulting = Assert.Single(plan.ResultingRules);
        Assert.Equal("新版原廠修正說明", resulting.Description);
    }

    [Fact]
    public void 使用者改過的builtin規則內容有異且未勾選覆蓋時維持SkippedModifiedBuiltin()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", description: "使用者自訂修改", modifiedBy: 5) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", description: "原廠新版") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(0, plan.Updated);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(RuleImportAction.SkippedModifiedBuiltin, plan.Items[0].Action);
        Assert.Contains("曾被修改過", plan.Items[0].Detail);
        var resulting = Assert.Single(plan.ResultingRules);
        Assert.Equal("使用者自訂修改", resulting.Description);
        Assert.Equal(5, resulting.ModifiedBy);
    }

    [Fact]
    public void 使用者改過的builtin規則勾選覆蓋時被種子覆蓋且清空修改追蹤()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", description: "使用者自訂修改", modifiedBy: 5, modifiedAt: new DateTime(2026, 1, 1)) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", description: "原廠最新版") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: true);

        Assert.Equal(1, plan.Updated);
        Assert.Equal(0, plan.Skipped);
        Assert.Equal(RuleImportAction.UpdatedBuiltin, plan.Items[0].Action);
        Assert.Contains("已用內建種子最新內容覆蓋", plan.Items[0].Detail);
        var resulting = Assert.Single(plan.ResultingRules);
        Assert.Equal("原廠最新版", resulting.Description);
        Assert.Null(resulting.ModifiedBy);
        Assert.Null(resulting.ModifiedAt);
    }

    [Fact]
    public void 未修改過的builtin規則原廠更新時保留使用者停用Enabled的選擇()
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", enabled: false, description: "舊說明", modifiedBy: null) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", enabled: true, description: "原廠修正說明") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(1, plan.Updated);
        Assert.Equal(RuleImportAction.UpdatedBuiltin, plan.Items[0].Action);
        var resulting = Assert.Single(plan.ResultingRules);
        Assert.Equal("原廠修正說明", resulting.Description);
        Assert.False(resulting.Enabled); // 操作決定（停用）被保留
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5L)]
    public void 內容相同時不論ModifiedBy有無值皆為SkippedUnchanged(long? modifiedBy)
    {
        var existing = new List<KnownIssueRule> { Rule("builtin-a", description: "同內容", modifiedBy: modifiedBy) };
        var seed = new List<KnownIssueRule> { Rule("builtin-a", description: "同內容") };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(0, plan.Updated);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(RuleImportAction.SkippedUnchanged, plan.Items[0].Action);
        Assert.Contains("相同，略過", plan.Items[0].Detail);
    }

    [Fact]
    public void 新增與衝突判定在分流後維持不變()
    {
        var existing = new List<KnownIssueRule>
        {
            Rule("custom-conflict", origin: "custom", description: "自訂衝突"),
            Rule("builtin-keep", origin: "builtin", description: "庫內既有")
        };
        var seed = new List<KnownIssueRule>
        {
            Rule("custom-conflict", origin: "builtin", description: "種子衝突"),
            Rule("builtin-new", origin: "builtin", description: "全新種子")
        };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(1, plan.Added);
        Assert.Equal(1, plan.Conflicts);
        Assert.Equal(RuleImportAction.Conflict, plan.Items.First(i => i.Id == "custom-conflict").Action);
        Assert.Equal(RuleImportAction.Added, plan.Items.First(i => i.Id == "builtin-new").Action);
    }

    [Fact]
    public void Updated計數正確反映多筆未修改builtin規則的原廠更新()
    {
        var existing = new List<KnownIssueRule>
        {
            Rule("builtin-1", description: "舊1", modifiedBy: null),
            Rule("builtin-2", description: "舊2", modifiedBy: null),
            Rule("builtin-3", description: "舊3", modifiedBy: 5) // 使用者改過，不計入
        };
        var seed = new List<KnownIssueRule>
        {
            Rule("builtin-1", description: "新1"),
            Rule("builtin-2", description: "新2"),
            Rule("builtin-3", description: "新3")
        };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(2, plan.Updated);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void 混合情境含未改過更新_已改過跳過_未變更_新增四種情境計數各自正確()
    {
        var existing = new List<KnownIssueRule>
        {
            Rule("b-unmodified", description: "舊版說明", modifiedBy: null),
            Rule("b-user-modified", description: "使用者客製", modifiedBy: 42),
            Rule("b-unchanged", description: "相同說明", modifiedBy: null),
            Rule("c-conflict", origin: "custom", description: "自訂衝突")
        };
        var seed = new List<KnownIssueRule>
        {
            Rule("b-unmodified", description: "原廠最新修正說明"),
            Rule("b-user-modified", description: "原廠最新說明"),
            Rule("b-unchanged", description: "相同說明"),
            Rule("b-new", description: "全新規則"),
            Rule("c-conflict", origin: "builtin", description: "種子衝突")
        };

        var plan = RuleImportPlanner.BuildPlan(existing, seed, overwriteBuiltin: false);

        Assert.Equal(1, plan.Added);       // b-new
        Assert.Equal(1, plan.Updated);     // b-unmodified
        Assert.Equal(2, plan.Skipped);     // b-user-modified (SkippedModifiedBuiltin) + b-unchanged (SkippedUnchanged)
        Assert.Equal(1, plan.Conflicts);   // c-conflict

        Assert.Equal(RuleImportAction.UpdatedBuiltin, plan.Items.First(i => i.Id == "b-unmodified").Action);
        Assert.Equal(RuleImportAction.SkippedModifiedBuiltin, plan.Items.First(i => i.Id == "b-user-modified").Action);
        Assert.Equal(RuleImportAction.SkippedUnchanged, plan.Items.First(i => i.Id == "b-unchanged").Action);
        Assert.Equal(RuleImportAction.Added, plan.Items.First(i => i.Id == "b-new").Action);
        Assert.Equal(RuleImportAction.Conflict, plan.Items.First(i => i.Id == "c-conflict").Action);

        // 驗證 ResultingRules 內容
        var rUnmodified = plan.ResultingRules.First(r => r.Id == "b-unmodified");
        Assert.Equal("原廠最新修正說明", rUnmodified.Description);

        var rUserModified = plan.ResultingRules.First(r => r.Id == "b-user-modified");
        Assert.Equal("使用者客製", rUserModified.Description);

        var rNew = plan.ResultingRules.First(r => r.Id == "b-new");
        Assert.Equal("全新規則", rNew.Description);
    }
}

