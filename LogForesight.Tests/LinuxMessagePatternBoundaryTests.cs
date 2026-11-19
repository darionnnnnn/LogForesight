using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Linux 規則片語的邊界（F4、F5）：sudo 認證失敗的單複數、fail2ban 的 Ban 不吃 Unban、
/// 以及規則驗證對「本地命中但下推取不到」的片語給警告（只警告、不拒絕）。
/// 讀寫 KnownIssueCatalog 共用靜態狀態，納入序列執行的 collection。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class LinuxMessagePatternBoundaryTests : IDisposable
{
    public LinuxMessagePatternBoundaryTests() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
    public void Dispose() => KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());

    private static EventLogEntryData Entry(string program, string message, int minute = 0) => new()
    {
        TimeGenerated = DateTime.Today.AddMinutes(minute),
        LogName = LogAggregator.LinuxLogName,
        Source = program,
        EventId = 0,
        EntryType = LogEntryType.Error,
        Message = message
    };

    // ── F4：sudo 單複數 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("pam_unix(sudo:auth): authentication failure; logname=bob uid=1000")]
    [InlineData("pam_unix(sudo:auth): 3 authentication failures; logname=bob uid=1000")]
    [InlineData("bob : 1 incorrect password attempt ; TTY=pts/0")]
    [InlineData("bob : 3 incorrect password attempts ; TTY=pts/0")]
    public void sudo認證失敗單數與複數訊息都命中(string message)
    {
        var rule = KnownIssueCatalog.FindLinuxRule("sudo", null, message);

        Assert.NotNull(rule);
        Assert.Equal("builtin-linux-sudo-auth-failure", rule!.Id);
    }

    [Fact]
    public void sudo規則片語單複數都明列以便下推到Sentinel整詞片語()
    {
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-linux-sudo-auth-failure");

        Assert.Contains("authentication failure", rule.MessagePatterns);
        Assert.Contains("authentication failures", rule.MessagePatterns);
        Assert.Contains("incorrect password attempt", rule.MessagePatterns);
        Assert.Contains("incorrect password attempts", rule.MessagePatterns);

        var clauses = SentinelQueryBuilder.LinuxRuleProgramClauses(new[] { rule });
        var clause = Assert.Single(clauses);
        Assert.Contains("\"authentication failure\"", clause);
        Assert.Contains("\"authentication failures\"", clause);
        Assert.Contains("\"incorrect password attempts\"", clause);
    }

    // ── F4：fail2ban Ban／Unban ─────────────────────────────────────────

    [Theory]
    [InlineData("NOTICE  [sshd] Ban 203.0.113.9")]
    [InlineData("Ban 203.0.113.9")]                      // 片語在訊息開頭
    [InlineData("NOTICE  [sshd] BAN 203.0.113.9")]       // 不分大小寫
    public void fail2ban的Ban訊息命中封鎖規則(string message)
    {
        var rule = KnownIssueCatalog.FindLinuxRule("fail2ban.actions", null, message);

        Assert.NotNull(rule);
        Assert.Equal("builtin-linux-security-fail2ban-ban", rule!.Id);
    }

    [Theory]
    [InlineData("NOTICE  [sshd] Unban 203.0.113.9")]
    [InlineData("Unban 203.0.113.9")]
    [InlineData("NOTICE  [sshd] UNBAN 203.0.113.9")]
    public void fail2ban的Unban不命中封鎖規則(string message)
    {
        Assert.Null(KnownIssueCatalog.FindLinuxRule("fail2ban.actions", null, message));
    }

    [Fact]
    public void Unban不計入封鎖次數而Ban照計()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(Enumerable.Range(0, 3).Select(i => Entry("fail2ban.actions", $"NOTICE  [sshd] Ban 203.0.113.{i}", i)));
        logs.AddRange(Enumerable.Range(0, 5).Select(i => Entry("fail2ban.actions", $"NOTICE  [sshd] Unban 203.0.113.{i}", 10 + i)));

        var signatures = LogAggregator.Aggregate(logs);

        var ban = Assert.Single(signatures, s => s.RuleId == "builtin-linux-security-fail2ban-ban");
        Assert.Equal(3, ban.Count);
    }

    // ── F5：下推落差警告 ────────────────────────────────────────────────

    private static KnownIssueRule LinuxRule(params string[] patterns) => new()
    {
        Id = "custom-linux-phrase",
        Origin = "custom",
        Enabled = true,
        Scope = "all",
        Platform = "linux",
        ProgramPattern = "sshd",
        MessagePatterns = patterns,
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
    public void 片段Fail產生下推警告但規則仍可儲存()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule("Fail") });

        Assert.Single(outcome.ValidRules);          // 警告不是拒絕
        Assert.Empty(outcome.SkippedRules);
        var warning = Assert.Single(outcome.ShadowWarnings);
        Assert.Contains("custom-linux-phrase", warning);
        Assert.Contains("Fail", warning);
        Assert.Contains("不是完整的詞", warning);
    }

    [Fact]
    public void 完整詞Failed沒有警告()
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule("Failed") });

        Assert.Single(outcome.ValidRules);
        Assert.Empty(outcome.ShadowWarnings);
    }

    [Theory]
    [InlineData("Failed password")]         // 多詞片語、末詞完整
    [InlineData(" Ban ")]                   // 兩端空白＝詞界
    [InlineData("incorrect password attempts")]
    [InlineData("Fail ")]                   // 結尾空白表示詞界：只比對獨立的 Fail
    public void 完整詞或帶詞界的片語沒有警告(string phrase)
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule(phrase) });

        Assert.Empty(outcome.ShadowWarnings);
    }

    [Theory]
    [InlineData("Failed passw")]            // 末詞被切斷
    [InlineData("ailed password")]          // 首詞被切掉前綴
    public void 多詞片語任一端被切斷也警告(string phrase)
    {
        var outcome = RuleValidator.Validate(new List<KnownIssueRule> { LinuxRule(phrase) });

        Assert.Single(outcome.ShadowWarnings);
    }

    [Fact]
    public void 停用的規則不產生下推警告()
    {
        var rule = LinuxRule("Fail");
        var disabled = new KnownIssueRule
        {
            Id = rule.Id, Origin = rule.Origin, Enabled = false, Scope = rule.Scope, Platform = "linux",
            ProgramPattern = rule.ProgramPattern, MessagePatterns = rule.MessagePatterns,
            Category = rule.Category, Severity = rule.Severity, Description = rule.Description,
            CountThreshold = rule.CountThreshold, PlainExplanation = rule.PlainExplanation, Impact = rule.Impact,
            LikelyCauses = rule.LikelyCauses, NextSteps = rule.NextSteps
        };

        Assert.Empty(RuleValidator.Validate(new List<KnownIssueRule> { disabled }).ShadowWarnings);
    }

    [Fact]
    public void 內建種子規則不產生任何下推警告()
    {
        var outcome = RuleValidator.Validate(KnownIssueSeed.CreateRules());

        Assert.Empty(outcome.ShadowWarnings);
    }
}
