using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Sql;

/// <summary>
/// <see cref="IJsonBlobStore"/> 的資料庫後端：整份 JSON 存在 lf_blobs 的一列（key＝store 名稱）。
/// 讀改寫以交易保證原子。webdata 各 store 的方法本體因此不必改，同一份邏輯跑在檔案或 DB 上。
///
/// SQLite（測試/開發）以資料庫級寫入鎖序列化寫入；SqlServer（正式）以交易。低寫入頻率的
/// webdata 下更新遺失的風險小；真的撞上並發時記 log 並重試（見 Mutate）。
/// </summary>
public sealed class EfJsonBlobStore : IJsonBlobStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly string _key;
    private readonly object _lock = new();

    public EfJsonBlobStore(Func<LfDbContext> contextFactory, string key)
    {
        _contextFactory = contextFactory;
        _key = key;
    }

    public string Location => $"db:{_key}";

    public string? Read()
    {
        using var ctx = _contextFactory();
        return ctx.Blobs.AsNoTracking().FirstOrDefault(b => b.BlobKey == _key)?.Content;
    }

    public TResult Mutate<TResult>(Func<string?, (string content, TResult result)> mutation)
    {
        // 行程內序列化；跨程序靠 DB 交易（SQLite 寫入鎖／SqlServer 交易）
        lock (_lock)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var ctx = _contextFactory();
                    using var tx = ctx.Database.BeginTransaction();

                    var row = ctx.Blobs.FirstOrDefault(b => b.BlobKey == _key);
                    var (content, result) = mutation(row?.Content);

                    if (row == null)
                        ctx.Blobs.Add(new BlobRow { BlobKey = _key, Content = content, UpdatedAt = DateTime.Now });
                    else
                    {
                        row.Content = content;
                        row.UpdatedAt = DateTime.Now;
                    }

                    ctx.SaveChanges();
                    tx.Commit();
                    return result;
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
        // SQLite busy / SqlServer deadlock 等暫時性衝突：訊息含 busy/locked/deadlock 即重試
        var msg = ex.Message.ToLowerInvariant();
        return ex is DbUpdateException || msg.Contains("busy") || msg.Contains("locked") || msg.Contains("deadlock");
    }
}
