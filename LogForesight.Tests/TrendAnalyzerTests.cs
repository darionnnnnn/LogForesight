using LogForesight;
using Xunit;

namespace LogForesight.Tests;

public class TrendAnalyzerTests
{
    [Fact]
    public void 空歷史時全部標記Unknown()
    {
        var sig = Sig("System", "disk", 153, 5, IssueSeverity.Critical);
        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, new List<DailyAnalysisRecord>(), DateTime.Today, 5, 0);

        Assert.Equal(IssueTrend.Unknown, sig.Trend);
    }

    [Fact]
    public void 歷史平均兩倍以上且達最低次數時判為Rising並升級嚴重度()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Equal(IssueSeverity.Critical, sig.Severity); // High 升一級
        Assert.Contains(alerts, a => a.Contains("頻率上升"));
    }

    [Fact]
    public void 今日次數低於門檻時不判為Rising即使倍率達標()
    {
        // 今日 4 次 < RisingMinCount(5)，即使是歷史平均(1)的 4 倍也不該觸發 Rising——避免雜訊
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 1, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 4, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 4, 0);

        Assert.NotEqual(IssueTrend.Rising, sig.Trend);
    }

    [Fact]
    public void 歷史平均高且今日減半以下時判為Declining()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 20, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 5, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 5, 0);

        Assert.Equal(IssueTrend.Declining, sig.Trend);
    }

    [Fact]
    public void 次數與歷史平均相近時判為Recurring()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 4, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 4, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 4, 0);

        Assert.Equal(IssueTrend.Recurring, sig.Trend);
    }

    [Fact]
    public void 從未出現過的高嚴重度事件標記為New且產生告警()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 999, 1, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 3, IssueSeverity.Critical);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 3, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("首次出現"));
    }

    [Fact]
    public void DataIncomplete的歷史日排除在基準外()
    {
        var incomplete = HistoryDay(DateTime.Today.AddDays(-1), "disk", 153, 0, IssueSeverity.High);
        incomplete.DataIncomplete = true;
        var normalDays = Enumerable.Range(2, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 5, IssueSeverity.High));
        var history = new List<DailyAnalysisRecord> { incomplete }.Concat(normalDays).ToList();
        var sig = Sig("System", "disk", 153, 5, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 5, 0);

        Assert.Equal(5.0, sig.HistoryDailyAverage);
        Assert.Equal(5, sig.DaysSeenInHistory);
    }

    [Fact]
    public void Security無權限的歷史日排除在Security簽章基準外_非Security簽章不受影響()
    {
        var noSecurity = HistoryDay(DateTime.Today.AddDays(-1), "Security-Auditing", 4625, 0, IssueSeverity.High, "Security", IssueCategory.Security);
        noSecurity.SecurityLogAvailable = false;
        var normalDays = Enumerable.Range(2, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "Security-Auditing", 4625, 10, IssueSeverity.High, "Security", IssueCategory.Security))
            .ToList();
        var history = new List<DailyAnalysisRecord> { noSecurity }.Concat(normalDays).ToList();

        var securitySig = Sig("Security", "Security-Auditing", 4625, 10, IssueSeverity.High, IssueCategory.Security);
        TrendAnalyzer.Apply(new List<LogIssueSignature> { securitySig }, history, DateTime.Today, 0, 10);

        Assert.Equal(10.0, securitySig.HistoryDailyAverage);
        Assert.Equal(5, securitySig.DaysSeenInHistory);
    }

    [Fact]
    public void 整體錯誤量突增時產生告警()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 2, AuditEventCount = 0, RiskLevel = "低" })
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 20, todayAuditCount: 0);

        Assert.Contains(alerts, a => a.Contains("整體錯誤量突增"));
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
