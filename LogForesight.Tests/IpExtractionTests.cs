using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests;

/// <summary>IP 擷取的單一入口（IpExtractor）與 sshd 成功／失敗登入解析的 IPv6、認證方式放寬。</summary>
public class IpExtractionTests
{
    private static EventLogEntryData SshEvent(string message, DateTime time) => new()
    {
        TimeGenerated = time,
        LogName = LogAggregator.LinuxLogName,
        Source = "sshd",
        EventId = 0,
        EntryType = LogEntryType.Error,
        Message = message
    };

    [Theory]
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("010.001.002.003", "010.001.002.003")] // IPv4 保留原字串：與既有紀錄逐字相同
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3")]
    [InlineData("fe80::1%eth0", "fe80::1")]
    public void 合法位址依統一寫法回傳(string token, string expected) =>
        Assert.Equal(expected, IpExtractor.Normalize(token));

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("10.1")]
    [InlineData("12345")]
    [InlineData("12:30:45")]
    [InlineData("host.example.com")]
    [InlineData("")]
    public void 不合法的字串不當成位址(string token) => Assert.Null(IpExtractor.Normalize(token));

    [Fact]
    public void 文字中同時抽出IPv4與IPv6_內嵌IPv4不重複計算()
    {
        var found = IpExtractor.ExtractAll("src 10.1.2.3 and 2001:db8::5 and ::ffff:192.0.2.9 done");
        Assert.Equal(new[] { "10.1.2.3", "2001:db8::5", "192.0.2.9" }, found);
    }

    [Fact]
    public void 文字中的999點1點1點1與時間與程式識別字不被抽出()
    {
        Assert.Empty(IpExtractor.ExtractAll("from 999.1.1.1 at 12:30:45.123 in Foo::Bar and Class::Add"));
    }

    [Fact]
    public void 句尾黏著標點的IPv6仍抽得到()
    {
        Assert.Equal("2001:db8::1", IpExtractor.First("source was 2001:db8::1."));
        Assert.Equal("2001:db8::1", IpExtractor.First("source 2001:db8::1: refused"));
    }

    [Theory]
    [InlineData("Failed password for root from 2001:db8::1 port 4000 ssh2", "root", "2001:db8::1")]
    [InlineData("Failed password for invalid user bob from ::ffff:10.1.2.3 port 4000 ssh2", "bob", "10.1.2.3")]
    [InlineData("Invalid user alice from 2001:db8::2 port 22", "alice", "2001:db8::2")]
    [InlineData("Failed password for root from 10.9.8.7 port 4000 ssh2", "root", "10.9.8.7")]
    public void IPv6來源的失敗登入解析得到帳號與位址(string message, string user, string ip)
    {
        Assert.True(LinuxAuthParser.TryParseAuthFailure(message, out var u, out var i, out _));
        Assert.Equal(user, u);
        Assert.Equal(ip, i);
    }

    [Theory]
    [InlineData("Accepted password for root from 2001:db8::1 port 4000 ssh2", "root", "2001:db8::1")]
    [InlineData("Accepted keyboard-interactive/pam for admin from 10.0.0.5 port 22 ssh2", "admin", "10.0.0.5")]
    [InlineData("Accepted gssapi-with-mic for carol from ::ffff:10.0.0.6 port 22 ssh2", "carol", "10.0.0.6")]
    [InlineData("Accepted publickey for dave from fe80::1%eth0 port 22 ssh2: RSA SHA256:abc", "dave", "fe80::1")]
    public void 成功登入接受任何認證方式與IPv6(string message, string user, string ip)
    {
        Assert.True(LinuxAuthParser.TryParseAccepted(message, out var u, out var i));
        Assert.Equal(user, u);
        Assert.Equal(ip, i);
    }

    [Fact]
    public void 成功登入的位址是999點1點1點1時_帳號照取_位址為空()
    {
        Assert.True(LinuxAuthParser.TryParseAccepted("Accepted password for root from 999.1.1.1 port 22 ssh2", out var user, out var ip));
        Assert.Equal("root", user);
        Assert.Equal(string.Empty, ip);
    }

    [Fact]
    public void 非成功登入行不被當成成功登入()
    {
        Assert.False(LinuxAuthParser.TryParseAccepted("Failed password for root from 10.0.0.1 port 22 ssh2", out _, out _));
        Assert.False(LinuxAuthParser.TryParseAccepted("Connection accepted for root from 10.0.0.1 port 22", out _, out _));
    }

    [Fact]
    public void keyboard_interactive的成功登入觸發破解得手()
    {
        var t0 = DateTime.Today.AddHours(1);
        var logs = Enumerable.Range(0, 10)
            .Select(i => SshEvent($"Failed password for invalid user victim from 10.0.0.9 port {40000 + i} ssh2", t0.AddMinutes(i)))
            .ToList();
        logs.Add(SshEvent("Accepted keyboard-interactive/pam for victim from 10.0.0.9 port 40100 ssh2", t0.AddHours(1)));

        var findings = LinuxCorrelationAnalyzer.Detect(LogAggregator.Aggregate(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Equal(CorrelationPatternIds.LinuxSshBruteSuccess, finding.PatternId);
        Assert.Contains("victim", finding.Description);
    }

    [Fact]
    public void IPv6來源的破解得手以位址交集命中_壓縮與完整寫法視為同一位址()
    {
        var t0 = DateTime.Today.AddHours(1);
        var logs = Enumerable.Range(0, 10)
            .Select(i => SshEvent($"Failed password for invalid user a{i} from 2001:db8::1 port {40000 + i} ssh2", t0.AddMinutes(i)))
            .ToList();
        logs.Add(SshEvent("Accepted publickey for other from 2001:0DB8:0:0:0:0:0:1 port 40100 ssh2", t0.AddHours(1)));

        var findings = LinuxCorrelationAnalyzer.Detect(LogAggregator.Aggregate(logs), logs);

        var finding = Assert.Single(findings);
        Assert.Equal(CorrelationPatternIds.LinuxSshBruteSuccess, finding.PatternId);
        Assert.Contains("2001:db8::1", finding.Description);
    }

    [Fact]
    public void Windows訊息中的IPv6來源也進入帳號與位址彙總_迴路位址排除()
    {
        var msg = "Account Name:\tbob\nSource Network Address:\t2001:db8::7\n其他 ::1 與 127.0.0.1 與 999.1.1.1";
        var (accounts, ips) = LogAggregator.ExtractAccountsAndIps(new[] { msg });
        Assert.Contains("bob", accounts);
        Assert.Equal(new[] { "2001:db8::7" }, ips.ToArray());
    }

    [Fact]
    public void 失敗明細的來源位址_IPv4保持原樣_IPv6映射還原為IPv4()
    {
        var v4 = LogAggregator.ExtractLoginFailureDetail(4625,
            "Account Name:\tbob\nSource Network Address:\t10.20.30.40\nStatus:\t0xC000006D");
        var mapped = LogAggregator.ExtractLoginFailureDetail(4625,
            "Account Name:\tbob\nSource Network Address:\t::ffff:10.20.30.40\nStatus:\t0xC000006D");
        Assert.Equal("10.20.30.40", v4.Source);
        Assert.Equal("10.20.30.40", mapped.Source);
    }

    [Fact]
    public void 種子的成功登入規則片語為整詞Accepted且種子版本為14()
    {
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-linux-ssh-accept");
        Assert.Equal(new[] { "Accepted" }, rule.MessagePatterns);
        Assert.Equal(14, KnownIssueSeed.Version);
    }
}
