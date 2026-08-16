using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 「一團 JSON 文字」的原子讀寫（webdata 各 store 的儲存底層）：整份 JSON 存在 lf_blobs 的一列
/// （key＝store 名稱）。把「文字放哪裡、怎麼原子更新」與「store 的業務邏輯」分開
/// （docs/archive/HISTORY.md §4）。
///
/// <see cref="Mutate{TResult}"/> 是讀→改→寫的原子單位，以交易實作：呼叫端拿到目前內容、
/// 算出新內容，底層保證中途不被別人插入寫入（避免更新遺失——hosts 是批次與 Web 共同
/// 寫入的資料，這點是正確性關鍵）。
///
/// SQLite（測試/開發）以資料庫級寫入鎖序列化寫入；SqlServer（正式）以交易。低寫入頻率的
/// webdata 下更新遺失的風險小；真的撞上並發時記 log 並重試（見 Mutate）。
/// </summary>
public sealed class EfJsonBlobStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly string _key;
    private readonly object _lock = new();
    private readonly SqlPerformanceMonitor? _performance;

    public EfJsonBlobStore(Func<LfDbContext> contextFactory, string key, SqlPerformanceMonitor? performance = null)
    {
        _contextFactory = contextFactory;
        _key = key;
        _performance = performance;
    }

    /// <summary>供 log／Location 顯示（如「sqlserver:users」）</summary>
    public string Location => $"db:{_key}";

    /// <summary>目前內容；不存在回 null（首次執行的正常情況）</summary>
    public string? Read()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctx = _contextFactory();
        var content = ctx.Blobs.AsNoTracking().FirstOrDefault(b => b.BlobKey == _key)?.Content;
        _performance?.Record($"blob:{_key}:Read", sw.ElapsedMilliseconds);
        return content;
    }

    /// <summary>目前版本號；內容不存在回 0。只讀一個整數欄，不拉整份內容——
    /// 這是上層快取判定「要不要重讀」的探測點。</summary>
    public long ReadVersion()
    {
        using var ctx = _contextFactory();
        return ctx.Blobs.AsNoTracking()
            .Where(b => b.BlobKey == _key)
            .Select(b => b.Version)
            .FirstOrDefault();
    }

    /// <summary>目前內容與版本；內容不存在回 (null, 0)。
    /// 快取填充要用這個而不是分別呼叫 Read() 與 ReadVersion()——
    /// 分兩次讀之間內容可能被改掉，快取會存下「新內容配舊版本」而永遠不再更新。</summary>
    public (string? Content, long Version) ReadWithVersion()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ctx = _contextFactory();
        var row = ctx.Blobs.AsNoTracking()
            .Where(b => b.BlobKey == _key)
            .Select(b => new { b.Content, b.Version })
            .FirstOrDefault();
        _performance?.Record($"blob:{_key}:Read", sw.ElapsedMilliseconds);
        return row == null ? (null, 0) : (row.Content, row.Version);
    }

    /// <summary>讀→改→寫的原子操作。mutation 收目前內容、回 (新內容, 結果)</summary>
    public TResult Mutate<TResult>(Func<string?, (string content, TResult result)> mutation)
    {
        // 行程內序列化；跨程序靠 DB 交易（SQLite 寫入鎖／SqlServer 交易）
        //
        // 這把鎖是全站寫入處理狀態時的實際排隊點（docs/archive/SCALE-ISSUE-FIRST-PLAN.md §8.4）：
        // 整份 blob 越大，持有時間越長，其他請求就排得越久。量測涵蓋「等鎖＋讀改寫」
        // 整段——只量鎖內時間會漏掉真正讓使用者感覺卡住的那一半。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // SqlServer 啟用 EnableRetryOnFailure 後，execution strategy 不相容使用者自開的交易
                    // （BeginTransaction），必須包在 CreateExecutionStrategy().Execute(...) 內；
                    // Sqlite 沒有設定重試策略，這裡是 no-op（NonRetryingExecutionStrategy 只單純呼叫一次）。
                    // 用來取得 strategy 的 probe context 用完即丟——strategy 只由 DbContextOptions 決定，
                    // 不含任何連線狀態。
                    using var probe = _contextFactory();
                    var strategy = probe.Database.CreateExecutionStrategy();

                    var outcome = strategy.Execute(() =>
                    {
                        // 重試時必須用全新 context——同一個 context 的變更追蹤會殘留上一次嘗試
                        // 加入的列，重試時再 Add 一次會造成重複追蹤
                        using var ctx = _contextFactory();
                        using var tx = ctx.Database.BeginTransaction();

                        var row = ctx.Blobs.FirstOrDefault(b => b.BlobKey == _key);
                        var (content, result) = mutation(row?.Content);

                        if (row == null)
                            ctx.Blobs.Add(new BlobRow { BlobKey = _key, Content = content, UpdatedAt = DateTime.Now, Version = 1 });
                        else
                        {
                            row.Content = content;
                            row.UpdatedAt = DateTime.Now;
                            row.Version++;
                        }

                        ctx.SaveChanges();
                        tx.Commit();
                        return result;
                    });

                    _performance?.Record($"blob:{_key}:Mutate", sw.ElapsedMilliseconds);
                    return outcome;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    // 並發寫入撞鎖：短退避後重試（webdata 寫入低頻，實務上極少發生）
                    Log.Warn("[SQL] blob「{Key}」寫入撞並發，第 {Attempt}/{Max} 次重試：{Msg}", _key, attempt, maxAttempts, ex.Message);
                    Thread.Sleep(25 * attempt);
                }
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        // SQLite busy / SqlServer deadlock 等暫時性衝突：訊息含 busy/locked/deadlock 即重試。
        // user-function：連線池關閉後（見 StorageBackend.DisableSqlitePoolingIfUnset）此錯誤的
        // 根因已除，這裡留作第二道保險（如使用者自行在 ConnectionString 開回 Pooling）。
        var msg = ex.Message.ToLowerInvariant();
        return ex is DbUpdateException || msg.Contains("busy") || msg.Contains("locked") ||
               msg.Contains("deadlock") || msg.Contains("user-function");
    }
}
