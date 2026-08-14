using System.Collections.Concurrent;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>詢問 AI 詢問當下即時取回的現場事件（docs/archive/FEEDBACK-4-PLAN.md §5）</summary>
public record LiveEventFetchResult(List<string> Messages);

/// <summary>
/// 詢問 AI（實驗性）詢問當下向 Sentinel 即時查詢現場事件（docs/archive/FEEDBACK-4-PLAN.md §5）。
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

    /// <summary>取數本身的硬逾時——對話整體 60 秒，取數不能吃掉大半（docs/archive/FEEDBACK-4-PLAN.md §5 D4）</summary>
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

        var query = BuildQuery(host.Os, host.IpAddress!, source, eventId);

        // 當地日 00:00 → 翌日 00:00，轉 UTC 交給 SentinelClient——與 NetiqPipelineService 的
        // 日窗口同一套換算（TimeSpan.Zero 會把當地午夜當成 UTC 午夜，台灣環境整個窗口偏移 8 小時）
        var request = new SentinelSearchRequest(
            query.Filter,
            new DateTimeOffset(date.Date, TimeZoneInfo.Local.GetUtcOffset(date.Date)),
            new DateTimeOffset(date.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(date.Date.AddDays(1))),
            Fields: query.ProjectionFields,
            PageSize: MaxResults,
            MaxResults: MaxResults);

        var searchResult = await client.SearchAsync(request, timeoutCts.Token);
        if (searchResult.Events.Count == 0) return null;

        // Source/program 過濾在取回後於記憶體端再比對一次——filter 只鎖 IP+內容子句避免過嚴漏抓
        // （Linux 版還額外用了前綴萬用字元），這裡用完整值再篩一次收緊
        var messages = searchResult.Events
            .Where(e => !e.Fields.TryGetValue(query.SourceField, out var s) || string.Equals(s, source, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Fields.TryGetValue(SentinelFieldMap.Timestamp, out var t) ? t : string.Empty)
            .Take(MaxInjected)
            .Select(e => e.Fields.TryGetValue(SentinelFieldMap.Message, out var m) ? m : string.Empty)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Length > 500 ? m[..500] : m)
            .ToList();

        return messages.Count > 0 ? new LiveEventFetchResult(messages) : null;
    }

    internal readonly record struct LiveFetchQuery(string Filter, string[] ProjectionFields, string SourceField);

    /// <summary>
    /// 依主機 OS 組出現場取數的 filter／投影欄位／記憶體端二次過濾用的欄位鍵
    /// （docs/archive/FEEDBACK-12-PLAN.md §4.6 體檢揪出）。抽成純函數獨立於 HTTP 呼叫之外，
    /// 才能不架假 Sentinel 伺服器直接單元測試——這支服務原本只測「不符資格時提早回 null」
    /// 三道防線（見 SentinelEventFetchServiceTests 的類別文件），查詢本身怎麼組從未被驗證過，
    /// Linux 版倘若寫錯只會靜默回傳空結果，不會有任何錯誤或警告冒出來。
    ///
    /// Linux 事件沒有 EventId（恆 0）也沒有 <c>rv40</c> 欄位（docs/archive/FEEDBACK-12-PLAN.md §4.0
    /// 輪 A 實證）——沿用 Windows 的 EventId 子句會對 Linux Sentinel 查出 0 筆（`rv40` 欄位
    /// 不存在，不是「查無資料」而是「查詢條件對這個環境沒有意義」），現場取數會對 Linux 主機
    /// 整個靜默失效。改用 program（<c>sp</c>，即 <paramref name="source"/>）子句，是 Linux 面
    /// 能對應到「這個問題」的最接近欄位（EventKey/規則 Id 是本地概念，Sentinel 端沒有對應
    /// 欄位可查）。
    /// </summary>
    internal static LiveFetchQuery BuildQuery(string os, string ip, string source, int eventId)
    {
        var isLinux = os.Equals(WebHost.OsLinux, StringComparison.OrdinalIgnoreCase);
        var contentClause = isLinux
            ? $"{SentinelFieldMap.LinuxProgram}:{source}*"
            : $"{SentinelFieldMap.EventId}:\"{eventId}\"";
        var filter = $"{SentinelQueryBuilder.BuildIpClause(new[] { ip })} AND {contentClause}";

        var sourceField = isLinux ? SentinelFieldMap.LinuxProgram : SentinelFieldMap.Source;
        var projectionFields = isLinux
            ? new[] { SentinelFieldMap.Timestamp, SentinelFieldMap.Message, SentinelFieldMap.Severity, SentinelFieldMap.LinuxProgram }
            : new[] { SentinelFieldMap.Timestamp, SentinelFieldMap.Message, SentinelFieldMap.Severity, SentinelFieldMap.Source, SentinelFieldMap.LogName };

        return new LiveFetchQuery(filter, projectionFields, sourceField);
    }
}
