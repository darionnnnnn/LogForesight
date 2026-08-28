using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Task N: MissingDateFinder 待跑條件切換與 AI 完全失敗狀態待補測試。
/// 驗證：
/// 1. 新模式下，已有紀錄但 AI 未成功分析的主機日會被判定為待跑
/// 2. 新模式下，已成功且有 AI 分析的主機日被略過
/// 3. 預設模式（旗標關閉）的判定行為與改動前完全相同
/// 4. AI 完全失敗寫入的主機日，後續執行能辨識為待補（且統計內容與風險等級不受影響）
/// 5. ScheduleController.RunPreview 在新模式下預覽台數只計算待補跑或未執行的主機
/// </summary>
[Collection("KnownIssueCatalogState")]
public sealed class MissingDateFinderRequireAiTests : IDisposable
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
    public void 新模式下_已有紀錄但AI未成功分析的主機日會被判定為待跑()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test-host");
        var targetDate = DateTime.Today.AddDays(-1);

        // 寫入一筆已有紀錄但 AI 未成功（AiAnalyzed=false, AiPending=true）的主機日
        var record = new DailyAnalysisRecord
        {
            Date = targetDate,
            HostId = 1,
            Host = "test-host",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = true,
            Headline = "今日分析摘要暫缺（AI 服務未回應）"
        };
        store.Append(record);

        // 驗證新模式（requireAi: true）下判定為待跑
        var missing = MissingDateFinder.Find(store, lookbackDays: 3, requireAi: true);

        Assert.Contains(targetDate, missing);
    }

    [Fact]
    public void 新模式下_已成功且有AI分析的主機日被略過()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test-host");
        var targetDate = DateTime.Today.AddDays(-1);

        // 寫入一筆已成功且有 AI 分析的紀錄（AiAnalyzed=true, AiPending=false）
        var record = new DailyAnalysisRecord
        {
            Date = targetDate,
            HostId = 1,
            Host = "test-host",
            RiskLevel = "高",
            AiAnalyzed = true,
            AiPending = false,
            Headline = "磁碟異常，建議儘速更換"
        };
        store.Append(record);

        // 驗證新模式（requireAi: true）下 targetDate 被略過（不在待跑清單中）
        var missing = MissingDateFinder.Find(store, lookbackDays: 1, requireAi: true);

        Assert.DoesNotContain(targetDate, missing);
    }

    [Fact]
    public void 預設模式_旗標關閉的判定行為與改動前完全相同()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test-host");
        var yesterday = DateTime.Today.AddDays(-1);
        var twoDaysAgo = DateTime.Today.AddDays(-2);

        // 昨天的紀錄即使 AI 未成功（AiAnalyzed=false），只要有紀錄存在，預設模式就認定已完成（不待跑）
        var record = new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 1,
            Host = "test-host",
            RiskLevel = "中",
            AiAnalyzed = false,
            AiPending = false,
            Headline = "（統計模式紀錄，未呼叫 AI 分析）"
        };
        store.Append(record);

        // 預設模式（requireAi: false）
        var missing = MissingDateFinder.Find(store, lookbackDays: 2, requireAi: false);

        // 昨天已有紀錄 -> 不在待跑清單中
        Assert.DoesNotContain(yesterday, missing);
        // 前天無紀錄 -> 仍在待跑清單中
        Assert.Contains(twoDaysAgo, missing);
    }

    [Fact]
    public async Task AI完全失敗寫入的主機日_後續執行能辨識為待補_且統計內容與風險等級不受影響()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test-host");
        var yesterday = DateTime.Today.AddDays(-1);

        // 模擬 AI 服務完全斷線失敗（Success=false, Content="", Error="llama.cpp offline"）
        var ai = new FakeAiService
        {
            Behavior = (_, _) => Task.FromResult(new AiResponse
            {
                Success = false,
                Error = "llama.cpp connection refused",
                Content = ""
            })
        };
        var sink = new FakeReportSink();
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(
            new EventLogService(), ai, store, new FakeSuppressionStore(),
            reportService: reportService, host: "test-host", hostId: 1);

        var events = MakeHighRiskDiskEvents(20);
        var record = await service.AnalyzeDayAsync(yesterday, events, useAi: true);

        // 1. 斷言統計內容、規則命中與風險等級完全不受影響
        Assert.Equal("高", record.RiskLevel);
        Assert.NotEmpty(record.TopIssues);
        Assert.Contains(record.TopIssues, i => i.RuleId == "builtin-storage-disk-io");
        Assert.Equal(20, record.ErrorCount);

        // 2. 斷言 AI 狀態標記為待補
        Assert.False(record.AiAnalyzed);
        Assert.True(record.AiPending);
        Assert.Contains("今日分析摘要暫缺", record.Headline);

        // 3. 驗證後續執行在新模式下能辨識該主機日為待跑
        var missingDates = MissingDateFinder.Find(store, lookbackDays: 3, requireAi: true);
        Assert.Contains(yesterday, missingDates);

        // 4. 模擬 AI 服務恢復上線，以 RetryAiAsync 補跑
        ai.Behavior = null;
        ai.NextContent = """{"risk_level":"高","headline":"磁碟即將故障","story":"偵測到大量磁碟I/O錯誤，硬碟可能即將故障。","trend_story":"","action":"今天就要更換"}""";

        var outcome = await service.RetryAiAsync(record, historyDays: 14);

        // 驗證補跑結果成功且風險等級與統計依然完整
        Assert.True(outcome.AiAnalyzed);
        Assert.Equal("高", outcome.RiskLevel);
        Assert.Equal("磁碟即將故障", outcome.Headline);
        Assert.Contains("偵測到大量磁碟I/O錯誤", outcome.Summary);
    }

    [Fact]
    public void ScheduleController_RunPreview_新模式下預覽台數只計算待補跑或未執行的主機()
    {
        var store = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var yesterday = DateTime.Today.AddDays(-1);

        // Local host: 低風險日（AI 刻意不呼叫，AiAnalyzed=false、AiPending=false 是合法終局狀態）
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 0,
            Host = Environment.MachineName,
            RiskLevel = "低",
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 1: 低風險日（AI 刻意不呼叫，合法終局狀態）
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 1,
            Host = "host-ok",
            RiskLevel = "低",
            AiAnalyzed = false,
            AiPending = false
        });

        // Host 2: 已有紀錄但 AI 失敗
        store.Append(new DailyAnalysisRecord
        {
            Date = yesterday,
            HostId = 2,
            Host = "host-failed-ai",
            RiskLevel = "高",
            AiAnalyzed = false,
            AiPending = true
        });

        // Host 3: 無任何紀錄

        var hosts = new FakeHostStore();
        var sentinel = new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true };
        hosts.Upsert(new WebHost { HostId = 1, HostName = "host-ok", IpAddress = "10.0.0.1", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });
        hosts.Upsert(new WebHost { HostId = 2, HostName = "host-failed-ai", IpAddress = "10.0.0.2", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });
        hosts.Upsert(new WebHost { HostId = 3, HostName = "host-missing", IpAddress = "10.0.0.3", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });

        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(sentinel);

        var controller = new ScheduleController(
            new ScheduleOptionsStore(_fx.Blob("schedule_options")),
            null!,
            new SchedulerRunState(),
            hosts, sentinels, new RecordingAuditService(), new FakeCurrentUser(), new FakeUserStore(),
            records: store, settingsStore: new FakeSystemSettingsStore(), userDisplayNames: new UserDisplayNameService(new FakeSystemSettingsStore()));

        // 預設模式（onlyMissingOrFailed = false）：預覽全部 4 台（3 台 NetIQ + 1 台本機）
        var defaultPreview = controller.RunPreview("all", segment: null, hostId: null, onlyMissingOrFailed: false, backfillDays: 1);
        Assert.Equal(4, defaultPreview.Data!.HostCount);

        // 新模式（onlyMissingOrFailed = true）：本機與 host 1 已成功故略過，只算 host 2 (AI 失敗) 與 host 3 (無紀錄) = 2 台
        var missingPreview = controller.RunPreview("all", segment: null, hostId: null, onlyMissingOrFailed: true, backfillDays: 1);
        Assert.Equal(2, missingPreview.Data!.HostCount);
    }
}
