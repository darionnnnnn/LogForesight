using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>PRTG 擷取新鮮度紀錄：連續取得 0 筆的累計與可疑判定</summary>
public class PrtgFreshnessTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private PrtgFreshnessStore CreateStore() => new(new EfJsonBlobStore(_fx.NewContext, PrtgFreshnessStore.BlobKey));

    [Fact]
    public void 曾有資料後連續三次取得0筆_判定可疑()
    {
        var store = CreateStore();
        store.Record(PrtgFreshnessStore.Sensors, 5);
        store.Record(PrtgFreshnessStore.Sensors, 0);
        store.Record(PrtgFreshnessStore.Sensors, 0);
        store.Record(PrtgFreshnessStore.Sensors, 0);

        var entry = store.GetAll()[PrtgFreshnessStore.Sensors];
        Assert.Equal(3, entry.ZeroStreak);
        Assert.Equal(0, entry.LastCount);
        Assert.True(entry.EverNonZero);
        Assert.True(PrtgFreshnessStore.IsSuspicious(entry));
    }

    [Fact]
    public void 連續兩次取得0筆_尚不可疑()
    {
        var store = CreateStore();
        store.Record(PrtgFreshnessStore.Sensors, 5);
        store.Record(PrtgFreshnessStore.Sensors, 0);
        store.Record(PrtgFreshnessStore.Sensors, 0);

        Assert.False(PrtgFreshnessStore.IsSuspicious(store.GetAll()[PrtgFreshnessStore.Sensors]));
    }

    [Fact]
    public void 從未取得非0筆的類別_記0筆五次仍不可疑()
    {
        var store = CreateStore();
        for (var i = 0; i < 5; i++) store.Record(PrtgFreshnessStore.Values, 0);

        var entry = store.GetAll()[PrtgFreshnessStore.Values];
        Assert.Equal(5, entry.ZeroStreak);
        Assert.False(entry.EverNonZero);
        Assert.False(PrtgFreshnessStore.IsSuspicious(entry));
    }

    [Fact]
    public void 取得非0筆一次_連續0筆歸零()
    {
        var store = CreateStore();
        store.Record(PrtgFreshnessStore.Devices, 3);
        for (var i = 0; i < 4; i++) store.Record(PrtgFreshnessStore.Devices, 0);
        Assert.True(PrtgFreshnessStore.IsSuspicious(store.GetAll()[PrtgFreshnessStore.Devices]));

        store.Record(PrtgFreshnessStore.Devices, 7);

        var entry = store.GetAll()[PrtgFreshnessStore.Devices];
        Assert.Equal(0, entry.ZeroStreak);
        Assert.Equal(7, entry.LastCount);
        Assert.False(PrtgFreshnessStore.IsSuspicious(entry));
    }

    [Fact]
    public void 各類別獨立累計()
    {
        var store = CreateStore();
        store.Record(PrtgFreshnessStore.Devices, 10);
        store.Record(PrtgFreshnessStore.Snapshot, 0);

        var all = store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(0, all[PrtgFreshnessStore.Devices].ZeroStreak);
        Assert.Equal(1, all[PrtgFreshnessStore.Snapshot].ZeroStreak);
    }
}
