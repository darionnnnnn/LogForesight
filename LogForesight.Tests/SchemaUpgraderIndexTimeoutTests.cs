using System.Data.Common;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 啟動同步路徑建大表索引的命令逾時：<c>IX_lf_permission_changes_dedupe_key_hash</c> 要用分析等級逾時，
/// 其餘索引與後續步驟維持原本逾時。SQL Server 的實際逾時行為未連線驗證，這裡以 SQLite 攔截命令確認逾時有帶上。
/// </summary>
public class SchemaUpgraderIndexTimeoutTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lf-idx-timeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(f); } catch { /* 暫存檔 */ }
        GC.SuppressFinalize(this);
    }

    private sealed class TimeoutProbe : DbCommandInterceptor
    {
        public List<(string Sql, int Timeout)> Created { get; } = new();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (command.CommandText.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("INDEX", StringComparison.OrdinalIgnoreCase))
                lock (Created) Created.Add((command.CommandText, command.CommandTimeout));
            return result;
        }
    }

    private LfDbContext NewContext(TimeoutProbe? probe = null)
    {
        var b = new DbContextOptionsBuilder<LfDbContext>().UseSqlite($"Data Source={_path}");
        if (probe != null) b.AddInterceptors(probe);
        return new LfDbContext(b.Options);
    }

    [Fact]
    public void 去重鍵雜湊索引建立時使用分析等級命令逾時_其他索引維持前景逾時()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
            ctx.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_lf_permission_changes_dedupe_key_hash");
            ctx.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_lf_permission_changes_created_at");
        }

        var probe = new TimeoutProbe();
        using (var ctx = NewContext(probe))
        {
            ctx.Database.SetCommandTimeout(StorageBackend.ForegroundCommandTimeoutSeconds);
            SchemaUpgrader.Upgrade(ctx);
            // 建索引後還原：同一個 context 之後的命令回到前景逾時
            Assert.Equal(StorageBackend.ForegroundCommandTimeoutSeconds, ctx.Database.GetCommandTimeout());
        }

        var hash = Assert.Single(probe.Created, c => c.Sql.Contains("IX_lf_permission_changes_dedupe_key_hash"));
        Assert.Equal(StorageBackend.AnalysisCommandTimeoutSeconds, hash.Timeout);
        var other = Assert.Single(probe.Created, c => c.Sql.Contains("IX_lf_permission_changes_created_at"));
        Assert.Equal(StorageBackend.ForegroundCommandTimeoutSeconds, other.Timeout);
    }
}
