using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 78 輪批次 5b（5-7）：執行前預覽只需要日層級狀態，改走不讀問題子表的窄查詢；回應與改動前相同。
/// </summary>
public sealed class RunPreviewBatch5bTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LogForesight-preview5b-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly SqlCommandRecorder _recorder = new();

    public RunPreviewBatch5bTests()
    {
        Directory.CreateDirectory(_root);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "preview.db")};Pooling=False"
        }, _root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LogIssueSignature Issue(int id) => new()
    {
        LogName = "System", Source = "disk", EventId = id, EntryType = LogEntryType.Warning,
        Category = IssueCategory.Hardware, Severity = IssueSeverity.High, Count = 1
    };

    /// <summary>混合資料：缺日、AI 待補、AI 已分析、低風險、只有 PRTG 的主機日、本機與 NetIQ 兩種來源、不同日期。</summary>
    private void SeedMixed(DateTime day)
    {
        var store = _backend.RecordStore();
        void Add(long hostId, string host, DateTime date, string risk, bool aiAnalyzed, bool aiPending, AnalysisLogSource source = AnalysisLogSource.Netiq) =>
            store.Append(new DailyAnalysisRecord
            {
                Date = date, HostId = hostId, Host = host, RiskLevel = risk, AiAnalyzed = aiAnalyzed, AiPending = aiPending,
                LogSource = source, TopIssues = new List<LogIssueSignature> { Issue((int)hostId) }
            });
        Add(1, "preview-host-1", day, RiskLevels.High, aiAnalyzed: true, aiPending: false);    // 完整
        Add(2, "preview-host-2", day, RiskLevels.High, aiAnalyzed: false, aiPending: false);   // AI 未跑 → 要補
        Add(3, "preview-host-3", day, RiskLevels.Low, aiAnalyzed: false, aiPending: false);    // 低風險 → 不需 AI
        Add(4, "preview-host-4", day, RiskLevels.High, aiAnalyzed: false, aiPending: true);    // AI 待補中 → 不重複補
        // 5 號主機沒有任何紀錄 → 缺日
        Add(6, "preview-host-6", day, RiskLevels.High, aiAnalyzed: true, aiPending: false, AnalysisLogSource.Prtg);   // 只有 PRTG → 仍是缺日
        Add(0, Environment.MachineName, day, RiskLevels.Medium, aiAnalyzed: false, aiPending: false, AnalysisLogSource.Local);
    }

    private ScheduleController Controller(IAnalysisRecordQuery? records = null)
    {
        var hosts = new FakeHostStore();
        foreach (var id in new long[] { 1, 2, 3, 4, 5, 6 })
            hosts.Upsert(new WebHost
            {
                HostId = id, HostName = "preview-host-" + id, IpAddress = $"10.0.0.{id}", Os = WebHost.OsWindows,
                SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true
            });
        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });
        var settings = new FakeSystemSettingsStore();
        return new ScheduleController(
            new ScheduleOptionsStore(_backend.Blob("schedule_options")), null!, new SchedulerRunState(),
            hosts, sentinels, new RecordingAuditService(), new FakeCurrentUser(), new FakeUserStore(),
            records ?? _backend.RecordStore(), settings, new UserDisplayNameService(settings), null!, new AiAnalysisRunState(),
            storageBackend: _backend);
    }

    /// <summary>A5：回應與改動前相同。預言機＝直接用完整的輕量查詢重做一次舊的計數。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void A5_執行預覽的回應與以完整輕量查詢算出的相同(int backfillDays)
    {
        var day = DateTime.Today.AddDays(-1);
        SeedMixed(day);
        SeedMixed(day.AddDays(-1));
        var controller = Controller();

        var response = controller.RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: backfillDays);

        // 預言機：舊做法（QueryLightweight 整批含問題列）算出的計數
        var from = DateTime.Today.AddDays(-backfillDays);
        var to = DateTime.Today.AddDays(-1);
        var records = _backend.RecordStore().QueryLightweight(new RecordQueryFilter { From = from, To = to })
            .Where(r => r.LogSource != AnalysisLogSource.Prtg).ToList();
        const bool useAi = true;   // 控制器的 _webAi 為 null → aiDisabled=false → useAi=true
        var byHost = records.Where(r => r.HostId != 0).GroupBy(r => r.HostId).ToDictionary(g => g.Key, g => g.ToList());
        var expected = 0;
        foreach (var id in new long[] { 1, 2, 3, 4, 5, 6 })
            if (!byHost.TryGetValue(id, out var list) || list.Count < backfillDays || list.Any(r => HostDayPostProcessor.NeedsBackfill(r, useAi)))
                expected++;
        var local = records.Where(r => string.Equals(r.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (local.Count < backfillDays || local.Any(r => HostDayPostProcessor.NeedsBackfill(r, useAi))) expected++;

        Assert.True(response.Success);
        Assert.Equal(expected, response.Data!.HostCount);
        Assert.True(expected > 0);
    }

    /// <summary>窄查詢與完整輕量查詢在共同欄位上逐筆相同（筆數、順序、每個欄位），且不讀問題子表。</summary>
    [Fact]
    public void A5_窄查詢與完整輕量查詢共同欄位相同_且不讀問題子表()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedMixed(day);
        SeedMixed(day.AddDays(-2));
        var full = _backend.RecordStore().QueryLightweight(new RecordQueryFilter { From = day.AddDays(-3), To = day });

        var connection = _backend.CreateContext().Database.GetDbConnection();
        var recordingStore = new EfAnalysisRecordStore(
            () => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(_recorder).Options),
            "default");
        _recorder.Clear();
        var narrow = recordingStore.QueryDayStatusLightweight(new RecordQueryFilter { From = day.AddDays(-3), To = day });

        Assert.Equal(full.Count, narrow.Count);
        for (var i = 0; i < full.Count; i++)
        {
            Assert.Equal(full[i].HostId, narrow[i].HostId);
            Assert.Equal(full[i].Host, narrow[i].Host);
            Assert.Equal(full[i].Date, narrow[i].Date);
            Assert.Equal(full[i].RiskLevel, narrow[i].RiskLevel);
            Assert.Equal(full[i].AiAnalyzed, narrow[i].AiAnalyzed);
            Assert.Equal(full[i].AiPending, narrow[i].AiPending);
            Assert.Equal(full[i].LogSource, narrow[i].LogSource);
            Assert.Equal(full[i].LatestNetiqAttemptStatus, narrow[i].LatestNetiqAttemptStatus);
            Assert.Empty(narrow[i].TopIssues);
        }
        Assert.NotEmpty(full.SelectMany(r => r.TopIssues));
        Assert.DoesNotContain(_recorder.Selects(), c => c.Sql.Contains("lf_top_issues", StringComparison.OrdinalIgnoreCase));
        Assert.Single(_recorder.Selects());
    }

    /// <summary>帶問題層級條件時退回完整的輕量查詢（覆核需要問題列），結果與完整查詢相同。</summary>
    [Fact]
    public void A5_帶問題層級條件時退回完整輕量查詢()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedMixed(day);
        var store = _backend.RecordStore();
        var filter = new RecordQueryFilter { From = day, To = day, EventId = 2 };

        var narrow = store.QueryDayStatusLightweight(filter);
        var full = store.QueryLightweight(filter);

        Assert.Equal(full.Select(r => r.HostId), narrow.Select(r => r.HostId));
        Assert.Equal(full.Select(r => r.TopIssues.Count), narrow.Select(r => r.TopIssues.Count));
    }

    /// <summary>A5：預覽本身不讀問題子表（記錄資料庫指令，確認沒有任何一道查詢碰 lf_top_issues），回應仍與完整輕量查詢相同。</summary>
    [Fact]
    public void A5_執行預覽_不讀問題子表_回應不變()
    {
        var day = DateTime.Today.AddDays(-1);
        SeedMixed(day);
        var expected = Controller().RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1).Data!.HostCount;

        var connection = _backend.CreateContext().Database.GetDbConnection();
        var recordingStore = new EfAnalysisRecordStore(
            () => new LfDbContext(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(connection).AddInterceptors(_recorder).Options),
            "default");
        var controller = Controller(recordingStore);
        _recorder.Clear();
        var response = controller.RunPreview("all", null, null, onlyMissingOrFailed: true, backfillDays: 1);

        Assert.Equal(expected, response.Data!.HostCount);
        Assert.NotEmpty(_recorder.Selects());
        Assert.DoesNotContain(_recorder.Selects(), c => c.Sql.Contains("lf_top_issues", StringComparison.OrdinalIgnoreCase));
    }
}
