using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 慢查詢的覆蓋範圍與可讀性（回饋四十五輪 B6）。
///
/// 釘住兩件事：**埋點涵蓋 PRTG 鏡像／行式日誌／權限異動這三個過去完全沒有埋點的 store**，
/// 以及**統計從「最慢的那一筆」改成「最慢的前 N 支」**（名稱、次數、最大耗時、最近一次）。
/// 門檻 0 的 monitor 讓每一次操作都算「慢」，不必真的讓查詢跑兩秒。
/// </summary>
public class SlowQueryCoverageTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>門檻 0：每一次操作都達到門檻，等同「把所有埋點都亮起來」</summary>
    private static SqlPerformanceMonitor AlwaysSlow() => new(thresholdMs: 0);

    private static IReadOnlyList<string> Names(SqlPerformanceMonitor monitor) =>
        monitor.Snapshot().TopSlowOperations.Select(o => o.Operation).ToList();

    // ── C1：三個 store 的埋點 ────────────────────────────────────────────

    [Fact]
    public void PRTG鏡像的查詢方法有埋點()
    {
        var monitor = AlwaysSlow();
        var store = new EfPrtgStore(_fx.NewContext, monitor);

        store.GetAllDevices();
        store.GetAllSensors();
        store.GetValues(DateTime.Today, DateTime.Today.AddDays(1));

        var names = Names(monitor);
        Assert.Contains("prtg:GetAllDevices", names);
        Assert.Contains("prtg:GetAllSensors", names);
        Assert.Contains("prtg:GetValues", names);
    }

    [Fact]
    public void 行式日誌store的查詢方法有埋點()
    {
        var monitor = AlwaysSlow();
        var store = new EfJsonLogStore(_fx.NewContext, "audit", monitor);

        store.ReadLines();
        store.ReadLastLines(5);
        store.ReadLines(DateTime.Today, DateTime.Today.AddDays(1));
        store.ReadPage(0, 10);

        var names = Names(monitor);
        Assert.Contains("log:audit:ReadLines", names);
        Assert.Contains("log:audit:ReadLastLines", names);
        // 本輪 BatchRunStore 改呼叫帶日期的讀取方法，它必須自己有名字（與整份讀回分得開）
        Assert.Contains("log:audit:ReadLinesByDate", names);
        Assert.Contains("log:audit:ReadPage", names);
    }

    [Fact]
    public void 權限異動store的查詢方法有埋點()
    {
        var monitor = AlwaysSlow();
        var store = new PermissionChangeStore(_fx.NewContext, monitor);

        store.Query(new PermissionChangeQueryFilter { Page = 1, PageSize = 10 });
        store.CountPending(null);
        store.Get("不存在");

        var names = Names(monitor);
        Assert.Contains("permchange:Query", names);
        Assert.Contains("permchange:CountPending", names);
        Assert.Contains("permchange:Get", names);
    }

    /// <summary>
    /// 三個 store 由 <see cref="StorageBackend"/> 建立——建立路徑沒把 monitor 傳進去的話，
    /// 上面三條測試綠了但正式站台上仍然一個埋點都不會亮。
    /// </summary>
    [Fact]
    public void StorageBackend建立的三個store都接上monitor()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-slow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "s.db")}" }, dir);

            // 門檻是預設 2000ms，這裡不比對慢操作清單，只確認 monitor 的總操作數被這三個 store 推進
            var before = backend.Performance.Snapshot().TotalOperations;

            backend.PrtgStore().GetAllDevices();
            backend.LogStore("audit").ReadLines();
            backend.PermissionChanges().CountPending(null);

            Assert.True(backend.Performance.Snapshot().TotalOperations >= before + 3,
                "三個 store 的查詢都應計入 StorageBackend.Performance");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── C2：最慢的前 N 支 ────────────────────────────────────────────────

    [Fact]
    public void 超過N種操作時只留最慢的N支且依最大耗時排序()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 10);

        // 25 種操作，耗時 100..2500：只有最慢的 10 支（1600..2500）該留下
        for (var i = 1; i <= 25; i++) monitor.Record($"op-{i}", i * 100);

        var top = monitor.Snapshot().TopSlowOperations;
        Assert.Equal(SqlPerformanceMonitor.TopSlowCapacity, top.Count);
        Assert.Equal(2500, top[0].MaxMs);
        Assert.Equal("op-25", top[0].Operation);
        Assert.Equal(1600, top[^1].MaxMs);

        // 依最大耗時由大到小
        Assert.Equal(top.Select(o => o.MaxMs).OrderByDescending(v => v).ToList(), top.Select(o => o.MaxMs).ToList());
        Assert.DoesNotContain("op-1", top.Select(o => o.Operation));
    }

    [Fact]
    public void 同一操作重複時次數累加且最大耗時取最大值()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 10);

        monitor.Record("prtg:GetValues", 300);
        monitor.Record("prtg:GetValues", 900);
        monitor.Record("prtg:GetValues", 500);

        var top = monitor.Snapshot().TopSlowOperations;
        var entry = Assert.Single(top);
        Assert.Equal("prtg:GetValues", entry.Operation);
        Assert.Equal(3, entry.Count);
        Assert.Equal(900, entry.MaxMs);
        Assert.NotEqual(default, entry.LastAt);
    }

    [Fact]
    public void 未達門檻不進清單但仍計入總操作數()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 100);

        monitor.Record("fast", 99);
        monitor.Record("fast", 1);

        var snapshot = monitor.Snapshot();
        Assert.Empty(snapshot.TopSlowOperations);
        Assert.Equal(2, snapshot.TotalOperations);
        Assert.Equal(0, snapshot.SlowOperations);
    }

    /// <summary>Snapshot 回傳的是複本：呼叫端改它不該污染監控內部狀態</summary>
    [Fact]
    public void 快照是複本_改不到內部狀態()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 10);
        monitor.Record("op", 500);

        monitor.Snapshot().TopSlowOperations[0].MaxMs = 999_999;

        Assert.Equal(500, monitor.Snapshot().TopSlowOperations[0].MaxMs);
    }

    /// <summary>
    /// 執行緒安全靠**結構**保證而不是壓力測試：未達門檻的路徑一律不碰集合、不進鎖；
    /// 只有達到門檻的罕見路徑才進鎖。這裡釘住那段註解與唯一一把鎖的位置，
    /// 避免日後有人「順手」把 lock 搬到 Record 的第一行（等於每次量測都進鎖）。
    /// </summary>
    [Fact]
    public void 只有超過門檻才進鎖()
    {
        var path = Path.Combine(RepoRoot(), "LogForesight.Core", "Persistence", "Sql", "SqlPerformanceMonitor.cs");
        var text = File.ReadAllText(path);

        // Record 內只保留「未達門檻就 return」在前的結構：門檻判斷必須出現在第一個 lock 之前
        var thresholdIndex = text.IndexOf("if (elapsedMs < ThresholdMs) return;", StringComparison.Ordinal);
        var firstLockIndex = text.IndexOf("lock (", StringComparison.Ordinal);
        Assert.True(thresholdIndex > 0 && firstLockIndex > thresholdIndex,
            "門檻判斷必須在任何 lock 之前——否則每一次量測都會進鎖");

        Assert.Contains("只有「達到門檻」的罕見路徑才進", text);
    }

    // ── C1／C2：既有埋點名稱不得改 ──────────────────────────────────────

    [Fact]
    public void 既有埋點名稱維持不變()
    {
        var root = RepoRoot();
        var records = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Persistence", "Sql", "EfAnalysisRecordStore.cs"));
        var blob = File.ReadAllText(Path.Combine(root, "LogForesight.Core", "Persistence", "Sql", "EfJsonBlobStore.cs"));

        foreach (var name in new[]
                 {
                     "records:Query", "records:QueryLightweight", "records:QueryPage",
                     "records:QueryPendingAi", "records:CountPendingAi"
                 })
        {
            Assert.Contains($"_performance?.Record(\"{name}\"", records);
        }

        Assert.Contains("_performance?.Record($\"blob:{_key}:Read\"", blob);
        Assert.Contains("_performance?.Record($\"blob:{_key}:Mutate\"", blob);
    }

    // ── C2：前端「尚無慢查詢」 ──────────────────────────────────────────

    [Fact]
    public void 設定頁在清單為空時顯示尚無慢查詢()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "LogForesight.Web", "wwwroot", "js", "pages", "settings.js"));

        Assert.Contains("renderSlowQueries", js);
        Assert.Contains("detail.topSlowOperations", js);
        Assert.Contains("尚無慢查詢", js);

        // 空清單走的是「一句話」分支，不是建表
        var emptyBranch = js.IndexOf("if (list.length === 0)", StringComparison.Ordinal);
        var tableBuild = js.IndexOf("createElement('table')", StringComparison.Ordinal);
        Assert.True(emptyBranch > 0 && tableBuild > emptyBranch, "清單為空時要早退，不該走到建表");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln");
        return dir!.FullName;
    }
}

/// <summary>健康診斷 DTO 帶出慢查詢清單（回饋四十五輪 B6）</summary>
public class HealthSlowQueryDtoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-health-slow-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HealthSlowQueryDtoTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>HealthService 只用得到 MailNotificationService.GetSuspendedRecipients()，其餘相依給最小替身</summary>
    private HealthService NewService() => new(
        _backend, new SchedulerRunState(), _backend.TopIssueBackfiller(),
        new MailNotificationService(
            new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
            new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
            new MailNotifyStateStore(_backend.Blob("mail_notify_state"))));

    [Fact]
    public void 診斷檢查帶出最慢前幾支()
    {
        _backend.Performance.Record("prtg:GetValues", 7000);
        _backend.Performance.Record("prtg:GetValues", 3000);
        _backend.Performance.Record("permchange:Query", 4000);

        var dto = NewService().GetDetail();

        Assert.Equal(2, dto.TopSlowOperations.Count);
        Assert.Equal("prtg:GetValues", dto.TopSlowOperations[0].Operation);
        Assert.Equal(7000, dto.TopSlowOperations[0].MaxMs);
        Assert.Equal(2, dto.TopSlowOperations[0].Count);
        Assert.Equal("permchange:Query", dto.TopSlowOperations[1].Operation);
    }

    [Fact]
    public void 沒有慢操作時清單為空而不是null()
    {
        Assert.Empty(NewService().GetDetail().TopSlowOperations);
    }
}
