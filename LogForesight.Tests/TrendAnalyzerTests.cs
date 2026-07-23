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
    public void 只在不完整日出現過的簽章不判為New而是Recurring()
    {
        // 釘住「趨勢說首次出現、卻有昨日次數」的矛盾：昨天（不完整日）出現過 4 次，
        // 可靠歷史因排除不完整日而為空，但存在性判定要看全部歷史——曾出現過就不是首次。
        var incomplete = HistoryDay(DateTime.Today.AddDays(-1), "Resource-Exhaustion", 2004, 4, IssueSeverity.High);
        incomplete.DataIncomplete = true;
        var history = new List<DailyAnalysisRecord> { incomplete };
        var sig = Sig("System", "Resource-Exhaustion", 2004, 1, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 1, 0);

        Assert.Equal(IssueTrend.Recurring, sig.Trend);
        Assert.Equal(4, sig.PreviousDayCount);
        Assert.DoesNotContain(alerts, a => a.Contains("首次出現"));
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

    // ── 新頻道暖身（防切換日告警風暴）────────────────────────────────────

    [Fact]
    public void 新頻道可靠歷史不足暖身天數時首次出現不告警不升級()
    {
        // Defender 頻道剛上線：只有 2 天讀取歷史（< WarmupDays=3），此簽章從未出現過。
        // 應標記 New（供紀錄），但不產生「首次出現」告警、也不升級嚴重度——避免切換日風暴。
        var history = Enumerable.Range(1, 2)
            .Select(d => DefenderHistoryDay(DateTime.Today.AddDays(-d), 9999, 0)) // 別的事件，本簽章不在其中
            .ToList();
        var sig = Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", 1116, 3, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Equal(IssueSeverity.High, sig.Severity);              // 未升級
        Assert.DoesNotContain(alerts, a => a.Contains("首次出現"));   // 暖身期不告警
    }

    [Fact]
    public void 新頻道可靠歷史達暖身天數後首次出現照常告警()
    {
        var history = Enumerable.Range(1, 3)
            .Select(d => DefenderHistoryDay(DateTime.Today.AddDays(-d), 9999, 0))
            .ToList();
        var sig = Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", 1116, 3, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("首次出現"));
    }

    [Fact]
    public void 舊紀錄不算入新頻道基準_Defender簽章視為暖身()
    {
        // 舊紀錄（ChannelsRead=null）對 Defender 頻道一律視為未讀，即使歷史很長，
        // 新頻道的可靠歷史仍為 0 → 暖身，首次出現不吵
        var history = Enumerable.Range(1, 14)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), RiskLevel = "低", ChannelsRead = null })
            .ToList();
        var sig = Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", 1116, 3, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.DoesNotContain(alerts, a => a.Contains("首次出現"));
    }

    private static DailyAnalysisRecord DefenderHistoryDay(DateTime date, int eventId, int count)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            ChannelsRead = new List<string> { "System", "Application", "Security", ChannelCatalog.DefenderChannel },
            TopIssues = new List<LogIssueSignature>
            {
                Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", eventId, count, IssueSeverity.High, IssueCategory.Security)
            }
        };

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
