using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// PRTG 兩張大表的區間查詢索引（回饋四十五輪 B5 C1）：
/// 數值表以 <c>period_start</c> 區間查、狀態變更表以 <c>changed_at</c> 區間查，
/// 而既有複合索引的前導欄都是 <c>sensor_objid</c>，這類查詢吃不到。
/// 「規格即測試」：索引清單直接向資料庫問（<c>pragma_index_list</c>），不是斷言本檔硬編的字串。
/// </summary>
public class SchemaUpgraderPrtgIndexTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("lf_prtg_values", "IX_lf_prtg_values_period")]
    [InlineData("lf_prtg_state_changes", "IX_lf_prtg_state_ch_changed")]
    public void 升級後_兩張大表各自含有新的單欄區間索引(string table, string indexName)
    {
        using var ctx = _fx.NewContext();
        SchemaUpgrader.Upgrade(ctx);

        var indexes = GetIndexNames(ctx, table);

        Assert.Contains(indexName, indexes);
        // 命名規範（docs/DB-SPEC.md）：識別字長度 ≤ 30
        Assert.True(indexName.Length <= 30, $"索引名 '{indexName}' 超過 30 字元");
    }

    [Theory]
    [InlineData("lf_prtg_values", "IX_lf_prtg_values_period", "period_start")]
    [InlineData("lf_prtg_state_changes", "IX_lf_prtg_state_ch_changed", "changed_at")]
    public void 新索引_索引欄位就是區間查詢用的那一欄(string table, string indexName, string column)
    {
        using var ctx = _fx.NewContext();
        SchemaUpgrader.Upgrade(ctx);

        // pragma_index_info：索引真正的欄位組成，避免「名字對了、欄位建錯」的假綠
        var columns = ctx.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_info('" + indexName + "')")
            .ToList();

        Assert.Equal(new[] { column }, columns);
        Assert.Contains(indexName, GetIndexNames(ctx, table));
    }

    [Fact]
    public void 冪等_連續兩次升級不出錯且索引不重覆建立()
    {
        using var ctx = _fx.NewContext();

        var ex1 = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));
        var ex2 = Record.Exception(() => SchemaUpgrader.Upgrade(ctx));

        Assert.Null(ex1);
        Assert.Null(ex2);

        foreach (var (table, indexName) in new[]
                 {
                     ("lf_prtg_values", "IX_lf_prtg_values_period"),
                     ("lf_prtg_state_changes", "IX_lf_prtg_state_ch_changed")
                 })
        {
            var hits = GetIndexNames(ctx, table).Count(n => n == indexName);
            Assert.Equal(1, hits);
        }
    }

    [Fact]
    public void 既有部署路徑_砍表重建後同樣補上兩個索引()
    {
        // 模擬既有部署：表已存在但沒有這兩個索引——砍掉重建走的是 SchemaUpgrader 的手寫 DDL
        using (var drop = _fx.NewContext())
        {
            drop.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_values");
            drop.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS lf_prtg_state_changes");
        }

        using var ctx = _fx.NewContext();
        SchemaUpgrader.Upgrade(ctx);

        Assert.Contains("IX_lf_prtg_values_period", GetIndexNames(ctx, "lf_prtg_values"));
        Assert.Contains("IX_lf_prtg_state_ch_changed", GetIndexNames(ctx, "lf_prtg_state_changes"));
    }

    private static List<string> GetIndexNames(LfDbContext ctx, string table) =>
        ctx.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_index_list('" + table + "')")
            .ToList();
}
