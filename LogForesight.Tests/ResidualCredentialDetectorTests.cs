using System.Diagnostics;
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
    public void 明細被截斷時不得標殘留_避免分母縮水把攻擊形狀誤判成集中()
    {
        // 明細封頂 50 組時丟掉的都是尾巴小組。若用「保留下來的組」當分母，
        // 分散的攻擊形狀（多帳號多來源）會被算成高集中度而誤判成殘留並靜音。
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 300, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        // 保留下來的明細看起來很集中（前兩組佔 100%），但那是截斷造成的假象
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "svc_task", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 38 }
        };
        // 總量設成 85：78/85 = 92% ≥ 80%，集中度條件會過——此時只有「截斷守門」擋得下來，
        // 這樣突變測試才隔離得出守門本身是否生效（分母正確性由下一個測試負責）。
        todaySig.LoginFailureTotalCount = 85;
        todaySig.LoginFailureDetailsTruncated = true;

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
    }

    [Fact]
    public void 集中度以截斷前總量為分母()
    {
        // 未截斷但總量欄位有值時，分母要用總量（與明細加總一致）——
        // 這條釘住「分母來源」這個決定，避免日後有人改回用保留組數加總。
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 100, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "other", Source = "WKS02", LogonType = 3, ReasonCode = "bad_password", Count = 60 }
        };
        todaySig.LoginFailureTotalCount = 100;
        todaySig.LoginFailureDetailsTruncated = false;

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "other", Source = "WKS02", LogonType = 3, ReasonCode = "bad_password", Count = 30 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        // 前兩組佔 100/100 = 100% ≥ 80%，且型態機械、非 unknown_user、跨日重現 → 命中
        Assert.True(todaySig.ResidualCredentialRetry);
    }

    [Fact]
    public void 舊資料無總量欄位時退回明細加總_照常判定()
    {
        // A1 之前寫入的 ContentJson 沒有 LoginFailureTotalCount／Truncated 欄位（反序列化為 0／false）。
        // 未滿封頂組數時，分母退回明細加總是唯一可行也正確的相容路徑——這條測試釘住 fallback，
        // 日後有人拿掉它，這裡會紅。
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 42, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        todaySig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 },
            new() { Account = "admin", Source = "10.0.0.1", LogonType = 3, ReasonCode = "bad_password", Count = 2 }
        };
        // 刻意不設 LoginFailureTotalCount（模擬舊資料）

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.True(todaySig.ResidualCredentialRetry);
    }

    [Fact]
    public void 舊資料滿封頂組數且無總量_視為疑似截斷不判定()
    {
        // 舊資料當初若真的被截斷，Truncated 反序列化為 false、總量為 0——守門會失效。
        // 保守推測：「滿 50 組且無總量」視為疑似截斷、不判定（誤傷的只是剛好 50 組的合法情況，
        // 代價是維持既有攻擊語意，方向安全）。
        var targetDate = new DateTime(2026, 8, 25);
        var todaySig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 200, IssueSeverity.High, IssueCategory.Security);
        todaySig.ElevatesDayRisk = true;
        var details = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 100 },
            new() { Account = "svc_task", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 60 }
        };
        // 補滿到剛好 50 組（尾巴小組）
        for (int i = 0; i < 48; i++)
        {
            details.Add(new LoginFailureDetail { Account = $"u{i}", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 1 });
        }
        todaySig.LoginFailureDetails = details;
        // 刻意不設總量與截斷旗標（模擬舊資料）

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { todaySig }, history, targetDate);

        Assert.False(todaySig.ResidualCredentialRetry);
        Assert.Equal(IssueSeverity.High, todaySig.Severity);
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

    // ── 4740 帳號鎖定與殘留憑證交叉比對（A4c）─────────────────────────

    /// <summary>
    /// A4c：4740 帳號鎖定簽章的帳號若與當日殘留憑證重試帳號有交集，
    /// 嚴重度由 High 降為 Medium、清除 ElevatesDayRisk、填入關聯依據說明。
    /// </summary>
    [Fact]
    public void 事件4740_與殘留帳號交集時降級且填入依據()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = "相關帳號(1個): svc_backup";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.Medium, lockoutSig.Severity);
        Assert.False(lockoutSig.ElevatesDayRisk);
        Assert.False(lockoutSig.ResidualCredentialRetry);
        Assert.NotNull(lockoutSig.ResidualCredentialBasis);
        Assert.Contains("svc_backup", lockoutSig.ResidualCredentialBasis);
        Assert.Equal("可能由殘留憑證重試觸發：帳號 svc_backup 當日有疑似殘留的重複登入失敗", lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// A4c：4740 帳號與當日殘留帳號不同時維持 High 嚴重度，不做任何調整。
    /// </summary>
    [Fact]
    public void 事件4740_帳號不同時維持High嚴重度()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = "相關帳號(1個): administrator";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.High, lockoutSig.Severity);
        Assert.True(lockoutSig.ElevatesDayRisk);
        Assert.Null(lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// A4c：4740 的 KeyDetails 為 null 時不做任何調整（保守，維持 High）。
    /// </summary>
    [Fact]
    public void 事件4740_KeyDetails為null時維持High嚴重度()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = null;

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.High, lockoutSig.Severity);
        Assert.True(lockoutSig.ElevatesDayRisk);
        Assert.Null(lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// A4c：4740 簽章的 ResidualCredentialRetry 旗標不得被設為 true（它不是登入失敗簽章）。
    /// </summary>
    [Fact]
    public void 事件4740_ResidualCredentialRetry旗標不得被設為true()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = "相關帳號(1個): svc_backup";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.False(lockoutSig.ResidualCredentialRetry);
    }

    /// <summary>
    /// A4c 回歸保護：當日沒有任何殘留簽章時，4740 簽章完全不受影響。
    /// </summary>
    [Fact]
    public void 事件4740_無任何殘留簽章時不受影響()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = "相關帳號(1個): svc_backup";

        var history = new List<DailyAnalysisRecord>();

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.High, lockoutSig.Severity);
        Assert.True(lockoutSig.ElevatesDayRisk);
        Assert.Null(lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// A4c：KeyDetails 帳號解析含省略號時，正確去除省略號並解析出完整帳號清單。
    /// </summary>
    [Fact]
    public void KeyDetails帳號解析_含省略號時正確去除省略號()
    {
        var keyDetails = "相關帳號(8個): a, b, c, d, e…；來源IP(2個): 10.0.0.1, 10.0.0.2";
        var accounts = ResidualCredentialDetector.ExtractAccountsFromKeyDetails(keyDetails);

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, accounts);
    }

    /// <summary>
    /// A4c：4740 命中多個殘留帳號時以頓號串接。
    /// </summary>
    [Fact]
    public void 事件4740_多個殘留帳號交集時以頓號串接()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var sig1 = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        sig1.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };
        var sig2 = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 30, IssueSeverity.High, IssueCategory.Security);
        sig2.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "svc_sync", Source = "WKS02", LogonType = 3, ReasonCode = "bad_password", Count = 30 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 2, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyDetails = "相關帳號(2個): svc_backup, svc_sync";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "svc_backup", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 },
                new() { Account = "svc_sync", Source = "WKS02", LogonType = 3, ReasonCode = "bad_password", Count = 15 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { sig1, sig2, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.Medium, lockoutSig.Severity);
        Assert.Equal("可能由殘留憑證重試觸發：帳號 svc_backup、svc_sync 當日有疑似殘留的重複登入失敗", lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// 修 bug 的證據測試：4740 簽章有 7 個帳號，殘留帳號是第 6 個
    /// （舊實作用 KeyDetails 字串永遠看不到的位置，因 KeyDetails 只列前 5 個）。
    /// 判定走結構化 KeyAccounts 欄位時能正確命中交叉比對、降級為 Medium、ElevatesDayRisk 為 false、
    /// ResidualCredentialBasis 非空且包含第 6 個帳號。
    /// </summary>
    [Fact]
    public void 事件4740_第6個帳號命中殘留憑證時走結構化欄位降級()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "user6", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        // 同時設定 KeyAccounts（7 個）與 KeyDetails 字串（照真實格式只含前 5 個加省略號）
        lockoutSig.KeyAccounts = new List<string> { "user1", "user2", "user3", "user4", "user5", "user6", "user7" };
        lockoutSig.KeyDetails = "相關帳號(7個): user1, user2, user3, user4, user5…；來源IP(1個): 10.0.0.5";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "user6", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.Medium, lockoutSig.Severity);
        Assert.False(lockoutSig.ElevatesDayRisk);
        Assert.False(lockoutSig.ResidualCredentialRetry);
        Assert.NotNull(lockoutSig.ResidualCredentialBasis);
        Assert.Contains("user6", lockoutSig.ResidualCredentialBasis);
        Assert.Equal("可能由殘留憑證重試觸發：帳號 user6 當日有疑似殘留的重複登入失敗", lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// Fallback 測試：舊資料的 4740 簽章無 KeyAccounts（為 null），
    /// 但 KeyDetails 字串中包含殘留帳號（在前 5 個之內），退回字串解析仍可正確命中降級。
    /// </summary>
    [Fact]
    public void 事件4740_無KeyAccounts時退回KeyDetails字串解析仍有效()
    {
        var targetDate = new DateTime(2026, 8, 25);
        var loginFailSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4625, 40, IssueSeverity.High, IssueCategory.Security);
        loginFailSig.ElevatesDayRisk = true;
        loginFailSig.LoginFailureDetails = new List<LoginFailureDetail>
        {
            new() { Account = "user3", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 40 }
        };

        var lockoutSig = Sig("Security", "Microsoft-Windows-Security-Auditing", 4740, 1, IssueSeverity.High, IssueCategory.Security);
        lockoutSig.ElevatesDayRisk = true;
        lockoutSig.KeyAccounts = null;
        lockoutSig.KeyDetails = "相關帳號(5個): user1, user2, user3, user4, user5；來源IP(1個): 10.0.0.5";

        var history = new List<DailyAnalysisRecord>
        {
            CreateHistoryRecord(targetDate.AddDays(-1), new List<LoginFailureDetail>
            {
                new() { Account = "user3", Source = "WKS01", LogonType = 3, ReasonCode = "bad_password", Count = 20 }
            })
        };

        ResidualCredentialDetector.Mark(new List<LogIssueSignature> { loginFailSig, lockoutSig }, history, targetDate);

        Assert.Equal(IssueSeverity.Medium, lockoutSig.Severity);
        Assert.False(lockoutSig.ElevatesDayRisk);
        Assert.False(lockoutSig.ResidualCredentialRetry);
        Assert.NotNull(lockoutSig.ResidualCredentialBasis);
        Assert.Contains("user3", lockoutSig.ResidualCredentialBasis);
        Assert.Equal("可能由殘留憑證重試觸發：帳號 user3 當日有疑似殘留的重複登入失敗", lockoutSig.ResidualCredentialBasis);
    }

    /// <summary>
    /// LogAggregator 端測試：一批含 7 個不同帳號的 Security 事件聚合後，
    /// 簽章的結構化 KeyAccounts 欄位包含全部 7 個帳號（未截斷），
    /// 而 KeyDetails 顯示字串仍依規範只列出前 5 個加省略號。
    /// </summary>
    [Fact]
    public void LogAggregator_Security事件抽取結構化KeyAccounts與摘要KeyDetails()
    {
        var logs = new List<EventLogEntryData>();
        for (int i = 1; i <= 7; i++)
        {
            logs.Add(new EventLogEntryData
            {
                LogName = "Security",
                Source = "Microsoft-Windows-Security-Auditing",
                EventId = 4740,
                EntryType = EventLogEntryType.SuccessAudit,
                Message = $"A user account was locked out.\nAccount Name: user{i}\nCaller Computer Name: WKS01",
                TimeGenerated = DateTime.Today
            });
        }

        var sigs = LogAggregator.Aggregate(logs);
        var sig = Assert.Single(sigs);

        Assert.NotNull(sig.KeyAccounts);
        Assert.Equal(7, sig.KeyAccounts.Count);
        Assert.Equal(new[] { "user1", "user2", "user3", "user4", "user5", "user6", "user7" }, sig.KeyAccounts);
        Assert.False(sig.KeyAccountsTruncated);

        Assert.NotNull(sig.KeyDetails);
        Assert.Equal("相關帳號(7個): user1, user2, user3, user4, user5…", sig.KeyDetails);
        Assert.DoesNotContain("user6", sig.KeyDetails);
        Assert.DoesNotContain("user7", sig.KeyDetails);
    }
}
