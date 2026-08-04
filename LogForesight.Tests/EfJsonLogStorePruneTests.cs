using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// P0-3：<see cref="EfJsonLogStore.Prune"/>——依插入時間戳記整批刪除，SQL 端整批刪不撈回記憶體。
/// </summary>
public class EfJsonLogStorePruneTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// 直接改寫底層列的 CreatedAt 來模擬「較早之前寫入」，不必真的等待。
    /// <paramref name="occurrence"/> 是該 log_key 底下依附加順序的第幾筆（1-based）——
    /// Seq 是整張表的全域自增值，不是各 key 各自從 1 起算，不能拿絕對 seq 當參數。
    /// </summary>
    private void Backdate(string key, int occurrence, DateTime createdAt)
    {
        using var ctx = _fixture.NewContext();
        var row = ctx.LogLines
            .Where(l => l.LogKey == key)
            .OrderBy(l => l.Seq)
            .Skip(occurrence - 1)
            .First();
        row.CreatedAt = createdAt;
        ctx.SaveChanges();
    }

    [Fact]
    public void Prune_早於cutoff的行被刪除_邊界當天保留()
    {
        var store = _fixture.LogStore("k1");
        store.AppendLine("old"); // seq 1
        store.AppendLine("boundary"); // seq 2
        store.AppendLine("new"); // seq 3
        Backdate("k1", 1, DateTime.Today.AddDays(-91));
        Backdate("k1", 2, DateTime.Today.AddDays(-90));
        Backdate("k1", 3, DateTime.Today.AddDays(-1));

        var removed = store.Prune(DateTime.Today.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "boundary", "new" }, store.ReadLines());
    }

    [Fact]
    public void Prune_無可刪除時回0()
    {
        var store = _fixture.LogStore("k2");
        store.AppendLine("keep");

        Assert.Equal(0, store.Prune(DateTime.Today.AddDays(-90)));
        Assert.Single(store.ReadLines());
    }

    /// <summary>沒有時間戳記的既存列（schema 升級前寫入）一律保留，不因無法判斷年代而被誤刪</summary>
    [Fact]
    public void Prune_CreatedAt為null的既存列不刪()
    {
        var store = _fixture.LogStore("k3");
        store.AppendLine("legacy-no-timestamp");
        using (var ctx = _fixture.NewContext())
        {
            var row = ctx.LogLines.Single(l => l.LogKey == "k3");
            row.CreatedAt = null;
            ctx.SaveChanges();
        }

        var removed = store.Prune(DateTime.Today.AddYears(10)); // 極寬的 cutoff，只有沒時間戳記的列會被誤刪

        Assert.Equal(0, removed);
        Assert.Single(store.ReadLines());
    }

    /// <summary>不同 log key 各自獨立，Prune 一個 key 不影響其他 key 的資料</summary>
    [Fact]
    public void Prune_只影響指定log_key()
    {
        var storeA = _fixture.LogStore("a");
        var storeB = _fixture.LogStore("b");
        storeA.AppendLine("a-line");
        storeB.AppendLine("b-line");
        Backdate("a", 1, DateTime.Today.AddDays(-100));
        Backdate("b", 1, DateTime.Today.AddDays(-100));

        storeA.Prune(DateTime.Today.AddDays(-90));

        Assert.Empty(storeA.ReadLines());
        Assert.Single(storeB.ReadLines());
    }
}
