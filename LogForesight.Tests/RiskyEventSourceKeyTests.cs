using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class RiskyEventSourceKeyTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Date = new(2026, 8, 10);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 寫入使用SourceKey_Ready查詢支援Unicode且保留主機日期事件條件()
    {
        var store = new EfRiskyEventStore(_fx.NewContext, () => true);
        store.ReplaceDay(1, Date, new List<RiskyEvent>
        {
            Event(1, Date, "évent", 7, "match"),
            Event(1, Date, "évent", 8, "wrong-event")
        });
        store.ReplaceDay(2, Date, new List<RiskyEvent> { Event(2, Date, "évent", 7, "wrong-host") });
        store.ReplaceDay(1, Date.AddDays(1), new List<RiskyEvent> { Event(1, Date.AddDays(1), "évent", 7, "wrong-date") });

        using (var ctx = _fx.NewContext())
        {
            var row = Assert.Single(ctx.RiskyEvents
                .Where(x => x.HostId == 1 && x.Date == Date && x.EventId == 7)
                .ToList());
            Assert.Equal(WorkOrderIssueKey.SourceKeyOf("évent"), row.SourceKey);
        }

        var result = store.Query(1, Date, "ÉVENT", 7, maxResults: 20);

        var match = Assert.Single(result);
        Assert.Equal("match", match.Message);
    }

    [Fact]
    public void LegacyGate仍以Source查詢可讀取SourceKey為null的舊列()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.RiskyEvents.Add(Row(1, Date, "Storahci", 129, "legacy", sourceKey: null));
            ctx.SaveChanges();
        }

        var store = new EfRiskyEventStore(_fx.NewContext, () => false);

        Assert.Single(store.Query(1, Date, "STORAHCI", 129, maxResults: 20));
    }

    [Fact]
    public void Ready與Legacy_SQL分別使用source_key與UPPER()
    {
        using var ctx = _fx.NewContext();

        var readySql = EfRiskyEventStore.ApplySourceFilter(ctx.RiskyEvents, "ÉVENT", sourceKeyReady: true)
            .ToQueryString();
        var legacySql = EfRiskyEventStore.ApplySourceFilter(ctx.RiskyEvents, "STORAHCI", sourceKeyReady: false)
            .ToQueryString();

        Assert.Contains("source_key", readySql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upper(", readySql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upper(", legacySql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 舊列可由背景批次逐批回填並在完成前保持未Ready()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.RiskyEvents.AddRange(
                Row(1, Date, "évent", 7, "one", sourceKey: null),
                Row(1, Date, "Storahci", 129, "two", sourceKey: null));
            ctx.SaveChanges();
        }

        var store = new EfRiskyEventStore(_fx.NewContext);

        Assert.Equal(1, store.BackfillSourceKeysBatch(batchSize: 1));
        Assert.False(store.AreAllSourceKeysBackfilled());
        Assert.Equal(1, store.BackfillSourceKeysBatch(batchSize: 1));
        Assert.Equal(0, store.BackfillSourceKeysBatch(batchSize: 1));
        Assert.True(store.AreAllSourceKeysBackfilled());

        using var verify = _fx.NewContext();
        Assert.All(verify.RiskyEvents, row => Assert.Equal(
            WorkOrderIssueKey.SourceKeyOf(row.Source), row.SourceKey));
    }

    [Fact]
    public void Schema升級補上source_key欄位與Ready索引()
    {
        using (var ctx = _fx.NewContext())
        {
            ctx.Database.ExecuteSqlRaw("DROP TABLE lf_risky_events");
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE lf_risky_events (
                    id INTEGER NOT NULL CONSTRAINT PK_lf_risky_events PRIMARY KEY AUTOINCREMENT,
                    host_id INTEGER NOT NULL,
                    date TEXT NOT NULL,
                    log_name TEXT NOT NULL,
                    source TEXT NOT NULL,
                    event_id INTEGER NOT NULL,
                    entry_type INTEGER NOT NULL,
                    event_time TEXT NOT NULL,
                    message TEXT NOT NULL,
                    rule_id TEXT NULL,
                    created_at TEXT NOT NULL
                )
                """);

            SchemaUpgrader.Upgrade(ctx);

            var columns = ctx.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM pragma_table_info('lf_risky_events')").ToList();
            var indexes = ctx.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM pragma_index_list('lf_risky_events')").ToList();

            Assert.Contains("source_key", columns, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("IX_lf_risky_events_host_id_date_source_key_event_id", indexes,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static RiskyEvent Event(long hostId, DateTime date, string source, int eventId, string message) => new()
    {
        HostId = hostId,
        Date = date,
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = EventLogEntryType.Error,
        EventTime = date,
        Message = message,
        CreatedAt = date
    };

    private static RiskyEventRow Row(
        long hostId, DateTime date, string source, int eventId, string message, string? sourceKey) => new()
    {
        HostId = hostId,
        Date = date,
        LogName = "System",
        Source = source,
        SourceKey = sourceKey,
        EventId = eventId,
        EntryType = EventLogEntryType.Error,
        EventTime = date,
        Message = message,
        CreatedAt = date
    };
}
