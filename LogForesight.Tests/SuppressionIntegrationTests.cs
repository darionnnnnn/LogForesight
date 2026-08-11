using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 抑制目標四型（回饋十五輪 A）跑過完整分析管線（<see cref="LogAnalysisService.AnalyzeDayAsync"/>）
/// 的端到端驗證——單元測試（SuppressionTests／TrendAnalyzerTests／CorrelationAnalyzerTests）已經
/// 各自釘住純函數層的行為，這裡補的是「四型抑制真的能從設定一路生效到最終風險判定」這件事，
/// 尤其是 A1 的核心情境：未命中規則（RuleId==null）的 Other 類簽章過去完全沒有抑制掛載點。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class SuppressionIntegrationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>刻意用查無規則的 Source/EventId，確保聚合後一定是 Other 類（RuleId==null）——
    /// 與 KnownIssueCatalogTests「FindRule對未命中規則的來源回傳null」同一個查無來源慣例。</summary>
    private static List<EventLogEntryData> MakeUnknownEvents(int count, DateTime day) =>
        Enumerable.Range(0, count).Select(i => new EventLogEntryData
        {
            TimeGenerated = day.AddHours(1).AddMinutes(i),
            EntryType = EventLogEntryType.Error,
            LogName = "Application",
            Source = "TotallyUnknownVendorApp",
            EventId = 99999,
            Message = $"未知錯誤 #{i}"
        }).ToList();

    private static LogAnalysisService MakeService(IAnalysisRecordStore history, ISuppressionStore suppressions) =>
        new(new EventLogService(), new FakeAiService(), history, suppressions, host: "SRV-TEST");

    /// <summary>
    /// A1 核心情境：簽章級抑制對 RuleId==null 的 Other 類簽章生效。刻意用「慢速惡化」
    /// （SlowTrendAnalyzer）而不是「頻率上升」（TrendAnalyzer）驗證——Other 類固定 Severity=Low
    /// （見 KnownIssueCatalog.ClearToOther），Rising 分支現在有嚴重度閘門（回饋十五輪 A-4），
    /// Low 簽章的 Rising 本來就不會產生告警，用它來測會分不清是閘門擋下還是抑制擋下。
    /// 慢速趨勢層完全不看嚴重度，是這裡唯一能乾淨隔離驗證抑制本身效果的路徑。
    /// </summary>
    [Fact]
    public async Task 簽章級抑制使未命中規則的Other類簽章不產生慢速惡化告警且不拉高風險()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var suppressionStore = new FakeSuppressionStore();
        var service = MakeService(history, suppressionStore);
        var signatureKey = IssueSignatureKey.For("Application", "TotallyUnknownVendorApp", 99999, EventLogEntryType.Error);

        // 前 7 天（T-13..T-7）每天 x1、近期 6 天（T-6..T-1）每天 x2、今日 x5：
        // priorTotal=7、recentTotal=12+5=17，17>=10 且 17>=7*1.5=10.5，本該觸發慢速惡化
        for (int d = 13; d >= 7; d--)
        {
            await service.AnalyzeDayAsync(DateTime.Today.AddDays(-d), MakeUnknownEvents(1, DateTime.Today.AddDays(-d)), useAi: false);
        }
        for (int d = 6; d >= 1; d--)
        {
            await service.AnalyzeDayAsync(DateTime.Today.AddDays(-d), MakeUnknownEvents(2, DateTime.Today.AddDays(-d)), useAi: false);
        }

        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Signature, SignatureKey = signatureKey,
                Scope = SuppressionScopes.Site, Reason = "測試：未命中規則的雜訊"
            }
        });

        var record = await service.AnalyzeDayAsync(DateTime.Today, MakeUnknownEvents(5, DateTime.Today), useAi: false);

        var issue = Assert.Single(record.TopIssues);
        Assert.Null(issue.RuleId); // 確認真的是 Other 類、未命中規則——A1 指出的缺口情境
        Assert.True(issue.Suppressed);
        Assert.DoesNotContain(record.TrendAlerts, a => a.Contains("慢速惡化"));
        Assert.Contains(record.SuppressedTrendAlerts, a => a.Contains("慢速惡化"));
        Assert.Equal("低", record.RiskLevel);
    }

    /// <summary>對照組：同一情境不建立抑制，慢速惡化告警應正常出現且拉高風險——
    /// 證明上一個測試的「沒告警」確實是抑制生效，不是情境本身就不會觸發。</summary>
    [Fact]
    public async Task 未抑制的對照組Other類簽章慢速惡化時正常告警並拉高風險()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var service = MakeService(history, new FakeSuppressionStore());

        for (int d = 13; d >= 7; d--)
        {
            await service.AnalyzeDayAsync(DateTime.Today.AddDays(-d), MakeUnknownEvents(1, DateTime.Today.AddDays(-d)), useAi: false);
        }
        for (int d = 6; d >= 1; d--)
        {
            await service.AnalyzeDayAsync(DateTime.Today.AddDays(-d), MakeUnknownEvents(2, DateTime.Today.AddDays(-d)), useAi: false);
        }

        var record = await service.AnalyzeDayAsync(DateTime.Today, MakeUnknownEvents(5, DateTime.Today), useAi: false);

        Assert.Contains(record.TrendAlerts, a => a.Contains("慢速惡化"));
        Assert.Equal("中", record.RiskLevel); // trendAlerts.Count>0 直接判中風險
    }

    /// <summary>
    /// A1 第二個缺口：整體錯誤量突增過去不掛任何簽章，結構上沒有抑制掛載點。
    /// </summary>
    [Fact]
    public async Task 總量抑制使整體錯誤量突增不進TrendAlerts且不拉高風險()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var suppressionStore = new FakeSuppressionStore();
        var service = MakeService(history, suppressionStore);

        // 5 天基準歷史（無事件，ErrorCount=0）：落入「多數日無錯誤」分支，今日達固定門檻（≥10）即觸發
        for (int d = 5; d >= 1; d--)
        {
            await service.AnalyzeDayAsync(DateTime.Today.AddDays(-d), new List<EventLogEntryData>(), useAi: false);
        }

        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new() { TargetType = SuppressionTargetTypes.Volume, VolumeKind = VolumeKinds.Error, Scope = SuppressionScopes.Site, Reason = "測試：本機常態高錯誤量" }
        });

        var errorEvents = Enumerable.Range(0, 20).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(i),
            EntryType = EventLogEntryType.Error,
            LogName = "System",
            Source = "SomeNoisyApp",
            EventId = 1,
            Message = $"錯誤 #{i}"
        }).ToList();

        var record = await service.AnalyzeDayAsync(DateTime.Today, errorEvents, useAi: false);

        Assert.DoesNotContain(record.TrendAlerts, a => a.Contains("整體錯誤量突增"));
        Assert.Contains(record.SuppressedTrendAlerts, a => a.Contains("整體錯誤量突增"));
        Assert.Equal("低", record.RiskLevel);
    }

    /// <summary>
    /// A1 第三個缺口：跨 log 關聯訊號過去無抑制路徑，且命中 ElevatesDayRisk 的模式會直接判高風險——
    /// 這是本輪影響面最大的一種抑制，這裡驗證命中後確實整批移出 correlations、不再拉高風險。
    /// </summary>
    [Fact]
    public async Task 關聯模式抑制使命中的關聯訊號不進CorrelationAlerts且不再判定為高風險()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var suppressionStore = new FakeSuppressionStore();
        suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Correlation, CorrelationPatternId = CorrelationPatternIds.IntrusionChain,
                Scope = SuppressionScopes.Site, Reason = "測試：已知的內部弱點掃描演練"
            }
        });
        var service = MakeService(history, suppressionStore);

        // 【入侵鏈】：同日大量登入失敗（4625 x15）加帳號建立（4720）
        var events = new List<EventLogEntryData>();
        events.AddRange(Enumerable.Range(0, 15).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(i),
            EntryType = EventLogEntryType.FailureAudit,
            LogName = "Security",
            Source = "Security-Auditing",
            EventId = 4625,
            Message = $"登入失敗 #{i}"
        }));
        events.Add(new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(20),
            EntryType = EventLogEntryType.SuccessAudit,
            LogName = "Security",
            Source = "Security-Auditing",
            EventId = 4720,
            Message = "已建立使用者帳戶"
        });

        var record = await service.AnalyzeDayAsync(DateTime.Today, events, useAi: false);

        Assert.DoesNotContain(record.CorrelationAlerts, a => a.Contains("入侵鏈"));
        Assert.Contains(record.SuppressedCorrelationAlerts, a => a.Contains("入侵鏈"));
        Assert.NotEqual("高", record.RiskLevel);
    }

    /// <summary>對照組：同一情境不建立關聯抑制，應正常判高風險——證明上一測試的降級確實是抑制生效。</summary>
    [Fact]
    public async Task 未抑制的對照組入侵鏈關聯訊號正常判定為高風險()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var service = MakeService(history, new FakeSuppressionStore());

        var events = new List<EventLogEntryData>();
        events.AddRange(Enumerable.Range(0, 15).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(i),
            EntryType = EventLogEntryType.FailureAudit,
            LogName = "Security",
            Source = "Security-Auditing",
            EventId = 4625,
            Message = $"登入失敗 #{i}"
        }));
        events.Add(new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(20),
            EntryType = EventLogEntryType.SuccessAudit,
            LogName = "Security",
            Source = "Security-Auditing",
            EventId = 4720,
            Message = "已建立使用者帳戶"
        });

        var record = await service.AnalyzeDayAsync(DateTime.Today, events, useAi: false);

        Assert.Contains(record.CorrelationAlerts, a => a.Contains("入侵鏈"));
        Assert.Equal("高", record.RiskLevel);
    }
}
