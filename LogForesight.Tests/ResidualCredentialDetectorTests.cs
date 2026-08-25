using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using Xunit;
using static LogForesight.Tests.TestData;

namespace LogForesight.Tests;

public class ResidualCredentialDetectorTests
{
    private static DailyAnalysisRecord CreateHistoryRecord(DateTime date, List<LoginFailureDetail>? details)
    {
        var sig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, details?.Sum(d => d.Count) ?? 0, IssueSeverity.High, IssueCategory.Security);
        sig.LoginFailureDetails = details;
        return new DailyAnalysisRecord
        {
            Date = date.Date,
            Host = "TEST-SRV",
            TopIssues = new List<LogIssueSignature> { sig }
        };
    }

    [Fact]
    public void 情境01_典型OTP殘留命中_調降嚴重度並填入判定依據()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "admin", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 1 },
            new() { Account = "user2", Source = "10.0.0.2", LogonType = 3, ReasonCode = "bad_password", Count = 1 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.Medium, todaySig.Severity);
        Assert.False(todaySig.ElevatesDayRisk);
        Assert.NotNull(todaySig.ResidualCredentialBasis);
        Assert.Equal("疑似殘留憑證重試：svc_backup 自 WKS01 重複失敗 40 次（網路登入，密碼錯誤），近 7 天已重複出現", todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境02_首日不命中_跨日確認制核心_維持High嚴重度()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "admin", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 1 },
            new() { Account = "user2", Source = "10.0.0.2", LogonType = 3, ReasonCode = "bad_password", Count = 1 }
        };

        var history = new List<DailyAnalysisRecord>(); // 首日：歷史無任何紀錄

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.True(todaySig.ElevatesDayRisk);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 重新分析同一天時_該日自身的舊紀錄不得充當跨日重現()
    {
        // Append 不刪舊列：重新分析已分析過的日期時，ReadRecent 會撈到該日的舊紀錄。
        // 若把它當成「另一天」，首日就會達到跨日門檻，跨日確認制形同虛設。
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        var details = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "admin", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 2 }
        };
        todaySig.LoginFailureDetails = details;

        // 歷史裡只有 targetDate 當天自己的舊紀錄（同一組合），沒有任何更早的日子
        var history = new List<DailyAnalysisRecord> { CreateHistoryRecord(targetDate, details) };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.True(todaySig.ElevatesDayRisk);
    }

    [Fact]
    public void 情境03_密碼噴灑不命中_集中度不足()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 30, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        // 同一來源打 15 個不同帳號、每個 2 次
        todaySig.LoginFailureDetails = Enumerable.Range(1, 15)
            .Select(i => new LoginFailureDetail { Account = $"user_{i}", Source = "10.0.0.9", LogonType = 3, ReasonCode = "bad_password", Count = 2 })
            .ToList();

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "user_1", Source = "10.0.0.9", LogonType = 3, ReasonCode = "bad_password", Count = 2 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境04_分散暴力破解不命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 24, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        // 單一帳號 administrator，來自 12 個不同來源，LogonType 10
        todaySig.LoginFailureDetails = Enumerable.Range(1, 12)
            .Select(i => new LoginFailureDetail { Account = "administrator", Source = $"192.168.1.{i}", LogonType = 10, ReasonCode = "bad_password", Count = 2 })
            .ToList();

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "administrator", Source = "192.168.1.1", LogonType = 10, ReasonCode = "bad_password", Count = 2 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境05_帳號不存在不命中_ReasonCode為unknown_user()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_unknown", Source = "WKS01", LogonType = 3, ReasonCode = "unknown_user", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_unknown", Source = "WKS01", LogonType = 3, ReasonCode = "unknown_user", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境06_互動登入不命中_LogonType為2()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "john_doe", Source = "WKS01", LogonType = 2, ReasonCode = "bad_password", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "john_doe", Source = "WKS01", LogonType = 2, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境07_RDP登入不命中_LogonType為10()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "administrator", Source = "192.168.1.50", LogonType = 10, ReasonCode = "bad_password", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "administrator", Source = "192.168.1.50", LogonType = 10, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境08_空帳號不標記()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境09_事件4771走替代判準命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4771, 50, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "user1", Source = "10.0.0.1", LogonType = null, ReasonCode = "bad_password", Count = 45 },
            new() { Account = "user2", Source = "10.0.0.2", LogonType = null, ReasonCode = "bad_password", Count = 5 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "user1", Source = "10.0.0.1", LogonType = null, ReasonCode = "bad_password", Count = 30 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.Medium, todaySig.Severity);
        Assert.False(todaySig.ElevatesDayRisk);
        Assert.Equal("疑似殘留憑證重試：user1 自 10.0.0.1 重複失敗 45 次（密碼錯誤），近 7 天已重複出現", todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境10_事件4771單一組集中度不足不命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4771, 100, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        // 前 2 組合計 85% (≥ 80%)，但單一最大組只有 60% (< 80%)
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "user1", Source = "10.0.0.1", LogonType = null, ReasonCode = "bad_password", Count = 60 },
            new() { Account = "user2", Source = "10.0.0.2", LogonType = null, ReasonCode = "bad_password", Count = 25 },
            new() { Account = "user3", Source = "10.0.0.3", LogonType = null, ReasonCode = "bad_password", Count = 15 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "user1", Source = "10.0.0.1", LogonType = null, ReasonCode = "bad_password", Count = 30 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境11_Linux_ssh認證失敗命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Linux", "sshd", 0, 100, IssueSeverity.High, IssueCategory.Security);
        todaySig.EventKey = "builtin-linux-ssh-bruteforce";
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "deployer", Source = "192.168.1.100", LogonType = null, ReasonCode = "bad_password", Count = 95 },
            new() { Account = "root", Source = "10.0.0.5", LogonType = null, ReasonCode = "bad_password", Count = 5 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            new()
            {
                Date = targetDate.AddDays(-2),
                Host = "linux-srv",
                TopIssues = new List<LogIssueSignature>
                {
                    new()
                    {
                        LogName = "Linux",
                        Source = "sshd",
                        EventKey = "builtin-linux-ssh-bruteforce",
                        LoginFailureDetails = new List<LoginFailureDetail>
                        {
                            new() { Account = "deployer", Source = "192.168.1.100", LogonType = null, ReasonCode = "bad_password", Count = 50 }
                        }
                    }
                }
            }
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.Medium, todaySig.Severity);
        Assert.False(todaySig.ElevatesDayRisk);
        Assert.Equal("疑似殘留憑證重試：deployer 自 192.168.1.100 重複失敗 95 次（密碼錯誤），近 7 天已重複出現", todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境12_歷史無明細視為未重現()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        // 模擬 A1 之前的舊資料：TopIssues 有 4625 但 LoginFailureDetails 為 null
        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), null)
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境13_跨日比對忽略ReasonCode差異仍算重現()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        // 今日是 account_locked
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "account_locked", Count = 40 }
        };

        // 歷史那天是 bad_password
        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.Medium, todaySig.Severity);
        Assert.Equal("疑似殘留憑證重試：svc_backup 自 WKS01 重複失敗 40 次（網路登入，帳號已鎖定），近 7 天已重複出現", todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境14_非登入失敗簽章不受影響()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("System", "Disk", 153, 10, IssueSeverity.High, IssueCategory.Storage);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = null;

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Null(todaySig.ResidualCredentialBasis);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.True(todaySig.ElevatesDayRisk);
    }

    [Fact]
    public void 情境15_7天窗邊界_targetDate減6天在窗內命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        // targetDate - 6 天（2026-08-19）在 7 天窗內
        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-6), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.Medium, todaySig.Severity);
    }

    [Fact]
    public void 情境15_7天窗邊界_targetDate減7天在窗外不命中()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        // targetDate - 7 天（2026-08-18）在 7 天窗外
        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-7), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
        Assert.Null(todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 情境16_基準文字內容斷言包含關鍵詞()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "admin", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 2 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.NotNull(todaySig.ResidualCredentialBasis);
        Assert.Contains("svc_backup", todaySig.ResidualCredentialBasis);
        Assert.Contains("WKS01", todaySig.ResidualCredentialBasis);
        Assert.Contains("網路登入", todaySig.ResidualCredentialBasis);
        Assert.Contains("密碼錯誤", todaySig.ResidualCredentialBasis);
    }

    [Theory]
    [InlineData(4, "排程工作")]
    [InlineData(5, "服務")]
    [InlineData(8, "登入類型 8")]
    public void Windows4625_LogonType白話轉譯正確(int logonType, string expectedType)
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_app", Source = "SRV01", LogonType = logonType, ReasonCode = "bad_password", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_app", Source = "SRV01", LogonType = logonType, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        if (logonType is 4 or 5)
        {
            Assert.True(todaySig.ResidualCredentialRetry);
            Assert.Contains(expectedType, todaySig.ResidualCredentialBasis);
        }
        else
        {
            // LogonType 8 is not mechanical (not 3,4,5)
            Assert.False(todaySig.ResidualCredentialRetry);
        }
    }

    [Theory]
    [InlineData("password_expired", "密碼已過期")]
    [InlineData("account_locked", "帳號已鎖定")]
    [InlineData("account_disabled", "帳號已停用")]
    [InlineData("account_locked_or_disabled", "帳號已鎖定或停用")]
    [InlineData("account_expired", "帳號已到期")]
    [InlineData("logon_time_restriction", "登入時段限制")]
    [InlineData("workstation_restriction", "工作站限制")]
    [InlineData("unknown_reason_code", "原因不明")]
    [InlineData("", "原因不明")]
    public void ReasonCode白話轉譯正確(string reasonCode, string expectedReason)
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_app", Source = "SRV01", LogonType = 3, ReasonCode = reasonCode, Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_app", Source = "SRV01", LogonType = 3, ReasonCode = reasonCode, Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Contains(expectedReason, todaySig.ResidualCredentialBasis);
    }

    [Fact]
    public void 來源為空時白話顯示來源不明()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_app", Source = "", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_app", Source = "", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
        Assert.Equal("疑似殘留憑證重試：svc_app 自 來源不明 重複失敗 40 次（網路登入，密碼錯誤），近 7 天已重複出現", todaySig.ResidualCredentialBasis);
    }
}
