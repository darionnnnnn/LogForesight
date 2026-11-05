using System.Data.Common;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 64 輪 A-2：SQLite 關池後每個「檢查缺什麼」查詢都會重開連線，升級器上百個檢查讓既有 DB 的建構要 1 秒以上；
/// <see cref="SchemaUpgrader.Upgrade"/> 整段必須只開一條連線。突變（拿掉 OpenConnection）會讓開啟次數變成上百次。
/// </summary>
public sealed class SchemaUpgraderConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lf-upg-conn-" + Guid.NewGuid().ToString("N"));

    private sealed class OpenCounter : DbConnectionInterceptor
    {
        public int Opened;
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Interlocked.Increment(ref Opened);
            base.ConnectionOpened(connection, eventData);
        }
    }

    private LfDbContext Create(OpenCounter counter)
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "a.db")};Pooling=False")
            .AddInterceptors(counter)
            .Options;
        return new LfDbContext(options);
    }

    [Fact]
    public void 既有且已升級的資料庫_Upgrade整段只開一條連線()
    {
        var warm = new OpenCounter();
        using (var ctx = Create(warm))
        {
            ctx.Database.EnsureCreated();
            SchemaUpgrader.Upgrade(ctx);
        }

        var counter = new OpenCounter();
        using (var ctx = Create(counter))
        {
            SchemaUpgrader.Upgrade(ctx);
            Assert.Equal(System.Data.ConnectionState.Closed, ctx.Database.GetDbConnection().State);
        }
        Assert.Equal(1, counter.Opened);
    }

    [Fact]
    public void 呼叫端已開啟連線時_Upgrade不另開也不關閉()
    {
        var counter = new OpenCounter();
        using var ctx = Create(counter);
        ctx.Database.EnsureCreated();
        ctx.Database.OpenConnection();
        var before = counter.Opened;
        SchemaUpgrader.Upgrade(ctx);
        Assert.Equal(before, counter.Opened);
        Assert.Equal(System.Data.ConnectionState.Open, ctx.Database.GetDbConnection().State);
        ctx.Database.CloseConnection();
    }

    /// <summary>
    /// 真路徑計數：<see cref="LogForesight.Core.Persistence.StorageBackend"/> 只收 <see cref="DbCommandInterceptor"/>，
    /// 而關池時每次重開連線都會換一個 sqlite3 handle，所以數「升級器檢查查詢」用過幾個不同的 handle。
    /// </summary>
    private sealed class HandleCounter : DbCommandInterceptor
    {
        private readonly HashSet<object> _handles = new(ReferenceEqualityComparer.Instance);
        public int Distinct { get { lock (_handles) return _handles.Count; } }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("pragma_table_info", StringComparison.OrdinalIgnoreCase)
                && command.Connection is Microsoft.Data.Sqlite.SqliteConnection { Handle: { } handle })
                lock (_handles) _handles.Add(handle);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    [Fact]
    public void 經StorageBackend真路徑_升級器所有檢查共用一條實體連線()
    {
        Directory.CreateDirectory(_dir);
        var settings = new LogForesight.Core.Configuration.StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "real.db")}"
        };
        var counter = new HandleCounter();
        _ = new LogForesight.Core.Persistence.StorageBackend(settings, _dir, counter);
        Assert.Equal(1, counter.Distinct);
    }

    private sealed class ThrowingInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("pragma_table_info", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("模擬升級中途失敗");
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    [Fact]
    public void Upgrade中途擲例外_仍關閉自己開的連線()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "b.db")};Pooling=False")
            .Options;
        using (var seed = new LfDbContext(options)) seed.Database.EnsureCreated();

        var failing = new DbContextOptionsBuilder<LfDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "b.db")};Pooling=False")
            .AddInterceptors(new ThrowingInterceptor())
            .Options;
        using var ctx = new LfDbContext(failing);
        Assert.Throws<InvalidOperationException>(() => SchemaUpgrader.Upgrade(ctx));
        Assert.Equal(System.Data.ConnectionState.Closed, ctx.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 兩個StorageBackend對同一檔並行建構_皆成功(bool wal)
    {
        Directory.CreateDirectory(_dir);
        var settings = new LogForesight.Core.Configuration.StorageSettings
        {
            Type = "Sqlite", SqliteWal = wal, ConnectionString = $"Data Source={Path.Combine(_dir, wal ? "w.db" : "d.db")}"
        };
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => new LogForesight.Core.Persistence.StorageBackend(settings, _dir))).ToArray();
        var backends = await Task.WhenAll(tasks);
        Assert.All(backends, Assert.NotNull);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
