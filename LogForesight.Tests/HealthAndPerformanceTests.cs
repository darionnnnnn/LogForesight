using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 慢操作統計與健康檢查（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
///
/// 這兩者是「企業級可運維」的最低門檻：沒有它們，「站台現在是不是變慢了」
/// 只能靠人上伺服器翻 log。測試釘住的是**門檻語意**與**不洩漏內部細節**兩件事。
/// </summary>
public class SqlPerformanceMonitorTests
{
    [Fact]
    public void 未達門檻不計入慢操作()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 100);
        monitor.Record("records:Query", 99);

        var snapshot = monitor.Snapshot();
        Assert.Equal(1, snapshot.TotalOperations);
        Assert.Equal(0, snapshot.SlowOperations);
        Assert.Equal(0, snapshot.SlowestMs);
        Assert.Null(snapshot.LastSlowAt);
    }

    /// <summary>門檻是「達到即算」而非「超過才算」——邊界值要講死，否則調門檻時沒人知道包不包含</summary>
    [Fact]
    public void 達到門檻即計入()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 100);
        monitor.Record("blob:issue_handling:Mutate", 100);

        var snapshot = monitor.Snapshot();
        Assert.Equal(1, snapshot.SlowOperations);
        Assert.Equal(100, snapshot.SlowestMs);
        Assert.Equal("blob:issue_handling:Mutate", snapshot.SlowestOperation);
        Assert.NotNull(snapshot.LastSlowAt);
    }

    [Fact]
    public void 只記住最慢的一筆_較快的後續不覆蓋()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 10);
        monitor.Record("slow-one", 500);
        monitor.Record("fast-one", 20);

        var snapshot = monitor.Snapshot();
        Assert.Equal(2, snapshot.SlowOperations);
        Assert.Equal(500, snapshot.SlowestMs);
        Assert.Equal("slow-one", snapshot.SlowestOperation);
    }

    [Fact]
    public void 併發記錄_計數不遺失()
    {
        var monitor = new SqlPerformanceMonitor(thresholdMs: 10);

        Parallel.For(0, 200, i => monitor.Record($"op-{i}", i % 2 == 0 ? 50 : 1));

        var snapshot = monitor.Snapshot();
        Assert.Equal(200, snapshot.TotalOperations);
        Assert.Equal(100, snapshot.SlowOperations);
    }
}

/// <summary>健康檢查端點的行為（匿名層與診斷層的資訊邊界）</summary>
public class HealthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-health-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HealthServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "h.db")}" }, _dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 暫存目錄刪不掉不影響結論 */ }
        GC.SuppressFinalize(this);
    }

    private HealthService NewService() => new(_backend, new SchedulerRunState());

    [Fact]
    public void 存活檢查_資料庫可達時回ok()
    {
        var dto = NewService().GetLiveness();

        Assert.Equal(HealthStatuses.Ok, dto.Status);
        Assert.True(dto.StorageOk);
        Assert.False(string.IsNullOrWhiteSpace(dto.Version));
    }

    /// <summary>
    /// 匿名端點**只有三個欄位**。這條測試存在的理由是防止日後有人「順手」把診斷資訊
    /// 加進 liveness——連線字串、資料表、使用者數都是可用來探查系統的資訊，
    /// 而 liveness 是設計上要給未登入者（監控系統）讀的。
    /// </summary>
    [Fact]
    public void 存活檢查_不洩漏任何內部細節()
    {
        var dto = NewService().GetLiveness();

        var properties = dto.GetType().GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Status", "StorageOk", "Version" }, properties);
    }

    [Fact]
    public void 診斷檢查_慢操作比例過高時為degraded()
    {
        // 門檻預設 2000ms；灌 100 次操作、其中 10 次慢 → 10% ≥ 5%
        for (var i = 0; i < 90; i++) _backend.Performance.Record("fast", 1);
        for (var i = 0; i < 10; i++) _backend.Performance.Record("slow", 5000);

        var dto = NewService().GetDetail();

        Assert.Equal(HealthStatuses.Degraded, dto.Status);
        Assert.Equal(10, dto.SlowOperations);
        Assert.Equal(5000, dto.SlowestMs);
        Assert.Equal("slow", dto.SlowestOperation);
        // 總數不寫死：GetDetail 自己的資料庫可達性探測也是一次資料層操作，會一併計入。
        // 這是刻意的——探測若因為某種原因變慢，它本身就該出現在慢操作統計裡
        Assert.True(dto.TotalOperations >= 100, $"總操作數 {dto.TotalOperations} 應至少含灌入的 100 次");
    }

    [Fact]
    public void 診斷檢查_無慢操作時為ok()
    {
        var dto = NewService().GetDetail();

        Assert.Equal(HealthStatuses.Ok, dto.Status);
        Assert.Null(dto.StorageError);
        Assert.False(dto.AnalysisRunning);
    }

    /// <summary>分析執行中要看得出來——E1（夜間分析與 Web 同行程）時，這是「畫面為什麼變慢」的第一線索</summary>
    [Fact]
    public void 診斷檢查_反映分析執行狀態()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("manual", out _));
        runState.ReportProgress("分析主機", 30, 100);

        var dto = new HealthService(_backend, runState).GetDetail();

        Assert.True(dto.AnalysisRunning);
        Assert.Equal("manual", dto.AnalysisTrigger);
        Assert.Equal("分析主機", dto.AnalysisPhase);
        Assert.Equal(30, dto.AnalysisDone);
        Assert.Equal(100, dto.AnalysisTotal);
    }
}
