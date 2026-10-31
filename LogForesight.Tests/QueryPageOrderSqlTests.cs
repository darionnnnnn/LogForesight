using System.Data.Common;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 紀錄分頁的 SQL 必須以 record_id 當最後一個排序鍵。SQLite 在主排序鍵相同時通常依 rowid 輸出，
/// 翻頁測試在 SQLite 上看不出少了唯一次要鍵；SQL Server 則不保證同分列的順序，會跨頁重複或漏列。
/// 所以直接檢查產生的 SQL 文字。
/// </summary>
public class QueryPageOrderSqlTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlRecorder _recorder = new();

    public QueryPageOrderSqlTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).AddInterceptors(_recorder).Options);

    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    [Theory]
    [InlineData("date", true)]
    [InlineData("date", false)]
    [InlineData("host", true)]
    [InlineData("host", false)]
    [InlineData("risk", true)]
    [InlineData("risk", false)]
    [InlineData(null, false)]
    public void 分頁SQL的最後一個排序鍵是record_id(string? sort, bool ascending)
    {
        var store = new EfAnalysisRecordStore(NewContext, "test");
        for (var i = 1; i <= 3; i++)
            store.Append(new DailyAnalysisRecord
            {
                HostId = i, Host = $"H{i}", Date = new DateTime(2026, 7, 1), RiskLevel = RiskLevels.High
            });

        _recorder.Commands.Clear();
        store.QueryPage(new RecordQueryFilter(), 1, 2, sort, ascending);

        var paged = _recorder.Commands.Where(c => c.Contains("lf_daily_records") && c.Contains("ORDER BY") && c.Contains("LIMIT")).ToList();
        Assert.NotEmpty(paged);
        foreach (var sql in paged)
        {
            var orderBy = sql[(sql.LastIndexOf("ORDER BY", StringComparison.Ordinal) + "ORDER BY".Length)..];
            orderBy = orderBy[..orderBy.IndexOf("LIMIT", StringComparison.Ordinal)];
            var lastKey = orderBy.Split(',').Last();
            Assert.Contains("record_id", lastKey);
        }
    }
}
