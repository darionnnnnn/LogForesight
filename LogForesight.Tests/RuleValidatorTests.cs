using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 規則載入後的驗證：手動編輯 rules.json 打錯一條不該讓整份規則失效，這裡逐一驗證每種
/// 不合格情境都被正確攔下，且不影響其餘規則；遮蔽偵測則驗證正例與反例。
/// </summary>
public class RuleValidatorTests
{
    private static KnownIssueRule Rule(
        string id = "custom-test-rule",
        string origin = "custom",
        bool enabled = true,
        string scope = "all",
        bool matchAllEventIds = false,
        string? matchFilter = null,
        string sourcePattern = "TestSource",
        int[]? eventIds = null,
        IssueCategory category = IssueCategory.Other,
        IssueSeverity severity = IssueSeverity.Medium,
        string description = "desc",
        int countThreshold = 1,
        string plainExplanation = "explanation",
        string impact = "impact",
        string[]? likelyCauses = null,
        string[]? nextSteps = null) => new()
        {
            Id = id,
            Origin = origin,
            Enabled = enabled,
            Scope = scope,
            MatchAllEventIds = matchAllEventIds,
            MatchFilter = matchFilter,
            SourcePattern = sourcePattern,
            EventIds = eventIds ?? new[] { 1 },
            Category = category,
            Severity = severity,
            Description = description,
            CountThreshold = countThreshold,
            PlainExplanation = plainExplanation,
            Impact = impact,
            LikelyCauses = likelyCauses ?? new[] { "cause" },
            NextSteps = nextSteps ?? new[] { "step" }
        };

    [Fact]
    public void 合格規則通過驗證()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule() });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.SkippedRules);
        Assert.Empty(outcome.ShadowWarnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Id空白時跳過(string id)
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(id: id) });

        Assert.Empty(outcome.ValidRules);
        Assert.Single(outcome.SkippedRules);
    }

    [Fact]
    public void Id重複時第二條被跳過_第一條保留()
    {
        var rules = new List<KnownIssueRule> { Rule(id: "dup"), Rule(id: "dup") };

        var outcome = RuleValidator.Validate(rules);

        Assert.Single(outcome.ValidRules);
        Assert.Single(outcome.SkippedRules);
        Assert.Contains("重複", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void ScopeAll以外的值不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(scope: "host-only") });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("尚未支援", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void MatchFilter非null時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(matchFilter: "something") });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("MatchFilter", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void EventIds為空且未宣告MatchAll時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(eventIds: Array.Empty<int>(), matchAllEventIds: false) });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("MatchAllEventIds", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void EventIds為空但MatchAllEventIds為true時合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(eventIds: Array.Empty<int>(), matchAllEventIds: true) });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.SkippedRules);
    }

    [Fact]
    public void EventIds含非正整數時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(eventIds: new[] { 1, -5 }) });

        Assert.Empty(outcome.ValidRules);
    }

    [Fact]
    public void CountThreshold小於1時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(countThreshold: 0) });

        Assert.Empty(outcome.ValidRules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Description空白時不合格(string description)
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(description: description) });

        Assert.Empty(outcome.ValidRules);
    }

    [Fact]
    public void 欄位超過長度上限時不合格()
    {
        var tooLong = new string('x', RuleSchemaLimits.DescriptionMaxLength + 1);
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(description: tooLong) });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("長度上限", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void LikelyCauses為空時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(likelyCauses: Array.Empty<string>()) });

        Assert.Empty(outcome.ValidRules);
    }

    [Fact]
    public void NextSteps含空白項目時不合格()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { Rule(nextSteps: new[] { "  " }) });

        Assert.Empty(outcome.ValidRules);
    }

    [Fact]
    public void 單條不合格不影響其餘規則載入()
    {
        var rules = new List<KnownIssueRule> { Rule(id: "bad", description: ""), Rule(id: "good") };

        var outcome = RuleValidator.Validate(rules);

        Assert.Single(outcome.ValidRules);
        Assert.Equal("good", outcome.ValidRules[0].Id);
        Assert.Single(outcome.SkippedRules);
    }

    // ── 遮蔽偵測 ──────────────────────────────────────────────

    [Fact]
    public void 較泛用的規則排在前面時後面的具體規則被判定為遮蔽()
    {
        var broad = Rule(id: "broad", sourcePattern: "Security-Auditing", matchAllEventIds: true);
        var specific = Rule(id: "specific", sourcePattern: "Security-Auditing", eventIds: new[] { 4625 });

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { broad, specific });

        Assert.Equal(2, outcome.ValidRules.Count);
        Assert.Single(outcome.ShadowWarnings);
        Assert.Contains("specific", outcome.ShadowWarnings[0]);
        Assert.Contains("broad", outcome.ShadowWarnings[0]);
    }

    [Fact]
    public void 具體規則排在泛用規則前面時不觸發遮蔽()
    {
        var specific = Rule(id: "specific", sourcePattern: "Security-Auditing", eventIds: new[] { 4625 });
        var broad = Rule(id: "broad", sourcePattern: "Security-Auditing", matchAllEventIds: true);

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { specific, broad });

        Assert.Empty(outcome.ShadowWarnings);
    }

    [Fact]
    public void 不同來源不觸發遮蔽()
    {
        var a = Rule(id: "a", sourcePattern: "disk", eventIds: new[] { 1 });
        var b = Rule(id: "b", sourcePattern: "Ntfs", eventIds: new[] { 1 });

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { a, b });

        Assert.Empty(outcome.ShadowWarnings);
    }

    [Fact]
    public void 停用的規則不會造成遮蔽()
    {
        var disabledBroad = Rule(id: "broad", sourcePattern: "Security-Auditing", matchAllEventIds: true, enabled: false);
        var specific = Rule(id: "specific", sourcePattern: "Security-Auditing", eventIds: new[] { 4625 });

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { disabledBroad, specific });

        Assert.Empty(outcome.ShadowWarnings);
    }

    /// <summary>
    /// 停用的規則本身也不該「被」判定為遮蔽——它依定義就不參與比對，說它永遠不會命中沒有意義。
    /// 這正是 README 建議的改法（停用 builtin ＋另外加一條 custom）會踩到的情境：
    /// 若這裡誤報，selftest 會因為使用者照著文件操作而變成紅燈（遮蔽警告在 selftest 算 fail）。
    /// </summary>
    [Fact]
    public void 停用的規則本身不會被判定為被遮蔽()
    {
        var broad = Rule(id: "broad", sourcePattern: "Security-Auditing", matchAllEventIds: true);
        var disabledSpecific = Rule(id: "specific", sourcePattern: "Security-Auditing", eventIds: new[] { 4625 }, enabled: false);

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { broad, disabledSpecific });

        Assert.Empty(outcome.ShadowWarnings);
    }
}
