using System.Diagnostics;
using LogForesight;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 持久層的行為驗證：無風險日精簡（數字全留、文字砍掉）與週體檢附掛。
/// 每個測試用獨立的暫存檔，測完刪除，不碰到真實 history.txt。
/// </summary>
public class JsonlAnalysisRecordStoreTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"lf_test_{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void 無風險日精簡_保留計數與趨勢數字但砍除範例訊息與KeyDetails()
    {
        var store = new JsonlAnalysisRecordStore(_tempFile);
        var record = new DailyAnalysisRecord
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

        store.Append(record);
        var read = Assert.Single(store.ReadRecent(1));
        var issue = Assert.Single(read.TopIssues);

        // 數字全留（趨勢基準所需）
        Assert.Equal(7, issue.Count);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(4, issue.DistinctMessageCount);
        Assert.Equal(3.5, issue.HistoryDailyAverage);
        Assert.Equal(5, issue.DaysSeenInHistory);
        Assert.Equal("01:00", issue.FirstSeen);

        // 文字砍掉（體積大戶，無風險日基準用不到）
        Assert.Empty(issue.SampleMessages);
        Assert.Null(issue.KeyDetails);
    }

    [Fact]
    public void 風險中以上_完整保留範例訊息與KeyDetails()
    {
        var store = new JsonlAnalysisRecordStore(_tempFile);
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today,
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
            }
        };

        store.Append(record);
        var issue = Assert.Single(store.ReadRecent(1).Single().TopIssues);

        Assert.Equal(2, issue.SampleMessages.Count);
        Assert.Equal("相關帳號(2個): admin, guest", issue.KeyDetails);
    }

    [Fact]
    public void 週體檢附掛_更新既有當日紀錄且LastWeeklyCheckupDate可讀回()
    {
        var store = new JsonlAnalysisRecordStore(_tempFile);
        var date = DateTime.Today;
        store.Append(new DailyAnalysisRecord { Date = date, RiskLevel = "低" });

        Assert.Null(store.LastWeeklyCheckupDate());

        store.AttachWeeklyCheckup(date, new WeeklyCheckupResult
        {
            CheckupDate = date,
            HasFindings = true,
            Conclusion = "本週磁碟錯誤緩慢上升"
        });

        var read = Assert.Single(store.ReadRecent(1));
        Assert.NotNull(read.WeeklyCheckup);
        Assert.True(read.WeeklyCheckup!.HasFindings);
        Assert.Equal("本週磁碟錯誤緩慢上升", read.WeeklyCheckup.Conclusion);
        Assert.Equal(date.Date, store.LastWeeklyCheckupDate());
    }

    [Fact]
    public void Host與DeepDives可完整序列化與讀回()
    {
        var store = new JsonlAnalysisRecordStore(_tempFile);
        var record = new DailyAnalysisRecord
        {
            Date = DateTime.Today,
            Host = "SRV-DB01",
            RiskLevel = "高",
            TopIssues = new List<LogIssueSignature> { new() { LogName = "System", Source = "disk", EventId = 153, Count = 1 } },
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

        store.Append(record);
        var read = Assert.Single(store.ReadRecent(1));

        Assert.Equal("SRV-DB01", read.Host);
        var deepDive = Assert.Single(read.DeepDives);
        Assert.Equal(IssueCategory.Storage, deepDive.Category);
        var finding = Assert.Single(deepDive.Findings);
        Assert.Equal("磁碟壞軌", finding.Problem);
        Assert.Equal("更換硬碟", finding.NextSteps.Single());
    }

    [Fact]
    public void HasRecord與缺漏偵測邏輯不變()
    {
        var store = new JsonlAnalysisRecordStore(_tempFile);
        store.Append(new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-1), RiskLevel = "低" });

        Assert.True(store.HasRecord(DateTime.Today.AddDays(-1)));
        Assert.False(store.HasRecord(DateTime.Today));
    }
}
