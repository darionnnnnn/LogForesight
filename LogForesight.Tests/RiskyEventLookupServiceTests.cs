using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="RiskyEventLookupService"/>：風險 log 暫存優先、Sentinel 即時查詢 fallback
/// （docs/WEB-SCHEDULER-PLAN.md §2.2.4）。
/// </summary>
public class RiskyEventLookupServiceTests
{
    private static readonly WebHost Host = new() { HostId = 7, HostName = "SRV-A" };
    private static readonly DateTime Date = new(2026, 7, 31);

    [Fact]
    public async Task 暫存有資料時直接回傳不查Sentinel()
    {
        var store = new FakeRiskyEventStore { Rows = new List<RiskyEvent> { RiskyEvt("stored-msg") } };
        var fetcher = new FakeSentinelEventFetcher { Result = new LiveEventFetchResult(new List<string> { "should-not-be-used" }) };
        var service = new RiskyEventLookupService(store, fetcher);

        var result = await service.FindAsync(Host, Date, "disk", 153);

        Assert.NotNull(result);
        Assert.Equal(new List<string> { "stored-msg" }, result!.Messages);
        Assert.False(fetcher.Called);
    }

    [Fact]
    public async Task 暫存查無時fallback即時查詢()
    {
        var store = new FakeRiskyEventStore { Rows = new List<RiskyEvent>() };
        var liveResult = new LiveEventFetchResult(new List<string> { "live-msg" });
        var fetcher = new FakeSentinelEventFetcher { Result = liveResult };
        var service = new RiskyEventLookupService(store, fetcher);

        var result = await service.FindAsync(Host, Date, "disk", 153);

        Assert.True(fetcher.Called);
        Assert.Same(liveResult, result);
    }

    [Fact]
    public async Task 暫存與即時查詢皆無結果時回傳null()
    {
        var store = new FakeRiskyEventStore { Rows = new List<RiskyEvent>() };
        var fetcher = new FakeSentinelEventFetcher { Result = null };
        var service = new RiskyEventLookupService(store, fetcher);

        var result = await service.FindAsync(Host, Date, "disk", 153);

        Assert.Null(result);
        Assert.True(fetcher.Called);
    }

    [Fact]
    public async Task 查詢傳入正確的主機日期與簽章參數()
    {
        var store = new FakeRiskyEventStore { Rows = new List<RiskyEvent>() };
        var fetcher = new FakeSentinelEventFetcher { Result = null };
        var service = new RiskyEventLookupService(store, fetcher);

        await service.FindAsync(Host, Date, "disk", 153);

        Assert.Equal(7, store.LastHostId);
        Assert.Equal(Date, store.LastDate);
        Assert.Equal("disk", store.LastSource);
        Assert.Equal(153, store.LastEventId);
    }

    private static RiskyEvent RiskyEvt(string message) => new()
    {
        HostId = 7,
        Date = Date,
        LogName = "System",
        Source = "disk",
        EventId = 153,
        Message = message,
        EventTime = Date
    };

    private class FakeRiskyEventStore : IRiskyEventStore
    {
        public List<RiskyEvent> Rows { get; set; } = new();
        public long LastHostId;
        public DateTime LastDate;
        public string LastSource = "";
        public int LastEventId;

        public void ReplaceDay(long hostId, DateTime date, List<RiskyEvent> events) =>
            throw new NotSupportedException("查詢面用不到寫入");

        public List<RiskyEvent> Query(long hostId, DateTime date, string source, int eventId, int maxResults)
        {
            LastHostId = hostId;
            LastDate = date;
            LastSource = source;
            LastEventId = eventId;
            return Rows;
        }

        public int Prune(int retentionDays) => throw new NotSupportedException("查詢面用不到清理");
    }

    private class FakeSentinelEventFetcher : ISentinelEventFetcher
    {
        public LiveEventFetchResult? Result { get; set; }
        public bool Called { get; private set; }

        public Task<LiveEventFetchResult?> FetchAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(Result);
        }
    }
}
