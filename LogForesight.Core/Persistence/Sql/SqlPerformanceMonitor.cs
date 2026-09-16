using System.Diagnostics;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 資料層的慢操作計數與門檻告警（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
///
/// **為什麼需要它**：改版前每個 SQL 操作都有 `[SQL]` 的 Debug/Info log（含耗時），
/// 但那是「事後翻 log」的資料——要回答「現在站台是不是變慢了」得有人去撈、去比對。
/// 企業級部署需要的是一個可被監控系統輪詢的數字。
///
/// 這一層刻意做得極薄：只累加計數、記住最慢的前幾支操作，**不保留歷史序列**
/// （那是監控系統的職責，不是應用程式的）。計數狀態全部以 <see cref="Interlocked"/> 更新，
/// 不加鎖——量測本身不該成為新的競爭點。
///
/// **為什麼要「前幾支」而不只是最慢的一筆**（回饋四十五輪 B6）：管理者看到「最慢 7 秒」
/// 卻不知道是哪幾支慢、各慢幾次，沒辦法決定要去看哪一頁。因此另外保留最慢的前
/// <see cref="TopSlowCapacity"/> 支，各記命中次數、最大耗時與最近一次發生時間。
///
/// 由 <see cref="StorageBackend"/> 建立並持有（每行程一份），透過 <c>/api/health</c> 對外。
/// </summary>
public sealed class SqlPerformanceMonitor
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 預設慢操作門檻。2 秒的取捨：使用者對「按下去到畫面出來」的耐受大約在 1~3 秒，
    /// 單一資料層操作若吃掉 2 秒，整個請求幾乎確定已經超出可接受範圍。
    /// </summary>
    public const int DefaultThresholdMs = 2000;

    /// <summary>
    /// 保留幾支最慢的操作。10 的取捨：一頁畫面看得完，也足以涵蓋「同一次事件牽連的數個查詢」；
    /// 再多就變成要捲動的長表，反而看不出重點。
    /// </summary>
    public const int TopSlowCapacity = 10;

    private long _totalOperations;
    private long _slowOperations;
    private long _slowestMs;
    private string _slowestOperation = string.Empty;
    private long _lastSlowAtTicks;

    /// <summary>
    /// 最慢前 N 支的累計（key＝操作名稱）。
    ///
    /// **鎖的位置與取捨**：<see cref="Record"/> 會在**每一次**資料層操作上被呼叫，
    /// 所以未達門檻的路徑（絕大多數）維持原本的無鎖原子操作，一律不碰這個集合、不進鎖；
    /// 只有「達到門檻」的罕見路徑才進 <see cref="_topLock"/> 更新集合。
    /// 慢操作依定義就是少數，而且它本身已經花了兩秒以上——多一次極短的鎖不會成為新的瓶頸。
    /// <see cref="Snapshot"/> 也進同一把鎖，但它只有健康頁輪詢時才呼叫。
    /// </summary>
    private readonly Dictionary<string, SlowOperationStat> _topSlow = new(StringComparer.Ordinal);

    private readonly object _topLock = new();

    public SqlPerformanceMonitor(int thresholdMs = DefaultThresholdMs)
    {
        ThresholdMs = thresholdMs;
    }

    public int ThresholdMs { get; }

    /// <summary>
    /// 記錄一次資料層操作。<paramref name="operation"/> 要能定位到程式碼裡的哪條路徑
    /// （例如 <c>Query</c>、<c>blob:issue_handling:Mutate</c>），不要只寫「SQL」——
    /// 慢的時候第一個問題永遠是「哪一條慢」。
    /// </summary>
    public void Record(string operation, long elapsedMs)
    {
        Interlocked.Increment(ref _totalOperations);
        if (elapsedMs < ThresholdMs) return;

        Interlocked.Increment(ref _slowOperations);
        Interlocked.Exchange(ref _lastSlowAtTicks, DateTime.Now.Ticks);

        // 最慢的一筆：CAS 迴圈避免兩條執行緒同時更新時互相覆蓋成較小值
        while (true)
        {
            var current = Interlocked.Read(ref _slowestMs);
            if (elapsedMs <= current) break;
            if (Interlocked.CompareExchange(ref _slowestMs, elapsedMs, current) != current) continue;

            // 名稱與數值不是同一個原子單位（極少數情況下可能配到相鄰兩筆的組合）——
            // 這是診斷輔助資訊，不值得為它加鎖拖慢每一次量測
            Volatile.Write(ref _slowestOperation, operation);
            break;
        }

        RecordTopSlow(operation ?? string.Empty, elapsedMs);

        Log.Warn("[SQL][慢] {Operation} 耗時 {Ms}ms，超過門檻 {Threshold}ms", operation, elapsedMs, ThresholdMs);
    }

    /// <summary>
    /// 把一次慢操作併進「最慢前 N 支」：同名累加次數、最大耗時取大值、時間取最近一次；
    /// 新名稱讓集合超過上限時，踢掉目前最大耗時最小的那一支（新來的若自己就是最小，就是它自己被踢）。
    /// 只在達到門檻時才會走到這裡——見 <see cref="_topSlow"/> 對鎖的說明。
    /// </summary>
    private void RecordTopSlow(string operation, long elapsedMs)
    {
        var now = DateTime.Now;
        lock (_topLock)
        {
            if (_topSlow.TryGetValue(operation, out var existing))
            {
                existing.Count++;
                if (elapsedMs > existing.MaxMs) existing.MaxMs = elapsedMs;
                existing.LastAt = now;
                return;
            }

            _topSlow[operation] = new SlowOperationStat
            {
                Operation = operation,
                Count = 1,
                MaxMs = elapsedMs,
                LastAt = now
            };

            if (_topSlow.Count <= TopSlowCapacity) return;

            var weakest = _topSlow.Values.OrderBy(v => v.MaxMs).ThenBy(v => v.LastAt).First();
            _topSlow.Remove(weakest.Operation);
        }
    }

    /// <summary>目前累計狀況（供 <c>/api/health</c> 呈現）</summary>
    public SqlPerformanceSnapshot Snapshot()
    {
        var lastSlowTicks = Interlocked.Read(ref _lastSlowAtTicks);
        return new SqlPerformanceSnapshot
        {
            ThresholdMs = ThresholdMs,
            TotalOperations = Interlocked.Read(ref _totalOperations),
            SlowOperations = Interlocked.Read(ref _slowOperations),
            SlowestMs = Interlocked.Read(ref _slowestMs),
            SlowestOperation = Volatile.Read(ref _slowestOperation),
            LastSlowAt = lastSlowTicks == 0 ? null : new DateTime(lastSlowTicks),
            TopSlowOperations = TopSlowSnapshot()
        };
    }

    /// <summary>最慢前 N 支的複本（依最大耗時由大到小）。回傳的是複本，呼叫端改不到內部狀態。</summary>
    private List<SlowOperationStat> TopSlowSnapshot()
    {
        lock (_topLock)
        {
            return _topSlow.Values
                .OrderByDescending(v => v.MaxMs)
                .ThenByDescending(v => v.Count)
                .Take(TopSlowCapacity)
                .Select(v => new SlowOperationStat
                {
                    Operation = v.Operation,
                    Count = v.Count,
                    MaxMs = v.MaxMs,
                    LastAt = v.LastAt
                })
                .ToList();
        }
    }
}

/// <summary>單一慢操作的累計（<see cref="SqlPerformanceMonitor.TopSlowCapacity"/> 支之一）</summary>
public sealed class SlowOperationStat
{
    /// <summary>操作名稱（<c>分類:方法名</c>）</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>達到門檻的次數</summary>
    public long Count { get; set; }

    /// <summary>最大耗時（毫秒）</summary>
    public long MaxMs { get; set; }

    /// <summary>最近一次達到門檻的時間</summary>
    public DateTime LastAt { get; set; }
}

/// <summary>
/// 慢操作量測的計時範圍：<c>using var _ = _performance.Measure("分類:方法名");</c>
/// 一行就涵蓋方法的全部離開路徑（多個 return、例外），不必在每個 return 前補一次 Record。
/// 內部仍是同一套 <c>monitor?.Record(...)</c> 的可選依賴語意——monitor 為 null 時完全不做事。
/// </summary>
public readonly struct SqlOperationScope : IDisposable
{
    private readonly SqlPerformanceMonitor? _monitor;
    private readonly string _operation;
    private readonly long _startTimestamp;

    internal SqlOperationScope(SqlPerformanceMonitor? monitor, string operation)
    {
        _monitor = monitor;
        _operation = operation;
        _startTimestamp = monitor == null ? 0 : Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (_monitor == null) return;
        var elapsedMs = (long)((Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency);
        _monitor.Record(_operation, elapsedMs);
    }
}

/// <summary>可選依賴（monitor 可能是 null）也能寫成一行的量測入口</summary>
public static class SqlPerformanceMonitorExtensions
{
    public static SqlOperationScope Measure(this SqlPerformanceMonitor? monitor, string operation) =>
        new(monitor, operation);
}

/// <summary>慢操作統計的當下快照</summary>
public sealed class SqlPerformanceSnapshot
{
    public int ThresholdMs { get; init; }
    public long TotalOperations { get; init; }
    public long SlowOperations { get; init; }
    public long SlowestMs { get; init; }
    public string SlowestOperation { get; init; } = string.Empty;
    public DateTime? LastSlowAt { get; init; }

    /// <summary>最慢的前幾支操作，依最大耗時由大到小（回饋四十五輪 B6）</summary>
    public IReadOnlyList<SlowOperationStat> TopSlowOperations { get; init; } = Array.Empty<SlowOperationStat>();
}
