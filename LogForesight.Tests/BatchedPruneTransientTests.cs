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
        Assert.False(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Exception("UNIQUE constraint failed"))));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateException("x", new Exception("database is locked"))));
        Assert.True(EfJsonBlobStore.IsTransient(new DbUpdateConcurrencyException("x")));
    }
}
