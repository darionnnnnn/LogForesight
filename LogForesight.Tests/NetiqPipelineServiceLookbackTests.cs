using System.ComponentModel.DataAnnotations;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="NetiqPipelineService.ResolveLookbackDays"/>——首次執行與缺漏日回補統一套用
/// BackfillDays，只夾在 <see cref="NetiqOptions.MaxBackfillDaysLimit"/>（回望窗口與趨勢基線
/// 窗口是兩件不同的事，不互相夾住）。
/// </summary>
public class NetiqPipelineServiceLookbackTests
{
    [Fact]
    public void 預設值1只回補一天()
    {
        Assert.Equal(1, NetiqPipelineService.ResolveLookbackDays(backfillDays: 1));
    }

    [Fact]
    public void BackfillDays設為3時回補三天內缺漏()
    {
        Assert.Equal(3, NetiqPipelineService.ResolveLookbackDays(backfillDays: 3));
    }

    /// <summary>回望天數不再被趨勢窗口（14）夾住——設 30 就回望 30 天</summary>
    [Fact]
    public void BackfillDays設為30時不被趨勢窗口夾住()
    {
        Assert.Equal(30, NetiqPipelineService.ResolveLookbackDays(backfillDays: 30));
    }

    [Fact]
    public void BackfillDays設為180時放寬通過()
    {
        Assert.Equal(180, NetiqPipelineService.ResolveLookbackDays(backfillDays: 180));
    }

    [Fact]
    public void BackfillDays超過絕對天花板上限時夾在上限()
    {
        Assert.Equal(NetiqOptions.MaxBackfillDaysLimit, NetiqPipelineService.ResolveLookbackDays(backfillDays: 400));
    }

    [Fact]
    public void BackfillDays為0時維持不回望的既有語意()
    {
        Assert.Equal(0, NetiqPipelineService.ResolveLookbackDays(backfillDays: 0));
        Assert.Equal(0, NetiqPipelineService.ResolveLookbackDays(backfillDays: -3));
    }

    [Theory]
    [InlineData(180, 180)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(500, 365)]
    [InlineData(0, 1)]
    public void GetEffectiveBackfillDaysLimit_依RetentionDays計算且不超過絕對天花板(int retentionDays, int expected)
    {
        Assert.Equal(expected, NetiqOptions.GetEffectiveBackfillDaysLimit(retentionDays));
    }

    /// <summary>DTO 驗證邊界：365 通過、366 被拒（與 pipeline 夾值共用同一個絕對天花板常數）</summary>
    [Theory]
    [InlineData(365, true)]
    [InlineData(366, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void BackfillDays的DTO驗證以絕對天花板常數為界(int value, bool expectValid)
    {
        var dto = new UpdateNetiqOptionsRequest { BackfillDays = value };
        var ctx = new ValidationContext(dto) { MemberName = nameof(UpdateNetiqOptionsRequest.BackfillDays) };
        var ok = Validator.TryValidateProperty(value, ctx, new List<ValidationResult>());
        Assert.Equal(expectValid, ok);
    }

    /// <summary>驗證 1：TriggerRunRequest.BackfillDays 超過絕對天花板 → 被 [Range] 擋下（不是 clamp）</summary>
    [Theory]
    [InlineData(365, true)]
    [InlineData(366, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void TriggerRunRequest的BackfillDays以絕對天花板為界(int value, bool expectValid)
    {
        var dto = new TriggerRunRequest { BackfillDays = value };
        var ctx = new ValidationContext(dto) { MemberName = nameof(TriggerRunRequest.BackfillDays) };
        var ok = Validator.TryValidateProperty(value, ctx, new List<ValidationResult>());
        Assert.Equal(expectValid, ok);
    }

    /// <summary>驗證 2：BackfillDays 在天花板內、但超過目前 RetentionDays → 回 400，訊息含上限數字與說明。
    /// 直接測抽出的驗證方法：先前為了建構整個 controller 而在正式碼加了恆真的 null 分支，
    /// 「通過」的斷言實際上來自常數——那是測試假通過，已改揉。</summary>
    [Fact]
    public void ValidateBackfillDays_超過RetentionDays時拒絕且訊息含上限數字()
    {
        var ex = Assert.Throws<DomainException>(() => ScheduleController.ValidateBackfillDays(200, retentionDays: 180));

        Assert.Contains("180", ex.Message);
        Assert.Contains("超過保留天數的回補資料會在下次清理時被刪除", ex.Message);
    }

    /// <summary>驗證 3：BackfillDays 等於 RetentionDays → 通過；未指定也通過</summary>
    [Fact]
    public void ValidateBackfillDays_等於上限或未指定時通過()
    {
        ScheduleController.ValidateBackfillDays(180, retentionDays: 180);
        ScheduleController.ValidateBackfillDays(null, retentionDays: 180);
    }

    /// <summary>驗證 4：去重鍵查詢起點依實際回望天數推算（例如回望 5 天 → 起點是 12 天前），不隨絕對上限常數改變</summary>
    [Fact]
    public async Task NetiqPipeline_去重鍵查詢起點依實際回望天數加七天推算()
    {
        using var fixture = new EfSqliteFixture();
        var dir = Path.Combine(Path.GetTempPath(), "lf-dedupe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "p.db")}" }, dir);

            var sentinels = new FakeSentinelStore();
            var sentinel = sentinels.Upsert(new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true });
            var hosts = new FakeHostStore();
            hosts.Upsert(new WebHost { HostId = 1, HostName = "host1", IpAddress = "10.0.0.1", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });

            var client = new FakeSentinelSearchClient();
            var batchRunStore = new BatchRunStore(backend.LogStore("r1"), backend.LogStore("r2"));
            var runRecorder = new BatchRunRecorder(batchRunStore, "test", Array.Empty<string>());
            var caseCoordinator = new IssueCaseCoordinator(
                backend.IssueCaseStore(), backend.IssueHandlingStore(), backend.RecordHandlingStore(),
                backend.RecordStore(), hosts, new IssueOwnerStore(backend.Blob("issue_owners")));
            var reportService = new RiskReportService(new FakeAiService(), new FakeReportSink());

            var spyStore = new SpyPermissionChangeStore();

            var pipeline = new NetiqPipelineService(
                backend, new NetiqOptions { BackfillDays = 5 }, sentinels, hosts, new EventLogService(),
                new FakeAiService(), new FakeSuppressionStore(), reportService, runRecorder, caseCoordinator,
                new RecordingRunConsole(new List<string>()),
                clientFactory: FakeSentinelSearchClientFactory.Single(client),
                permissionChangeStore: spyStore);

            var hostList = HostListSelection.FromStore(hosts, sentinels);
            await pipeline.RunAsync(hostList, 14);

            // 回望 5 天 → 起點為 12 天前 (5 + 7 = 12)，絕不是以 365 常數推算的 372 天前
            var expectedDate = DateTime.Today.AddDays(-12);
            Assert.NotNull(spyStore.LastAppendedSince);
            Assert.Equal(expectedDate, spyStore.LastAppendedSince.Value.Date);
            Assert.NotEqual(DateTime.Today.AddDays(-(NetiqOptions.MaxBackfillDaysLimit + 7)), spyStore.LastAppendedSince.Value.Date);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private class SpyPermissionChangeStore : PermissionChangeStore
    {
        public DateTime? LastAppendedSince { get; private set; }
        public SpyPermissionChangeStore() : base(() => null!) { }

        public override HashSet<string> GetDedupeKeys(DateTime? appendedSince = null)
        {
            LastAppendedSince = appendedSince;
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}

/// <summary>
/// Sentinel 平行度的行程內上限（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-3）。
///
/// 分析已搬進網站行程執行（本輪定案不拆獨立 worker），平行度不再只是「分析跑多快」，
/// 而是「同時有幾條執行緒在跟使用者的請求搶 thread pool 與連線」。管理者的設定仍然有效，
/// 但被夾在一個安全上限內——真的需要更快，該做的是拆 worker，不是把這個數字調大。
/// </summary>
public class NetiqPipelineParallelismTests
{
    [Fact]
    public void 設定值在上限內時照設定執行()
    {
        Assert.Equal(2, NetiqPipelineService.ResolveParallelism(2));
    }

    [Fact]
    public void 設定值超過行程內上限時夾住()
    {
        Assert.Equal(NetiqOptions.MaxParallelServersLimit, NetiqPipelineService.ResolveParallelism(8));
    }

    /// <summary>設 1＝完全依序，是既有語意（docs/archive/FEEDBACK-3-PLAN.md #2），不該被上限邏輯改掉</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void 設定值小於一時一律視為依序(int configured, int expected)
    {
        Assert.Equal(expected, NetiqPipelineService.ResolveParallelism(configured));
    }
}

/// <summary>
/// 單一 Sentinel 內部的批次查詢平行度（回饋十三輪 D），與 <see cref="NetiqPipelineParallelismTests"/>
/// 同一個「行程架構上限，不是效能旋鈕」理由，只是維度不同——見 NetiqOptions.MaxParallelQueriesPerServer。
/// </summary>
public class NetiqPipelineQueryParallelismTests
{
    [Fact]
    public void 設定值在上限內時照設定執行()
    {
        Assert.Equal(2, NetiqPipelineService.ResolveQueryParallelism(2));
    }

    [Fact]
    public void 設定值超過行程內上限時夾住()
    {
        Assert.Equal(NetiqOptions.MaxParallelQueriesPerServerLimit, NetiqPipelineService.ResolveQueryParallelism(8));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void 設定值小於一時一律視為依序(int configured, int expected)
    {
        Assert.Equal(expected, NetiqPipelineService.ResolveQueryParallelism(configured));
    }
}
