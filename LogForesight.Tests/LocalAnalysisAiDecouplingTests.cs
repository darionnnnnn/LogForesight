using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 批次 E：本機分析路徑改走 AI 分析排程單元測試。
/// 驗證本機分析只跑統計段、零同步 AI 呼叫、標記 AiPending，以及 HostId=0 與 replaceExisting 語意。
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class LocalAnalysisAiDecouplingTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static List<EventLogEntryData> MakeHighRiskDiskEvents(int count = 20) =>
        Enumerable.Range(0, count).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(-(i % 20)),
            EntryType = EventLogEntryType.Error,
            LogName = "System",
            Source = "disk",
            EventId = 153,
            Message = $"磁碟發生 I/O 錯誤 #{i}"
        }).ToList();

    [Fact]
    public async Task 本機分析不再同步呼叫AI_紀錄標記AiPending為true且替身呼叫次數為零()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            host: "LOCAL-SRV", hostId: 42);

        var yesterday = DateTime.Today.AddDays(-1);
        var record = await service.AnalyzeDayStatisticalAsync(yesterday, MakeHighRiskDiskEvents(), useAi: true);

        Assert.Equal(0, ai.Calls);
        Assert.True(record.AiPending);
        Assert.False(record.AiAnalyzed);
        Assert.Equal("高", record.RiskLevel);
        Assert.Contains("排隊中", record.Headline);

        var stored = history.ReadRecent(yesterday, 1).Single();
        Assert.True(stored.AiPending);
        Assert.False(stored.AiAnalyzed);
        Assert.Equal("高", stored.RiskLevel);
    }

    [Fact]
    public async Task AI未設定時AiPending為false且替身呼叫次數為零()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            host: "LOCAL-SRV", hostId: 42);

        var yesterday = DateTime.Today.AddDays(-1);
        var record = await service.AnalyzeDayStatisticalAsync(yesterday, MakeHighRiskDiskEvents(), useAi: false);

        Assert.Equal(0, ai.Calls);
        Assert.False(record.AiPending);
        Assert.False(record.AiAnalyzed);
        Assert.Equal("高", record.RiskLevel);

        var stored = history.ReadRecent(yesterday, 1).Single();
        Assert.False(stored.AiPending);
        Assert.False(stored.AiAnalyzed);
    }

    [Fact]
    public async Task replaceExisting刪除緊貼寫入_分析途中拋例外舊紀錄仍保留()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var yesterday = DateTime.Today.AddDays(-1);

        history.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            Host = "LOCAL-SRV",
            HostId = 42,
            RiskLevel = "低",
            Headline = "舊分析結果"
        });

        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            host: "LOCAL-SRV", hostId: 42);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeDayStatisticalAsync(yesterday, MakeHighRiskDiskEvents(), useAi: true, ct: cts.Token, replaceExisting: true));

        var stored = history.ReadRecent(yesterday, 1);
        Assert.Single(stored);
        Assert.Equal("舊分析結果", stored[0].Headline);
    }

    [Fact]
    public async Task HostId為0時不標待補_AiPending為false()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            host: "LOCAL-SRV", hostId: 0);

        var yesterday = DateTime.Today.AddDays(-1);
        var record = await service.AnalyzeDayStatisticalAsync(yesterday, MakeHighRiskDiskEvents(), useAi: true);

        Assert.Equal(0, ai.Calls);
        Assert.False(record.AiPending);

        var stored = history.ReadRecent(yesterday, 1).Single();
        Assert.False(stored.AiPending);
    }

    [Fact]
    public async Task 重跑取代成功時舊紀錄被新統計紀錄取代且標記AiPending()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var yesterday = DateTime.Today.AddDays(-1);

        history.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            Host = "LOCAL-SRV",
            HostId = 42,
            RiskLevel = "低",
            Headline = "舊分析結果"
        });

        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            host: "LOCAL-SRV", hostId: 42);

        var record = await service.AnalyzeDayStatisticalAsync(yesterday, MakeHighRiskDiskEvents(), useAi: true, replaceExisting: true);

        Assert.Equal(0, ai.Calls);
        Assert.True(record.AiPending);

        var stored = history.ReadRecent(yesterday, 1);
        Assert.Single(stored);
        Assert.Equal("（統計已完成，AI 分析排隊中）", stored[0].Headline);
        Assert.True(stored[0].AiPending);
    }

    /// <summary>
    /// 接線守衛：上面幾條測試驗的是 LogAnalysisService 的統計段行為，
    /// **不會**因為 AnalysisOrchestrator 改回呼叫 AnalyzeDayAsync（行內跑 AI）而轉紅——
    /// 實測過：把主流程那一行換回 AnalyzeDayAsync，五條測試照樣全綠。
    /// 因此另以原始碼守衛釘住主流程的選擇，否則本批次的契約沒有任何回歸保護。
    /// </summary>
    [Fact]
    public void 主流程本機路徑必須走統計段而非行內AI()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");

        var path = Path.Combine(dir!.FullName, "LogForesight.Core", "Service", "AnalysisOrchestrator.cs");
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        var source = File.ReadAllText(path);

        Assert.Contains("AnalyzeDayStatisticalAsync(", source);

        // 註解裡提到方法名不算違規，只禁止實際呼叫（await ...AnalyzeDayAsync(）
        Assert.DoesNotContain("analysisService.AnalyzeDayAsync(", source);
    }
}
