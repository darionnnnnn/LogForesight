using LogForesight.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>量測輔助本身的正確性：輔助算錯，靠它的規模與記憶體斷言就全是假守門。</summary>
public sealed class MeasurementHelperTests
{
    [Fact]
    public void 指令記錄器回報讀取指令數與讀取呼叫次數()
    {
        var recorder = new SqlCommandRecorder();
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);

        var (commands, readCalls) = recorder.Measure(() =>
        {
            ctx.Database.SqlQueryRaw<int>("SELECT 1 AS Value UNION ALL SELECT 2 UNION ALL SELECT 3").ToList();
            ctx.Database.SqlQueryRaw<int>("SELECT 1 AS Value UNION ALL SELECT 2").ToList();
            ctx.Database.SqlQueryRaw<int>("SELECT 1 AS Value WHERE 0").ToList();
        });

        Assert.Equal(3, commands);
        // 3＋2＋0 列，三道都讀到底各多一次回 false 的呼叫
        Assert.Equal(8, readCalls);
        Assert.Equal(3, recorder.Selects().Count);
    }

    [Fact]
    public void 指令記錄器清空後重新計數()
    {
        var recorder = new SqlCommandRecorder();
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var ctx = new LfDbContext(new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite(connection).AddInterceptors(recorder).Options);

        ctx.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToList();
        recorder.Clear();

        Assert.Empty(recorder.Commands);
        Assert.Equal(0, recorder.ReadCalls);
    }

    private static object? s_holder;

    [Fact]
    public void 可回收性_被長駐欄位抓住時回報未回收_放開後回報已回收()
    {
        var held = MemoryProbe.Track(() =>
        {
            var payload = new byte[1024];
            s_holder = payload;
            return payload;
        });
        Assert.False(MemoryProbe.IsCollected(held));

        s_holder = null;
        Assert.True(MemoryProbe.IsCollected(held));
    }

    [Fact]
    public void 配置量_配置越多回報越大()
    {
        var small = MemoryProbe.AllocatedBy(() => GC.KeepAlive(new byte[1_000]));
        var large = MemoryProbe.AllocatedBy(() => GC.KeepAlive(new byte[100_000]));

        Assert.True(small >= 1_000, $"small={small}");
        Assert.True(large >= 100_000, $"large={large}");
        Assert.True(large > small * 10, $"small={small} large={large}");
    }
}
