using System.Diagnostics;
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

    [Fact]
    public void 抽取英文4625訊息結構化欄位()
    {
        var msg = @"An account failed to log on.

Subject:
	Security ID:		S-1-5-18
	Account Name:		DC01$
	Account Domain:		CONTOSO
	Logon ID:		0x3E7

Logon Type:			3

Account For Which Logon Failed:
	Security ID:		S-1-0-0
	Account Name:		alice
	Account Domain:		CONTOSO

Failure Information:
	Failure Reason:		Unknown user name or bad password.
	Status:			0xC000006D
	Sub Status:		0xC000006A

Network Information:
	Workstation Name:	WKS01
	Source Network Address:	192.168.1.100";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("alice", detail.Account);
        Assert.False(detail.IsComputerAccount);
        Assert.Equal(3, detail.LogonType);
        Assert.Equal("bad_password", detail.ReasonCode);
        Assert.Equal("WKS01", detail.Source);
    }

    [Fact]
    public void 抽取中文4625訊息結構化欄位()
    {
        var msg = @"帳戶無法登入。

主體:
	安全性識別碼:		S-1-5-18
	帳戶名稱:		DC01$
	帳戶網域:		CONTOSO
	登入識別碼:		0x3E7

登入類型:			3

登入失敗的帳戶:
	安全性識別碼:		S-1-0-0
	帳戶名稱:		alice
	帳戶網域:		CONTOSO

失敗資訊:
	失敗原因:		未知的使用者名稱或密碼不正確。
	狀態:			0xC000006D
	子狀態:			0xC000006A

網路資訊:
	工作站名稱:		WKS01
	來源網路位址:		192.168.1.100";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("alice", detail.Account);
        Assert.False(detail.IsComputerAccount);
        Assert.Equal(3, detail.LogonType);
        Assert.Equal("bad_password", detail.ReasonCode);
        Assert.Equal("WKS01", detail.Source);
    }

    [Fact]
    public void 抽取4625訊息中多次出現AccountName時取後者()
    {
        var msg = @"Subject:
	Account Name:		DC01$
Logon Type:			3
Account For Which Logon Failed:
	Account Name:		target_user";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("target_user", detail.Account);
        Assert.False(detail.IsComputerAccount);
    }

    [Fact]
    public void 抽取4625訊息SubStatus為0x0時退回讀Status()
    {
        var msg = @"Account Name: alice
Status:			0xC000006D
Sub Status:		0x0";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("0xc000006d", detail.ReasonCode);
    }

    [Fact]
    public void 抽取4625訊息未知SubStatus值保留原始小寫字串()
    {
        var msg = @"Account Name: alice
Status:			0xC000006D
Sub Status:		0xC0000099";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("0xc0000099", detail.ReasonCode);
    }

    [Fact]
    public void 抽取4625訊息電腦帳號進明細且IsComputerAccount為true()
    {
        var msg = @"Account For Which Logon Failed:
	Account Name:		WKS01$";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("WKS01$", detail.Account);
        Assert.True(detail.IsComputerAccount);
    }

    [Fact]
    public void 抽取4625訊息WorkstationName為減號時退回讀SourceNetworkAddress()
    {
        var msg = @"Account Name: alice
Workstation Name:	-
Source Network Address:	192.168.1.50";

        var detail = LogAggregator.ExtractLoginFailureDetail(4625, msg);

        Assert.Equal("192.168.1.50", detail.Source);
    }

    [Fact]
    public void 抽取英文4771訊息結構化欄位()
    {
        var msg = @"Kerberos pre-authentication failed.

Account Information:
	Account Name:		bob
	Service Name:		krbtgt/CONTOSO.COM

Network Information:
	Client Address:		::ffff:192.168.1.5
	Client Port:		51234

Additional Information:
	Failure Code:		0x18";

        var detail = LogAggregator.ExtractLoginFailureDetail(4771, msg);

        Assert.Equal("bob", detail.Account);
        Assert.False(detail.IsComputerAccount);
        Assert.Equal("192.168.1.5", detail.Source);
        Assert.Equal("bad_password", detail.ReasonCode);
        Assert.Null(detail.LogonType);
    }

    [Fact]
    public void 抽取4771訊息FailureCode為0x12對應為account_locked_or_disabled()
    {
        var msg = @"Account Information:
	Account Name:		bob
Client Address:		::ffff:192.168.1.5
Failure Code:		0x12";

        var detail = LogAggregator.ExtractLoginFailureDetail(4771, msg);

        Assert.Equal("account_locked_or_disabled", detail.ReasonCode);
    }

    [Fact]
    public void 同一組合出現多次合併為一筆且Count正確()
    {
        var msgs = new[]
        {
            "Account Name: alice\nLogon Type: 3\nSub Status: 0xC000006A\nWorkstation Name: WKS01",
            "Account Name: ALICE\nLogon Type: 3\nSub Status: 0xC000006A\nWorkstation Name: WKS01",
            "Account Name: bob\nLogon Type: 3\nSub Status: 0xC000006A\nWorkstation Name: WKS02"
        };

        var details = LogAggregator.ExtractLoginFailureDetails(4625, msgs);

        Assert.Equal(2, details.Count);

        var first = details[0];
        Assert.Equal("alice", first.Account);
        Assert.Equal(2, first.Count);
        Assert.Equal("WKS01", first.Source);
        Assert.Equal(3, first.LogonType);
        Assert.Equal("bad_password", first.ReasonCode);

        var second = details[1];
        Assert.Equal("bob", second.Account);
        Assert.Equal(1, second.Count);
        Assert.Equal("WKS02", second.Source);
        Assert.Equal(3, second.LogonType);
        Assert.Equal("bad_password", second.ReasonCode);
    }

    [Fact]
    public void 非登入失敗簽章的LoginFailureDetails為null()
    {
        var logs = new List<EventLogEntryData>
        {
            new()
            {
                LogName = "System",
                Source = "disk",
                EventId = 153,
                EntryType = EventLogEntryType.Warning,
                Message = "The IO operation at logical block address ... failed.",
                TimeGenerated = DateTime.Today
            }
        };

        var sigs = LogAggregator.Aggregate(logs);
        var sig = Assert.Single(sigs);
        Assert.Null(sig.LoginFailureDetails);
    }

    [Fact]
    public void 超過50組不同組合時最多保留50筆且依Count由大到小排序()
    {
        var msgs = new List<string>();
        for (int i = 1; i <= 60; i++)
        {
            int count = (i == 1) ? 5 : 1;
            for (int j = 0; j < count; j++)
            {
                msgs.Add($"Account Name: user{i}\nLogon Type: 3\nSub Status: 0xC000006A\nWorkstation Name: WKS{i}");
            }
        }

        var details = LogAggregator.ExtractLoginFailureDetails(4625, msgs);

        Assert.Equal(50, details.Count);
        Assert.Equal("user1", details[0].Account);
        Assert.Equal(5, details[0].Count);

        for (int i = 0; i < details.Count - 1; i++)
        {
            Assert.True(details[i].Count >= details[i + 1].Count);
        }
    }

    [Fact]
    public void ExtractAccountsAndIps既有行為未改變電腦帳號仍被跳過()
    {
        var msg = "Account Name: WKS01$\nSource Network Address: 10.0.0.1";
        var (accounts, ips) = LogAggregator.ExtractAccountsAndIps(new[] { msg });

        Assert.DoesNotContain("WKS01$", accounts);
        Assert.Contains("10.0.0.1", ips);
    }
}
