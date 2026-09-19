using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// <see cref="IRecordHandlingStore"/> 的真表實作（快照 ↔ lf_record_handling；
/// 歷程仍走 append-only 的 <see cref="EfJsonLogStore"/>，docs/archive/SCALE-ISSUE-FIRST-PLAN.md P3）。
///
/// **兩個改動，各修一個體檢項目**：
///   - 快照從整份 blob 改真表（根因 B）。
///   - 歷程的**讀取**下推到 SQL（體檢 S5／規劃 N4）：舊實作的 <c>GetLogs</c> 是
///     <c>ReadAllLogs()</c> 讀回全部行再過濾，而且建構式為了取歷程序號也整份讀一次——
///     這個 store 是 Singleton，等於**站台啟動時同步讀千萬列**，第一個觸發它的請求
///     長時間無回應。改為 GetLogs 以日期範圍先在 SQL 端窄化；序號的取得方式見
///     <see cref="AppendLog"/>（每次寫入只讀尾端幾行，不留任何記憶體快取）。
/// </summary>
public sealed class EfRecordHandlingStore : IRecordHandlingStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly EfJsonLogStore _logStore;
    private readonly object _logLock = new();

    public EfRecordHandlingStore(Func<LfDbContext> contextFactory, EfJsonLogStore logStore)
    {
        _contextFactory = contextFactory;
        _logStore = logStore;
    }

    // ── 快照 ────────────────────────────────────────────────────────────────

    public RecordHandling? Get(string hostName, DateTime date)
    {
        var key = HostNameKey.Of(hostName);
        var day = date.Date;

        using var ctx = _contextFactory();
        var row = ctx.RecordHandlings.AsNoTracking()
            .FirstOrDefault(h => h.HostNameKey == key && h.RecordDate == day);
        return row == null ? null : ToModel(row);
    }

    public List<RecordHandling> GetMany(IEnumerable<string> hostNames, DateTime from, DateTime to)
    {
        var keys = hostNames.Select(HostNameKey.Of).Distinct().ToList();
        if (keys.Count == 0) return new List<RecordHandling>();

        var f = from.Date;
        var t = to.Date;

        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => keys.Contains(h.HostNameKey) && h.RecordDate >= f && h.RecordDate <= t)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<RecordHandling> GetUnresolved()
    {
        // Unresolved 的定義在 Core 的 HandlingStatuses，下推成 IN 條件（EF 8：單一 JSON 參數）
        var unresolved = HandlingStatuses.Unresolved.ToList();

        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => unresolved.Contains(h.Status))
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public List<RecordHandling> GetByHandler(long userId)
    {
        using var ctx = _contextFactory();
        return ctx.RecordHandlings.AsNoTracking()
            .Where(h => h.HandlerId == userId)
            .ToList()
            .Select(ToModel)
            .ToList();
    }

    public void Save(RecordHandling handling)
    {
        var key = HostNameKey.Of(handling.HostName);
        var day = handling.Date.Date;

        using var ctx = _contextFactory();
        var row = ctx.RecordHandlings.FirstOrDefault(h => h.HostNameKey == key && h.RecordDate == day);

        if (row == null)
        {
            row = new RecordHandlingRow { HostName = handling.HostName, HostNameKey = key, RecordDate = day };
            ctx.RecordHandlings.Add(row);
        }

        row.HostName = handling.HostName;
        row.Status = handling.Status;
        row.HandlerId = handling.HandlerId;
        row.DueDate = handling.DueDate;
        row.Note = handling.Note;
        row.UpdatedAt = handling.UpdatedAt;

        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 樂觀鎖衝突（D3）：同一個風險日兩個人同時改處理狀態，後寫的要看到 409 不是 500
            throw new ConcurrentUpdateException($"{handling.HostName} {day:yyyy-MM-dd} 的處理狀態", ex);
        }
    }

    // ── 歷程（append-only，仍走 lf_log_lines）────────────────────────────────

    public void AppendLog(RecordHandlingLog log)
    {
        lock (_logLock)
        {
            // 每次寫入都重新探測尾端取得起點。
            // 理由：目前系統內可能存在多個獨立實例（例如 Web 端註冊的 Singleton，以及
            // AnalysisOrchestrator 內自建的 StorageBackend 實例）。如果使用記憶體快取序號，
            // 夜間分析寫入多筆歷程後，Web 端的快取會落後，導致隔天 Web 端寫入時發生 LogId 重號。
            // 探測只讀尾端一小段（同一次索引反向 seek），不值得為了快取承擔重號風險。
            var next = NextLogIdStart();
            _logStore.AppendLine(PrepareAndSerialize(log, next));
        }
    }

    public void AppendLogs(IReadOnlyList<RecordHandlingLog> logs)
    {
        if (logs.Count == 0) return;

        lock (_logLock)
        {
            var next = NextLogIdStart();
            var lines = new List<string>(logs.Count);
            foreach (var log in logs)
            {
                lines.Add(PrepareAndSerialize(log, next++));
            }
            _logStore.AppendLines(lines);
        }
    }

    private static string PrepareAndSerialize(RecordHandlingLog log, long logId)
    {
        log.LogId = logId;
        if (log.CreatedAt == default) log.CreatedAt = DateTime.Now;
        return JsonSerializer.Serialize(log, LfJsonOptions.Compact);
    }

    /// <summary>
    /// 單一風險日的處理歷程。**先在 SQL 端以插入時間窄化候選集**再於記憶體精確比對——
    /// 歷程列的業務日期在 JSON 內容裡（不是欄位），無法直接下推；但一天的歷程必然寫在
    /// 那一天之後，用插入時間當粗篩就能把千萬列縮到幾百列。
    ///
    /// 下界取業務日期當天 00:00（歷程不可能寫在事件發生之前）；上界不設——
    /// 使用者可能在事件發生後很久才回頭標記。
    /// </summary>
    public List<RecordHandlingLog> GetLogs(string hostName, DateTime date)
    {
        var lines = _logStore.ReadLines(from: date.Date, to: null);

        return JsonLogParser.Parse<RecordHandlingLog>(lines, LfJsonOptions.Compact)
            .Where(l => string.Equals(l.HostName, hostName, StringComparison.OrdinalIgnoreCase) &&
                        l.Date.Date == date.Date)
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.LogId)
            .ToList();
    }

    /// <summary>
    /// 續號探測的回看行數。要涵蓋**一整個插入批次**：批次寫入（<see cref="AppendLogs"/>）在 SQL Server 上
    /// 以 MERGE 一次插入多列，自增的 seq 不保證照清單順序配發，LogId 最大的那列不一定是 seq 最大的那列
    /// （EF 對 SQL Server 預設每批 42 列）。損毀通常是連續的一小段也在這個範圍內。
    /// 這仍是索引 (log_key, seq) 的同一次反向 seek。
    /// </summary>
    private const int LogIdProbeLines = 100;

    /// <summary>下一個 LogId＝回看窗內可解析的最大 LogId＋1（共用 <see cref="EfJsonLogStore.ProbeMaxId{T}"/>，
    /// 全部解析失敗時該方法記 Warn 回 0）</summary>
    private long NextLogIdStart() =>
        _logStore.ProbeMaxId<RecordHandlingLog>(l => l.LogId, LogIdProbeLines, "處理歷程", LfJsonOptions.Compact) + 1;

    // ── 保留天數（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4／G4）────────────────────
    //
    // **快照與歷程用不同的保留天數，這是刻意的**：
    //   - 快照（lf_record_handling）是「這台主機那一天現在是什麼狀態」，
    //     分析紀錄清掉之後就再也讀不到 → 跟著 RetentionDays（預設 120）。
    //   - 歷程（handling_log）是「誰在什麼時候把它標成什麼、為什麼」，
    //     那是**追責用的證據**，性質接近稽核而不是執行歷程 → 跟著 AuditRetentionDays
    //     （預設 730）。用 RunLogRetentionDays（90）會讓證據比被追究的事件更早消失。

    /// <summary>清除超過保留天數的日層級處理狀態快照（S-4，與分析紀錄同一個 RetentionDays）</summary>
    public int Prune(int retentionDays) => Prune(retentionDays, BatchedPrune.MaxRowsPerRun, BatchedPrune.BatchSize);

    /// <summary>上限與批次可調的多載，供測試以小數字驗證分批與上限行為</summary>
    internal int Prune(int retentionDays, int maxRows, int batchSize)
    {
        var cutoff = DateTime.Today.AddDays(-retentionDays);

        return BatchedPrune.Run<long>(_contextFactory,
            (ctx, take) => ctx.RecordHandlings
                .Where(h => h.RecordDate < cutoff)
                .OrderBy(h => h.RecordDate)
                .Select(h => h.Id)
                .Take(take)
                .ToList(),
            (ctx, ids) => ctx.RecordHandlings.Where(h => ids.Contains(h.Id)).ExecuteDelete(),
            ctx => ctx.RecordHandlings.Count(h => h.RecordDate < cutoff),
            "日處理狀態", maxRows, batchSize);
    }

    /// <summary>
    /// 清除超過保留天數的處理歷程（G4）。歷程過去**從來不會被清理**——6000 台環境下
    /// 它是千萬列級且無上限成長，而 <see cref="GetLogs"/> 的 SQL 端窄化效果會隨表成長遞減。
    ///
    /// 從最舊的一端刪不影響續號：續號探測讀的是**最後**幾行。
    /// </summary>
    public int PruneLogs(int retentionDays)
    {
        lock (_logLock)
        {
            return _logStore.Prune(DateTime.Today.AddDays(-retentionDays));
        }
    }

    private static RecordHandling ToModel(RecordHandlingRow row) => new()
    {
        HostName = row.HostName,
        Date = row.RecordDate,
        Status = row.Status,
        HandlerId = row.HandlerId,
        DueDate = row.DueDate,
        Note = row.Note,
        UpdatedAt = row.UpdatedAt
    };
}
