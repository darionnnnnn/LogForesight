using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// LogAggregator.ExtractAccountsAndIps 是【破解得手】【暴力破解→RDP 得手】比對 4625（失敗）與
/// 4624/RDP（成功）是否為同一組帳號/IP 的共用抽取邏輯。RDP 遷移後要能吃 TerminalServices 的
/// 訊息格式（User: DOMAIN\user、來源網路位址），且 DOMAIN\user 要同時產出純帳號，否則與 4625 的
/// 純帳號永遠對不上、交集落空。
/// </summary>
public class LogAggregatorExtractionTests
{
    [Fact]
    public void 抽取RDP英文訊息的帳號與IP()
    {
        var msg = "Remote Desktop Services: User authentication succeeded:\n" +
                  "  User: CONTOSO\\alice\n  Source Network Address: 203.0.113.5";

        var (accounts, ips) = LogAggregator.ExtractAccountsAndIps(new[] { msg });

        Assert.Contains("CONTOSO\\alice", accounts);
        Assert.Contains("alice", accounts);            // DOMAIN\user 同時產出純帳號
        Assert.Contains("203.0.113.5", ips);
    }

    [Fact]
    public void 抽取RDP繁中訊息的帳號()
    {
        var msg = "遠端桌面服務: 使用者驗證成功:\n  使用者: CONTOSO\\bob\n  來源網路位址: 198.51.100.7";

        var (accounts, ips) = LogAggregator.ExtractAccountsAndIps(new[] { msg });

        Assert.Contains("bob", accounts);
        Assert.Contains("198.51.100.7", ips);
    }

    [Fact]
    public void RDP的DOMAIN帳號與4625純帳號可交集()
    {
        var failed = "Account For Which Logon Failed:\n  Account Name: alice";      // 4625 常是純帳號
        var success = "User: CONTOSO\\alice\n  Source Network Address: 203.0.113.5"; // RDP 是 DOMAIN\user

        var (failedAccounts, _) = LogAggregator.ExtractAccountsAndIps(new[] { failed });
        var (successAccounts, _) = LogAggregator.ExtractAccountsAndIps(new[] { success });

        Assert.Contains("alice", failedAccounts.Intersect(successAccounts, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void 略過機器帳號與空值()
    {
        var msg = "Account Name: HOST$\nAccount Name: -\nUser: CONTOSO\\SVC$";

        var (accounts, _) = LogAggregator.ExtractAccountsAndIps(new[] { msg });

        Assert.DoesNotContain("HOST$", accounts);
        Assert.DoesNotContain("-", accounts);
        Assert.DoesNotContain("SVC$", accounts);           // DOMAIN\MACHINE$ 的純帳號也是機器帳號，一併略過
        Assert.DoesNotContain("CONTOSO\\SVC$", accounts);
    }

    [Fact]
    public void 略過本機與零位址IP()
    {
        var msg = "Source Network Address: 127.0.0.1\nSource Network Address: 0.0.0.0\nSource Network Address: 10.0.0.5";

        var (_, ips) = LogAggregator.ExtractAccountsAndIps(new[] { msg });

        Assert.DoesNotContain("127.0.0.1", ips);
        Assert.DoesNotContain("0.0.0.0", ips);
        Assert.Contains("10.0.0.5", ips);
    }
}
