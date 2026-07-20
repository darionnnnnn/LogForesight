using LogForesight;
using Xunit;

namespace LogForesight.Tests;

public class SlowTrendAnalyzerTests
{
    // 視窗定義（targetDate = T）：近期＝今日＋T-1..T-6（共 7 天）、前期＝T-7..T-13（共 7 天），兩側等長。
    // 兩個視窗必須等長，否則平穩訊號也會產生系統性倍率偏差、把 1.5 倍門檻實質放寬。

    [Fact]
    public void 近七天累計達前七天一點五倍且達最低次數時觸發慢速惡化告警()
    {
        // 前 7 天（T-7 ~ T-13）每天 x1 → priorTotal=7
        var prior = Enumerable.Range(7, 7).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 1));
        // 近期歷史 6 天（T-1 ~ T-6）每天 x2 → 12，加今日 x5 → recentTotal=17 ≥ 7*1.5=10.5 且 ≥10
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 2));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(5);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, out bool evaluated);

        Assert.True(evaluated);
        Assert.Contains(alerts, a => a.Contains("慢速惡化") && a.Contains("disk"));
    }

    [Fact]
    public void 兩側視窗等長_平穩訊號不會因視窗長度不一致而誤觸發()
    {
        // 每天固定 x3 完全平穩：priorTotal=21、recentTotal=3*6+3=21，倍率恰為 1.0。
        // 若近期視窗多算一天（8 vs 7）會變成 24/21≈1.14，雖仍未達 1.5，但門檻已被系統性放寬——
        // 這個案例釘住「兩側等長」這個不變量。
        var prior = Enumerable.Range(7, 7).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 3));
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 3));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(3);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today);

        Assert.Empty(alerts);
    }

    [Fact]
    public void 前七天資料不足七天時完全不比對且回報未評估()
    {
        // 只有 5 天前期資料（缺 2 天），即使近期暴增也不該觸發
        var prior = Enumerable.Range(7, 5).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 1));
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 20));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(50);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, out bool evaluated);

        Assert.Empty(alerts);
        Assert.False(evaluated); // 呼叫端據此申報「本日未檢查」，不讓沒告警被誤讀成沒問題
    }

    [Fact]
    public void 前七天總量為零時不觸發_屬TrendAnalyzer的New職責不重疊()
    {
        var prior = Enumerable.Range(7, 7).Select(d => EmptyHistoryDay(DateTime.Today.AddDays(-d)));
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 5));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(10);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, out bool evaluated);

        Assert.True(evaluated); // 有比對過，只是這個簽章不符合觸發條件
        Assert.Empty(alerts);
    }

    [Fact]
    public void 倍率未達一點五倍時不觸發即使總量都很大()
    {
        var prior = Enumerable.Range(7, 7).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 10));  // priorTotal=70
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 10)); // 60 + 今日 5 = 65
        var history = prior.Concat(recent).ToList();
        var sig = Sig(5);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today);

        Assert.Empty(alerts); // 65 < 70*1.5=105
    }

    [Fact]
    public void 近期累計低於最低次數門檻時不觸發即使倍率達標()
    {
        // priorTotal=4（T-7~T-10 各 x1、T-11~T-13 為 0），倍率門檻=6；
        // recentTotal=8（今日 x8、近期歷史皆 0）達倍率但未達 MinRecentCount(10)
        var prior = Enumerable.Range(7, 4).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 1))
            .Concat(Enumerable.Range(11, 3).Select(d => EmptyHistoryDay(DateTime.Today.AddDays(-d))));
        var recent = Enumerable.Range(1, 6).Select(d => EmptyHistoryDay(DateTime.Today.AddDays(-d)));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(8);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today);

        Assert.Empty(alerts);
    }

    [Fact]
    public void DataIncomplete的歷史日排除在兩個窗口外()
    {
        // 前期窗口本該有 7 天，其中一天標記 DataIncomplete，實際可靠天數只剩 6 → 不足七天，整批略過
        var prior = Enumerable.Range(7, 7).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 5)).ToList();
        prior[0].DataIncomplete = true;
        var recent = Enumerable.Range(1, 6).Select(d => HistoryDayFor(DateTime.Today.AddDays(-d), 20));
        var history = prior.Concat(recent).ToList();
        var sig = Sig(30);

        var alerts = SlowTrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, out bool evaluated);

        Assert.Empty(alerts);
        Assert.False(evaluated);
    }

    private static LogIssueSignature Sig(int count) => new()
    {
        LogName = "System",
        Source = "disk",
        EventId = 153,
        EntryType = System.Diagnostics.EventLogEntryType.Error,
        Count = count,
        Severity = IssueSeverity.Critical,
        Category = IssueCategory.Storage,
        FirstSeen = "00:00",
        LastSeen = "23:59"
    };

    private static DailyAnalysisRecord HistoryDayFor(DateTime date, int count) => new()
    {
        Date = date.Date,
        RiskLevel = "低",
        TopIssues = new List<LogIssueSignature>
        {
            new()
            {
                LogName = "System", Source = "disk", EventId = 153,
                EntryType = System.Diagnostics.EventLogEntryType.Error,
                Count = count, Severity = IssueSeverity.Critical, Category = IssueCategory.Storage,
                FirstSeen = "00:00", LastSeen = "23:59"
            }
        }
    };

    private static DailyAnalysisRecord EmptyHistoryDay(DateTime date) => new()
    {
        Date = date.Date,
        RiskLevel = "低",
        TopIssues = new List<LogIssueSignature>()
    };
}
