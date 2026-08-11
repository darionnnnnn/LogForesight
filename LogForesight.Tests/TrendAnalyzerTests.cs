using Xunit;
using static LogForesight.Tests.TestData;

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
    public void 歷史基準兩倍以上且達最低次數時判為Rising並升級嚴重度()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        // docs/archive/HISTORY.md #1（B1 三級化）：嚴重度封頂 High（原本 High 升一級到 Critical），
        // 改用旗標達成同樣的「直接判定高風險日」效果
        Assert.Equal(IssueSeverity.High, sig.Severity);
        Assert.True(sig.ElevatesDayRisk);
        Assert.Contains(alerts, a => a.Contains("頻率上升"));
    }

    /// <summary>
    /// Rising 嚴重度閘門（回饋十五輪 A-4）：Low 簽章的頻率上升不該有能力把當天拉成中風險——
    /// Trend/Escalate/ElevatesDayRisk 判定完全不受影響（仍照算供紀錄用），只是不產生告警文字。
    /// </summary>
    [Fact]
    public void Low嚴重度簽章頻率上升時不產生告警文字但趨勢與升級照算()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.Low);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Equal(IssueSeverity.Medium, sig.Severity); // Escalate 仍把 Low 升到 Medium
        Assert.False(sig.ElevatesDayRisk);                 // 升級前不是 High，旗標不設
        Assert.DoesNotContain(alerts, a => a.Contains("頻率上升")); // 但不吵、不拉風險
    }

    // ── 爆量例外（回饋十六輪批次B-1）─────────────────────────────────────
    // Low 簽章（升級前）一般被 Rising 閘門靜音，但單日暴增達基準 10 倍或絕對量 100 筆時，
    // 仍要打破閘門產生告警——用「頻率暴增」與一般「頻率上升」區分文字。

    [Fact]
    public void Low嚴重度簽章暴增達十倍基準時打破閘門產生告警()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 20, IssueSeverity.Low); // 基準2，今日20＝10倍

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 20, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("頻率暴增"));
        Assert.DoesNotContain(alerts, a => a.Contains("頻率上升"));
    }

    [Fact]
    public void Low嚴重度簽章未達爆量門檻時仍不產生告警()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 19, IssueSeverity.Low); // 基準2，19＜10倍(20)且＜絕對量100

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 19, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.DoesNotContain(alerts, a => a.Contains("頻率暴增"));
        Assert.DoesNotContain(alerts, a => a.Contains("頻率上升"));
    }

    /// <summary>基準較大時單靠 10 倍門檻會把「翻好幾倍但還沒到誇張量級」的暴增擋在外面——
    /// 絕對量 100 筆兜底：基準 15（10 倍＝150 遠高於 100），今日 100 仍應觸發。</summary>
    [Fact]
    public void Low嚴重度簽章達絕對量門檻時即使未達十倍基準仍觸發()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 15, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 100, IssueSeverity.Low); // 基準15，100＜150(10倍)但＝100(絕對量門檻)

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 100, 0);

        Assert.Contains(alerts, a => a.Contains("頻率暴增"));
    }

    /// <summary>暖身期（可靠歷史 &lt; WarmupDays）完全不告警，爆量例外也不例外——新頻道
    /// 上線第一天的倍率比較還不可靠，與一般 Rising 閘門同一套保護。</summary>
    [Fact]
    public void 暖身期時爆量例外也不觸發告警()
    {
        var history = Enumerable.Range(1, 2) // < WarmupDays(3)
            .Select(d => DefenderHistoryDay(DateTime.Today.AddDays(-d), 1116, 2))
            .ToList();
        var sig = Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", 1116, 30, IssueSeverity.Low);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.DoesNotContain(alerts, a => a.Contains("頻率暴增"));
    }

    // ── 首次出現且爆量的出口（回饋十七輪批次C）─────────────────────────────
    // New 分支只在 Severity>=High 時告警，Other 類簽章天生 Low、永遠不會走到——
    // 一個從未出現過的未知簽章單日暴增（如 500 筆）仍該被看見，只用絕對量門檻
    // （首次出現沒有歷史基準可乘）。

    [Fact]
    public void Low嚴重度首次出現且達絕對量門檻時觸發首次出現且大量告警()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 999, 1, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 100, IssueSeverity.Low); // 從未出現過，今日100＝絕對量門檻

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 100, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("首次出現且大量"));
    }

    [Fact]
    public void Low嚴重度首次出現且未達絕對量門檻時不告警()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 999, 1, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 99, IssueSeverity.Low); // 從未出現過，99＜絕對量門檻(100)

        // todayErrorCount 傳 0（不是 99）：這裡只測簽章層的首次出現判定，不測整體錯誤量突增
        // （那是獨立的總量層告警，todayErrorCount>=10 就會觸發，與這個簽章的次數無關）
        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Empty(alerts);
    }

    /// <summary>暖身期新頻道上線第一天，所有簽章都是首次出現——爆量出口也要受同一道
    /// 閘門保護，否則新頻道切換日會被自己的暖身資料觸發告警風暴。</summary>
    [Fact]
    public void 暖身期時首次出現且爆量的出口也不觸發告警()
    {
        var history = Enumerable.Range(1, 2) // < WarmupDays(3)
            .Select(d => DefenderHistoryDay(DateTime.Today.AddDays(-d), 1116, 2))
            .ToList();
        var sig = Sig(ChannelCatalog.DefenderChannel, "Microsoft-Windows-Windows Defender", 2222, 500, IssueSeverity.Low);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        Assert.Equal(IssueTrend.New, sig.Trend);
        Assert.Empty(alerts);
    }

    /// <summary>High 嚴重度首次出現走既有分支，不會被爆量出口的文字覆蓋或重複告警。</summary>
    [Fact]
    public void High嚴重度首次出現且大量時仍只產生一般首次出現告警不重複()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 999, 1, IssueSeverity.Low))
            .ToList();
        var sig = Sig("System", "disk", 153, 500, IssueSeverity.High);

        // todayErrorCount 傳 0：同上，只測簽章層告警，不讓總量層的「整體錯誤量突增」混進來
        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0);

        var alert = Assert.Single(alerts);
        Assert.Contains("首次出現：", alert);
        Assert.DoesNotContain("首次出現且大量", alert);
    }

    /// <summary>Medium（升級前）以上的簽章不受閘門影響，維持既有行為——這是既有測試
    /// 「歷史基準兩倍以上且達最低次數時判為Rising並升級嚴重度」（High）以外的邊界確認。</summary>
    [Fact]
    public void Medium嚴重度簽章頻率上升時仍正常產生告警文字()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.Medium))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.Medium);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 10, 0);

        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("頻率上升"));
    }

    /// <summary>
    /// 回饋十三輪 E：歷史基準改中位數的存在意義——單日爆量一次不該讓後續兩週都測不出真正的異常。
    /// 13 天基準量 2、其中 1 天爆量到 100：平均會被拉到 9.0（(13×2+100)/14），之後真正異常的
    /// 一天（15，接近基準的 7.5 倍）用平均門檻算是 15&gt;=18 不成立、判不出 Rising；
    /// 中位數對這種單一極端值不敏感，仍是 2.0，同一天用中位數門檻 15&gt;=4 成立，正確判定為異常。
    /// </summary>
    [Fact]
    public void 單日爆量不會墊高基準讓後續真正異常被平均值蓋掉()
    {
        var history = Enumerable.Range(1, 13)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .Append(HistoryDay(DateTime.Today.AddDays(-14), "disk", 153, 100, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 15, IssueSeverity.High);

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 15, 0);

        // 中位數＝2.0（排序後第 7、8 個皆為 2），不是被爆量拉高的平均 9.0
        Assert.Equal(2.0, sig.HistoryDailyAverage);
        Assert.Equal(IssueTrend.Rising, sig.Trend);
        Assert.Contains(alerts, a => a.Contains("頻率上升") && a.Contains("基準 x2"));
    }

    [Fact]
    public void 今日次數低於門檻時不判為Rising即使倍率達標()
    {
        // 今日 4 次 < RisingMinCount(5)，即使是歷史基準(1)的 4 倍也不該觸發 Rising——避免雜訊
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 1, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 4, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 4, 0);

        Assert.NotEqual(IssueTrend.Rising, sig.Trend);
    }

    [Fact]
    public void 歷史基準高且今日減半以下時判為Declining()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 20, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 5, IssueSeverity.High);

        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 5, 0);

        Assert.Equal(IssueTrend.Declining, sig.Trend);
    }

    [Fact]
    public void 次數與歷史基準相近時判為Recurring()
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

    /// <summary>
    /// 回饋十四輪 A1：零膨脹主機（錯誤只在部分日子出現）修復前的退化行為——14 天中 8 天
    /// ErrorCount=0、6 天為 100，含零值的中位數＝0（排序後第 7、8 個皆為 0），
    /// 0×RisingFactor 恆為 0，倍率條件恆真，規則退化成「今日 ≥10 筆」固定門檻，
    /// 今日 10 筆這種正常量也會被誤判為「整體錯誤量突增」。改用非零日中位數（100）後，
    /// 門檻回到 100×2=200，今日 10 筆不再誤觸發。
    /// </summary>
    [Fact]
    public void 整體錯誤量_零膨脹主機今日未達非零日基準兩倍時不觸發()
    {
        var history = Enumerable.Range(1, 8)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 0, RiskLevel = "低" })
            .Concat(Enumerable.Range(9, 6)
                .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 100, AuditEventCount = 0, RiskLevel = "低" }))
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 10, todayAuditCount: 0);

        Assert.DoesNotContain(alerts, a => a.Contains("整體錯誤量突增"));
    }

    /// <summary>同一份零膨脹歷史，今日達非零日基準（100）兩倍時仍要能正確觸發——修復不是把規則整個關掉。</summary>
    [Fact]
    public void 整體錯誤量_零膨脹主機今日達非零日基準兩倍時觸發()
    {
        var history = Enumerable.Range(1, 8)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 0, RiskLevel = "低" })
            .Concat(Enumerable.Range(9, 6)
                .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 100, AuditEventCount = 0, RiskLevel = "低" }))
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 200, todayAuditCount: 0);

        Assert.Contains(alerts, a => a.Contains("整體錯誤量突增") && a.Contains("基準 100"));
    }

    /// <summary>
    /// 歷史裡一筆非零錯誤日都沒有時，無基準可算，但不代表不用管——維持固定門檻（今日 ≥10 筆）
    /// 照樣觸發，只是文案誠實說「多數日無錯誤」，不再宣稱一個不存在的「基準 0 筆」。
    /// </summary>
    [Fact]
    public void 整體錯誤量_歷史全零時仍以絕對門檻觸發但文案不宣稱基準筆數()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 0, RiskLevel = "低" })
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 10, todayAuditCount: 0);

        Assert.Contains(alerts, a => a.Contains("整體錯誤量突增") && a.Contains("多數日無錯誤"));
        Assert.DoesNotContain(alerts, a => a.Contains("基準 0 筆"));
    }

    /// <summary>安全稽核事件量突增與整體錯誤量突增同一套修復（回饋十四輪 A1），同構驗證一份即可。</summary>
    [Fact]
    public void 安全稽核事件量突增_零膨脹主機基準改用非零日中位數()
    {
        var history = Enumerable.Range(1, 8)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 0, RiskLevel = "低" })
            .Concat(Enumerable.Range(9, 6)
                .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 100, RiskLevel = "低" }))
            .ToList();

        var belowThreshold = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 0, todayAuditCount: 10);
        Assert.DoesNotContain(belowThreshold, a => a.Contains("安全稽核事件量突增"));

        var atThreshold = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, todayErrorCount: 0, todayAuditCount: 200);
        Assert.Contains(atThreshold, a => a.Contains("安全稽核事件量突增") && a.Contains("基準 100"));
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

    // ── 總量抑制與結構化 refs（回饋十五輪 A-1／A-5）─────────────────────

    [Fact]
    public void 整體錯誤量突增_抑制旗標開啟時不進回傳值改進suppressedAlerts()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 2, AuditEventCount = 0, RiskLevel = "低" })
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, 20, 0,
            suppressErrorVolume: true, suppressAuditVolume: false, out var suppressed, out var refs);

        Assert.DoesNotContain(alerts, a => a.Contains("整體錯誤量突增"));
        Assert.Contains(suppressed, a => a.Contains("整體錯誤量突增"));
        Assert.DoesNotContain(refs, r => r.Kind == TrendAlertKinds.VolumeError);
    }

    [Fact]
    public void 安全稽核事件量突增_抑制旗標開啟時不進回傳值改進suppressedAlerts()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 0, AuditEventCount = 2, RiskLevel = "低" })
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, 0, 20,
            suppressErrorVolume: false, suppressAuditVolume: true, out var suppressed, out var refs);

        Assert.DoesNotContain(alerts, a => a.Contains("安全稽核事件量突增"));
        Assert.Contains(suppressed, a => a.Contains("安全稽核事件量突增"));
        Assert.DoesNotContain(refs, r => r.Kind == TrendAlertKinds.VolumeAudit);
    }

    [Fact]
    public void 總量抑制旗標關閉時行為與舊版本一致()
    {
        var history = Enumerable.Range(1, 5)
            .Select(d => new DailyAnalysisRecord { Date = DateTime.Today.AddDays(-d), ErrorCount = 2, AuditEventCount = 0, RiskLevel = "低" })
            .ToList();

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature>(), history, DateTime.Today, 20, 0,
            suppressErrorVolume: false, suppressAuditVolume: false, out var suppressed, out var refs);

        Assert.Contains(alerts, a => a.Contains("整體錯誤量突增"));
        Assert.Empty(suppressed);
        Assert.Contains(refs, r => r.Kind == TrendAlertKinds.VolumeError && r.IssueKey == null);
    }

    [Fact]
    public void alertRefs對應首次出現與頻率上升時帶對應的IssueKey()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);
        var expectedKey = IssueSignatureKey.For(sig.LogName, sig.Source, sig.EventId, sig.EntryType);

        // todayErrorCount/todayAuditCount 刻意傳 0：history 的 HistoryDay 輔助方法不帶 ErrorCount
        // （預設 0），非零的 todayErrorCount 會意外觸發「整體錯誤量突增（多數日無錯誤）」固定門檻，
        // 這裡只想單獨看簽章層的 Rising ref，不想跟總量告警混在一起斷言
        TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0,
            false, false, out _, out var refs);

        var rising = Assert.Single(refs);
        Assert.Equal(TrendAlertKinds.Signature, rising.Kind);
        Assert.Equal(expectedKey, rising.IssueKey);
    }

    [Fact]
    public void 被抑制的簽章頻率上升時文字進suppressedAlerts不進alertRefs()
    {
        var history = Enumerable.Range(1, 14)
            .Select(d => HistoryDay(DateTime.Today.AddDays(-d), "disk", 153, 2, IssueSeverity.High))
            .ToList();
        var sig = Sig("System", "disk", 153, 10, IssueSeverity.High);
        sig.Suppressed = true;

        var alerts = TrendAnalyzer.Apply(new List<LogIssueSignature> { sig }, history, DateTime.Today, 0, 0,
            false, false, out var suppressed, out var refs);

        Assert.Empty(alerts);
        Assert.Empty(refs);
        Assert.Contains(suppressed, a => a.Contains("頻率上升"));
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

    // Sig(...) 已搬到 TestDoubles\TestData.cs（與 SuppressionTests 原本逐字相同，已合併）。

    private static DailyAnalysisRecord HistoryDay(DateTime date, string source, int eventId, int count, IssueSeverity severity,
        string logName = "System", IssueCategory category = IssueCategory.Other)
        => new()
        {
            Date = date.Date,
            RiskLevel = "低",
            TopIssues = new List<LogIssueSignature> { Sig(logName, source, eventId, count, severity, category) }
        };
}
