using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 資料層的慢操作計數與門檻告警（docs/SCALE-ISSUE-FIRST-PLAN.md §8.2 E5）。
///
/// **為什麼需要它**：改版前每個 SQL 操作都有 `[SQL]` 的 Debug/Info log（含耗時），
/// 但那是「事後翻 log」的資料——要回答「現在站台是不是變慢了」得有人去撈、去比對。
/// 企業級部署需要的是一個可被監控系統輪詢的數字。
///
/// 這一層刻意做得極薄：只累加計數、記住最慢的一筆，**不保留歷史序列**
/// （那是監控系統的職責，不是應用程式的）。狀態全部以 <see cref="Interlocked"/> 更新，
/// 不加鎖——量測本身不該成為新的競爭點。
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

    private long _totalOperations;
    private long _slowOperations;
    private long _slowestMs;
    private string _slowestOperation = string.Empty;
    private long _lastSlowAtTicks;

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

        Log.Warn("[SQL][慢] {Operation} 耗時 {Ms}ms，超過門檻 {Threshold}ms", operation, elapsedMs, ThresholdMs);
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
            LastSlowAt = lastSlowTicks == 0 ? null : new DateTime(lastSlowTicks)
        };
    }
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
}
