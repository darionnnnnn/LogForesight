using LogForesight.Core.Analysis;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// J9：取消權杖傳到週體檢、本機事件記錄掃描、補查登入成功事件三處。
/// 每處兩側各一例：已取消 → 在第一個檢查點以 <see cref="OperationCanceledException"/> 結束；未取消 → 照舊完成。
/// （體檢階段在協調器內遇取消標為已停止，由 AnalysisOrchestrator 的 catch 完成；這裡驗證服務層的檢查點。）
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class CancellationPropagationJ9Tests : IDisposable
{
    private static readonly DateTime Day = new(2026, 10, 20);
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── 本機事件記錄掃描 ──────────────────────────────────────────────

    [Fact]
    public void ScanRange已取消時擲取消例外而不是回讀取失敗()
    {
        var service = new EventLogService(() => false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.ScanRange(Day, Day.AddDays(1), "Security", ct: cts.Token));
    }

    [Fact]
    public void ScanRange未取消時照舊回結果()
    {
        var service = new EventLogService(() => false);

        var result = service.ScanRange(Day, Day.AddDays(1), "Security", ct: CancellationToken.None);

        Assert.True(result.PlatformUnsupported);
    }

    [Fact]
    public async Task ScanRangeFromAllAsync已取消時擲取消例外()
    {
        var service = new EventLogService(() => false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanRangeFromAllAsync(Day, Day.AddDays(1), null, cts.Token));
    }

    [Fact]
    public async Task ScanRangeFromAllAsync未取消時三個來源都有結果()
    {
        var service = new EventLogService(() => false);

        var result = await service.ScanRangeFromAllAsync(Day, Day.AddDays(1), null, CancellationToken.None);

        Assert.Equal(EventLogService.DefaultLogNames.Length, result.BySource.Count);
    }

    // ── 補查登入成功事件 ──────────────────────────────────────────────

    [Fact]
    public async Task 補查登入成功事件進行中被取消_在補查後的檢查點結束且不產出得手結論()
    {
        using var cts = new CancellationTokenSource();
        var scannerCalls = 0;
        var service = NewAnalysis((_, _) =>
        {
            scannerCalls++;
            cts.Cancel(); // 模擬補查掃描進行中使用者按下停止
            return new EventLogScanResult { Entries = new List<EventLogEntryData>() };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeDayStatisticalAsync(Day, BruteForceFailures(), useAi: false, securityLogAvailable: true, ct: cts.Token));
        Assert.Equal(1, scannerCalls);
    }

    [Fact]
    public async Task 補查登入成功事件未取消時照舊產出得手結論()
    {
        var service = NewAnalysis((_, keep) => new EventLogScanResult
        {
            Entries = new[] { Logon(4624, "Account Name: victim\nSource Network Address: 203.0.113.9") }.Where(keep).ToList()
        });

        var record = await service.AnalyzeDayStatisticalAsync(Day, BruteForceFailures(), useAi: false,
            securityLogAvailable: true, ct: CancellationToken.None);

        Assert.Contains(record.CorrelationAlerts, a => a.Contains("破解得手", StringComparison.Ordinal));
    }

    // ── 週體檢 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 週體檢啟動前已取消_直接擲取消例外且不寫報告()
    {
        var sink = new FakeReportSink();
        var service = new WeeklyCheckupService(new FakeAiService(), SignalReader(), sink);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(Day, 7, ct: cts.Token));
        Assert.False(sink.Called);
    }

    [Fact]
    public async Task 週體檢AI呼叫期間被取消_回來後的檢查點結束且不寫報告()
    {
        using var cts = new CancellationTokenSource();
        var ai = new FakeAiService
        {
            NextContent = """{"conclusion":"回顧結論"}""",
            Behavior = (_, _) =>
            {
                cts.Cancel(); // AI 回應已成功，但使用者在此刻按下停止
                return Task.FromResult(new AiResponse { Success = true, Content = """{"conclusion":"回顧結論"}""" });
            }
        };
        var sink = new FakeReportSink();
        var service = new WeeklyCheckupService(ai, SignalReader(), sink);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunAsync(Day, 7, ct: cts.Token));
        Assert.False(sink.Called);
    }

    [Fact]
    public async Task 週體檢未取消時照舊完成並寫報告()
    {
        var ai = new FakeAiService { NextContent = """{"conclusion":"回顧結論"}""" };
        var sink = new FakeReportSink();
        var service = new WeeklyCheckupService(ai, SignalReader(), sink);

        var result = await service.RunAsync(Day, 7, ct: CancellationToken.None);

        Assert.True(result.Completed);
        Assert.True(sink.Called);
    }

    private static FakeReader SignalReader() => new(new List<DailyAnalysisRecord>
    {
        new() { Date = Day, RiskLevel = RiskLevels.High, Summary = "" }
    });

    private LogAnalysisService NewAnalysis(Func<DateTime, Func<EventLogEntryData, bool>, EventLogScanResult> scanner) =>
        new(new EventLogService(), new FakeAiService(),
            new EfAnalysisRecordStore(_fixture.NewContext, $"j9-{Guid.NewGuid():N}"),
            new FakeSuppressionStore(), host: "FIXTURE-J9", hostId: 90901,
            logSource: AnalysisLogSource.Local, localSecuritySupplementScanner: scanner);

    private static List<EventLogEntryData> BruteForceFailures() =>
        Enumerable.Range(0, 12).Select(i => new EventLogEntryData
        {
            TimeGenerated = Day.AddHours(1).AddMinutes(i),
            EntryType = LogEntryType.FailureAudit,
            LogName = "Security",
            Source = "Microsoft-Windows-Security-Auditing",
            EventId = 4625,
            Message = "Account Name: victim\nSource Network Address: 203.0.113.9"
        }).ToList();

    private static EventLogEntryData Logon(int eventId, string message) => new()
    {
        TimeGenerated = Day.AddHours(5),
        EntryType = LogEntryType.SuccessAudit,
        LogName = "Security",
        Source = "Microsoft-Windows-Security-Auditing",
        EventId = eventId,
        Message = message
    };
}
