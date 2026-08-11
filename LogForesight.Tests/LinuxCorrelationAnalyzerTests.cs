using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// LinuxCorrelationAnalyzer：Linux 主機的【SSH 破解得手】關聯（docs/FEEDBACK-12-PLAN.md §4.5，
/// 批 4C）——與 Windows 版 CorrelationAnalyzer 機制完全不同（regex 解析 msg 文字，不是
/// EventId 群組比對），獨立成一個測試檔。
/// </summary>
public class LinuxCorrelationAnalyzerTests
{
    private static EventLogEntryData SshEvent(string message, DateTime? time = null) => new()
    {
        TimeGenerated = time ?? DateTime.Today.AddHours(1),
        LogName = LogAggregator.LinuxLogName,
        Source = "sshd",
        EventId = 0,
        EntryType = EventLogEntryType.Error,
        Message = message
    };

    /// <summary>用 LogAggregator.Aggregate 產生真正帶正確 EventKey 的簽章清單——不手刻
    /// LogIssueSignature，避免簽章與原始事件（Detect 的第二個參數）各自表述、脫鉤成假陽性。</summary>
    private static List<LogIssueSignature> AggregateIssues(List<EventLogEntryData> logs) =>
        LogAggregator.Aggregate(logs);

    private static List<EventLogEntryData> MakeBruteforceEvents(int count, string user, string ip) =>
        Enumerable.Range(0, count)
            .Select(i => SshEvent($"Failed password for invalid user {user} from {ip} port {50000 + i} ssh2",
                DateTime.Today.AddHours(1).AddMinutes(i)))
            .ToList();

    private static List<EventLogEntryData> MakeAcceptEvents(int count, string user, string ip) =>
        Enumerable.Range(0, count)
            .Select(i => SshEvent($"Accepted password for {user} from {ip} port {50000 + i} ssh2",
                DateTime.Today.AddHours(2).AddMinutes(i)))
            .ToList();

    [Fact]
    public void 帳號重疊時命中且ElevatesDayRisk為true()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(10, "attacker1", "10.0.0.1"));
        logs.AddRange(MakeAcceptEvents(1, "attacker1", "10.0.0.9")); // 帳號同、IP 不同也算重疊

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Equal(IssueSeverity.High, finding.Severity);
        Assert.True(finding.ElevatesDayRisk);
        Assert.Contains("破解得手", finding.Description);
        Assert.Contains("attacker1", finding.Description);
        Assert.Equal(CorrelationPatternIds.LinuxSshBruteSuccess, finding.PatternId);
    }

    [Fact]
    public void IP重疊時命中即使帳號不同()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(10, "root", "10.0.0.5"));
        logs.AddRange(MakeAcceptEvents(1, "admin", "10.0.0.5")); // IP 同、帳號不同

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Contains("10.0.0.5", finding.Description);
    }

    [Fact]
    public void 失敗未達門檻時不命中()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(9, "attacker1", "10.0.0.1")); // 未達 10
        logs.AddRange(MakeAcceptEvents(1, "attacker1", "10.0.0.1"));

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        Assert.Empty(findings);
    }

    [Fact]
    public void 無成功登入事件時不命中()
    {
        var logs = MakeBruteforceEvents(10, "attacker1", "10.0.0.1");

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        Assert.Empty(findings);
    }

    [Fact]
    public void 帳號與IP皆無交集且全數可解析時誠實不命中()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(10, "attacker1", "10.0.0.1"));
        logs.AddRange(MakeAcceptEvents(1, "legit-user", "10.0.0.99")); // 完全不同帳號與 IP

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        Assert.Empty(findings); // 兩邊都解析成功、確實無交集——不該降級也不該誤報
    }

    [Fact]
    public void 部分事件解析失敗時降級為主機級比對且不拉高風險()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(9, "attacker1", "10.0.0.1"));
        logs.Add(SshEvent("Failed password for unparseable-format-line")); // 第 10 筆格式跟不上 regex
        logs.AddRange(MakeAcceptEvents(1, "legit-user", "10.0.0.99")); // 可解析但無交集

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Equal(IssueSeverity.Medium, finding.Severity);
        Assert.False(finding.ElevatesDayRisk); // 降級版不確定性高，不該拉高當日風險
        Assert.Contains("無法解析", finding.Description);
        Assert.Equal(CorrelationPatternIds.LinuxSshBruteUncertain, finding.PatternId);
    }

    [Fact]
    public void 精確命中時優先於降級版_不會同時出現兩筆()
    {
        var logs = new List<EventLogEntryData>();
        logs.AddRange(MakeBruteforceEvents(9, "attacker1", "10.0.0.1"));
        logs.Add(SshEvent("Failed password for unparseable-format-line")); // 有解析失敗
        logs.AddRange(MakeAcceptEvents(1, "attacker1", "10.0.0.9")); // 但精確命中也成立

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Equal(IssueSeverity.High, finding.Severity); // 精確命中版，不是降級版
    }

    [Fact]
    public void regex正確處理invalid_user可選段()
    {
        var logs = new List<EventLogEntryData>();
        // 不帶 "invalid user" 的合法帳號密碼打錯格式
        logs.AddRange(Enumerable.Range(0, 10)
            .Select(i => SshEvent($"Failed password for realuser from 10.0.0.1 port {50000 + i} ssh2",
                DateTime.Today.AddHours(1).AddMinutes(i)))
            .ToList());
        logs.AddRange(MakeAcceptEvents(1, "realuser", "10.0.0.1"));

        var findings = LinuxCorrelationAnalyzer.Detect(AggregateIssues(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Contains("realuser", finding.Description);
    }

    [Fact]
    public void 規則Id常數對應目前種子表()
    {
        // drift 護欄（比照 Windows 版 CorrelationAnalyzerRuleAlignmentTests 的精神）：
        // 這兩個 Id 若在種子表改名，這裡會先炸，而不是讓 Detect 靜默永遠比對不到任何簽章
        var seedIds = KnownIssueSeed.CreateRules().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(LinuxCorrelationAnalyzer.SshBruteforceRuleId, seedIds);
        Assert.Contains(LinuxCorrelationAnalyzer.SshAcceptRuleId, seedIds);
    }
}
