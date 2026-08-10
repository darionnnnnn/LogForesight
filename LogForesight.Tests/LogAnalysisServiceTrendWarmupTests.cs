using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 回饋十三輪 A8：新主機／歷史不足 <see cref="LogAnalysisService.BuildStatisticalRecordAsync"/>
/// 預設 historyDays（14 天）時的趨勢基準空窗申報。TrendAnalyzer 對「歷史不足」已有正確的
/// 靜默降級行為（history.Count==0 時全部標 Unknown、不產生 New/Rising 告警），但過去完全沒有
/// 對應的使用者申報——「沒有趨勢告警」與「趨勢層根本還沒有基準可比對」在畫面上長得一模一樣，
/// 這裡釘住 UncoveredChecks 的申報文字與門檻。
///
/// 門檻是 historyDays-1（見 LogAnalysisService 的 fullHistoryDays 註解）：ReadRecent 的窗口
/// 含當天本身，但當天的紀錄還沒寫入，history 最多只可能有 historyDays-1 筆。
/// </summary>
public class LogAnalysisServiceTrendWarmupTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static EventLogEntryData MakeEvent() => new()
    {
        TimeGenerated = DateTime.Today,
        LogName = "System",
        Source = "disk",
        EventId = 153,
        EntryType = EventLogEntryType.Error,
        Message = "測試事件"
    };

    private LogAnalysisService NewService(EfAnalysisRecordStore history) =>
        new(new EventLogService(), new FakeAiService(), history, new FakeSuppressionStore());

    private static void SeedHistoryDays(EfAnalysisRecordStore history, int days)
    {
        for (int i = 1; i <= days; i++)
        {
            history.Append(new DailyAnalysisRecord
            {
                HostId = 0, Host = "test", Date = DateTime.Today.AddDays(-i), RiskLevel = "低"
            });
        }
    }

    [Fact]
    public async Task 完全沒有歷史時申報趨勢基準建立中_0比13()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var service = NewService(history);

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, new List<EventLogEntryData> { MakeEvent() }, useAi: false);

        Assert.Contains(record.UncoveredChecks, c => c.Contains("趨勢基準建立中") && c.Contains("0/13"));
    }

    [Fact]
    public async Task 歷史累積12天未達門檻時繼續申報()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        SeedHistoryDays(history, 12);
        var service = NewService(history);

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, new List<EventLogEntryData> { MakeEvent() }, useAi: false);

        Assert.Contains(record.UncoveredChecks, c => c.Contains("趨勢基準建立中") && c.Contains("12/13"));
    }

    [Fact]
    public async Task 歷史累積滿13天_達到historyDays減一時不再申報()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        SeedHistoryDays(history, 13);
        var service = NewService(history);

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, new List<EventLogEntryData> { MakeEvent() }, useAi: false);

        Assert.DoesNotContain(record.UncoveredChecks, c => c.Contains("趨勢基準建立中"));
    }

    /// <summary>historyDays 參數可由呼叫端調整（NetIQ pipeline 傳 trendWindowDays）——
    /// 門檻要跟著參數走，不能寫死 13。</summary>
    [Fact]
    public async Task historyDays參數縮小時門檻跟著變()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        SeedHistoryDays(history, 4);
        var service = NewService(history);

        var (record, _) = await service.BuildStatisticalRecordAsync(
            DateTime.Today, new List<EventLogEntryData> { MakeEvent() }, useAi: false, historyDays: 5);

        Assert.DoesNotContain(record.UncoveredChecks, c => c.Contains("趨勢基準建立中"));
    }
}
