using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 保留清除抽出後的行為：未結案交辦單的事件清除（保留最新 200 筆與建單事件、已結案不碰），
/// 以及 <see cref="RetentionPruner.Run"/> 記錄當天執行日期。
/// </summary>
public class RetentionPrunerTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private EfWorkOrderStore Store() => new(_fx.NewContext);

    private static WorkOrder NewOrder(string source) => new()
    {
        SourceName = source,
        EventId = 1,
        IssueLabel = source,
        HandlerId = 1,
        Origin = WorkOrderOrigins.Manual,
        ScopeKind = WorkOrderScopes.Hosts,
        CreatedByAccount = "admin",
        CreatedAt = DateTime.Today.AddDays(-500)
    };

    /// <summary>1 筆建單事件（最舊）＋249 筆其他事件，時間由舊到新、每筆差一分鐘，最新一筆落在 newestAt</summary>
    private void Add250Events(long workOrderId, DateTime newestAt)
    {
        using var ctx = _fx.NewContext();
        for (var i = 0; i < 250; i++)
        {
            ctx.WorkOrderEvents.Add(new WorkOrderEventRow
            {
                WorkOrderId = workOrderId,
                Action = i == 0 ? WorkOrderEventActions.Created : WorkOrderEventActions.Appended,
                ActorAccount = "a",
                CreatedAt = newestAt.AddMinutes(i - 249)
            });
        }
        ctx.SaveChanges();
    }

    [Fact]
    public void 未結案單事件全部過期_剩最新200筆加建單事件()
    {
        var store = Store();
        var id = store.Insert(NewOrder("A"));
        Add250Events(id, DateTime.Today.AddDays(-100));

        var pruned = store.PruneOpenOrderEvents(30);

        var events = store.ListEvents(id);
        Assert.Equal(49, pruned);
        Assert.Equal(201, events.Count);
        Assert.Single(events, e => e.Action == WorkOrderEventActions.Created);
        // 留下的其他事件必須是時間最新的 200 筆
        var newestKept = events.Where(e => e.Action != WorkOrderEventActions.Created).Min(e => e.CreatedAt);
        Assert.Equal(DateTime.Today.AddDays(-100).AddMinutes(-199), newestKept);
    }

    [Fact]
    public void 未結案單事件全在保留期內_一筆不刪()
    {
        var store = Store();
        var id = store.Insert(NewOrder("A"));
        Add250Events(id, DateTime.Today.AddDays(-1));

        var pruned = store.PruneOpenOrderEvents(30);

        Assert.Equal(0, pruned);
        Assert.Equal(250, store.ListEvents(id).Count);
    }

    [Fact]
    public void 已結案單的事件不受影響()
    {
        var store = Store();
        var closed = NewOrder("A");
        closed.ClosedAt = DateTime.Today.AddDays(-1);
        var id = store.Insert(closed);
        Add250Events(id, DateTime.Today.AddDays(-100));

        var pruned = store.PruneOpenOrderEvents(30);

        Assert.Equal(0, pruned);
        Assert.Equal(250, store.ListEvents(id).Count);
    }

    [Fact]
    public void Run後LastRunDate為今天()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-retention-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "retention.db")}" }, dir);
            Assert.Null(RetentionPruner.LastRunDate(backend));

            RetentionPruner.Run(backend, new RetentionOptions(), new NullConsole(), batchRunStore: null);

            Assert.Equal(DateTime.Today, RetentionPruner.LastRunDate(backend));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NullConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
