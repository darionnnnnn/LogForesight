using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>格式異常降級的標題規則：規則層結論的挑選與次數後綴的組字。</summary>
public class AiDegradationTests
{
    private static LogIssueSignature Issue(string source, int eventId, int count, IssueSeverity severity,
        string? knownIssue = null, bool suppressed = false, bool elevates = false) => new()
    {
        LogName = "System", Source = source, EventId = eventId, Count = count, Severity = severity,
        KnownIssue = knownIssue, Suppressed = suppressed, ElevatesDayRisk = elevates
    };

    [Fact]
    public void 命中規則取規則白話說明_多個時取最嚴重者()
    {
        var issues = new List<LogIssueSignature>
        {
            Issue("disk", 7, 50, IssueSeverity.Medium, "磁碟空間即將不足"),
            Issue("storahci", 129, 2, IssueSeverity.High, "儲存控制器逾時重置"),
            Issue("noise", 1, 999, IssueSeverity.Critical)   // 最嚴重但沒命中規則：規則說明優先
        };

        Assert.Equal("儲存控制器逾時重置", AiDegradation.BuildRuleLayerHeadline(issues));
        Assert.Equal("儲存控制器逾時重置（AI 服務未回應，AI 待補）", AiDegradation.BuildServiceFailedHeadline(issues));
    }

    [Theory]
    [InlineData("磁碟錯誤偏多（AI 摘要待補，第 2 次）", "磁碟錯誤偏多")]
    [InlineData("磁碟錯誤偏多（AI 摘要已放棄：連續 3 次格式異常）", "磁碟錯誤偏多")]
    [InlineData("磁碟錯誤偏多", "磁碟錯誤偏多")]
    [InlineData("今日分析摘要暫缺（AI 服務未回應，AI 待補）", "今日分析摘要暫缺（AI 服務未回應，AI 待補）")]
    [InlineData("", "")]
    public void 剝後綴只剝降級與放棄_其他原樣(string headline, string expected)
    {
        Assert.Equal(expected, AiDegradation.StripSuffix(headline));
    }

    [Fact]
    public void AI服務完全失敗且沒有問題_標題只說摘要暫缺()
    {
        Assert.Equal("今日分析摘要暫缺（AI 服務未回應，AI 待補）",
            AiDegradation.BuildServiceFailedHeadline(new List<LogIssueSignature>()));
    }

    [Fact]
    public void 沒有命中規則取最嚴重問題的來源與次數()
    {
        var issues = new List<LogIssueSignature>
        {
            Issue("low-noise", 1, 500, IssueSeverity.Low),
            Issue("svc", 7034, 12, IssueSeverity.High),
            Issue("other", 2, 40, IssueSeverity.High)
        };

        Assert.Equal("other EventId 2 今日出現 40 次", AiDegradation.BuildRuleLayerHeadline(issues));
    }

    [Fact]
    public void 已抑制的問題不參與挑選_全部抑制時才使用()
    {
        var mixed = new List<LogIssueSignature>
        {
            Issue("muted", 1, 100, IssueSeverity.Critical, "被靜音的規則", suppressed: true),
            Issue("real", 2, 3, IssueSeverity.Low)
        };
        Assert.Equal("real EventId 2 今日出現 3 次", AiDegradation.BuildRuleLayerHeadline(mixed));

        var allMuted = new List<LogIssueSignature> { Issue("muted", 1, 100, IssueSeverity.Critical, "被靜音的規則", suppressed: true) };
        Assert.Equal("被靜音的規則", AiDegradation.BuildRuleLayerHeadline(allMuted));
    }

    [Fact]
    public void 沒有任何問題時退回固定句()
    {
        Assert.Equal("今日偵測到 0 個問題，請查看規則層明細", AiDegradation.BuildRuleLayerHeadline(new List<LogIssueSignature>()));
    }

    [Theory]
    [InlineData(1, "（AI 摘要待補，第 1 次）")]
    [InlineData(2, "（AI 摘要待補，第 2 次）")]
    [InlineData(3, "（AI 摘要已放棄：連續 3 次格式異常）")]
    public void 後綴依次數組字_達上限為放棄字樣(int attempts, string suffix)
    {
        Assert.Equal("結論" + suffix, AiDegradation.ComposeHeadline("結論", attempts));
        Assert.Equal(attempts >= 3, AiDegradation.IsAbandoned(attempts));
    }

    [Fact]
    public void 結論過長時先截斷_後綴一定完整保留()
    {
        var longConclusion = new string('長', 200);
        var headline = AiDegradation.ComposeHeadline(longConclusion, 3);

        Assert.EndsWith("（AI 摘要已放棄：連續 3 次格式異常）", headline);
        Assert.Equal(AiDegradation.MaxConclusionChars + "（AI 摘要已放棄：連續 3 次格式異常）".Length, headline.Length);
        Assert.Contains("…", headline);
    }
}
