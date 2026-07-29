namespace LogForesight.Web.Services;

/// <summary>Sentinel 目錄回報的一台主機（探索結果的最小單位）</summary>
public record NetiqDiscoveredHost(string HostName, string IpAddress);

/// <summary>探索連線/認證/輸入格式失敗——訊息可直接顯示給管理員（不含機敏內容）</summary>
public class NetiqDiscoveryException : Exception
{
    public NetiqDiscoveryException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 一次探索掃描的完整結果：主機清單＋**涵蓋範圍是顯示出來的事實，不是隱藏假設**
/// （docs/NETIQ-API-PLAN.md §3.4）。網段範圍掃描只涵蓋「窗口內有事件回報」的主機，
/// 精靈必須把這個限制講清楚，不能讓人以為結果是網段的完整名單。
/// </summary>
public sealed class NetiqDiscoveryResult
{
    public List<NetiqDiscoveredHost> Hosts { get; init; } = new();

    /// <summary>人看的涵蓋範圍說明，精靈直接顯示（如「掃描涵蓋『10.232.11.*』近 95 分鐘內有回報事件的主機」）</summary>
    public string CoverageNote { get; init; } = string.Empty;

    /// <summary>需要人工留意的異常（如查詢被截斷）——不是可以吞掉的雜訊，非空時精靈要顯示</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 向 Sentinel 主動查詢一個網段內有事件回報的主機（docs/HISTORY.md §1.2；
/// docs/NETIQ-API-PLAN.md §3.4「網段範圍掃描」，2026-07-29 定案）。
///
/// 真實 API 的認證方式與回應格式屬環境細節，因此隔離在介面後：<see cref="SentinelRestDirectoryClient"/>
/// 真連線、<see cref="StubNetiqDirectoryClient"/> 給開發與測試用固定資料，整條匯入 UI 流程
/// 不必有真 Sentinel 就能開發與驗收。
/// </summary>
public interface INetiqDirectoryClient
{
    /// <param name="subnetPrefix">網段前綴（如「10.232.11」）或 CIDR（如「10.232.11.0/24」）——
    /// 格式規則見 <see cref="SentinelQueryBuilder.NormalizeSubnetPrefix"/>，不合法時擲
    /// <see cref="NetiqDiscoveryException"/>。**必填**：探索是「掃一個網段」而不是「盲掃全站」，
    /// 全站事件量（實測單台 Sentinel 近 24h 達 2400 萬筆）下任何固定窗口設計涵蓋率都很差，
    /// 不如強制要求網段換來明確的涵蓋語意。</param>
    Task<NetiqDiscoveryResult> ListHostsAsync(SentinelServer server, string subnetPrefix, CancellationToken ct);
}

/// <summary>
/// 開發／測試用替身：依網段前綴生成示範資料，其中刻意含與既有主機重疊的 IP（僅當前綴匹配
/// 「10.1.2」時），讓「已登錄」「重疊復活」等分類在 demo 資料下也走得到。Development 環境注入。
/// </summary>
public class StubNetiqDirectoryClient : INetiqDirectoryClient
{
    public Task<NetiqDiscoveryResult> ListHostsAsync(SentinelServer server, string subnetPrefix, CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = SentinelQueryBuilder.NormalizeSubnetPrefix(subnetPrefix);
        }
        catch (ArgumentException ex)
        {
            throw new NetiqDiscoveryException(ex.Message);
        }

        var octets = normalized.Split('.');
        var hosts = new List<NetiqDiscoveredHost>();

        if (octets.Length == 3)
        {
            // 三段前綴：單一 /24 的示範資料
            if (normalized == "10.1.2")
            {
                hosts.Add(new NetiqDiscoveredHost("SRV-OO-WEB01", "10.1.2.11"));
                hosts.Add(new NetiqDiscoveredHost("SRV-OO-DB01", "10.1.2.12"));
            }
            for (var i = 20; i < 55; i++)
                hosts.Add(new NetiqDiscoveredHost($"SRV-{server.Name}-{i:D3}", $"{normalized}.{i}"));
        }
        else
        {
            // 兩段前綴：模擬真實環境下事件會分散在多個第三段（多個 /24）——
            // 依 Sentinel 名稱雜湊出第二個示範子網段，讓精靈的多網段分組在開發環境也看得到
            var seed = (server.Name.GetHashCode() & 0x7fffffff) % 200 + 1;
            foreach (var third in new[] { 9, seed })
            {
                for (var i = 5; i < 28; i++)
                    hosts.Add(new NetiqDiscoveredHost($"AP-{server.Name}-{third}-{i:D3}", $"{normalized}.{third}.{i}"));
            }
        }

        return Task.FromResult(new NetiqDiscoveryResult
        {
            Hosts = hosts,
            CoverageNote = $"（開發環境示範資料）「{normalized}.*」共 {hosts.Count} 台"
        });
    }
}

/// <summary>
/// Sentinel REST API 真連線：網段範圍掃描（docs/NETIQ-API-PLAN.md §3.4）。
///
/// 流程：(1) 計數查詢（`max-results:1`）拿該網段近 24h 的 `found`；(2) 依 found 與
/// <see cref="CoverageTargetResults"/> 算出實際掃描窗口——found 在目標值內就掃整整 24 小時
/// （涵蓋最好），事件量大的網段自動按比例縮短窗口（下限 <see cref="MinWindowHours"/>），
/// 讓互動掃描維持在可接受的等待時間；(3) 用該窗口只投影 repip／sn 兩欄，本地依 repip
/// distinct 出主機清單，HostName 取每個 IP 最常見的 sn 值（真實機器名）。
///
/// 逾時／頁數／併發全部沿用 <see cref="SentinelClient"/> 既有機制（單一佇列、token 重用登出、
/// job 用完即刪），只是把 <see cref="NetiqOptions"/> 的逾時／單頁筆數／單次上限換成互動掃描
/// 專用的值——批次夜間查詢可以等，管理員盯著畫面轉圈不行。
/// </summary>
public class SentinelRestDirectoryClient : INetiqDirectoryClient
{
    /// <summary>單次掃描目標取回的事件筆數上限。第二、三輪 probe 實證：單台網段近 24h
    /// 從幾萬到近 76 萬筆都有，這個值是「翻頁數（上限約 50 頁）」與「涵蓋率」的折衷。</summary>
    internal const int CoverageTargetResults = 50_000;

    /// <summary>掃描窗口下限（小時）。事件量極大的網段縮到這裡就不再縮，避免窗口小到
    /// 連零星活動的主機都掃不到——涵蓋率再差也要有個底線。</summary>
    internal const double MinWindowHours = 5.0 / 60.0;

    internal const double MaxWindowHours = 24.0;

    /// <summary>互動掃描的逾時秒數，刻意不用 NetiqOptions 設定的批次逾時（可能長達數分鐘）——
    /// 管理員盯著精靈畫面等，逾時要短，讓失敗訊息很快回來而不是乾等。</summary>
    internal const int InteractiveTimeoutSeconds = 30;

    private readonly NetiqOptionsStore? _netiqOptionsStore;
    private readonly NetiqOptions? _optionsOverride;
    private readonly HttpMessageHandler? _handler;

    public SentinelRestDirectoryClient(NetiqOptionsStore netiqOptionsStore)
    {
        _netiqOptionsStore = netiqOptionsStore;
    }

    /// <summary>
    /// 測試用建構子：直接帶入節流參數與 <see cref="HttpMessageHandler"/>，跳過
    /// <see cref="NetiqOptionsStore"/>（它背後是真實 DB，單元測試不需要為了幾個節流欄位
    /// 另外接一份資料庫——同 <see cref="SentinelClient"/> 自己的 handler 測試縫一樣的理由）。
    /// </summary>
    public SentinelRestDirectoryClient(NetiqOptions options, HttpMessageHandler handler)
    {
        _optionsOverride = options;
        _handler = handler;
    }

    public async Task<NetiqDiscoveryResult> ListHostsAsync(SentinelServer server, string subnetPrefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new NetiqDiscoveryException($"Sentinel「{server.Name}」未設定 BaseUrl。");

        string filter;
        string normalizedPrefix;
        try
        {
            normalizedPrefix = SentinelQueryBuilder.NormalizeSubnetPrefix(subnetPrefix);
            filter = SentinelQueryBuilder.BuildSubnetDiscoveryFilter(subnetPrefix);
        }
        catch (ArgumentException ex)
        {
            throw new NetiqDiscoveryException(ex.Message);
        }

        var baseOptions = _optionsOverride ?? _netiqOptionsStore!.Get();
        // 節流間隔／憑證逃生門沿用管理員在「NetIQ 維護」頁設定的值——那是連線行為本身的設定，
        // 互動或批次都該遵守；逾時／分頁／單次上限才是這裡要覆寫的「互動掃描」專屬值
        var interactiveOptions = new NetiqOptions
        {
            QueryDelayMs = baseOptions.QueryDelayMs,
            PageSize = 1000,
            MaxResultsPerJob = CoverageTargetResults,
            TimeoutSeconds = InteractiveTimeoutSeconds,
            RetryCount = baseOptions.RetryCount,
            AllowInvalidCertificates = baseOptions.AllowInvalidCertificates
        };

        try
        {
            await using var client = new SentinelClient(server, interactiveOptions, _handler);
            var now = DateTimeOffset.UtcNow;

            var countResult = await client.SearchAsync(
                new SentinelSearchRequest(filter, now.AddHours(-MaxWindowHours), now, MaxResults: 1), ct);
            var found24h = countResult.Found;

            if (found24h == 0)
            {
                return new NetiqDiscoveryResult
                {
                    CoverageNote = $"「{normalizedPrefix}.*」近 24 小時無任何事件回報，請確認網段是否正確，或稍後再試。"
                };
            }

            var windowHours = found24h <= CoverageTargetResults
                ? MaxWindowHours
                : Math.Max(MinWindowHours, MaxWindowHours * ((double)CoverageTargetResults / found24h));
            var start = now.AddHours(-windowHours);

            var mainResult = await client.SearchAsync(new SentinelSearchRequest(
                filter, start, now,
                new[] { SentinelFieldMap.HostIp, SentinelFieldMap.HostName },
                MaxResults: CoverageTargetResults), ct);

            var hosts = mainResult.Events
                .GroupBy(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostIp, string.Empty), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .Select(g => new NetiqDiscoveredHost(PickMostCommonHostName(g) ?? g.Key, g.Key))
                .ToList();

            var warnings = new List<string>();
            if (mainResult.Truncated)
            {
                warnings.Add($"事件量高於預估（found={mainResult.Found:N0}，僅取回 {mainResult.Events.Count:N0} 筆），" +
                             "本次掃描結果可能不完整，建議改掃更小的網段（如縮到 /24）。");
            }

            return new NetiqDiscoveryResult
            {
                Hosts = hosts,
                CoverageNote = $"掃描涵蓋「{normalizedPrefix}.*」近{FormatWindow(windowHours)}內有回報事件的主機，" +
                               $"共 {hosts.Count} 台（該網段近 24 小時共 {found24h:N0} 筆事件，已依量級自動決定掃描窗口）。",
                Warnings = warnings
            };
        }
        catch (SentinelClientException ex)
        {
            throw new NetiqDiscoveryException($"掃描 Sentinel「{server.Name}」失敗：{ex.Message}", ex);
        }
    }

    /// <summary>同一 IP 底下最常見的非空 sn 值——單一主機的事件不會每筆 sn 都不同，
    /// 取眾數比取第一筆更能防偶發的欄位缺席或雜訊值。</summary>
    private static string? PickMostCommonHostName(IEnumerable<SentinelEvent> events) =>
        events
            .Select(e => e.Fields.GetValueOrDefault(SentinelFieldMap.HostName))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private static string FormatWindow(double hours) =>
        hours >= 1
            ? $"{hours:0.#} 小時"
            : $"{Math.Max(1, (int)Math.Round(hours * 60))} 分鐘";
}
