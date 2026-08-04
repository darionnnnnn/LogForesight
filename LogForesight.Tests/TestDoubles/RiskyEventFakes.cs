using System.Threading;
using System.Threading.Tasks;
using LogForesight.Web.Services;

namespace LogForesight.Tests;

// ── 測試替身：RiskyEventLookupService 相關 ───────────────────────────────────
// 原本是 RiskyEventLookupServiceTests 裡的巢狀 private 類別，搬到這裡集中管理。

internal class FakeRiskyEventStore : IRiskyEventStore
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

internal class FakeSentinelEventFetcher : ISentinelEventFetcher
{
    public LiveEventFetchResult? Result { get; set; }
    public bool Called { get; private set; }

    public Task<LiveEventFetchResult?> FetchAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default)
    {
        Called = true;
        return Task.FromResult(Result);
    }
}
