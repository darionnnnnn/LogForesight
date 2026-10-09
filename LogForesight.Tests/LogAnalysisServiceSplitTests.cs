using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// LogAnalysisService 拆分統計段/AI 段（docs/archive/FEEDBACK-12-PLAN.md §3.3）之後的回歸測試：
/// AnalyzeDayAsync 的組合呼叫（統計段緊接著 AI 段）產出的最終紀錄與報告內容要與拆分前
/// 完全一致。這裡專門釘住 <c>CompleteAiAsync</c> 內部組出的暫用 <see cref="DailyAnalysisRecord"/>
/// 有沒有把 <see cref="RiskReportService.PrepareAsync"/> 會讀的欄位都填齊——實作過程中曾經
/// 只填了 TopIssues/RiskLevel/AiAnalyzed 幾個欄位，報告會靜靜少一大塊內容（Headline／Summary／
/// TrendAssessment／Action／UncoveredChecks／DataIncomplete），但不會有任何例外或建置警告。
/// </summary>
[Collection("KnownIssueCatalogState")]
public class LogAnalysisServiceSplitTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>命中 builtin-storage-disk-io（ElevatesDayRisk=true，Category=Storage≠Other）——
    /// 選 Storage 而非 Other 類別，報告產生時不會觸發 DeepDiveAsync 的第二個 AI 呼叫，
    /// 讓斷言只聚焦在「主分析的 AI 內容有沒有進報告」這一件事。</summary>
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
    public async Task 命中高風險規則時報告內容含AI產出的headline與summary()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService
        {
            NextContent = """{"risk_level":"高","headline":"磁碟即將故障","story":"偵測到大量磁碟I/O錯誤，硬碟可能即將故障。","trend_story":"","action":"今天就要處理"}"""
        };
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: reportService, host: "SRV-NETIQ-07", hostId: 42);

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-1), MakeHighRiskDiskEvents(), useAi: true);

        Assert.Equal(AnalysisLogSource.Local, record.LogSource);
        Assert.Equal(AnalysisLogSource.Local, Assert.Single(history.ReadRecent(record.Date, 1)).LogSource);
        Assert.Equal("高", record.RiskLevel);

        // 報告要歸到這個分析服務綁定的主機：升級前這裡完全沒把主機傳下去，
        // 多台主機同日同風險同類別的報告因而互相覆蓋
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = 42, HostName = "SRV-NETIQ-07" }, record.Date, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Equal(record.ReportFile, report.ReportId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(record.AiAnalyzed);
        Assert.NotNull(record.ReportFile);
        Assert.Contains("磁碟即將故障", report.Content);       // AI headline 有沒有進報告
        Assert.Contains("偵測到大量磁碟I/O錯誤", report.Content); // AI summary 有沒有進報告
        Assert.Contains("今天就要處理", report.Content);       // AI action 有沒有進報告
    }

    [Fact]
    public async Task AI報告使用完整統計父列輸入保留頻道盲區與已抑制問題()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService
        {
            NextContent = """{"risk_level":"高","headline":"磁碟即將故障","story":"大量磁碟 I/O 錯誤。","trend_story":"","action":"今天處理"}"""
        };
        var suppressedEvent = new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(-1), EntryType = EventLogEntryType.Error,
            LogName = "Application", Source = "QuietVendor", EventId = 9001,
            Message = "已知的低優先級雜訊"
        };
        var signatureKey = IssueSignatureKey.For("Application", "QuietVendor", 9001, EventLogEntryType.Error);
        var suppressions = new FakeSuppressionStore();
        suppressions.SaveAll(new List<RuleSuppression>
        {
            new() { TargetType = SuppressionTargetTypes.Signature, SignatureKey = signatureKey,
                Scope = SuppressionScopes.Site, Reason = "測試：已知雜訊" }
        });
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(new EventLogService(), ai, history, suppressions,
            reportService: reportService, host: "SRV-NETIQ-07", hostId: 42);
        var channels = new ChannelAvailability { Read = new List<string> { "System" }, Denied = new List<string> { "Security" } };

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-1),
            MakeHighRiskDiskEvents().Append(suppressedEvent).ToList(), useAi: true,
            securityLogAvailable: false, channels: channels);

        var persisted = Assert.Single(history.ReadRecent(record.Date, 1));
        Assert.Equal("高", record.RiskLevel);
        Assert.Equal(record.RiskLevel, persisted.RiskLevel);
        Assert.Equal(channels.Read, persisted.ChannelsRead);
        Assert.False(persisted.SecurityLogAvailable);
        Assert.Contains(persisted.TopIssues, issue => issue.Source == "QuietVendor" && issue.Suppressed);
        Assert.Contains(persisted.UncoveredChecks, check => check.Contains("Security", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(persisted.ReportFile);
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = 42, HostName = "SRV-NETIQ-07" }, persisted.Date, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Contains("Security", report.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>統計模式（useAi=false）路徑不受拆分影響：規則本身判定的高風險一樣要出報告，
    /// 只是報告內容不含 AI 產出（此路徑走 BuildStatisticalRecordAsync 的「不需要 AI」分支，
    /// 不經過 CompleteAiAsync 的暫用記錄，是拆分後的另一條獨立路徑，需要各自驗證）。</summary>
    [Fact]
    public async Task 統計模式下高風險規則一樣輸出報告但不含AI內容()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService();
        var reportService = new RiskReportService(ai, sink);
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: reportService);

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-1), MakeHighRiskDiskEvents(), useAi: false);

        Assert.Equal("高", record.RiskLevel);
        Assert.False(record.AiAnalyzed);
        Assert.Equal(0, ai.Calls);
        Assert.NotNull(record.ReportFile);
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = record.HostId, HostName = record.Host }, record.Date, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Contains("統計模式紀錄", report.Content);
    }

    [Fact]
    public async Task 非AI報告可延後到PRTG存檔後並同步持久化報告參照與證據版本()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var sink = new FakeReportSink();
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: new RiskReportService(ai, sink), host: "SRV-NETIQ-07", hostId: 42);
        var day = DateTime.Today.AddDays(-1);
        var (record, workItem) = await service.BuildStatisticalRecordAsync(
            day, MakeHighRiskDiskEvents(), useAi: false, deferNonAiReport: true);
        Assert.Null(workItem);
        Assert.Null(record.ReportFile);
        Assert.Null(sink.LastContent);

        var resourceFinding = new LogIssueSignature
        {
            LogName = "PRTG",
            Source = "PRTG:disk_free_trend",
            EventId = 0,
            EventKey = "prtg:disk_free_trend:17",
            RuleId = "builtin-prtg-resource-disk-pressure",
            Category = IssueCategory.Storage,
            Severity = IssueSeverity.Critical,
            Count = 1,
            FirstSeen = "10:00",
            LastSeen = "10:00",
            PrtgSourceGeneration = "saved-source-generation",
            PrtgResourceGeneration = "saved-resource-generation",
            PrtgChannelGeneration = "saved-channel-generation",
            PrtgRuleAdmissionFingerprint = new string('A', 64),
            PrtgResourceReasonCodes = ["disk-two-hour-low-water"],
            SampleMessages = ["兩小時可用空間均值 4.2%，合格涵蓋 120 分鐘"]
        };
        record.TopIssues.Add(resourceFinding);
        history.Append(record);

        Assert.True(await service.FinalizeNonAiReportAfterPrtgAttachmentAsync(record, MakeHighRiskDiskEvents()));

        var saved = Assert.Single(history.ReadRecent(day, 1));
        Assert.NotNull(saved.ReportFile);
        Assert.Equal(HostDayWorkflowFingerprint.PrtgInputFingerprint(saved), saved.PrtgReportEvidenceFingerprint);
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = 42, HostName = "SRV-NETIQ-07" }, day, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Contains("正式資源原因：兩個完整小時皆處於磁碟低水位", report.Content);
        Assert.Equal(saved.ReportFile, record.ReportFile);
        Assert.Equal(saved.PrtgReportEvidenceFingerprint, record.PrtgReportEvidenceFingerprint);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task 已保存主機日的報告附掛失敗會留獨立標記並在新服務重試()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore(),
            reportService: new RiskReportService(ai, new FakeReportSink()), host: "REPORT-RETRY", hostId: 882);
        var day = DateTime.Today.AddDays(-3);
        var record = await service.AnalyzeDayStatisticalAsync(day, MakeHighRiskDiskEvents(), useAi: false,
            deferNonAiReport: true);
        Assert.True(record.RiskReportPending);
        Assert.Null(record.ReportFile);

        using (var context = _fx.NewContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "CREATE TRIGGER fail_report_parent_update BEFORE UPDATE ON lf_daily_records BEGIN SELECT RAISE(ABORT, 'fixture parent update failure'); END;");

        Assert.False(await service.FinalizeNonAiReportAfterPrtgAttachmentAsync(record, MakeHighRiskDiskEvents()));
        var afterFailure = Assert.Single(history.ReadRecent(day, 1));
        Assert.True(afterFailure.RiskReportPending);
        Assert.Null(afterFailure.ReportFile);
        Assert.Null(new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = 882, HostName = "REPORT-RETRY" }, day, ReportKinds.DailyRisk));

        using (var context = _fx.NewContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database,
                "DROP TRIGGER fail_report_parent_update;");

        // A new service instance models a process restart. It reads the durable parent/cache only;
        // report preparation is explicitly non-AI and guarded attach rechecks the same parent image.
        var restarted = new LogAnalysisService(new EventLogService(), ai,
            new EfAnalysisRecordStore(_fx.NewContext, "test"), new FakeSuppressionStore(),
            reportService: new RiskReportService(ai, new FakeReportSink()), host: "REPORT-RETRY", hostId: 882);
        Assert.True(await restarted.FinalizePendingRiskReportAsync(afterFailure));

        var completed = Assert.Single(history.ReadRecent(day, 1));
        Assert.False(completed.RiskReportPending);
        Assert.NotNull(completed.ReportFile);
        Assert.Equal(HostDayWorkflowFingerprint.PrtgInputFingerprint(completed),
            completed.PrtgReportEvidenceFingerprint);
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = 882, HostName = "REPORT-RETRY" }, day, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Contains("風險事件快取為空", report.Content);
        Assert.Equal(0, ai.Calls);
    }

    /// <summary>
    /// 回饋十四輪 A2：AiWorkItem.Logs 的窄化移進 BuildStatisticalRecordAsync 本身——回傳的
    /// workItem 拿到手時 Logs 已經是 RiskyEventSelector 的選取結果，不是原始 logs 全量。
    /// 未命中規則、且首次執行（無歷史）故 Trend 恆 Unknown 的雜訊事件不該進到 workItem.Logs。
    /// </summary>
    [Fact]
    public async Task BuildStatisticalRecordAsync傳回的workItem其Logs已窄化不含未命中規則的雜訊事件()
    {
        var history = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var ai = new FakeAiService();
        var service = new LogAnalysisService(new EventLogService(), ai, history, new FakeSuppressionStore());

        var diskEvents = MakeHighRiskDiskEvents(20); // 命中 builtin-storage-disk-io，應入選
        var noiseEvents = Enumerable.Range(0, 30).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddMinutes(-i),
            EntryType = EventLogEntryType.Information,
            LogName = "Application",
            Source = "UnrelatedNoisyApp",
            EventId = 5000,
            Message = $"例行訊息 #{i}"
        }).ToList(); // 未命中規則、首次執行歷史為空故 Trend=Unknown，不該入選
        var logs = diskEvents.Concat(noiseEvents).ToList();

        var (_, workItem) = await service.BuildStatisticalRecordAsync(DateTime.Today.AddDays(-1), logs, useAi: true);

        Assert.NotNull(workItem);
        Assert.Equal(diskEvents.Count, workItem!.Logs.Count);
        Assert.All(workItem.Logs, e => Assert.Equal("disk", e.Source));
        Assert.True(workItem.Logs.Count < logs.Count);
    }

    [Fact]
    public void 時序過濾_成功在失敗之後_判定得手保留帳號()
    {
        var candidates = new[] { "alice" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 9, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 14, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Contains("alice", result);
        Assert.Single(result);
    }

    [Fact]
    public void 時序過濾_成功在失敗之前_不判定得手過濾帳號()
    {
        var candidates = new[] { "alice" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 17, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 9, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.DoesNotContain("alice", result);
        Assert.Empty(result);
    }

    [Fact]
    public void 時序過濾_同時間視為通過保留帳號()
    {
        var candidates = new[] { "alice" };
        var sameTime = new DateTime(2026, 8, 26, 10, 0, 0);
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = sameTime
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = sameTime
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Contains("alice", result);
        Assert.Single(result);
    }

    [Fact]
    public void 時序過濾_失敗面缺時間資料_保守保留帳號()
    {
        var candidates = new[] { "alice" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 14, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Contains("alice", result);
        Assert.Single(result);
    }

    [Fact]
    public void 時序過濾_成功面缺時間資料_保守保留帳號()
    {
        var candidates = new[] { "alice" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 9, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Contains("alice", result);
        Assert.Single(result);
    }

    [Fact]
    public void 時序過濾_IP成功早於失敗_被過濾()
    {
        var candidates = new[] { "10.0.0.5" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.5"] = new(2026, 8, 26, 17, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.5"] = new(2026, 8, 26, 9, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.DoesNotContain("10.0.0.5", result);
        Assert.Empty(result);
    }

    [Fact]
    public void 時序過濾_多帳號部分過濾_僅保留時序合格者()
    {
        var candidates = new[] { "alice", "bob" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 9, 0, 0),
            ["bob"] = new(2026, 8, 26, 17, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 14, 0, 0),
            ["bob"] = new(2026, 8, 26, 9, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Contains("alice", result);
        Assert.DoesNotContain("bob", result);
        Assert.Single(result);
    }

    [Fact]
    public void 時序過濾_全部帳號被過濾後回傳空交集()
    {
        var candidates = new[] { "alice" };
        var earliestFailure = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 17, 0, 0)
        };
        var latestSuccess = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["alice"] = new(2026, 8, 26, 9, 0, 0)
        };

        var result = LogAnalysisService.FilterByTiming(candidates, earliestFailure, latestSuccess);

        Assert.Empty(result);
    }

    // ── 問題靜音（分析側）──────────────────────────────────────────────────

    private static List<EventLogEntryData> MakeNtfsEvents(int count = 20) =>
        Enumerable.Range(0, count).Select(i => new EventLogEntryData
        {
            TimeGenerated = DateTime.Today.AddHours(-(i % 20)),
            EntryType = EventLogEntryType.Error,
            LogName = "System",
            Source = "Ntfs",
            EventId = 55,
            Message = $"檔案系統結構損毀 #{i}"
        }).ToList();

    private LogAnalysisService CreateMuteAwareService(FakeIssueOwnerStore owners, FakeReportSink sink)
    {
        var ai = new FakeAiService();
        return new LogAnalysisService(new EventLogService(), ai, new EfAnalysisRecordStore(_fx.NewContext, "test"),
            new MuteAwareSuppressionStore(new FakeSuppressionStore(), owners),
            reportService: new RiskReportService(ai, sink));
    }

    private static FakeIssueOwnerStore MutedDisk(DateTime from, DateTime to, string reason = "更換磁碟陣列中")
    {
        var owners = new FakeIssueOwnerStore();
        owners.Upsert(new IssueProfile
        {
            SourceName = "DISK", EventId = 153,
            Mutes = new List<MuteInterval> { new() { From = from, To = to, Reason = reason, ByAccount = "admin" } }
        });
        return owners;
    }

    [Fact]
    public async Task 問題靜音_紀錄日在區間內_標Suppressed且報告已抑制段顯示靜音至()
    {
        var day = DateTime.Today.AddDays(-1);
        var sink = new FakeReportSink();
        var service = CreateMuteAwareService(MutedDisk(day, DateTime.Today.AddDays(2)), sink);

        var record = await service.AnalyzeDayAsync(day, MakeHighRiskDiskEvents().Concat(MakeNtfsEvents()).ToList(), useAi: false);

        Assert.True(record.TopIssues.Single(i => i.Source == "disk").Suppressed);
        Assert.False(record.TopIssues.Single(i => i.Source == "Ntfs").Suppressed);
        Assert.Equal("高", record.RiskLevel); // 由未靜音的 Ntfs 拉高，確保報告會產出
        var report = new EfReportStore(_fx.NewContext).Read(
            new HostKey { HostId = record.HostId, HostName = record.Host }, day, ReportKinds.DailyRisk);
        Assert.NotNull(report);
        Assert.Contains("已抑制的告警 1 項", report.Content);
        Assert.Contains($"靜音至 {DateTime.Today.AddDays(2):yyyy-MM-dd}：更換磁碟陣列中", report.Content);
    }

    [Fact]
    public async Task 問題靜音_紀錄日在區間內_日風險不被該問題拉高()
    {
        var day = DateTime.Today.AddDays(-1);
        var service = CreateMuteAwareService(MutedDisk(day, day), new FakeReportSink());

        var record = await service.AnalyzeDayAsync(day, MakeHighRiskDiskEvents(), useAi: false);

        Assert.True(record.TopIssues.Single(i => i.Source == "disk").Suppressed);
        Assert.NotEqual("高", record.RiskLevel);
    }

    [Fact]
    public async Task 問題靜音_紀錄日在區間外_不標()
    {
        var day = DateTime.Today.AddDays(-1);
        var service = CreateMuteAwareService(MutedDisk(DateTime.Today, DateTime.Today.AddDays(3)), new FakeReportSink());

        var record = await service.AnalyzeDayAsync(day, MakeHighRiskDiskEvents(), useAi: false);

        Assert.False(record.TopIssues.Single(i => i.Source == "disk").Suppressed);
        Assert.Equal("高", record.RiskLevel);
    }

    /// <summary>以紀錄日判定而非執行時間：靜音昨天才設（今天仍在區間內），分析前天的資料不標</summary>
    [Fact]
    public async Task 問題靜音_昨天才設_分析前天資料不標()
    {
        var service = CreateMuteAwareService(MutedDisk(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(5)), new FakeReportSink());

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-2), MakeHighRiskDiskEvents(), useAi: false);

        Assert.False(record.TopIssues.Single(i => i.Source == "disk").Suppressed);
    }

    /// <summary>反向：過去的區間（今天已不在內），重新分析區間內的過去日子仍標記</summary>
    [Fact]
    public async Task 問題靜音_已到期的過去區間_重新分析區間內日子仍標記()
    {
        var service = CreateMuteAwareService(MutedDisk(DateTime.Today.AddDays(-5), DateTime.Today.AddDays(-3)), new FakeReportSink());

        var record = await service.AnalyzeDayAsync(DateTime.Today.AddDays(-4), MakeHighRiskDiskEvents(), useAi: false);

        Assert.True(record.TopIssues.Single(i => i.Source == "disk").Suppressed);
    }
}
