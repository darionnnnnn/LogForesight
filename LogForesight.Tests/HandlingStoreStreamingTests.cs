using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理歷程的 <c>GetLogs</c>／<c>GetReassignments</c> 必須逐頁串流讀 log 行（<c>EnumerateLines</c>），
/// 不得退回「一次把日期之後的全部行載進記憶體再篩」的 <c>ReadLines</c>：以 SQL 錄製器守住每一道
/// log 行查詢都帶分頁上限。
/// </summary>
public sealed class HandlingStoreStreamingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqlRecorder _recorder = new();

    public HandlingStoreStreamingTests()
    {
        _connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LfDbContext NewContext() => new(new DbContextOptionsBuilder<LfDbContext>().UseSqlite(_connection).AddInterceptors(_recorder).Options);

    private sealed class SqlRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }
        public List<string> LineReads() { lock (Commands) return Commands.Where(c => c.Contains("lf_log_lines") && c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList(); }
        public void Clear() { lock (Commands) Commands.Clear(); }
    }

    private static RecordHandlingLog Log(string host, DateTime date, string action = "note", long? previousHandler = null) => new()
    {
        HostName = host, Date = date, Status = "open", Action = action, PreviousHandlerId = previousHandler,
        CaseId = previousHandler is null ? null : "C1", CreatedAt = date.AddHours(9)
    };

    [Fact]
    public void GetLogs與GetReassignments_只以分頁查詢讀取log行()
    {
        var store = new EfRecordHandlingStore(NewContext, new EfJsonLogStore(NewContext, "handling_log"));
        var date = DateTime.Today.AddDays(-3);
        store.AppendLogs([Log("H1", date), Log("H2", date), Log("H1", date.AddDays(1)), Log("H1", date, HandlingActions.CaseReassign, previousHandler: 7)]);
        _recorder.Clear();

        var logs = store.GetLogs("H1", date);
        var reassigned = store.GetReassignments(7, date);

        Assert.Equal(2, logs.Count);
        Assert.Single(reassigned);
        var reads = _recorder.LineReads();
        Assert.NotEmpty(reads);
        // EnumerateLines 每頁帶 LIMIT 續讀；整段 ReadLines 沒有 LIMIT
        Assert.All(reads, sql => Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase));
    }
}
