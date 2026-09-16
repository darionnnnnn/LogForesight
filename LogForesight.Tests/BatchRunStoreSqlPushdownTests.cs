using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 B1：<see cref="BatchRunStore"/> 的查詢下推到 SQL。
///
/// 這裡不用假的 store 替身（<see cref="EfJsonLogStore"/> 是 sealed，且替身無法證明
/// 真正送到資料庫的是什麼），而是在真的 SQLite 連線上掛 EF 的命令攔截器，
/// 記下每一道 SQL 與參數——「有沒有全分區讀取」「下界是不是預期的 cutoff」因此是可觀測的事實。
/// </summary>
public class BatchRunStoreSqlPushdownTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlRecorder _recorder = new();

    public BatchRunStoreSqlPushdownTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_recorder)
            .Options);

    private EfJsonLogStore Store(string key) => new(NewContext, key);

    private BatchRunStore NewBatchRunStore() => new(Store("batch_runs"), Store("batch_run_logs"));

    /// <summary>執行過的 SELECT 語句與其參數值</summary>
    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<(string Sql, List<object?> Parameters)> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add((command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList()));
            return base.ReaderExecuting(command, eventData, result);
        }

        public void Clear() => Commands.Clear();

        /// <summary>讀取 lf_log_lines 的 SELECT（排除寫入時的其他語句）</summary>
        public List<(string Sql, List<object?> Parameters)> LineReads() =>
            Commands.Where(c => c.Sql.Contains("lf_log_lines") && c.Sql.StartsWith("SELECT")).ToList();
    }

    /// <summary>把某個 log_key 底下第 n 筆（1-based）的附加時間改掉，模擬「較早之前寫入」</summary>
    private void Backdate(string key, int occurrence, DateTime createdAt)
    {
        using var ctx = NewContext();
        var row = ctx.LogLines.Where(l => l.LogKey == key).OrderBy(l => l.Seq).Skip(occurrence - 1).First();
        row.CreatedAt = createdAt;
        ctx.SaveChanges();
    }

    private void AppendRaw(string key, string line)
    {
        using var ctx = NewContext();
        ctx.LogLines.Add(new LogLineRow { LogKey = key, Line = line, CreatedAt = DateTime.Now });
        ctx.SaveChanges();
    }

    private static BatchRun Run(string host, DateTime startedAt, DateTime? finishedAt = null) => new()
    {
        HostName = host,
        StartedAt = startedAt,
        FinishedAt = finishedAt,
        AppVersion = "1.0",
    };

    // ── C1：建構式不再全撈 ─────────────────────────────────────────────

    [Fact]
    public void 建構store不發生全分區讀取_只做反向seek取最後幾行()
    {
        var seed = NewBatchRunStore();
        var runId = seed.StartRun(Run("SRV-01", DateTime.Now));
        seed.AppendLog(new BatchRunLog { RunId = runId, Level = "Info", Message = "x" });
        _recorder.Clear();

        _ = NewBatchRunStore();

        var reads = _recorder.LineReads();
        Assert.Equal(2, reads.Count); // 兩個分區各一次
        Assert.All(reads, r =>
        {
            Assert.Contains("LIMIT", r.Sql); // 取最後 N 行
            Assert.Contains("DESC", r.Sql); // 反向 seek
        });
    }

    [Fact]
    public void 建構store_最後一行損毀仍取得正確的最後id_不從0重新續號()
    {
        var seed = NewBatchRunStore();
        var firstRunId = seed.StartRun(Run("SRV-01", DateTime.Now));
        var firstLogId = 0L;
        seed.AppendLog(new BatchRunLog { RunId = firstRunId, Level = "Info", Message = "m1" });
        firstLogId = seed.GetLogs(firstRunId).Single().LogId;

        AppendRaw("batch_runs", "{壞掉的 JSON");
        AppendRaw("batch_run_logs", "{壞掉的 JSON");

        var reopened = NewBatchRunStore();
        var nextRunId = reopened.StartRun(Run("SRV-02", DateTime.Now));
        reopened.AppendLog(new BatchRunLog { RunId = nextRunId, Level = "Info", Message = "m2" });

        Assert.Equal(firstRunId + 1, nextRunId);
        Assert.Equal(firstLogId + 1, reopened.GetLogs(nextRunId).Single().LogId);
    }

    // ── C2：近 N 天的查詢下推 ─────────────────────────────────────────

    [Fact]
    public void GetRecentRuns只讀取帶日期下界的查詢_下界等於cutoff含緩衝()
    {
        var store = NewBatchRunStore();
        store.StartRun(Run("SRV-01", DateTime.Now));
        _recorder.Clear();

        store.GetRecentRuns(7, null);

        var read = Assert.Single(_recorder.LineReads());
        Assert.Contains("created_at", read.Sql.ToLowerInvariant());
        var cutoff = DateTime.Today.AddDays(-6);
        Assert.Contains(cutoff.AddDays(-1), read.Parameters.OfType<DateTime>());
    }

    [Fact]
    public void GetRecentErrors只讀取帶日期下界的查詢_下界等於cutoff含緩衝()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Now));
        store.AppendLog(new BatchRunLog { RunId = runId, Level = "Error", Message = "boom" });
        _recorder.Clear();

        store.GetRecentErrors(3);

        var read = Assert.Single(_recorder.LineReads());
        var cutoff = DateTime.Today.AddDays(-2);
        Assert.Contains(cutoff.AddDays(-1), read.Parameters.OfType<DateTime>());
    }

    [Fact]
    public void GetRecentRuns行為不變_業務時間過濾與排序照舊()
    {
        var store = NewBatchRunStore();
        store.StartRun(Run("SRV-01", DateTime.Today.AddDays(-1).AddHours(3)));
        store.StartRun(Run("SRV-02", DateTime.Today.AddHours(8)));
        store.StartRun(Run("SRV-03", DateTime.Today.AddDays(-30))); // 超出天數，業務時間過濾掉

        var all = store.GetRecentRuns(7, null);
        Assert.Equal(new[] { "SRV-02", "SRV-01" }, all.Select(r => r.HostName)); // 新到舊

        Assert.Equal(new[] { "SRV-01" }, store.GetRecentRuns(7, new[] { "SRV-01" }).Select(r => r.HostName));
        Assert.Empty(store.GetRecentRuns(7, Array.Empty<string>()));
    }

    // ── 驗收 4：邊界——業務時間在範圍內、附加時間略早於 cutoff ───────────

    [Fact]
    public void 附加時間略早於cutoff但業務時間在cutoff當天的執行紀錄仍查得到()
    {
        var store = NewBatchRunStore();
        // 業務時間＝cutoff 當天凌晨，附加時間＝cutoff 前一小時（跨午夜寫入）
        var cutoff = DateTime.Today.AddDays(-6);
        store.StartRun(Run("SRV-01", cutoff.AddMinutes(5)));
        Backdate("batch_runs", 1, cutoff.AddHours(-1));

        var runs = store.GetRecentRuns(7, null);

        Assert.Equal("SRV-01", Assert.Single(runs).HostName);
    }

    [Fact]
    public void 附加時間略早於cutoff但業務時間在cutoff當天的診斷紀錄仍查得到()
    {
        var store = NewBatchRunStore();
        var cutoff = DateTime.Today.AddDays(-2);
        var runId = store.StartRun(Run("SRV-01", cutoff.AddMinutes(5)));
        store.AppendLog(new BatchRunLog
        {
            RunId = runId, Level = "Error", Message = "跨午夜的錯誤", LoggedAt = cutoff.AddMinutes(6),
        });
        Backdate("batch_run_logs", 1, cutoff.AddHours(-1));

        var errors = store.GetRecentErrors(3);

        Assert.Equal("跨午夜的錯誤", Assert.Single(errors).Message);
    }

    [Fact]
    public void GetRecentErrors行為不變_只取ErrorFatal且新到舊()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Now));
        store.AppendLog(new BatchRunLog { RunId = runId, Level = "Info", Message = "i", LoggedAt = DateTime.Now });
        store.AppendLog(new BatchRunLog
        {
            RunId = runId, Level = "Error", Message = "e1", LoggedAt = DateTime.Today.AddHours(1),
        });
        store.AppendLog(new BatchRunLog
        {
            RunId = runId, Level = "Fatal", Message = "f1", LoggedAt = DateTime.Today.AddHours(5),
        });
        store.AppendLog(new BatchRunLog
        {
            RunId = runId, Level = "Error", Message = "太舊", LoggedAt = DateTime.Today.AddDays(-30),
        });

        Assert.Equal(new[] { "f1", "e1" }, store.GetRecentErrors(3).Select(l => l.Message));
    }

    // ── C3：執行詳情改用時間窗口 ───────────────────────────────────────

    [Fact]
    public void GetLogs對執行中的run仍回傳診斷紀錄()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Now)); // FinishedAt 為 null＝執行中
        store.AppendLog(new BatchRunLog { RunId = runId, Level = "Warn", Message = "w1" });
        store.AppendLog(new BatchRunLog { RunId = runId, Level = "Error", Message = "e1" });

        Assert.Null(store.GetRun(runId)!.FinishedAt);
        Assert.Equal(new[] { "w1", "e1" }, store.GetLogs(runId).Select(l => l.Message)); // 依 LogId 遞增
    }

    /// <summary>
    /// GetLogs 的窄化沒有「撈不到就全撈」的退路，依據是「診斷行的業務時間與附加時間同源」
    /// （AppendLog 設定 LoggedAt 與底層寫入附加時間都是寫入當下）。這條測試釘住那個前提：
    /// 有診斷行的執行，**只靠窄化查詢**就要取得全部的行。
    /// 將來若有人改變 AppendLog 設定時間的方式、讓兩個時間不再同源，這條會紅。
    /// </summary>
    [Fact]
    public void GetLogs只靠窄化查詢就取得該run全部的診斷行()
    {
        var store = NewBatchRunStore();
        var run = Run("SRV-01", DateTime.Now);
        var runId = store.StartRun(run);
        var expected = Enumerable.Range(1, 5).Select(i => $"m{i}").ToArray();
        foreach (var m in expected)
        {
            store.AppendLog(new BatchRunLog { RunId = runId, Level = "Warn", Message = m });
        }

        run.FinishedAt = DateTime.Now;
        store.FinishRun(run);
        _recorder.Clear();

        var logs = store.GetLogs(runId);

        Assert.Equal(expected, logs.Select(l => l.Message)); // 一行都沒漏
        var logReads = _recorder.LineReads()
            .Where(c => c.Parameters.Contains("batch_run_logs"))
            .ToList();
        var read = Assert.Single(logReads); // 只讀一次，沒有退回全撈的第二次
        Assert.Contains("created_at", read.Sql.ToLowerInvariant()); // 而且是帶時間窗口的查詢
        Assert.NotEmpty(read.Parameters.OfType<DateTime>());
    }

    /// <summary>
    /// 一趟乾淨執行（NLog target 只收 Warn 以上，零診斷行）是最常見的情況——
    /// 它不得因為「窄化查不到」而退回全撈，否則全撈就變成常態路徑、下推效益歸零。
    /// </summary>
    [Fact]
    public void GetLogs對零診斷行的執行不做全分區讀取()
    {
        var store = NewBatchRunStore();
        var run = Run("SRV-01", DateTime.Now);
        var runId = store.StartRun(run);
        run.FinishedAt = DateTime.Now;
        store.FinishRun(run);

        var other = store.StartRun(Run("SRV-02", DateTime.Now));
        store.AppendLog(new BatchRunLog { RunId = other, Level = "Warn", Message = "別人的" });
        _recorder.Clear();

        Assert.Empty(store.GetLogs(runId));

        var logReads = _recorder.LineReads().Where(c => c.Parameters.Contains("batch_run_logs")).ToList();
        var read = Assert.Single(logReads);
        Assert.Contains("created_at", read.Sql.ToLowerInvariant());
    }

    [Fact]
    public void GetLogs對找不到的runId回傳空清單()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Now));
        store.AppendLog(new BatchRunLog { RunId = runId, Level = "Warn", Message = "w1" });

        Assert.Empty(store.GetLogs(runId + 999));
    }

    [Fact]
    public void GetLogs只回傳該run的行_不混入其他run()
    {
        var store = NewBatchRunStore();
        var a = store.StartRun(Run("SRV-01", DateTime.Now));
        var b = store.StartRun(Run("SRV-02", DateTime.Now));
        store.AppendLog(new BatchRunLog { RunId = a, Level = "Warn", Message = "a1" });
        store.AppendLog(new BatchRunLog { RunId = b, Level = "Warn", Message = "b1" });
        store.AppendLog(new BatchRunLog { RunId = a, Level = "Error", Message = "a2" });

        Assert.Equal(new[] { "a1", "a2" }, store.GetLogs(a).Select(l => l.Message));
        Assert.Equal(new[] { "b1" }, store.GetLogs(b).Select(l => l.Message));
    }

    [Fact]
    public void GetRun取同RunId的最後一列_結束覆蓋開始()
    {
        var store = NewBatchRunStore();
        var run = Run("SRV-01", DateTime.Now);
        var runId = store.StartRun(run);
        run.FinishedAt = DateTime.Now;
        run.ExitCode = 0;
        run.ErrorCount = 3;
        store.FinishRun(run);

        var found = store.GetRun(runId)!;
        Assert.NotNull(found.FinishedAt);
        Assert.Equal(0, found.ExitCode);
        Assert.Equal(3, found.ErrorCount);
        Assert.Null(store.GetRun(runId + 1));
    }

    [Fact]
    public void GetRun對超出保留窗口的舊執行仍查得到_退回全撈()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Today.AddDays(-400)));
        Backdate("batch_runs", 1, DateTime.Today.AddDays(-400)); // 附加時間遠早於預設保留窗口

        var found = store.GetRun(runId);

        Assert.NotNull(found);
        Assert.Equal("SRV-01", found!.HostName);
    }

    /// <summary>
    /// 執行紀錄是 append-only，「結束」列帶的是開始時配到的 RunId。取數與 AI 排程並行時附加順序可能是
    /// 「開始 A → 開始 B → 結束 B → 結束 A」，尾端那一列是較早的 A——續號若只看尾端第一筆可解析的列，
    /// 下一趟就會配出與 B 相同的號。
    /// </summary>
    [Fact]
    public void 續號_並行執行交錯結束後不與既有RunId撞號()
    {
        var seed = NewBatchRunStore();
        var a = Run("SRV-A", DateTime.Now);
        var b = Run("SRV-B", DateTime.Now);
        var idA = seed.StartRun(a);
        var idB = seed.StartRun(b);
        b.FinishedAt = DateTime.Now; seed.FinishRun(b);
        a.FinishedAt = DateTime.Now; seed.FinishRun(a);   // 尾端列的 RunId 是較早的 A

        var next = NewBatchRunStore().StartRun(Run("SRV-C", DateTime.Now));

        Assert.True(next > idB, $"新配的 {next} 必須大於已存在的最大 RunId {idB}");
        Assert.NotEqual(idA, next);
    }

    /// <summary>RunId 單調遞增：查詢的號碼比窗口內最大的還大，代表不存在，不必為它整份讀回。</summary>
    [Fact]
    public void GetRun對窗口內不存在的runId不做全分區讀取()
    {
        var store = NewBatchRunStore();
        var last = store.StartRun(Run("SRV-01", DateTime.Now));
        _recorder.Clear();

        Assert.Null(store.GetRun(last + 100));

        var reads = _recorder.LineReads().Where(c => c.Parameters.Contains("batch_runs")).ToList();
        Assert.Single(reads);                       // 只有窗口那一趟
        Assert.Contains("created_at", reads[0].Sql); // 且是帶日期下界的窄化查詢
    }

    [Fact]
    public void GetLogs對超出保留窗口的舊執行仍查得到()
    {
        var store = NewBatchRunStore();
        var runId = store.StartRun(Run("SRV-01", DateTime.Today.AddDays(-400), DateTime.Today.AddDays(-400).AddHours(1)));
        store.AppendLog(new BatchRunLog
        {
            RunId = runId, Level = "Warn", Message = "舊的", LoggedAt = DateTime.Today.AddDays(-400),
        });
        Backdate("batch_runs", 1, DateTime.Today.AddDays(-400));
        Backdate("batch_run_logs", 1, DateTime.Today.AddDays(-400));

        Assert.Equal("舊的", Assert.Single(store.GetLogs(runId)).Message);
    }

    /// <summary>
    /// 兩個實例寫同一張表（DI 的 Singleton 查詢用、AnalysisOrchestrator 自己 new 的寫入用）：
    /// 查詢端每次都要讀 DB，不能做成常駐投影，否則永遠看不到剛寫入的執行紀錄。
    /// </summary>
    [Fact]
    public void 另一個實例寫入的執行紀錄_查詢端立刻看得到()
    {
        var reader = NewBatchRunStore();
        Assert.Empty(reader.GetRecentRuns(7, null));

        var writer = NewBatchRunStore();
        var runId = writer.StartRun(Run("SRV-99", DateTime.Now));
        writer.AppendLog(new BatchRunLog { RunId = runId, Level = "Warn", Message = "w" });

        Assert.Equal("SRV-99", Assert.Single(reader.GetRecentRuns(7, null)).HostName);
        Assert.Equal("w", Assert.Single(reader.GetLogs(runId)).Message);
    }

    /// <summary>沒有時間戳記的既存列（schema 升級前寫入）一律視為在範圍內，不得被窄化濾掉</summary>
    [Fact]
    public void 沒有附加時間的既存列仍查得到()
    {
        var store = NewBatchRunStore();
        AppendRaw("batch_runs", JsonSerializer.Serialize(
            new BatchRun { RunId = 1, HostName = "SRV-OLD", StartedAt = DateTime.Today.AddHours(2) },
            LfJsonOptions.Compact));
        using (var ctx = NewContext())
        {
            var row = ctx.LogLines.Single(l => l.LogKey == "batch_runs");
            row.CreatedAt = null;
            ctx.SaveChanges();
        }

        Assert.Equal("SRV-OLD", Assert.Single(store.GetRecentRuns(7, null)).HostName);
        Assert.Equal("SRV-OLD", store.GetRun(1)!.HostName);
    }
}
