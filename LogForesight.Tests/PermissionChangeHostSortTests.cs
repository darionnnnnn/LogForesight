using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 權限異動清單依主機名排序：改用正規化的 host_name_key，兩個後端順序一致（SQLite 原本區分大小寫、SQL Server 不區分）。
/// 測資刻意用「小寫 a、大寫 B、小寫 c」：依原值逐字元排序（SQLite）會得到 B、a、c，依 host_name_key 才是 a、B、c。
/// </summary>
public sealed class PermissionChangeHostSortTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly PermissionChangeStore _store;

    public PermissionChangeHostSortTests() => _store = new PermissionChangeStore(_fixture.NewContext);

    public void Dispose() => _fixture.Dispose();

    private static readonly DateTime T0 = new(2026, 8, 20, 10, 0, 0);

    private void Seed() => _store.AppendChanges(new[]
    {
        new PermissionChangeRecord { ChangeId = "b1", HostName = "SRV-B", DetectedAt = T0.AddHours(1), Target = "t-b1" },
        new PermissionChangeRecord { ChangeId = "a1", HostName = "srv-a", DetectedAt = T0.AddHours(2), Target = "t-a1" },
        new PermissionChangeRecord { ChangeId = "c1", HostName = "srv-c", DetectedAt = T0.AddHours(3), Target = "t-c1" }
    });

    private List<string> Ids(bool ascending, int page = 1, int pageSize = 50) =>
        _store.Query(new PermissionChangeQueryFilter { Sort = "hostName", Ascending = ascending, Page = page, PageSize = pageSize })
            .Items.Select(i => i.ChangeId).ToList();

    [Fact]
    public void 主機名升冪依正規化鍵排序_不分大小寫()
    {
        Seed();
        Assert.Equal(new[] { "a1", "b1", "c1" }, Ids(ascending: true));
    }

    [Fact]
    public void 主機名降冪依正規化鍵排序_不分大小寫()
    {
        Seed();
        Assert.Equal(new[] { "c1", "b1", "a1" }, Ids(ascending: false));
    }

    [Fact]
    public void 同主機名多筆時依偵測時間新到舊_不同大小寫視為同一主機()
    {
        _store.AppendChanges(new[]
        {
            new PermissionChangeRecord { ChangeId = "old", HostName = "Srv-A", DetectedAt = T0, Target = "t1" },
            new PermissionChangeRecord { ChangeId = "new", HostName = "SRV-A", DetectedAt = T0.AddHours(5), Target = "t2" },
            new PermissionChangeRecord { ChangeId = "mid", HostName = "srv-a", DetectedAt = T0.AddHours(2), Target = "t3" },
            new PermissionChangeRecord { ChangeId = "z", HostName = "srv-z", DetectedAt = T0.AddHours(9), Target = "t4" }
        });

        Assert.Equal(new[] { "new", "mid", "old", "z" }, Ids(ascending: true));
        Assert.Equal(new[] { "z", "new", "mid", "old" }, Ids(ascending: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 分頁每頁一筆三頁合起來不重複不遺漏(bool ascending)
    {
        Seed();
        var pages = new[] { 1, 2, 3 }.Select(p => Ids(ascending, p, pageSize: 1)).ToList();

        Assert.All(pages, p => Assert.Single(p));
        var all = pages.SelectMany(p => p).ToList();
        Assert.Equal(3, all.Distinct().Count());
        Assert.Equal(ascending ? new[] { "a1", "b1", "c1" } : new[] { "c1", "b1", "a1" }, all);
    }

    [Theory]
    [InlineData(true, "host_name_key], ")]
    [InlineData(false, "host_name_key] DESC")]
    public void SQLServer翻譯_主機名排序用host_name_key(bool ascending, string expectedStart)
    {
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;")
            .Options;
        using var ctx = new LfDbContext(options);

        var sql = PermissionChangeStore.ApplyOrdering(ctx.PermissionChanges.AsNoTracking(), "hostname", ascending).ToQueryString();

        // 只看 ORDER BY 子句（SELECT 清單本來就含 host_name 欄）：第一個排序鍵是 host_name_key，次序鍵不變
        var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];
        Assert.Contains(expectedStart, orderBy, StringComparison.Ordinal);
        Assert.DoesNotContain("host_name]", orderBy, StringComparison.Ordinal);
        Assert.Contains("detected_at] DESC", orderBy, StringComparison.Ordinal);
        Assert.Contains("[id]", orderBy, StringComparison.Ordinal);
    }
}
