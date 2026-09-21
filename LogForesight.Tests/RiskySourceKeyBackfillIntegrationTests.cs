using Xunit;

namespace LogForesight.Tests;

public sealed class RiskySourceKeyBackfillIntegrationTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private static readonly DateTime Date = new(2026, 8, 12);

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TopIssue來源鍵完成後_風險事件分批完成並發布獨立Ready()
    {
        Seed(null, "évent", 7, "one");
        Seed(null, "Storahci", 129, "two");

        var topBackfiller = new TopIssueBackfiller(_fx.NewContext);
        topBackfiller.Run(CancellationToken.None);
        Assert.True(topBackfiller.SourceKeyProgress.Completed);
        Assert.False(topBackfiller.RiskySourceKeyProgress.Completed);

        topBackfiller.RunRiskySourceKeys(new EfRiskyEventStore(_fx.NewContext), CancellationToken.None);

        Assert.True(topBackfiller.RiskySourceKeyProgress.Completed);
        Assert.Equal(2, topBackfiller.RiskySourceKeyProgress.Done);
        Assert.Equal(2, topBackfiller.RiskySourceKeyProgress.Total);
    }

    [Fact]
    public void 取消保留已完成批次_重啟後接續並完成()
    {
        Seed(null, "évent", 7, "one");
        Seed(null, "Storahci", 129, "two");

        var store = new EfRiskyEventStore(_fx.NewContext);
        Assert.Equal(1, store.BackfillSourceKeysBatch(batchSize: 1));

        var topBackfiller = new TopIssueBackfiller(_fx.NewContext);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        topBackfiller.RunRiskySourceKeys(store, cancelled.Token);

        Assert.False(topBackfiller.RiskySourceKeyProgress.Completed);
        Assert.Equal(1, topBackfiller.RiskySourceKeyProgress.Done);

        topBackfiller.RunRiskySourceKeys(store, CancellationToken.None);

        Assert.True(topBackfiller.RiskySourceKeyProgress.Completed);
        Assert.Equal(2, topBackfiller.RiskySourceKeyProgress.Done);
    }

    [Fact]
    public void WebLookupGate只看RiskySourceKeyProgress完成狀態()
    {
        Seed(null, "évent", 7, "unicode");

        var topBackfiller = new TopIssueBackfiller(_fx.NewContext);
        var lookup = new EfRiskyEventStore(
            _fx.NewContext, () => topBackfiller.RiskySourceKeyProgress.Completed);

        Assert.Empty(lookup.Query(1, Date, "ÉVENT", 7, maxResults: 20));

        topBackfiller.RunRiskySourceKeys(new EfRiskyEventStore(_fx.NewContext), CancellationToken.None);

        var result = lookup.Query(1, Date, "ÉVENT", 7, maxResults: 20);
        Assert.Single(result);
        Assert.Equal("unicode", result[0].Message);
    }

    private void Seed(string? sourceKey, string source, int eventId, string message)
    {
        using var ctx = _fx.NewContext();
        ctx.RiskyEvents.Add(new RiskyEventRow
        {
            HostId = 1,
            Date = Date,
            LogName = "System",
            Source = source,
            SourceKey = sourceKey,
            EventId = eventId,
            EntryType = System.Diagnostics.EventLogEntryType.Error,
            EventTime = Date,
            Message = message,
            CreatedAt = Date
        });
        ctx.SaveChanges();
    }
}
