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

    // ── Linux 規則（docs/LINUX-RULES.md §1.3）──────────────────────

    private static KnownIssueRule LinuxRule(
        string id = "custom-linux-test-rule",
        string programPattern = "sshd",
        string eventNamePattern = "",
        string[]? messagePatterns = null,
        bool enabled = true) => new()
        {
            Id = id,
            Origin = "custom",
            Enabled = enabled,
            Scope = "all",
            Platform = "linux",
            ProgramPattern = programPattern,
            EventNamePattern = eventNamePattern,
            MessagePatterns = messagePatterns ?? Array.Empty<string>(),
            Category = IssueCategory.Security,
            Severity = IssueSeverity.Medium,
            Description = "desc",
            CountThreshold = 1,
            PlainExplanation = "explanation",
            Impact = "impact",
            LikelyCauses = new[] { "cause" },
            NextSteps = new[] { "step" }
        };

    [Fact]
    public void Platform不合法值時不合格()
    {
        var rule = Rule();
        var bad = new KnownIssueRule
        {
            Id = rule.Id, Origin = rule.Origin, Enabled = rule.Enabled, Scope = rule.Scope,
            Platform = "macos", SourcePattern = rule.SourcePattern, EventIds = rule.EventIds,
            Category = rule.Category, Severity = rule.Severity, Description = rule.Description,
            CountThreshold = rule.CountThreshold, PlainExplanation = rule.PlainExplanation, Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses, NextSteps = rule.NextSteps
        };

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { bad });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("Platform", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void Linux合格規則_ProgramPattern路通過驗證()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule(programPattern: "sshd") });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.SkippedRules);
    }

    [Fact]
    public void Linux合格規則_EventNamePattern路通過驗證()
    {
        var outcome = RuleValidator.Validate(
            new List<KnownIssueRule> { LinuxRule(programPattern: "", eventNamePattern: "AUTH_FAILURE") });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.SkippedRules);
    }

    [Fact]
    public void Linux規則ProgramPattern與EventNamePattern都空白時不合格()
    {
        var outcome = RuleValidator.Validate(
            new List<KnownIssueRule> { LinuxRule(programPattern: "", eventNamePattern: "") });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("至少要填一個", outcome.SkippedRules[0].Reason);
    }

    // ProgramPattern 會以裸 term 進 Sentinel Lucene filter（sp:{pattern}*，無引號跳脫保護，
    // 見 SentinelQueryBuilder.LinuxRuleProgramClauses），空白/括號/冒號等特殊字元會讓整份
    // Q1 filter 語法壞掉、夜間取數整批失敗——在驗證層擋下（全案體檢揪出的缺口）
    [Theory]
    [InlineData("my prog")]    // 空白：prog* 會脫離 sp 欄位變成獨立 term
    [InlineData("a:b")]        // 冒號：Lucene 欄位分隔符
    [InlineData("run-parts(")] // 括號：Lucene 群組語法
    [InlineData("x*y")]        // 萬用字元：子句自己會補尾端 *，中段 * 不受控
    public void Linux規則ProgramPattern含Lucene特殊字元時不合格(string programPattern)
    {
        var outcome = RuleValidator.Validate(
            new List<KnownIssueRule> { LinuxRule(programPattern: programPattern) });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("僅接受英數字", outcome.SkippedRules[0].Reason);
    }

    [Theory]
    [InlineData("CRON")]
    [InlineData("systemd-networkd")] // - 與 . 是 syslog identifier 的實務形狀，必須放行
    [InlineData("io.podman_3")]
    public void Linux規則ProgramPattern合法字元通過驗證(string programPattern)
    {
        var outcome = RuleValidator.Validate(
            new List<KnownIssueRule> { LinuxRule(programPattern: programPattern) });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.SkippedRules);
    }

    [Fact]
    public void Linux規則不可填SourcePattern等Windows專用欄位()
    {
        var rule = LinuxRule();
        var withSource = new KnownIssueRule
        {
            Id = rule.Id, Origin = rule.Origin, Enabled = rule.Enabled, Scope = rule.Scope,
            Platform = "linux", ProgramPattern = rule.ProgramPattern, SourcePattern = "disk",
            Category = rule.Category, Severity = rule.Severity, Description = rule.Description,
            CountThreshold = rule.CountThreshold, PlainExplanation = rule.PlainExplanation, Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses, NextSteps = rule.NextSteps
        };

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { withSource });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("Windows 專用欄位", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void Windows規則不可填ProgramPattern等Linux專用欄位()
    {
        var rule = Rule();
        var withProgram = new KnownIssueRule
        {
            Id = rule.Id, Origin = rule.Origin, Enabled = rule.Enabled, Scope = rule.Scope,
            Platform = "windows", SourcePattern = rule.SourcePattern, EventIds = rule.EventIds,
            ProgramPattern = "sshd",
            Category = rule.Category, Severity = rule.Severity, Description = rule.Description,
            CountThreshold = rule.CountThreshold, PlainExplanation = rule.PlainExplanation, Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses, NextSteps = rule.NextSteps
        };

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { withProgram });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("Linux 專用欄位", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void MessagePatterns超過條數上限時不合格()
    {
        var tooMany = Enumerable.Range(0, RuleSchemaLimits.MessagePatternsMaxCount + 1)
            .Select(i => $"pattern{i}").ToArray();

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule(messagePatterns: tooMany) });

        Assert.Empty(outcome.ValidRules);
        Assert.Contains("條數上限", outcome.SkippedRules[0].Reason);
    }

    [Fact]
    public void MessagePatterns含空白項目時不合格()
    {
        var outcome = RuleValidator.Validate(
            new List<KnownIssueRule> { LinuxRule(messagePatterns: new[] { "ok", "  " }) });

        Assert.Empty(outcome.ValidRules);
    }

    [Fact]
    public void Windows與Linux規則不會互相判定為遮蔽()
    {
        // Windows 端一條 match-all 泛用規則，Linux 端一條 program-only（無訊息篩選）泛用規則，
        // 兩者範圍字面上「看起來」都很寬，但平台分區的遮蔽偵測必須完全獨立。
        var windowsBroad = Rule(id: "win-broad", sourcePattern: "Security-Auditing", matchAllEventIds: true);
        var linuxBroad = LinuxRule(id: "linux-broad", programPattern: "sshd");

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { windowsBroad, linuxBroad });

        Assert.Empty(outcome.ShadowWarnings);
    }

    [Fact]
    public void Linux規則_不篩訊息的泛用規則排前面時具體規則被判定為遮蔽()
    {
        var broad = LinuxRule(id: "broad", programPattern: "sudo");
        var specific = LinuxRule(id: "specific", programPattern: "sudo", messagePatterns: new[] { "authentication failure" });

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { broad, specific });

        Assert.Single(outcome.ShadowWarnings);
        Assert.Contains("specific", outcome.ShadowWarnings[0]);
        Assert.Contains("broad", outcome.ShadowWarnings[0]);
    }

    [Fact]
    public void Linux規則_有篩訊息的規則排前面時不觸發遮蔽()
    {
        // earlier 有 MessagePatterns 篩選，充分條件不成立（訊息子字串涵蓋關係不做精確判定），
        // 保守不誤報——即使兩條規則 program 相同。
        var withMessages = LinuxRule(id: "with-messages", programPattern: "sudo", messagePatterns: new[] { "authentication failure" });
        var other = LinuxRule(id: "other", programPattern: "sudo", messagePatterns: new[] { "incorrect password attempt" });

        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { withMessages, other });

        Assert.Empty(outcome.ShadowWarnings);
    }
}
