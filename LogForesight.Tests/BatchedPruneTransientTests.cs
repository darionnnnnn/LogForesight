using Microsoft.EntityFrameworkCore;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 59 輪 B7：BatchedPrune 零進展中止與 blob 重試只針對暫時性錯誤。</summary>
public class BatchedPruneTransientTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    [Fact]
    public void BatchedPrune_刪除回0時零進展中止_不空轉到上限()
    {
        var deleteCalls = 0;
        var result = BatchedPrune.Run<int>(
            _fx.NewContext,
            (_, _) => new List<int> { 1 },
            (_, _) => { if (++deleteCalls > 3) throw new InvalidOperationException("清除迴圈沒有零進展中止"); return 0; },
            _ => 0,
            "測試",
            maxRows: 100,
            batchSize: 1);

        Assert.Equal(1, deleteCalls);
        Assert.Equal(0, result);
    }

    [Fact]
    public void IsTransient_只有暫時性錯誤才重試()
    {
        // 唯一鍵違規只可能來自「讀到 null 後別人先建了同 key」的首次寫入競態，重試時改走更新即成功
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed: lf_blobs.blob_key", 19))));
        // 截斷、FK、一般 SQLite 約束違規重試也不會好
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Exception("UNIQUE constraint failed"))));
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Microsoft.Data.Sqlite.SqliteException("FOREIGN KEY constraint failed", 787))));
        // 真實 SQLite 的外鍵／NOT NULL 違規主碼也是 19，只有延伸碼不同：不得當成唯一鍵重試
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Microsoft.Data.Sqlite.SqliteException("FOREIGN KEY constraint failed", 19, 787))));
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Microsoft.Data.Sqlite.SqliteException("NOT NULL constraint failed", 19, 1299))));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed", 19, 2067))));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Exception("database is locked"))));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateConcurrencyException("x")));
    }


    /// <summary>
    /// 首次寫入競態：讀到 null 之後、自己 Add 之前，別人先把同 key 的列建好——
    /// 自己的 Add 在 SaveChanges 撞主鍵唯一約束。重試時會讀到那一列改走更新，最終應成功且內容為自己寫的值。
    /// （同交易內用 raw SQL 模擬「別人先寫入」；第一次嘗試回滾後該列消失，第二次不再注入。）
    /// </summary>
    [Fact]
    public void Mutate_首次寫入撞唯一鍵_重試後成功且內容正確()
    {
        var store = _fx.Blob("race-first-write");
        var attempts = 0;
        var seenByMutation = new List<string?>();

        var result = store.MutateWithContext<int>((ctx, current) =>
        {
            attempts++;
            seenByMutation.Add(current);
            if (attempts == 1)
            {
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO lf_blobs (blob_key, content, updated_at, version) VALUES ('race-first-write', 'other', '2026-01-01 00:00:00', 1)");
            }
            return ("mine", attempts);
        });

        Assert.Equal(2, result);
        Assert.Equal(2, attempts);
        Assert.Equal("mine", store.Read());
    }
}
