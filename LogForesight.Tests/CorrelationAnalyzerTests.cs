using LogForesight;
using Xunit;

namespace LogForesight.Tests;

public class CorrelationAnalyzerTests
{
    [Fact]
    public void 入侵鏈_大量登入失敗加帳號建立()
    {
        AssertPattern("【入侵鏈】", new()
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security),
            Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security)
        });
    }

    [Fact]
    public void 破解得手_大量登入失敗後成功登入同一帳號()
    {
        var issues = new List<LogIssueSignature>
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security)
        };
        var match = new SuccessfulLogonMatch { MatchedAccounts = new List<string> { "victim" } };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord>(), DateTime.Today, match);

        Assert.Contains(findings, f => f.Description.Contains("【破解得手】") && f.Severity == IssueSeverity.Critical);
    }

    [Fact]
    public void 破解得手_無比對結果時不觸發()
    {
        var issues = new List<LogIssueSignature>
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security)
        };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord>(), DateTime.Today, successfulLogonMatch: null);

        Assert.DoesNotContain(findings, f => f.Description.Contains("【破解得手】"));
    }

    [Fact]
    public void 持久化_攻擊嘗試加新服務()
    {
        AssertPattern("【持久化】", new()
        {
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security),
            Sig("System", "Service Control Manager", 7045, 1, IssueSeverity.High, IssueCategory.Security)
        });
    }

    [Fact]
    public void 滅跡_稽核清除加其他安全事件()
    {
        AssertPattern("【滅跡】", new()
        {
            Sig("Security", "Security-Auditing", 1102, 1, IssueSeverity.Critical, IssueCategory.Security),
            Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security)
        });
    }

    [Fact]
    public void 提權植入_權限異動加新服務()
    {
        AssertPattern("【提權→植入】", new()
        {
            Sig("Security", "Security-Auditing", 4670, 1, IssueSeverity.High, IssueCategory.Security),
            Sig("System", "Service Control Manager", 7045, 1, IssueSeverity.High, IssueCategory.Security)
        });
    }

    [Fact]
    public void 儲存連鎖_兩種以上儲存層訊號同日()
    {
        AssertPattern("【儲存連鎖】", new()
        {
            Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage),
            Sig("System", "Ntfs", 55, 5, IssueSeverity.Critical, IssueCategory.Storage)
        });
    }

    [Fact]
    public void 儲存當機_儲存錯誤加非預期關機()
    {
        AssertPattern("【儲存→當機】", new()
        {
            Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage),
            Sig("System", "Kernel-Power", 41, 1, IssueSeverity.Critical, IssueCategory.Hardware)
        });
    }

    [Fact]
    public void 硬體不穩_WHEA加非預期重開()
    {
        AssertPattern("【硬體不穩】", new()
        {
            Sig("System", "WHEA-Logger", 1, 5, IssueSeverity.Critical, IssueCategory.Hardware),
            Sig("System", "Kernel-Power", 41, 1, IssueSeverity.Critical, IssueCategory.Hardware)
        });
    }

    [Fact]
    public void 崩潰服務失敗_應用程式崩潰加服務異常終止()
    {
        AssertPattern("【崩潰→服務失敗】", new()
        {
            Sig("Application", "Application Error", 1000, 3, IssueSeverity.Medium, IssueCategory.Service),
            Sig("System", "Service Control Manager", 7031, 3, IssueSeverity.Medium, IssueCategory.Service)
        });
    }

    [Fact]
    public void 崩潰循環資源耗盡_高頻服務終止加資源耗盡()
    {
        AssertPattern("【崩潰循環→資源耗盡】", new()
        {
            Sig("System", "Service Control Manager", 7031, 100, IssueSeverity.Medium, IssueCategory.Service),
            Sig("System", "Resource-Exhaustion-Detector", 2004, 1, IssueSeverity.High, IssueCategory.Resource)
        });
    }

    [Fact]
    public void 崩潰循環資源耗盡_未達百次門檻不觸發()
    {
        var issues = new List<LogIssueSignature>
        {
            Sig("System", "Service Control Manager", 7031, 99, IssueSeverity.Medium, IssueCategory.Service),
            Sig("System", "Resource-Exhaustion-Detector", 2004, 1, IssueSeverity.High, IssueCategory.Resource)
        };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord>(), DateTime.Today);

        Assert.DoesNotContain(findings, f => f.Description.Contains("【崩潰循環→資源耗盡】"));
    }

    [Fact]
    public void 時間偏移驗證失敗_時間同步失敗加登入失敗()
    {
        AssertPattern("【時間偏移→驗證失敗】", new()
        {
            Sig("System", "Time-Service", 29, 3, IssueSeverity.Medium, IssueCategory.Config),
            Sig("Security", "Security-Auditing", 4625, 15, IssueSeverity.High, IssueCategory.Security)
        });
    }

    [Fact]
    public void 跨日入侵鏈_昨日大量登入失敗加今日帳號異動()
    {
        var yesterdayBrute = HistoryDay(DateTime.Today.AddDays(-1), "Security-Auditing", 4625, 15, IssueSeverity.High, "Security", IssueCategory.Security);
        var issues = new List<LogIssueSignature>
        {
            Sig("Security", "Security-Auditing", 4720, 1, IssueSeverity.High, IssueCategory.Security)
        };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord> { yesterdayBrute }, DateTime.Today);

        Assert.Contains(findings, f => f.Description.Contains("【跨日入侵鏈】"));
    }

    [Fact]
    public void 儲存持續劣化_連續兩日出現儲存錯誤()
    {
        var yesterdayStorage = HistoryDay(DateTime.Today.AddDays(-1), "disk", 153, 5, IssueSeverity.Critical, "System", IssueCategory.Storage);
        var issues = new List<LogIssueSignature>
        {
            Sig("System", "disk", 153, 5, IssueSeverity.Critical, IssueCategory.Storage)
        };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord> { yesterdayStorage }, DateTime.Today);

        Assert.Contains(findings, f => f.Description.Contains("【儲存持續劣化】"));
    }

    [Fact]
    public void 無任何組合條件時不產生任何關聯訊號()
    {
        var issues = new List<LogIssueSignature>
        {
            Sig("System", "disk", 153, 2, IssueSeverity.Critical, IssueCategory.Storage) // 單一訊號，沒有組合
        };

        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord>(), DateTime.Today);

        Assert.Empty(findings);
    }

    private static void AssertPattern(string pattern, List<LogIssueSignature> issues)
    {
        var findings = CorrelationAnalyzer.Detect(issues, new List<DailyAnalysisRecord>(), DateTime.Today);
        Assert.Contains(findings, f => f.Description.Contains(pattern));
    }

    private static LogIssueSignature Sig(string logName, string source, int eventId, int count, IssueSeverity severity,
        IssueCategory category = IssueCategory.Other)
        => new()
        {
            LogName = logName,
            Source = source,
            EventId = eventId,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            Count = count,
            Severity = severity,
            Category = category,
            FirstSeen = "00:00",
            LastSeen = "23:59"
        };

    private static DailyAnalysisRecord HistoryDay(DateTime date, string source, int eventId, int count, IssueSeverity severity,
        string logName = "System", IssueCategory category = IssueCategory.Other)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { Sig(logName, source, eventId, count, severity, category) }
        };
}
