using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 慢操作統計與健康檢查（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
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

    private HealthService NewService() => new(_backend, new SchedulerRunState(), _backend.TopIssueBackfiller(), NewMailService());

    /// <summary>HealthService 只用得到 GetSuspendedRecipients()（回饋十七輪批次B-1），
    /// 其餘相依給最小可用的替身即可</summary>
    private MailNotificationService NewMailService() => new(
        new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
        new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
        new MailNotifyStateStore(_backend.Blob("mail_notify_state")));

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

        var dto = new HealthService(_backend, runState, _backend.TopIssueBackfiller(), NewMailService()).GetDetail();

        Assert.True(dto.AnalysisRunning);
        Assert.Equal("manual", dto.AnalysisTrigger);
        Assert.Equal("分析主機", dto.AnalysisPhase);
        Assert.Equal(30, dto.AnalysisDone);
        Assert.Equal(100, dto.AnalysisTotal);
    }

    /// <summary>首見日合併失敗重試：連續失敗達上限（3次）後停止重試並標記為失敗狀態</summary>
    [Fact]
    public async Task 首見日合併_連續失敗達上限後停止重試並標記為失敗狀態()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lf-failing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "f.db");
            var tempBackend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={dbPath}" },
                tempDir);

            // 破壞 table 模擬執行期查詢或合併失敗
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DROP TABLE lf_top_issues;";
                cmd.ExecuteNonQuery();
            }

            var service = new IssueFirstSeenSeedHostedService(tempBackend, new DataVersionStamp())
            {
                InitialDelay = TimeSpan.Zero,
                RetryInterval = TimeSpan.Zero
            };

            // 走 BackgroundService 的公開入口而非測試墊片：StartAsync 會啟動 ExecuteAsync 並回傳
            // 代表它的 Task（ExecuteTask），等它結束就等於等重試迴圈跑完
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StartAsync(cts.Token);
            await service.ExecuteTask!;

            Assert.Equal(IssueFirstSeenSeedHostedService.MaxRetries, service.Progress.Failures);
            Assert.Equal(IssueFirstSeenSeedStates.Failed, service.Progress.State);
            Assert.NotNull(service.Progress.LastError);
            Assert.True(service.Progress.IsFailed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>/api/health/detail 回應需包含首見日合併狀態，且3次失敗時反映為 degraded</summary>
    [Fact]
    public void 健康檢查_診斷檢查含首見日合併狀態與降級反映()
    {
        var runState = new SchedulerRunState();
        var seedService = new IssueFirstSeenSeedHostedService(_backend, new DataVersionStamp());

        // 1. 初始/未開始狀態
        var healthService = new HealthService(_backend, runState, _backend.TopIssueBackfiller(), NewMailService(), seedService);
        var detail1 = healthService.GetDetail();
        Assert.Equal(IssueFirstSeenSeedStates.NotStarted, detail1.IssueFirstSeenSeedState);
        Assert.Equal(0, detail1.IssueFirstSeenSeedFailures);
        Assert.Null(detail1.IssueFirstSeenSeedError);
        Assert.Equal(HealthStatuses.Ok, detail1.Status);

        // 2. 已跳過或已完成狀態
        seedService.Progress.State = IssueFirstSeenSeedStates.Skipped;
        var detail2 = healthService.GetDetail();
        Assert.Equal(IssueFirstSeenSeedStates.Skipped, detail2.IssueFirstSeenSeedState);
        Assert.Equal(HealthStatuses.Ok, detail2.Status);

        // 3. 連續失敗達上限（3次）狀態 -> 狀態反映為 degraded
        seedService.Progress.State = IssueFirstSeenSeedStates.Failed;
        seedService.Progress.Failures = IssueFirstSeenSeedHostedService.MaxRetries;
        seedService.Progress.LastError = "逾時或連線中斷";
        var detail3 = healthService.GetDetail();
        Assert.Equal(IssueFirstSeenSeedStates.Failed, detail3.IssueFirstSeenSeedState);
        Assert.Equal(3, detail3.IssueFirstSeenSeedFailures);
        Assert.Equal("逾時或連線中斷", detail3.IssueFirstSeenSeedError);
        Assert.Equal(HealthStatuses.Degraded, detail3.Status);
    }
}
