using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AI prompt 的【PRTG 監控訊號】區塊（docs/PRTG-SPEC.md §9）：
/// PRTG finding 不是 Windows／Linux 事件（EventId 恆為 0），混進事件清單會被當成一筆奇怪的事件，
/// 因此自成一段；且只餵已由規則判定過的 finding，不餵原始數值。
/// </summary>
public class AnalysisPromptBuilderPrtgTests
{
    private const string PrtgSectionTitle = "【PRTG 監控訊號】";

    private static readonly Dictionary<string, KnownIssueRule> SeedRules =
        KnownIssueSeed.CreateRules().ToDictionary(r => r.Id);

    private static LogIssueSignature PrtgFinding(long sensorObjid = 2001, string code = "down")
    {
        var rule = SeedRules.TryGetValue($"builtin-prtg-{code}", out var r)
            ? r
            : new KnownIssueRule { Id = $"test-{code}", PrtgRuleCode = code, Description = "測試說明" };
        return PrtgFindingMapper.ToSignature(
            new PrtgFinding(1001, sensorObjid, code, "Sensor down", 60, rule), DateTime.Today);
    }

    private static LogIssueSignature WindowsIssue() => new()
    {
        LogName = "System",
        Source = "disk",
        EventId = 7,
        Count = 3,
        Severity = IssueSeverity.High,
        Category = IssueCategory.Storage,
        KnownIssue = "磁碟控制器錯誤",
        SampleMessages = new List<string> { "The device has a bad block." }
    };

    private static string Build(params LogIssueSignature[] issues) =>
        AnalysisPromptBuilder.BuildPrompt(
            DateTime.Today, issues.ToList(), errorCount: 1, warningCount: 0, auditCount: 0,
            history: new List<DailyAnalysisRecord>(), trendAlerts: new List<string>(),
            correlations: new List<CorrelationFinding>(), screening: null,
            dataIncomplete: false, uncoveredChecks: new List<string>(), serverDescription: "測試主機");

    [Fact]
    public void 有PRTG_finding時輸出獨立區塊()
    {
        var prompt = Build(WindowsIssue(), PrtgFinding());

        Assert.Contains(PrtgSectionTitle, prompt);
        // 規則的白話說明要出現，讓 AI 讀得懂這個訊號代表什麼
        Assert.Contains("PRTG 監控 sensor 持續 Down 達門檻", prompt);
    }

    [Fact]
    public void PRTG_finding不混進事件清單()
    {
        // 混進去的話 EventId=0 會被印成「PRTG#0」這種讀不出意義的列。
        var prompt = Build(WindowsIssue(), PrtgFinding());

        Assert.Contains(PrtgSectionTitle, prompt);

        // 事件清單逐筆的格式是「{LogName}/{Source} xN」（AppendIssue），PRTG finding 的
        // LogName 是 PRTG——只要它被當成事件印出來，prompt 裡就會出現 "PRTG/"。
        Assert.DoesNotContain("PRTG/", prompt);

        // 事件清單本身仍要有 Windows 那筆
        Assert.Contains("System/", prompt);
    }

    [Fact]
    public void 沒有PRTG_finding時不留空標題()
    {
        var prompt = Build(WindowsIssue());

        Assert.DoesNotContain(PrtgSectionTitle, prompt);
    }

    [Fact]
    public void 只有PRTG_finding時事件清單不受影響()
    {
        // 全部 issues 都是 PRTG 時，事件清單為空但 PRTG 區塊仍要出現。
        var prompt = Build(PrtgFinding(), PrtgFinding(2002, "flapping"));

        Assert.Contains(PrtgSectionTitle, prompt);
        Assert.Contains("狀態頻繁震盪", prompt);
        Assert.DoesNotContain("PRTG/", prompt);
    }

    private static LogIssueSignature DownFindingWithDetail(string detail, long sensorObjid = 3001)
    {
        var finding = PrtgFindingMapper.ToSignature(
            new PrtgFinding(1001, sensorObjid, "down", detail, 60, SeedRules["builtin-prtg-down"]), DateTime.Today);
        return finding;
    }

    [Fact]
    public void PRTG列同時印出規則描述與Detail量值()
    {
        var prompt = Build(DownFindingWithDetail("裝置 DB01／sensor Ping 持續 Down 達 720 分鐘"));

        var line = prompt.Split('\n').Single(l => l.Contains("達 720 分鐘"));
        Assert.Contains(SeedRules["builtin-prtg-down"].Description, line);
        Assert.Contains($"{SeedRules["builtin-prtg-down"].Description}：裝置 DB01", line);
    }

    [Fact]
    public void PRTG規則描述為空時只印Detail()
    {
        var finding = DownFindingWithDetail("sensor CPU 達 95%");
        finding.KnownIssue = null;

        var prompt = Build(finding);

        Assert.Contains("] sensor CPU 達 95%", prompt);
        Assert.DoesNotContain("：sensor CPU", prompt);
    }

    [Fact]
    public void PRTG_Detail為空時只印規則描述()
    {
        var finding = DownFindingWithDetail(string.Empty);
        var description = SeedRules["builtin-prtg-down"].Description;

        var prompt = Build(finding);

        Assert.Contains($"] {description}", prompt);
        Assert.DoesNotContain($"{description}：", prompt);
    }

    [Fact]
    public void 已抑制的PRTG_finding不印()
    {
        var kept = DownFindingWithDetail("保留的 sensor 達 720 分鐘", 3001);
        var suppressed = DownFindingWithDetail("被抑制的 sensor 達 999 分鐘", 3002);
        suppressed.Suppressed = true;

        var prompt = Build(kept, suppressed);

        Assert.Contains("保留的 sensor 達 720 分鐘", prompt);
        Assert.DoesNotContain("被抑制的 sensor", prompt);
    }

    [Fact]
    public void PRTG_finding全部被抑制時不留空標題()
    {
        var suppressed = DownFindingWithDetail("被抑制的 sensor 達 999 分鐘");
        suppressed.Suppressed = true;

        var prompt = Build(WindowsIssue(), suppressed);

        Assert.DoesNotContain(PrtgSectionTitle, prompt);
    }
}
