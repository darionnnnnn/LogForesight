using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 單筆主機日讀取（<see cref="EfAnalysisRecordStore.GetOne"/>）：風險日詳情、每次標記、指派與 AI 排程都走它。
/// 讀回的列數只能與「要找的主機」有關，不能與當天有幾台主機有關（第 77 輪 D-1）；比對規則與 HostMatcher 一致。
/// </summary>
public sealed class RecordStoreSingleReadTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly SqlCommandRecorder _recorder = new();
    private static readonly DateTime Day = DateTime.Today.AddDays(-1);

    public void Dispose() => _fx.Dispose();

    private EfAnalysisRecordStore Store() => new(() =>
        new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(_fx.NewContext().Database.GetDbConnection())
            .AddInterceptors(_recorder).Options), "sqlite-in-memory");

    private static DailyAnalysisRecord Rec(long hostId, string host, string risk = "低") =>
        new() { Date = Day, HostId = hostId, Host = host, RiskLevel = risk, Headline = host };

    private static HostKey Key(long id, string name) => new() { HostId = id, HostName = name };

    [Fact]
    public void 讀回列數與當天主機數無關()
    {
        var store = Store();
        for (var i = 1; i <= 200; i++) store.Append(Rec(i, $"h{i}"));

        var (_, few) = _recorder.Measure(() => Assert.NotNull(store.GetOne(new[] { Key(7, "h7") }, Day)));
        for (var i = 201; i <= 400; i++) store.Append(Rec(i, $"h{i}"));
        var (_, many) = _recorder.Measure(() => Assert.NotNull(store.GetOne(new[] { Key(7, "h7") }, Day)));

        Assert.Equal(few, many);
        // 一道主表查詢只讀回 1 列（另加讀到底的一次）；問題子表等附帶讀取也不隨主機數成長
        Assert.True(many < 20, $"讀取呼叫 {many} 次，應只讀到這台主機的列");
    }

    [Fact]
    public void 主機編號命中()
    {
        var store = Store();
        store.Append(Rec(1, "a", "高"));
        store.Append(Rec(2, "b", "中"));

        Assert.Equal("中", store.GetOne(new[] { Key(2, "b") }, Day)!.RiskLevel);
    }

    [Fact]
    public void 編號不為零的列只認編號_改名後舊名不會撈到別台()
    {
        var store = Store();
        store.Append(Rec(1, "same-name", "高"));

        // 名稱相同但編號不同：HostMatcher 不以名稱比對編號非 0 的列
        Assert.Null(store.GetOne(new[] { Key(2, "same-name") }, Day));
    }

    [Fact]
    public void 編號為零的舊列以名稱命中()
    {
        var store = Store();
        store.Append(Rec(0, "legacy", "中"));
        store.Append(Rec(5, "other", "高"));

        Assert.Equal("中", store.GetOne(new[] { Key(9, "legacy") }, Day)!.RiskLevel);
    }

    [Fact]
    public void 都不命中回null()
    {
        var store = Store();
        store.Append(Rec(1, "a"));

        Assert.Null(store.GetOne(new[] { Key(3, "c") }, Day));
        Assert.Null(store.GetOne(Array.Empty<HostKey>(), Day));
    }

    [Fact]
    public void 多個主機鍵依傳入順序擇一()
    {
        var store = Store();
        store.Append(Rec(10, "alive", "低"));
        store.Append(Rec(11, "tombstone", "高"));

        Assert.Equal("低", store.GetOne(new[] { Key(10, "alive"), Key(11, "tombstone") }, Day)!.RiskLevel);
        Assert.Equal("高", store.GetOne(new[] { Key(11, "tombstone"), Key(10, "alive") }, Day)!.RiskLevel);
    }
}
