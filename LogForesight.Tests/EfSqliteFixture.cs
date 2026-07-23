using LogForesight;
using LogForesight.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Tests;

/// <summary>
/// 測試用的 SQLite in-memory 後端：一條開啟中的連線（in-memory DB 的生命週期綁在連線上），
/// schema 建好，並提供 <see cref="Blob"/> 產生 EF blob store——讓 webdata 各 store 的合約測試
/// 可跑在資料庫後端，驗證「換 IJsonBlobStore 底層、store 邏輯不變」逐位一致。
/// </summary>
public sealed class EfSqliteFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public EfSqliteFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).Options);

    public IJsonBlobStore Blob(string key) => new EfJsonBlobStore(NewContext, key);

    public IJsonLogStore LogStore(string key) => new EfJsonLogStore(NewContext, key);

    public void Dispose() => _connection.Dispose();
}
