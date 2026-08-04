using System.Diagnostics;
using LogForesight.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary><see cref="IRiskyEventStore"/> 的合約測試（docs/archive/WEB-SCHEDULER-PLAN.md §2）。</summary>
public class RiskyEventStoreContractTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();

    private IRiskyEventStore CreateStore() => new EfRiskyEventStore(_fx.NewContext);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly DateTime Date = new(2026, 7, 31);

    private static RiskyEvent Event(long hostId, DateTime date, string source, int eventId, string message, DateTime? eventTime = null) => new()
    {
        HostId = hostId,
        Date = date.Date,
        LogName = "System",
        Source = source,
        EventId = eventId,
        EntryType = EventLogEntryType.Error,
        EventTime = eventTime ?? date,
        Message = message,
        RuleId = "builtin-disk-error",
        CreatedAt = DateTime.Now
    };

    [Fact]
    public void ReplaceDay寫入後Query可查回()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "disk", 153, "msg-1") });

        var result = store.Query(1, Date, "disk", 153, maxResults: 20);

        Assert.Single(result);
        Assert.Equal("msg-1", result[0].Message);
    }

    [Fact]
    public void Query依事件時間新到舊排序()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent>
        {
            Event(1, Date, "disk", 153, "old", eventTime: Date.AddHours(1)),
            Event(1, Date, "disk", 153, "new", eventTime: Date.AddHours(5)),
            Event(1, Date, "disk", 153, "mid", eventTime: Date.AddHours(3))
        });

        var result = store.Query(1, Date, "disk", 153, maxResults: 20);

        Assert.Equal(new[] { "new", "mid", "old" }, result.Select(e => e.Message));
    }

    [Fact]
    public void Query受maxResults限制()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, Enumerable.Range(0, 10)
            .Select(i => Event(1, Date, "disk", 153, $"msg-{i}", eventTime: Date.AddMinutes(i)))
            .ToList());

        var result = store.Query(1, Date, "disk", 153, maxResults: 3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Query不比對其他主機日期或簽章()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "disk", 153, "match") });

        Assert.Empty(store.Query(2, Date, "disk", 153, 20));              // 不同主機
        Assert.Empty(store.Query(1, Date.AddDays(1), "disk", 153, 20));   // 不同日期
        Assert.Empty(store.Query(1, Date, "disk", 999, 20));              // 不同 EventId
        Assert.Empty(store.Query(1, Date, "storahci", 153, 20));          // 不同 Source
    }

    [Fact]
    public void Query的Source比對不分大小寫()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "Storahci", 129, "match") });

        Assert.Single(store.Query(1, Date, "STORAHCI", 129, 20));
    }

    [Fact]
    public void ReplaceDay重寫同一天會清除舊列不殘留()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "disk", 153, "first-run") });
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "disk", 153, "second-run") });

        var result = store.Query(1, Date, "disk", 153, 20);

        Assert.Single(result);
        Assert.Equal("second-run", result[0].Message);
    }

    [Fact]
    public void ReplaceDay不影響其他主機或其他日期的既有資料()
    {
        var store = CreateStore();
        store.ReplaceDay(1, Date, new List<RiskyEvent> { Event(1, Date, "disk", 153, "host1") });
        store.ReplaceDay(2, Date, new List<RiskyEvent> { Event(2, Date, "disk", 153, "host2") });
        store.ReplaceDay(1, Date.AddDays(1), new List<RiskyEvent> { Event(1, Date.AddDays(1), "disk", 153, "tomorrow") });

        store.ReplaceDay(1, Date, new List<RiskyEvent>()); // 覆寫成空清單

        Assert.Empty(store.Query(1, Date, "disk", 153, 20));
        Assert.Single(store.Query(2, Date, "disk", 153, 20));
        Assert.Single(store.Query(1, Date.AddDays(1), "disk", 153, 20));
    }

    [Fact]
    public void Prune清除超過保留天數的紀錄()
    {
        var store = CreateStore();
        var old = DateTime.Today.AddDays(-20);
        var recent = DateTime.Today.AddDays(-5);
        store.ReplaceDay(1, old, new List<RiskyEvent> { Event(1, old, "disk", 153, "old") });
        store.ReplaceDay(1, recent, new List<RiskyEvent> { Event(1, recent, "disk", 153, "recent") });

        var pruned = store.Prune(retentionDays: 14);

        Assert.Equal(1, pruned);
        Assert.Empty(store.Query(1, old, "disk", 153, 20));
        Assert.Single(store.Query(1, recent, "disk", 153, 20));
    }

    [Fact]
    public void Prune無過期紀錄時回傳0()
    {
        var store = CreateStore();
        store.ReplaceDay(1, DateTime.Today, new List<RiskyEvent> { Event(1, DateTime.Today, "disk", 153, "today") });

        Assert.Equal(0, store.Prune(retentionDays: 14));
    }
}
