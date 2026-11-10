using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>「某日用哪一份 PRTG 主機對應」（docs/PRTG-SPEC.md §4）只採用含 ok 列的對應；用檔案型 SQLite。</summary>
public class PrtgHostMapForDayTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lf-mapforday-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<LfDbContext> _options;

    public PrtgHostMapForDayTests()
    {
        _options = new DbContextOptionsBuilder<LfDbContext>().UseSqlite($"Data Source={_path}").Options;
        using var ctx = new LfDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private EfPrtgStore Store() => new(() => new LfDbContext(_options));

    private static void Seed(EfPrtgStore store, DateTime day, params string[] statuses)
    {
        var rows = statuses.Select((s, i) => new PrtgHostMapRow
        {
            MapDate = day, DeviceObjid = 100 + i,
            HostId = s == PrtgMapStatus.Ok ? 1 + i : null, HostName = "SRV" + i, MapStatus = s
        }).ToList();
        store.ReplaceHostMapForDate(day, rows);
    }

    private static readonly DateTime Today = DateTime.Today;

    private const int Window = 31;

    [Fact]
    public void 當天只有unmatched且隔天有ok_回隔天()
    {
        var store = Store();
        var d = Today.AddDays(-3);
        Seed(store, d, PrtgMapStatus.Unmatched);
        Seed(store, d.AddDays(1), PrtgMapStatus.Ok);
        var (mapDate, rows) = store.GetHostMapForDay(d, Window);
        Assert.Equal(d.AddDays(1), mapDate);
        Assert.Contains(rows, r => r.MapStatus == PrtgMapStatus.Ok);
    }

    [Fact]
    public void 當天無列_隔天只有unmatched_第三天有ok_回第三天()
    {
        var store = Store();
        var d = Today.AddDays(-4);
        Seed(store, d.AddDays(1), PrtgMapStatus.Unmatched);
        Seed(store, d.AddDays(2), PrtgMapStatus.Ok);
        Assert.Equal(d.AddDays(2), store.GetHostMapForDay(d, Window).MapDate);
    }

    [Fact]
    public void 當天有ok回當天_當天只有unmatched則回窗內較早含ok的一份()
    {
        var store = Store();
        var d = Today.AddDays(-5);
        Seed(store, d, PrtgMapStatus.Ok, PrtgMapStatus.Unmatched);
        Assert.Equal(d, store.GetHostMapForDay(d, Window).MapDate);
        Assert.Equal(2, store.GetHostMapForDay(d, Window).Rows.Count);

        var store2 = Store();
        var e = Today.AddDays(-2);
        Seed(store2, e.AddDays(-3), PrtgMapStatus.Ok);
        Seed(store2, e, PrtgMapStatus.Unmatched);
        Assert.Equal(e.AddDays(-3), store2.GetHostMapForDay(e, Window).MapDate);
    }

    [Fact]
    public void 窗內與之後都沒有含ok的對應_回null與空清單()
    {
        var store = Store();
        var d = Today.AddDays(-4);
        Seed(store, d, PrtgMapStatus.Unmatched);
        Seed(store, d.AddDays(1), PrtgMapStatus.Conflict);
        var (mapDate, rows) = store.GetHostMapForDay(d, Window);
        Assert.Null(mapDate);
        Assert.Empty(rows);
    }

    [Fact]
    public void 之後唯一含ok的對應晚於今天_不採用()
    {
        var store = Store();
        var d = Today.AddDays(-2);
        Seed(store, Today.AddDays(1), PrtgMapStatus.Ok);
        Assert.Null(store.GetHostMapForDay(d, Window).MapDate);
    }

    [Fact]
    public void 無錨點的最新對應仍回全unmatched的那一份()
    {
        var store = Store();
        Seed(store, Today.AddDays(-2), PrtgMapStatus.Ok);
        Seed(store, Today.AddDays(-1), PrtgMapStatus.Unmatched);
        Assert.Equal(Today.AddDays(-1), store.GetLatestHostMapWithDate().MapDate);
    }

    [Fact]
    public void 某日對應查詢_SQLServer與SQLite都翻譯得出來()
    {
        foreach (var options in new[]
                 {
                     new DbContextOptionsBuilder<LfDbContext>().UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options,
                     new DbContextOptionsBuilder<LfDbContext>().UseSqlite("Data Source=:memory:").Options
                 })
        {
            using var ctx = new LfDbContext(options);
            var sql = EfPrtgStore.BuildOkMapDatesQuery(ctx.PrtgHostMaps, Today.AddDays(-30), Today).ToQueryString();
            Assert.Contains("map_status", sql);
        }
    }
}
