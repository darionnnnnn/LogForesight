using LogForesight;
using Xunit;

namespace LogForesight.Tests;

public class RecordStorageShaperTests
{
    [Fact]
    public void 低風險日_保留計數與趨勢數字但砍除範例訊息與KeyDetails()
    {
        var record = LowRiskRecord();

        var shaped = RecordStorageShaper.ForStorage(record);
        var issue = Assert.Single(shaped.TopIssues);

        Assert.Equal(7, issue.Count);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(4, issue.DistinctMessageCount);
        Assert.Equal(3.5, issue.HistoryDailyAverage);
        Assert.Equal(5, issue.DaysSeenInHistory);
        Assert.Empty(issue.SampleMessages);
        Assert.Null(issue.KeyDetails);
    }

    [Fact]
    public void 低風險日_Host與DeepDives原樣帶過()
    {
        var record = LowRiskRecord();
        record.Host = "SRV-01";

        var shaped = RecordStorageShaper.ForStorage(record);

        Assert.Equal("SRV-01", shaped.Host);
        Assert.Empty(shaped.DeepDives); // 低風險日從不觸發深析，恆為空
    }

    [Fact]
    public void 風險中以上_完全不精簡_含DeepDives()
    {
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            Host = "SRV-02",
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature>
            {
                new()
                {
                    LogName = "System", Source = "disk", EventId = 153, Count = 7,
                    Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                    SampleMessages = new List<string> { "sample A", "sample B" },
                    KeyDetails = "相關帳號(2個): admin, guest"
                }
            },
            DeepDives = new List<CategoryDeepDive>
            {
                new()
                {
                    Category = IssueCategory.Storage,
                    Findings = new List<DeepDiveFinding>
                    {
                        new() { Problem = "磁碟壞軌", Impact = "資料遺失風險", LikelyCauses = new() { "硬碟老化" }, NextSteps = new() { "更換硬碟" } }
                    }
                }
            }
        };

        var shaped = RecordStorageShaper.ForStorage(record);

        Assert.Same(record, shaped); // 風險中以上直接回傳原物件，不建立副本
        var issue = Assert.Single(shaped.TopIssues);
        Assert.Equal(2, issue.SampleMessages.Count);
        Assert.Equal("相關帳號(2個): admin, guest", issue.KeyDetails);
        var deepDive = Assert.Single(shaped.DeepDives);
        Assert.Equal(IssueCategory.Storage, deepDive.Category);
        Assert.Single(deepDive.Findings);
    }

    [Fact]
    public void 低風險但無TopIssues時直接回傳原物件()
    {
        var record = new DailyAnalysisRecord { Date = DateTime.Today, RiskLevel = "低", TopIssues = new List<LogIssueSignature>() };

        var shaped = RecordStorageShaper.ForStorage(record);

        Assert.Same(record, shaped);
    }

    /// <summary>
    /// 反射式防護：低風險日精簡時，除了「刻意精簡」的 TopIssues 子欄位外，DailyAnalysisRecord 的
    /// 每一個頂層欄位都必須原樣保留。手動逐欄位複製的陷阱是「未來加欄位忘了複製 → 低風險日靜默掉資料」；
    /// 這個測試把每個頂層欄位設成非預設值後比對，任何被漏掉的欄位都會讓測試失敗（而不是等上線才發現）。
    /// </summary>
    [Fact]
    public void 低風險日精簡_除刻意精簡外的每個頂層欄位都保留()
    {
        var original = new DailyAnalysisRecord
        {
            Date = new DateTime(2026, 7, 20),
            Host = "SRV-REFLECT",
            ErrorCount = 3,
            WarningCount = 2,
            AuditEventCount = 1,
            TrendAlerts = new List<string> { "trend-alert" },
            CorrelationAlerts = new List<string> { "corr-alert" },
            RiskLevel = "低",
            Headline = "headline",
            Summary = "summary",
            TrendAssessment = "assessment",
            Action = "action",
            AiAnalyzed = false,
            ScreenedTailCount = 7,
            ScreeningNotes = new List<string> { "note" },
            ReportFile = "report/path.txt",
            DataIncomplete = true,
            SecurityLogAvailable = false,
            UncoveredChecks = new List<string> { "uncovered" },
            WeeklyCheckup = new WeeklyCheckupResult { CheckupDate = new DateTime(2026, 7, 20), HasFindings = true, Conclusion = "wc" },
            DeepDives = new List<CategoryDeepDive> { new() { Category = IssueCategory.Storage } },
            TopIssues = new List<LogIssueSignature> { new() { LogName = "System", Source = "disk", EventId = 153, Count = 1, SampleMessages = new() { "x" } } }
        };

        var shaped = RecordStorageShaper.ForStorage(original);

        foreach (var prop in typeof(DailyAnalysisRecord).GetProperties())
        {
            if (prop.Name == nameof(DailyAnalysisRecord.TopIssues))
            {
                continue; // TopIssues 是唯一被刻意改寫的欄位，另由上面的測試檢查
            }

            var expected = prop.GetValue(original);
            var actual = prop.GetValue(shaped);
            Assert.Equal(expected, actual); // 欄位若被漏複製，shaped 會是預設值 → 失敗
        }

        // TopIssues 本身：筆數保留、samples 被精簡
        Assert.Equal(original.TopIssues.Count, shaped.TopIssues.Count);
        Assert.Empty(shaped.TopIssues[0].SampleMessages);
    }

    private static DailyAnalysisRecord LowRiskRecord() => new()
    {
        Date = DateTime.Today,
        RiskLevel = "低",
        ErrorCount = 3,
        TopIssues = new List<LogIssueSignature>
        {
            new()
            {
                LogName = "System", Source = "disk", EventId = 153, Count = 7,
                Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                FirstSeen = "01:00", LastSeen = "05:00",
                DistinctMessageCount = 4, HistoryDailyAverage = 3.5, DaysSeenInHistory = 5,
                SampleMessages = new List<string> { "sample A", "sample B", "sample C" },
                KeyDetails = "相關帳號(2個): admin, guest"
            }
        }
    };
}
