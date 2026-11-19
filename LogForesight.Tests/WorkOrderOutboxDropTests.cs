using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>交辦 outbox 超限丟棄：記一次警告、累計近期丟棄筆數；沒有丟棄時兩者都不發生。</summary>
[Collection("NLogGlobalConfig")]
public sealed class WorkOrderOutboxDropTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static WorkOrderOutboxItem Item(int i) => new()
    {
        Kind = WorkOrderOutboxKinds.Created, WorkOrderId = i, RecipientUserId = 1, ActorAccount = "admin"
    };

    private (WorkOrderOutboxStore Outbox, WorkOrderOutboxDropStore Drops) NewStores()
    {
        var drops = new WorkOrderOutboxDropStore(_fx.Blob(WorkOrderOutboxDropStore.BlobKey));
        return (new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey), _clock, drops), drops);
    }

    [Fact]
    public void 塞滿後再加一筆_記警告並寫入丟棄紀錄()
    {
        var (outbox, drops) = NewStores();
        outbox.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(Item).ToList());

        using var capture = NLogCapture.For(typeof(WorkOrderOutboxStore));
        var dropped = outbox.Enqueue(Item(WorkOrderOutboxStore.MaxItems + 1));

        Assert.Equal(1, dropped);
        Assert.Contains(capture.Logs, l => l.StartsWith("Warn|") && l.Contains("已移除 1 筆"));
        var state = drops.Get();
        Assert.Equal(1, state.RecentDropped);
        Assert.NotNull(state.LastDroppedAt);
    }

    [Fact]
    public void 未超過上限_沒有警告也沒有丟棄紀錄()
    {
        var (outbox, drops) = NewStores();

        using var capture = NLogCapture.For(typeof(WorkOrderOutboxStore));
        var dropped = outbox.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(Item).ToList());

        Assert.Equal(0, dropped);
        Assert.DoesNotContain(capture.Logs, l => l.StartsWith("Warn|"));
        Assert.Null(drops.Get().LastDroppedAt);
        Assert.Equal(0, drops.Get().RecentDropped);
    }

    [Fact]
    public void 近期內再次丟棄累計筆數_超過窗口後重新起算()
    {
        var (outbox, drops) = NewStores();
        outbox.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(Item).ToList());
        outbox.Enqueue(Item(30001));
        _clock.Advance(TimeSpan.FromDays(6));
        outbox.Enqueue(Item(30002));
        Assert.Equal(2, drops.Get().RecentDropped);   // 窗口內累加

        _clock.Advance(WorkOrderOutboxDropStore.RecentWindow + TimeSpan.FromHours(1));
        outbox.Enqueue(Item(30003));
        Assert.Equal(1, drops.Get().RecentDropped);   // 超過七天沒丟棄，重新起算
    }

    [Fact]
    public void 沒有接丟棄紀錄的store_照樣回傳丟棄筆數且不擲例外()
    {
        var outbox = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey), _clock);
        outbox.Enqueue(Enumerable.Range(1, WorkOrderOutboxStore.MaxItems).Select(Item).ToList());

        Assert.Equal(1, outbox.Enqueue(Item(40000)));
    }
}
