namespace LogForesight.Web.Services;

/// <summary>
/// 詢問 AI 現場事件取得（docs/WEB-SCHEDULER-PLAN.md §2.2.4）：先查風險 log 暫存
/// （<see cref="IRiskyEventStore"/>，毫秒級、本機與 NetIQ 主機皆有），查無才 fallback 既有的
/// Sentinel 即時查詢（<see cref="ISentinelEventFetcher"/>，僅 NetIQ 主機、開關/節流語意不變）。
///
/// 暫存資料只涵蓋「規則命中或趨勢異常」的簽章（見 <see cref="RiskyEventSelector"/>）且有
/// 保留天數上限，查無不代表沒有訊號，只代表這裡沒有——正確的下一步是照舊 fallback，
/// 不是回傳「找不到」。
/// </summary>
public interface IRiskyEventLookup
{
    Task<LiveEventFetchResult?> FindAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default);
}

public class RiskyEventLookupService : IRiskyEventLookup
{
    /// <summary>注入 prompt 的則數上限，與 SentinelEventFetchService.MaxInjected 同一預算量級——
    /// 實際截斷／token 預算控管在 AiInsightService（PromptBudget）</summary>
    private const int MaxInjected = 20;

    private readonly IRiskyEventStore _riskyEvents;
    private readonly ISentinelEventFetcher _liveFetcher;

    public RiskyEventLookupService(IRiskyEventStore riskyEvents, ISentinelEventFetcher liveFetcher)
    {
        _riskyEvents = riskyEvents;
        _liveFetcher = liveFetcher;
    }

    public async Task<LiveEventFetchResult?> FindAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default)
    {
        var stored = _riskyEvents.Query(host.HostId, date, source, eventId, MaxInjected);
        if (stored.Count > 0)
        {
            return new LiveEventFetchResult(stored.Select(e => e.Message).ToList());
        }

        return await _liveFetcher.FetchAsync(host, date, source, eventId, ct);
    }
}
