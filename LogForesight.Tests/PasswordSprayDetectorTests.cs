using LogForesight.Core.Analysis;
using Xunit;
using static LogForesight.Tests.TestData;

namespace LogForesight.Tests;

public class PasswordSprayDetectorTests
{
    [Fact]
    public void 測試01_典型噴灑命中_同一來源打15個帳號各2次_產生High嚴重度關聯訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 30, IssueSeverity.High, IssueCategory.Security);
        issue.LoginFailureDetails = Enumerable.Range(1, 15)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "10.0.0.9",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        var finding = Assert.Single(findings);
        Assert.Equal(CorrelationPatternIds.PasswordSpray, finding.PatternId);
        Assert.Equal(IssueSeverity.High, finding.Severity);
        Assert.True(finding.ElevatesDayRisk);
        Assert.Equal("【密碼噴灑】來源 10.0.0.9 對 15 個不同帳號嘗試登入，每個帳號僅失敗 2 次以內——刻意避開帳號鎖定門檻的密碼噴灑特徵，應立即封鎖該來源並檢查是否有帳號已被猜中", finding.Description);
    }

    [Fact]
    public void 測試02_帳號數不足不命中_同一來源打9個帳號各2次_不產生訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 18, IssueSeverity.High, IssueCategory.Security);
        issue.LoginFailureDetails = Enumerable.Range(1, 9)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "10.0.0.9",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試03_每帳號次數過高不命中_同一來源打15個帳號其中一個失敗4次_不產生訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 32, IssueSeverity.High, IssueCategory.Security);
        var details = Enumerable.Range(1, 14)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "10.0.0.9",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();
        details.Add(new LoginFailureDetail
        {
            Account = "user15",
            Source = "10.0.0.9",
            LogonType = 3,
            ReasonCode = "bad_password",
            Count = 4
        });
        issue.LoginFailureDetails = details;

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試04_邊界值命中_10個帳號各3次_產生關聯訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 30, IssueSeverity.High, IssueCategory.Security);
        issue.LoginFailureDetails = Enumerable.Range(1, 10)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "10.0.0.9",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 3
            })
            .ToList();

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        var finding = Assert.Single(findings);
        Assert.Equal(CorrelationPatternIds.PasswordSpray, finding.PatternId);
        Assert.Equal(IssueSeverity.High, finding.Severity);
        Assert.True(finding.ElevatesDayRisk);
        Assert.Equal("【密碼噴灑】來源 10.0.0.9 對 10 個不同帳號嘗試登入，每個帳號僅失敗 3 次以內——刻意避開帳號鎖定門檻的密碼噴灑特徵，應立即封鎖該來源並檢查是否有帳號已被猜中", finding.Description);
    }

    [Fact]
    public void 測試05_同帳號多筆明細要先合併再判次數_alice合併後達4次_不命中()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 32, IssueSeverity.High, IssueCategory.Security);
        var details = Enumerable.Range(1, 14)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "10.0.0.9",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();
        // alice 拆成兩筆明細（各 2 次，合併後 4 次）
        details.Add(new LoginFailureDetail
        {
            Account = "alice",
            Source = "10.0.0.9",
            LogonType = 3,
            ReasonCode = "bad_password",
            Count = 2
        });
        details.Add(new LoginFailureDetail
        {
            Account = "alice",
            Source = "10.0.0.9",
            LogonType = 5,
            ReasonCode = "bad_password",
            Count = 2
        });
        issue.LoginFailureDetails = details;

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試06_來源為空字串不參與_15個帳號但Source為空字串_不產生訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 30, IssueSeverity.High, IssueCategory.Security);
        issue.LoginFailureDetails = Enumerable.Range(1, 15)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試07_多來源各自判定_來源A命中且來源B不命中_只產生來源A訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 36, IssueSeverity.High, IssueCategory.Security);
        var details = Enumerable.Range(1, 15)
            .Select(i => new LoginFailureDetail
            {
                Account = $"userA_{i}",
                Source = "10.0.0.1",
                LogonType = 3,
                ReasonCode = "bad_password",
                Count = 2
            })
            .ToList();
        details.AddRange(Enumerable.Range(1, 3).Select(i => new LoginFailureDetail
        {
            Account = $"userB_{i}",
            Source = "10.0.0.2",
            LogonType = 3,
            ReasonCode = "bad_password",
            Count = 2
        }));
        issue.LoginFailureDetails = details;

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        var finding = Assert.Single(findings);
        Assert.Contains("10.0.0.1", finding.Description);
        Assert.DoesNotContain("10.0.0.2", finding.Description);
    }

    [Fact]
    public void 測試08_殘留形狀不命中_單一帳號失敗40次_不產生噴灑訊號()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        issue.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試09_Linux簽章同樣適用_sshd同一來源打12個帳號各1次_產生關聯訊號()
    {
        var issue = Sig("Linux", "sshd", 0, 12, IssueSeverity.High, IssueCategory.Security);
        issue.EventKey = "builtin-linux-ssh-bruteforce";
        issue.LoginFailureDetails = Enumerable.Range(1, 12)
            .Select(i => new LoginFailureDetail
            {
                Account = $"user{i}",
                Source = "192.168.1.50",
                LogonType = null,
                ReasonCode = "bad_password",
                Count = 1
            })
            .ToList();

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        var finding = Assert.Single(findings);
        Assert.Equal(CorrelationPatternIds.PasswordSpray, finding.PatternId);
        Assert.Equal(IssueSeverity.High, finding.Severity);
        Assert.True(finding.ElevatesDayRisk);
        Assert.Equal("【密碼噴灑】來源 192.168.1.50 對 12 個不同帳號嘗試登入，每個帳號僅失敗 1 次以內——刻意避開帳號鎖定門檻的密碼噴灑特徵，應立即封鎖該來源並檢查是否有帳號已被猜中", finding.Description);
    }

    [Fact]
    public void 測試10_非登入失敗簽章不受影響_LoginFailureDetails為null_不產生訊號()
    {
        var issue = Sig("System", "Service Control Manager", 7031, 5, IssueSeverity.High, IssueCategory.Service);
        issue.LoginFailureDetails = null;

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Empty(findings);
    }

    [Fact]
    public void 測試11_訊號數上限5_8個來源命中時只回傳相異帳號數最多的前5個()
    {
        var issue = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 200, IssueSeverity.High, IssueCategory.Security);
        var details = new List<LoginFailureDetail>();

        // 來源 1: 11 帳號, 來源 2: 12 帳號, ..., 來源 8: 18 帳號
        for (int srcIdx = 1; srcIdx <= 8; srcIdx++)
        {
            var src = $"10.0.0.{srcIdx}";
            int numAccounts = 10 + srcIdx; // 11 ~ 18 個帳號
            for (int accIdx = 1; accIdx <= numAccounts; accIdx++)
            {
                details.Add(new LoginFailureDetail
                {
                    Account = $"user_{srcIdx}_{accIdx}",
                    Source = src,
                    LogonType = 3,
                    ReasonCode = "bad_password",
                    Count = 1
                });
            }
        }
        issue.LoginFailureDetails = details;

        var findings = PasswordSprayDetector.Detect(new List<LogIssueSignature> { issue });

        Assert.Equal(5, findings.Count);
        // 依相異帳號數由大到小取前 5：來源 8 (18), 7 (17), 6 (16), 5 (15), 4 (14)
        Assert.Contains("10.0.0.8", findings[0].Description);
        Assert.Contains("10.0.0.7", findings[1].Description);
        Assert.Contains("10.0.0.6", findings[2].Description);
        Assert.Contains("10.0.0.5", findings[3].Description);
        Assert.Contains("10.0.0.4", findings[4].Description);
    }

    [Fact]
    public void 測試12_CorrelationPatternIds_IsValid認得新模式()
    {
        Assert.True(CorrelationPatternIds.IsValid("password-spray"));
        Assert.True(CorrelationPatternIds.IsValid(CorrelationPatternIds.PasswordSpray));
    }
}
