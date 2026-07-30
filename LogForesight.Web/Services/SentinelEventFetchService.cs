using System.Collections.Concurrent;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>詢問 AI 詢問當下即時取回的現場事件（docs/FEEDBACK-4-PLAN.md §5）</summary>
public record LiveEventFetchResult(List<string> Messages);

/// <summary>
/// 詢問 AI（實驗性）詢問當下向 Sentinel 即時查詢現場事件（docs/FEEDBACK-4-PLAN.md §5）。
///
/// **確定性預取，不是 MCP**：查詢參數（IP＋EventId＋當日時間窗）完全由伺服器端從
/// issueKey 推導，模型不參與「查什麼」的決策——理由見規劃文件 §5 的 MCP 評估：
/// 本站 AI 端是無 function calling 能力的地端小模型，讓模型自主決定工具呼叫的失敗模式是
/// 「靜默不呼叫或亂呼叫」，恰好違反本專案「程式能確定性算的不交給 AI」的鐵律；
/// 且事件內容是攻擊者可控字串，模型自主觸發查詢會擴大注入面。
///
/// 三道節流閘（都是刻意的加值層降級設計，不是效能微調）：
///   1. 開關（NetiqOptions.ChatLiveFetchEnabled，預設 false）——管理者顯式決定要不要
///      對 Sentinel 加這道白天查詢負載；
///   2. 全站併發上限 1（<see cref="_gate"/>）——Sentinel 白天要服務其他工具，搶不到旗標
///      直接跳過（回 null），不排隊等待；
///   3. 10 分鐘記憶體快取——同一問題同一天的後續詢問／其他使用者重用，不重複打 Sentinel。
///
/// 任何失敗（開關關閉、非 NetIQ 主機、連線失敗、逾時、搶不到併發旗標、0 筆結果）一律回
/// null——呼叫端（AiInsightService.ChatAsync）據此略過現場事件區塊，符合 AI 加值層
/// 「掛掉不影響頁面」的既有鐵律。唯讀查詢，不寫稽核（不是使用者主動的異動操作）。
/// </summary>
public interface ISentinelEventFetcher
{
    Task<LiveEventFetchResult?> FetchAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default);
}

public class SentinelEventFetchService : ISentinelEventFetcher
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>全站併發上限 1：一次只有一個詢問 AI 的請求能對 Sentinel 發即時查詢</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly ConcurrentDictionary<string, (DateTime Expiry, LiveEventFetchResult? Result)> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>取數本身的硬逾時——對話整體 60 秒，取數不能吃掉大半（docs/FEEDBACK-4-PLAN.md §5 D4）</summary>
    private const int FetchTimeoutSeconds = 15;

    /// <summary>Sentinel 查詢上限（這是「部分關鍵 log」，不是把整天搬回來）</summary>
    private const int MaxResults = 50;

    /// <summary>注入 prompt 的則數上限——實際截斷／token 預算控管在 AiInsightService（PromptBudget）</summary>
    private const int MaxInjected = 20;

    private readonly INetiqServerCatalog _catalog;
    private readonly NetiqOptionsStore _netiqOptionsStore;

    public SentinelEventFetchService(INetiqServerCatalog catalog, NetiqOptionsStore netiqOptionsStore)
    {
        _catalog = catalog;
        _netiqOptionsStore = netiqOptionsStore;
    }

    public async Task<LiveEventFetchResult?> FetchAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct = default)
    {
        var options = _netiqOptionsStore.Get();
        if (!options.ChatLiveFetchEnabled) return null;

        // 僅 NetIQ 主機：本機直讀主機在 Web 端沒有即時讀取 Event Log 的路徑，
        // 靜默維持現狀（不顯示任何取數跡象，不誤導使用者以為有查過）
        if (string.IsNullOrWhiteSpace(host.NetiqServer) || string.IsNullOrWhiteSpace(host.IpAddress)) return null;

        var cacheKey = $"{host.HostName}|{date:yyyy-MM-dd}|{source}|{eventId}";
        if (Cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow) return cached.Result;

        // 搶不到併發旗標就直接跳過——不排隊等待，這是加值功能不是主業
        if (!await Gate.WaitAsync(0, ct))
        {
            Log.Debug("詢問 AI 現場取數：併發旗標忙碌中，本次跳過（{Host}/{Date}/{Source}/{EventId}）", host.HostName, date, source, eventId);
            return null;
        }

        try
        {
            var result = await FetchInternalAsync(host, date, source, eventId, ct);
            Cache[cacheKey] = (DateTime.UtcNow.Add(CacheTtl), result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "詢問 AI 現場取數失敗（靜默降級）：{Host}/{Date}/{Source}/{EventId}", host.HostName, date, source, eventId);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<LiveEventFetchResult?> FetchInternalAsync(WebHost host, DateTime date, string source, int eventId, CancellationToken ct)
    {
        var server = _catalog.GetServer(host.NetiqServer);
        if (server == null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(FetchTimeoutSeconds));

        var netiqOptions = _netiqOptionsStore.Get();
        await using var client = new SentinelClient(server, netiqOptions);

        var filter = $"{SentinelQueryBuilder.BuildIpClause(new[] { host.IpAddress! })} AND {SentinelFieldMap.EventId}:\"{eventId}\"";
        var request = new SentinelSearchRequest(
            filter,
            new DateTimeOffset(date.Date, TimeSpan.Zero),
            new DateTimeOffset(date.Date.AddDays(1), TimeSpan.Zero),
            Fields: new[] { SentinelFieldMap.Timestamp, SentinelFieldMap.Message, SentinelFieldMap.Severity, SentinelFieldMap.Source, SentinelFieldMap.LogName },
            PageSize: MaxResults,
            MaxResults: MaxResults);

        var searchResult = await client.SearchAsync(request, timeoutCts.Token);
        if (searchResult.Events.Count == 0) return null;

        // Source 過濾在取回後於記憶體端比對——Sentinel 端 obssvcname 的比對語意依 probe 定案為準，
        // filter 只鎖 IP+EventId 避免過嚴漏抓，這裡用完整 Source 字串再篩一次
        var messages = searchResult.Events
            .Where(e => !e.Fields.TryGetValue(SentinelFieldMap.Source, out var s) || string.Equals(s, source, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Fields.TryGetValue(SentinelFieldMap.Timestamp, out var t) ? t : string.Empty)
            .Take(MaxInjected)
            .Select(e => e.Fields.TryGetValue(SentinelFieldMap.Message, out var m) ? m : string.Empty)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Length > 500 ? m[..500] : m)
            .ToList();

        return messages.Count > 0 ? new LiveEventFetchResult(messages) : null;
    }
}
