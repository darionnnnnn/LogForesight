using System.Collections.Concurrent;
using LogForesight.Core;
using LogForesight.Core.Analysis;

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
/// （docs/NETIQ-API-REFERENCE.md §3.4）。網段範圍掃描只涵蓋「窗口內有事件回報」的主機，
/// 精靈必須把這個限制講清楚，不能讓人以為結果是網段的完整名單。
/// </summary>
public sealed class NetiqDiscoveryResult
{
    public List<NetiqDiscoveredHost> Hosts { get; init; } = new();

    /// <summary>人看的涵蓋範圍說明，精靈直接顯示（如「掃描涵蓋『192.168.0.*』近 95 分鐘內有回報事件的主機」）</summary>
    public string CoverageNote { get; init; } = string.Empty;

    /// <summary>需要人工留意的異常（如查詢被截斷）——不是可以吞掉的雜訊，非空時精靈要顯示</summary>
    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 向 Sentinel 主動查詢一個網段內有事件回報的主機（docs/archive/HISTORY.md §1.2；
/// docs/NETIQ-API-REFERENCE.md §3.4「網段範圍掃描」，2026-07-29 定案）。
///
/// 真實 API 的認證方式與回應格式屬環境細節，因此隔離在介面後：<see cref="SentinelRestDirectoryClient"/>
/// 真連線、<see cref="StubNetiqDirectoryClient"/> 給開發與測試用固定資料，整條匯入 UI 流程
/// 不必有真 Sentinel 就能開發與驗收。
/// </summary>
public interface INetiqDirectoryClient
{
    /// <param name="subnetPrefix">網段前綴（如「192.168.0」）或 CIDR（如「192.168.0.0/24」）——
    /// 格式規則見 <see cref="SentinelQueryBuilder.NormalizeSubnetPrefix"/>，不合法時擲
    /// <see cref="NetiqDiscoveryException"/>。**必填**：探索是「掃一個網段」而不是「盲掃全站」，
    /// 全站事件量（實測單台 Sentinel 近 24h 達 2400 萬筆）下任何固定窗口設計涵蓋率都很差，
    /// 不如強制要求網段換來明確的涵蓋語意。</param>
    /// <param name="knownIps">已登錄、**不需要重新發現**的主機 IP
    /// （docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §3.3）。在 server 端就被排除，
    /// 沒有新主機時 found=0、一次查詢即結束——重掃因此從「又拉數萬筆」變成趨近免費。
    /// null／空＝首掃行為（不排除）。**回傳結果不含這些主機**，呼叫端自行合成
    /// （它們的名稱以主機清單為準，比事件裡的 sn 更可靠）。</param>
    /// <param name="concurrency">網段分段的平行掃描併發度（1~<see cref="SentinelRestDirectoryClient.MaxScanConcurrency"/>，預設 1＝依序）。</param>
    Task<NetiqDiscoveryResult> ListHostsAsync(
        SentinelServer server, string subnetPrefix, CancellationToken ct,
        IReadOnlyCollection<string>? knownIps = null,
        Action<string, int>? onProgress = null,
        int? totalBudgetSecondsOverride = null,
        ScanGranularity granularity = ScanGranularity.Slash24,
        int concurrency = 1);
}

/// <summary>
/// 開發／測試用替身：依網段前綴生成示範資料，其中刻意含與既有主機重疊的 IP（僅當前綴匹配
/// 「192.168.0」時——與精靈輸入框的建議範例同一個值，跟著打就能看到這個情境），
/// 讓「已登錄」「重疊復活」等分類在 demo 資料下也走得到。Development 環境注入。
/// </summary>
public class StubNetiqDirectoryClient : INetiqDirectoryClient
{
    /// <param name="knownIps">**刻意忽略**：示範資料的價值就在於「已登錄／重疊復活」等分類
    /// 在 demo 下也走得到，把已登錄的濾掉等於把要示範的東西刪光。真實 client 才排除。</param>
    public Task<NetiqDiscoveryResult> ListHostsAsync(
        SentinelServer server, string subnetPrefix, CancellationToken ct,
        IReadOnlyCollection<string>? knownIps = null,
        Action<string, int>? onProgress = null,
        int? totalBudgetSecondsOverride = null,
        ScanGranularity granularity = ScanGranularity.Slash24,
        int concurrency = 1)
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
            if (normalized == "192.168.0")
            {
                hosts.Add(new NetiqDiscoveredHost("SRV-OO-WEB01", "192.168.0.11"));
                hosts.Add(new NetiqDiscoveredHost("SRV-OO-DB01", "192.168.0.12"));
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
            CoverageNote = $"（離線示範資料）「{normalized}.*」共 {hosts.Count} 台",
            // 固定台數／固定網段數是這份示範資料產生器本身的邏輯（見上方迴圈），不是掃描結果被截斷——
            // 放進 Warnings 而不是只寫在 CoverageNote：前端已有現成的醒目警告框渲染這個欄位
            // （imports.js renderCoverageNote），CoverageNote 是灰色小字很容易被忽略，
            // 曾被誤以為「掃描功能有 bug」（單一網段恆 35 台、兩網段各恆 23 台、恆最多 2 個網段）。
            Warnings = new List<string>
            {
                "這是離線示範資料（固定台數與網段數，非真實 Sentinel 掃描結果）。" +
                "要對真實 Sentinel 試掃，請至「NetIQ 維護」頁關閉「使用離線示範資料」開關。"
            }
        });
    }
}

/// <summary>
/// Sentinel REST API 真連線：網段範圍掃描（docs/NETIQ-API-REFERENCE.md §3.4；
/// **2026-08-06 涵蓋保證改版** docs/archive/NETIQ-DISCOVERY-PLAN-2026-08-06.md §三）。
///
/// <para><b>涵蓋保證（本類別存在的意義，改版的核心）</b>：掃描結束時，
/// (a) 近 24 小時內有 ≥1 筆 System/Application 事件的主機、
/// (b) 近 <see cref="SupplementWindowMinutes"/> 分鐘內有 ≥1 筆任何頻道事件的主機——
/// **保證入列，或明確警告「清單可能不完整」，二者必居其一**。
/// 兩個窗口都完全沒講話的主機是事件掃描原理上看不到的（它沒發事件），
/// 那是資料源的極限而不是實作缺陷，<see cref="NetiqDiscoveryResult.CoverageNote"/>
/// 永遠把這件事講明。</para>
///
/// <para><b>與舊版的本質差異</b>：舊版用「自適應窗口」控制取回筆數——事件越多窗口越短
/// （下限曾是 5 分鐘），而縮掉的那段時間裡安靜主機的事件一併消失，**且不觸發任何警告**。
/// 那是靜默漏機。新版窗口固定 24 小時，改用「窄化 filter ＋ 殘差輪掃」控制筆數：
/// 觸頂時把已發現的主機排除後重查，每輪取回的只有還沒見過的主機，數學上保證收斂；
/// 輪數或子句用盡才停，並且**一定發警告**。</para>
///
/// <para><b>三段掃描</b>：
/// (1) 主掃描 <see cref="SentinelQueryBuilder.BuildSubnetProbeFilter"/>——窄化到
/// System/Application 頻道（probe 實測佔 0.05%，每台約 155 筆/日），
/// 取回量正比於主機數而非事件量；
/// (2) 補充掃描——全事件、固定短窗，撈「主掃描漏掉的 Security-only 主機」，
/// 並排除主掃描已見（否則取回的絕大多數是已知主機的 Security 洪流）；
/// (3) 兩段各自的殘差輪掃。結果依 repip 聯集。</para>
///
/// 逾時／頁數／併發全部沿用 <see cref="SentinelClient"/> 既有機制（單一佇列、token 重用登出、
/// job 用完即刪），只是把 <see cref="NetiqOptions"/> 的逾時／單頁筆數／單次上限換成互動掃描
/// 專用的值——批次夜間查詢可以等，管理員盯著畫面轉圈不行。
/// </summary>
public class SentinelRestDirectoryClient : INetiqDirectoryClient
{
    /// <summary>單次掃描可展開的最大分段數。/16 在 /24 粒度是 256 段（在預算內掃得完）；
    /// /16 配 /26 會展開 1024 段、**刻意被此上限擋下**——細粒度的用途是「已知某個 /24
    /// 特別吵時針對它掃」，不是拿來掃整個 /16。更寬的輸入已由
    /// SentinelQueryBuilder.MinPrefixOctets 擋掉，這個上限是防禦性的第二道閘門。</summary>
    internal const int MaxSegments = 512;

    /// <summary>
    /// 互動掃描的最大分段併發度。這是行程架構上限不是效能旋鈕：
    /// 互動掃描可能與夜間 pipeline 同時對同一台 Sentinel 施壓
    /// （理由比照 <see cref="NetiqOptions.MaxParallelQueriesPerServerLimit"/>）。
    /// </summary>
    internal const int MaxScanConcurrency = 3;

    /// <summary>單輪掃描取回的事件筆數上限。第二、三輪 probe 實證：單台網段近 24h
    /// 從幾萬到近 76 萬筆都有，這個值是「翻頁數（上限約 50 頁）」與「單輪涵蓋」的折衷。
    /// **觸頂不再代表漏機**——觸頂會進殘差輪掃（見 <see cref="MaxResidualRounds"/>）。</summary>
    internal const int CoverageTargetResults = 50_000;

    /// <summary>
    /// 掃描窗口固定 24 小時。**刻意不再自適應縮短**——舊版「事件越多窗口越短」正是
    /// 靜默漏機的機制：被縮掉的時間裡，安靜主機的少數幾筆事件一併消失，而畫面上
    /// 沒有任何跡象顯示這件事發生過。控制取回量改用窄化 filter＋殘差輪掃，
    /// 那兩者都不會犧牲時間涵蓋。
    /// </summary>
    internal const double WindowHours = 24.0;

    /// <summary>
    /// 殘差輪掃的輪數上限。每輪把已發現的主機排除後重查，取回的只有還沒見過的主機，
    /// 因此新主機數嚴格遞增、殘差嚴格遞減——收斂是必然的，這個上限只是防止
    /// 極端情況下無限迴圈。5 輪 × 50,000 筆 ÷ 155 筆/台/日 ≈ 1,600 台/網段，
    /// 遠超單一網段的實務規模（/24 最多 254 台）。
    /// </summary>
    internal const int MaxResidualRounds = 5;

    /// <summary>補充掃描的殘差輪數上限。比主掃描少，因為它的職責只是「補主掃描的漏」，
    /// 殘差本來就小；真的連 2 輪都填不滿代表該網段主機數已超出掃描能力，該警告了。</summary>
    internal const int SupplementResidualRounds = 2;

    /// <summary>
    /// 排除子句的 IP 數上限。Lucene 預設 <c>maxClauseCount</c> 是 1024，
    /// 留一半餘裕給窄化子句與未來擴充。/24 全滿 254 台也在限內；
    /// 超過就停止排除（並依情境警告或退回無排除版）——寧可多取回一些，
    /// 也不要送出一個會被 Sentinel 整個拒絕的查詢。
    /// </summary>
    internal const int ExclusionClauseLimit = 500;

    /// <summary>提早停頁門檻：連續這麼多頁都沒出現沒見過的主機，就停止翻頁。
    /// 探索只需要相異主機 IP，不需要把事件翻完——一輪 50 頁約 85 秒，
    /// 幾乎吃光整趟 90 秒預算，殘差輪掃因此永遠跑不完。
    /// **提早停頁不等於已涵蓋**：該輪仍視為觸頂（Truncated）並繼續進殘差輪。</summary>
    internal const int NoNewHostPagesBeforeStop = 3;

    /// <summary>補充掃描的窗口（分鐘）。短窗對 Security 的涵蓋率天生高——
    /// 只要主機活著，60 分鐘內幾乎必有 Security 事件（登入、稽核、票證），
    /// 而 Security 正是主掃描刻意排除掉的那一塊。兩段各用自己擅長的頻道×窗口組合。</summary>
    internal const int SupplementWindowMinutes = 60;

    /// <summary>補充掃描的單輪取回上限。比主掃描小一個量級：它掃的是殘差
    /// （已排除主掃描發現的全部主機），量本來就該很小；真的填滿代表殘差主機很多，
    /// 殘差輪掃會接手。</summary>
    internal const int SupplementMaxResults = 10_000;

    /// <summary>單次 REST 呼叫的逾時秒數，刻意不用 NetiqOptions 設定的批次逾時（可能長達數分鐘）——
    /// 管理員盯著精靈畫面等，逾時要短，讓失敗訊息很快回來而不是乾等。</summary>
    internal const int InteractiveTimeoutSeconds = 30;

    /// <summary>背景掃描的總預算秒數。掃描改為背景工作後沒有人在瀏覽器前乾等，
    /// 預算可以放到真正夠用的量級——下一階段的網段自動分割會把寬網段拆成上百個子網段，
    /// 90 秒的互動預算遠遠不夠。仍保留上限：無上限的背景工作會在 Sentinel 端長期佔資源。</summary>
    internal const int BackgroundTotalBudgetSeconds = 900;

    /// <summary>
    /// **整趟掃描**的總預算秒數。<see cref="InteractiveTimeoutSeconds"/> 只約束單次 REST 呼叫與
    /// job 輪詢階段（<c>SentinelClient.PollUntilTerminalAsync</c>），**分頁迴圈不受它約束**：
    /// <see cref="CoverageTargetResults"/>÷1000 筆/頁＝最多 50 次連續翻頁請求，實測 Sentinel
    /// 單次查詢往返約 1.7 秒（第一輪 probe），最壞情況可以讓管理員在精靈畫面前乾等一分半以上，
    /// 沒有任何上限——「逾時要短」的承諾等於沒有兌現。這裡用一個涵蓋整個 <see cref="ListHostsAsync"/>
    /// 的 deadline 補上，逾時就明確報錯並建議縮小網段，**不回傳半套結果**（半套清單會讓人以為
    /// 那就是該網段的全部主機，違反本專案「沒查 ≠ 沒事」的誠實申報原則）。
    /// </summary>
    internal const int InteractiveTotalBudgetSeconds = 90;

    private readonly NetiqOptionsStore? _netiqOptionsStore;
    private readonly NetiqOptions? _optionsOverride;
    private readonly HttpMessageHandler? _handler;
    private readonly int _totalBudgetSeconds = InteractiveTotalBudgetSeconds;

    public SentinelRestDirectoryClient(NetiqOptionsStore netiqOptionsStore)
    {
        _netiqOptionsStore = netiqOptionsStore;
    }

    /// <summary>
    /// 測試用建構子：直接帶入節流參數與 <see cref="HttpMessageHandler"/>，跳過
    /// <see cref="NetiqOptionsStore"/>（它背後是真實 DB，單元測試不需要為了幾個節流欄位
    /// 另外接一份資料庫——同 <see cref="SentinelClient"/> 自己的 handler 測試縫一樣的理由）。
    /// </summary>
    /// <param name="totalBudgetSeconds">覆寫 <see cref="InteractiveTotalBudgetSeconds"/>——
    /// 驗證「總預算會中止掃描」的測試不該真的等 90 秒。</param>
    public SentinelRestDirectoryClient(NetiqOptions options, HttpMessageHandler handler, int? totalBudgetSeconds = null)
    {
        _optionsOverride = options;
        _handler = handler;
        if (totalBudgetSeconds is > 0) _totalBudgetSeconds = totalBudgetSeconds.Value;
    }

    public async Task<NetiqDiscoveryResult> ListHostsAsync(
        SentinelServer server, string subnetPrefix, CancellationToken ct,
        IReadOnlyCollection<string>? knownIps = null,
        Action<string, int>? onProgress = null,
        int? totalBudgetSecondsOverride = null,
        ScanGranularity granularity = ScanGranularity.Slash24,
        int concurrency = 1)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new NetiqDiscoveryException($"Sentinel「{server.Name}」未設定 BaseUrl。");

        string normalizedPrefix;
        IReadOnlyList<ScanSegment> segments;
        try
        {
            normalizedPrefix = SentinelQueryBuilder.NormalizeSubnetPrefix(subnetPrefix);
            segments = SentinelQueryBuilder.ExpandToSegments(subnetPrefix, granularity);
            if (segments.Count > MaxSegments)
            {
                throw new NetiqDiscoveryException(
                    $"網段範圍過大（展開後共 {segments.Count} 個分段，超過單次掃描上限 {MaxSegments} 個），請縮小輸入範圍或改用較粗的掃描粒度。");
            }
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

        var budgetSeconds = totalBudgetSecondsOverride is > 0
            ? totalBudgetSecondsOverride.Value
            : _totalBudgetSeconds;

        // 整趟掃描的總預算（見 InteractiveTotalBudgetSeconds）——分頁迴圈本身不受單次逾時約束，
        // 沒有這道 deadline 就沒有任何東西能保證互動操作會在可接受的時間內結束
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        var scanCt = budgetCts.Token;

        var effectiveConcurrency = Math.Clamp(concurrency, 1, MaxScanConcurrency);
        var poolSize = Math.Max(1, Math.Min(effectiveConcurrency, segments.Count));

        var clients = new List<SentinelClient>(poolSize);
        for (var i = 0; i < poolSize; i++)
        {
            clients.Add(new SentinelClient(server, interactiveOptions, _handler));
        }
        var clientPool = new ConcurrentBag<SentinelClient>(clients);

        try
        {
            var now = DateTimeOffset.UtcNow;

            // 重掃時已登錄主機在 server 端就被排除（§3.3）——沒有新主機時 found=0，
            // 一次查詢就結束。它們不需要被「重新發現」，呼叫端自己合成回結果。
            var alreadyKnown = NormalizeKnownIps(knownIps);
            var warnings = new List<string>();

            // ESM 事件來源目錄（§五）：開關預設關，開著才試。成功就直接回——
            // 它是「已註冊主機」而不是「窗口內講過話的主機」，涵蓋語意嚴格較好。
            // 失敗（無權限／格式不符）落到下面的事件掃描，並把原因講出來。
            if (server.UseEsmDirectory)
            {
                var esm = await TryEsmDirectoryAsync(clients[0], normalizedPrefix, alreadyKnown, warnings, scanCt);
                if (esm != null) return esm;
            }

            var segmentOutcomes = new SegmentOutcome[segments.Count];
            for (var i = 0; i < segments.Count; i++)
            {
                segmentOutcomes[i] = new SegmentOutcome();
            }

            var indexedSegments = segments.Select((seg, idx) => (Segment: seg, Index: idx)).ToList();
            var completedSegmentsCount = 0;
            var runningDiscoveredHostsCount = 0;
            var budgetExceeded = false;
            var isSingleSegment = segments.Count == 1;

            await Parallel.ForEachAsync(indexedSegments, new ParallelOptions
            {
                MaxDegreeOfParallelism = poolSize,
                CancellationToken = ct
            }, async (item, itemCt) =>
            {
                var (segment, index) = item;

                if (scanCt.IsCancellationRequested)
                {
                    if (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        if (isSingleSegment)
                        {
                            throw new NetiqDiscoveryException(BudgetExceededMessage(normalizedPrefix, budgetSeconds));
                        }
                        budgetExceeded = true;
                        return;
                    }
                    ct.ThrowIfCancellationRequested();
                }

                if (!clientPool.TryTake(out var client))
                    throw new InvalidOperationException("Sentinel client pool 耗盡（不應發生：並行度已受池大小節制）。");

                try
                {
                    var scan = new ScanState(alreadyKnown);

                    // 警告直接寫進這一段的 outcome（每段只有一條執行緒碰它，不需要鎖）。
                    // 先寫區域清單、跑完才搬進 outcome 的話，段中途因預算用盡被中斷時，
                    // 已經產生的涵蓋警告（如「NOT 排除語法未生效」）會連同清單一起消失——
                    // 那正是最需要讓管理者看到的診斷。
                    var outcome = segmentOutcomes[index];

                    if (isSingleSegment)
                    {
                        onProgress?.Invoke("主掃描中", 0);
                    }

                    // ── 主掃描：窄化頻道、固定 24h 窗口、觸頂進殘差輪 ──────────────────
                    var mainOutcome = await RunResidualRoundsAsync(
                        client, scanCt,
                        exclude => SentinelQueryBuilder.BuildSubnetProbeFilter(segment, exclude, server.Os),
                        now.AddHours(-WindowHours), now,
                        CoverageTargetResults, MaxResidualRounds, scan, segment.Prefix, segment.Ips, outcome.Warnings, $"主掃描（{segment.Label}）");

                    if (isSingleSegment)
                    {
                        onProgress?.Invoke("主掃描完成", scan.DiscoveredHosts().Count);
                        onProgress?.Invoke("補充掃描中", scan.DiscoveredHosts().Count);
                    }

                    // ── 補充掃描：全事件短窗，撈主掃描漏掉的 Security-only 主機 ──────────
                    // 排除主掃描已見是關鍵（§3.2）：不排除的話取回的絕大多數是已知主機的
                    // Security 洪流，純浪費；排除後殘差極小，短窗的涵蓋承諾才在 cap 內成立。
                    var supplementOutcome = await RunResidualRoundsAsync(
                        client, scanCt,
                        exclude => SentinelQueryBuilder.BuildSubnetDiscoveryFilter(segment, exclude),
                        now.AddMinutes(-SupplementWindowMinutes), now,
                        SupplementMaxResults, SupplementResidualRounds, scan, segment.Prefix, segment.Ips, outcome.Warnings, $"補充掃描（{segment.Label}）",
                        // 主掃描發現的主機超過排除上限時，退回無排除照掃（§3.2）——跳過補充掃描
                        // 等於把「撈 Security-only 漏網主機」整段棄守，掃了至少還有機會補到一些
                        fallbackToUnexcludedFirstRound: true);

                    outcome.Incomplete = !mainOutcome.Complete || !supplementOutcome.Complete;

                    // 主掃描 0 台但補充掃描有台數＝這個環境的 collector 可能不轉送 System/Application，
                    // 窄化 filter 因此失效（規劃 §3.8 的已知風險）。說出來讓人去查，不要靜靜地
                    // 每次都靠補充掃描的短窗硬撐——那樣涵蓋率會長期偏低而沒有人知道原因。
                    if (mainOutcome.NewHosts == 0 && supplementOutcome.NewHosts > 0)
                    {
                        outcome.Warnings.Add(
                            $"主掃描（{segment.Label}）（System/Application 頻道）沒有找到任何主機，全部由補充掃描的短窗撈到——" +
                            "這台 Sentinel 的 collector 可能不轉送這兩個頻道，掃描涵蓋率會因此偏低。" +
                            "請至「診斷」分頁執行一次診斷確認頻道覆蓋情形。");
                    }

                    outcome.Hosts.AddRange(scan.DiscoveredHosts());
                    outcome.Completed = true;

                    if (!isSingleSegment)
                    {
                        var completed = Interlocked.Increment(ref completedSegmentsCount);
                        var totalDiscovered = Interlocked.Add(ref runningDiscoveredHostsCount, outcome.Hosts.Count);
                        onProgress?.Invoke($"掃描中 {completed}/{segments.Count}（{segment.Label}）", totalDiscovered);
                    }
                }
                catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    if (isSingleSegment)
                    {
                        throw new NetiqDiscoveryException(BudgetExceededMessage(normalizedPrefix, budgetSeconds));
                    }
                    budgetExceeded = true;
                }
                catch (SentinelClientException ex) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    if (isSingleSegment)
                    {
                        throw new NetiqDiscoveryException(BudgetExceededMessage(normalizedPrefix, budgetSeconds), ex);
                    }
                    budgetExceeded = true;
                }
                finally
                {
                    clientPool.Add(client);
                }
            });
            var allHosts = new List<NetiqDiscoveredHost>();
            var incompleteSegments = new List<string>();

            for (var i = 0; i < segments.Count; i++)
            {
                var outcome = segmentOutcomes[i];

                // 警告不分完成與否都要留：段中途被預算中斷，它先前產生的涵蓋警告仍然成立。
                warnings.AddRange(outcome.Warnings);

                // 主機與「掃了但沒掃乾淨」只採計跑完的段——沒跑完的段會被列進下面的
                // 「未掃描的網段」，同時又貢獻半套主機清單會自相矛盾。
                if (!outcome.Completed) continue;

                allHosts.AddRange(outcome.Hosts);
                if (outcome.Incomplete)
                {
                    incompleteSegments.Add(segments[i].Label);
                }
            }

            if (budgetExceeded)
            {
                var completedCount = segmentOutcomes.Count(o => o.Completed);
                var unscanned = segments
                    .Where((s, idx) => !segmentOutcomes[idx].Completed)
                    .Select(s => s.Label)
                    .ToList();
                warnings.Add(
                    $"掃描時間用盡，已完成 {completedCount}/{segments.Count} 段，" +
                    $"未掃描的網段：{FormatSegmentList(unscanned)}。");
            }

            if (allHosts.Count == 0 && warnings.Count == 0)
            {
                return new NetiqDiscoveryResult
                {
                    CoverageNote = alreadyKnown.Count > 0
                        ? (isSingleSegment
                            ? $"「{normalizedPrefix}.*」近 24 小時沒有已登錄主機以外的事件回報（已登錄 {alreadyKnown.Count} 台未重查）。"
                            : $"「{normalizedPrefix}.*」（共掃 {segments.Count} 個子網段）近 24 小時沒有已登錄主機以外的事件回報（已登錄 {alreadyKnown.Count} 台未重查）。")
                        : (isSingleSegment
                            ? $"「{normalizedPrefix}.*」近 24 小時無任何事件回報，請確認網段是否正確，或稍後再試。"
                            : $"「{normalizedPrefix}.*」（共掃 {segments.Count} 個子網段）近 24 小時無任何事件回報，請確認網段是否正確，或稍後再試。")
                };
            }

            return new NetiqDiscoveryResult
            {
                Hosts = allHosts,
                CoverageNote = BuildCoverageNote(normalizedPrefix, allHosts.Count, alreadyKnown.Count, segments.Count, incompleteSegments),
                Warnings = warnings
            };
        }
        // 總預算用盡：只在「呼叫端自己沒取消」時才轉成可顯示的錯誤——呼叫端取消（管理員關掉分頁、
        // 瀏覽器斷線）是正常的取消語意，不該被包裝成一則假的失敗訊息往上丟。
        // SentinelClient 內部把逾時歸類為可重試例外，重試耗盡後會包成 SentinelClientException，
        // 因此這裡兩種例外都要看 budgetCts 有沒有被觸發。
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new NetiqDiscoveryException(BudgetExceededMessage(normalizedPrefix, budgetSeconds));
        }
        catch (SentinelClientException ex) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new NetiqDiscoveryException(BudgetExceededMessage(normalizedPrefix, budgetSeconds), ex);
        }
        catch (SentinelClientException ex)
        {
            throw new NetiqDiscoveryException($"掃描 Sentinel「{server.Name}」失敗：{ex.Message}", ex);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    /// <summary>ESM 事件來源目錄的端點路徑（一般 REST 資源，不走 event-search job 生命週期）</summary>
    internal const string EsmEventSourcePath = "/SentinelRESTServices/objects/eventsource";

    /// <summary>
    /// 嘗試以 ESM 目錄探索（§五）。成功回傳結果、失敗回 null 並在 <paramref name="warnings"/>
    /// 留下**可行動**的說明，由呼叫端落回事件掃描。
    ///
    /// <para><b>三種失敗都要吵</b>：開關是人開的，開著卻每次都走退路代表這個環境不支援——
    /// 訊息要能讓人決定「把開關關掉」或「把回應貼回來定案格式」。安靜地退路等於讓一個
    /// 壞掉的捷徑假裝在工作，下次還是有人踩。</para>
    /// </summary>
    private static async Task<NetiqDiscoveryResult?> TryEsmDirectoryAsync(
        SentinelClient client, string normalizedPrefix, IReadOnlyList<string> alreadyKnown,
        List<string> warnings, CancellationToken ct)
    {
        string body;
        try
        {
            body = await client.RawGetAsync(EsmEventSourcePath, ct);
        }
        catch (SentinelClientException ex)
        {
            warnings.Add(
                "已開啟「以 ESM 事件來源目錄探索」，但這個帳號讀不到目錄" +
                $"（{ex.Message}），本次已改用事件掃描。" +
                "請確認該帳號具備 ESM 唯讀權限，或關閉此開關。");
            return null;
        }

        var parsed = SentinelEsmDirectory.Parse(body);
        if (!parsed.Usable)
        {
            // **關鍵區分**：解析不出來 ≠ 這台 Sentinel 沒有主機。後者會讓管理員以為機房空了。
            warnings.Add(
                $"ESM 事件來源目錄的回應格式與預期不符（收到 {parsed.RawEntryCount} 筆條目，" +
                "無法解析出任何主機），本次已改用事件掃描。" +
                "請至「診斷」分頁執行診斷並回報步驟 6 的輸出，以便定案欄位對應。");
            return null;
        }

        var known = new HashSet<string>(alreadyKnown, StringComparer.OrdinalIgnoreCase);
        var prefix = normalizedPrefix + ".";
        var hosts = parsed.Sources
            .Where(s => s.IpAddress.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(s => !known.Contains(s.IpAddress))
            .Select(s => new NetiqDiscoveredHost(s.Name, s.IpAddress))
            .ToList();

        var knownNote = alreadyKnown.Count > 0 ? $"，另有已登錄的 {alreadyKnown.Count} 台未重新列出" : string.Empty;
        return new NetiqDiscoveryResult
        {
            Hosts = hosts,
            CoverageNote =
                $"來源：Sentinel 事件來源目錄（已註冊主機的完整清單，**含目前沒有事件回報的主機**）。" +
                $"目錄共 {parsed.RawEntryCount} 筆條目、可解析 {parsed.Sources.Count} 台，" +
                $"其中屬於「{normalizedPrefix}.*」的有 {hosts.Count} 台{knownNote}。",
            Warnings = warnings
        };
    }

    /// <summary>
    /// 殘差輪掃：同一個窗口反覆查詢，每輪把**已發現的主機**排除掉，直到某一輪沒有觸頂為止。
    ///
    /// <para>這是「取回筆數有上限」與「不可以靜默漏掉主機」兩個要求的交集解。
    /// 舊版遇到量太大是縮短窗口——那等於用時間換筆數，而被裁掉的時間裡安靜主機的
    /// 少數幾筆事件就這樣消失了，畫面上還看不出來。改成排除已見主機之後，
    /// 每一輪取回的事件全部來自「還沒見過的主機」，新主機數嚴格遞增、殘差嚴格遞減，
    /// 收斂是必然的；窗口自始至終不動。</para>
    ///
    /// <para>停止條件三選一：(1) 某輪未觸頂＝殘差已掃完，**完整**；
    /// (2) 輪數用盡；(3) 排除清單超過 <see cref="ExclusionClauseLimit"/>。
    /// 後兩者都會加一則 Warning——**知道可能漏，就一定要說**。</para>
    /// </summary>
    /// <param name="fallbackToUnexcludedFirstRound">排除清單超過子句上限時，第一輪改以
    /// **無排除**方式照掃（規劃 §3.2：補充掃描的職責是撈漏網主機，跳過它等於整段棄守；
    /// 無排除地掃仍可能在 cap 內撈到部分未知主機，比不掃好）。主掃描不用這個退路——
    /// 它自己就是排除清單的來源，第一輪本來就從空清單開始。</param>
    private async Task<RoundsOutcome> RunResidualRoundsAsync(
        SentinelClient client, CancellationToken ct,
        Func<IReadOnlyCollection<string>, string> filterFactory,
        DateTimeOffset start, DateTimeOffset end,
        int maxResults, int maxRounds, ScanState scan, string segmentPrefix, IReadOnlyList<string>? segmentIps,
        List<string> warnings, string stageName,
        bool fallbackToUnexcludedFirstRound = false)
    {
        var newHosts = 0;

        for (var round = 0; round < maxRounds; round++)
        {
            var exclusion = scan.SegmentExclusionOrNull(segmentPrefix, segmentIps);
            // 排除清單已超出子句上限：再送出去會被 Sentinel 整個拒絕。
            // 第一輪且允許退路 → 無排除照掃（取回會混入已知主機的事件，Absorb 自然去重）；
            // 其餘情況只能停在這裡——這是「可能漏」的情況之一，必須警告。
            if (exclusion is null)
            {
                if (round == 0 && fallbackToUnexcludedFirstRound)
                {
                    exclusion = Array.Empty<string>();
                }
                else
                {
                    warnings.Add(
                        $"{stageName}：已發現的主機數超過單次查詢的排除上限（{ExclusionClauseLimit} 台），" +
                        "無法繼續縮小查詢範圍，清單可能不完整——請改掃更小的網段（如把 /16 拆成數個 /24 分次掃）。");
                    return new RoundsOutcome(newHosts, Complete: false);
                }
            }

            // 該輪專用的提早停頁觀察者：連續 NoNewHostPagesBeforeStop 頁都沒有新主機就喊停。
            // 初始內容包含 scan 目前已見的全部 IP，避免把上一輪已知主機誤認為新主機。
            // 觀察者只判斷是否翻頁，不修改 scan 狀態，維持 Absorb 單一寫入點。
            var roundSeenIps = new HashSet<string>(scan.SeenIps, StringComparer.OrdinalIgnoreCase);
            var consecutiveNoNewPages = 0;

            var result = await client.SearchAsync(new SentinelSearchRequest(
                filterFactory(exclusion), start, end,
                new[] { SentinelFieldMap.HostIp, SentinelFieldMap.HostName },
                MaxResults: maxResults,
                PageObserver: pageEvents =>
                {
                    var hasNewHost = false;
                    foreach (var evt in pageEvents)
                    {
                        var ip = evt.Fields.GetValueOrDefault(SentinelFieldMap.HostIp);
                        if (!string.IsNullOrEmpty(ip) && roundSeenIps.Add(ip))
                        {
                            hasNewHost = true;
                        }
                    }

                    if (hasNewHost)
                        consecutiveNoNewPages = 0;
                    else
                        consecutiveNoNewPages++;

                    return consecutiveNoNewPages < NoNewHostPagesBeforeStop;
                }), ct);

            // NOT 子句在本環境尚未實測（probe 驗過 OR 50~100 子句、片語、前綴萬用字元，
            // 沒驗過 NOT）。萬一這個語法被忽略，每輪都會取回同一批已知主機、白白燒完輪數，
            // 而使用者只會看到「掃很久」不知道原因。這裡當場驗證：取回的事件若含
            // 已排除的 repip，就是排除沒生效——立刻停止輪掃並說明白。
            if (exclusion.Count > 0 && scan.ContainsExcluded(result.Events, exclusion))
            {
                warnings.Add(
                    $"{stageName}：查詢的排除語法（NOT）在這台 Sentinel 上未生效，已停止逐輪縮小範圍，" +
                    "清單可能不完整。請回報此訊息以便調整查詢寫法。");
                return new RoundsOutcome(newHosts, Complete: false);
            }

            newHosts += scan.Absorb(result.Events);

            if (!result.Truncated)
            {
                // 沒觸頂＝這一輪把殘差掃乾淨了，涵蓋保證成立
                return new RoundsOutcome(newHosts, Complete: true);
            }
        }

        warnings.Add(
            $"{stageName}：這個網段的主機數超出單次掃描能力（已掃 {maxRounds} 輪仍未取完），" +
            "清單可能不完整——請改掃更小的網段（如把 /16 拆成數個 /24 分次掃）。");
        return new RoundsOutcome(newHosts, Complete: false);
    }

    private static string BudgetExceededMessage(string normalizedPrefix, int budgetSeconds) =>
        $"掃描「{normalizedPrefix}.*」超過 {budgetSeconds} 秒仍未完成，已中止。" +
        "該網段的事件量可能過大——請改掃更小的網段（如把 /16 縮成 /24），或稍後離峰時段再試。" +
        "（未回傳部分結果：半套清單會被誤認為該網段的完整主機名單。）";

    private static string FormatSegmentList(IReadOnlyList<string> segments, int maxDisplay = 5)
    {
        if (segments.Count <= maxDisplay)
        {
            return string.Join("、", segments);
        }
        return $"{string.Join("、", segments.Take(maxDisplay))} 等 {segments.Count} 個";
    }

    private static string BuildCoverageNote(
        string normalizedPrefix, int hostCount, int knownCount, int totalSegments, IReadOnlyList<string> incompleteSegments)
    {
        var known = knownCount > 0 ? $"，另有已登錄的 {knownCount} 台未重新查詢" : string.Empty;
        var baseNote = $"掃描涵蓋「{normalizedPrefix}.*」近 24 小時內有 System/Application 事件、" +
                       $"或近 {SupplementWindowMinutes} 分鐘內有任何事件回報的主機，共 {hostCount} 台{known}。";

        if (totalSegments > 1)
        {
            var segNote = incompleteSegments.Count > 0
                ? $"共掃 {totalSegments} 個子網段，其中 {incompleteSegments.Count} 段未能完整收斂（{FormatSegmentList(incompleteSegments)}）。"
                : $"共掃 {totalSegments} 個子網段。";
            baseNote += segNote;
        }

        return baseNote + "（兩個窗口內都完全沒有事件回報的主機，事件掃描原理上看不到。）";
    }

    /// <summary>合法且去重的已登錄 IP；超過子句上限就整組放棄排除——
    /// 退回一般掃描只是比較慢，送出超長 filter 則是整趟失敗。</summary>
    private static IReadOnlyList<string> NormalizeKnownIps(IReadOnlyCollection<string>? knownIps)
    {
        if (knownIps is null || knownIps.Count == 0) return Array.Empty<string>();

        var normalized = knownIps
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 分段掃描後排除清單改依 /24 網段過濾（單段最多 254 台），此處 > ExclusionClauseLimit
        // 的整組放棄分支實務上已幾乎不會觸發，保留作為全域輸入驗證的防禦性第二道閘門。
        return normalized.Count > ExclusionClauseLimit ? Array.Empty<string>() : normalized;
    }

    /// <summary>單一分段掃描的產出（以分段索引固定收集，確保合併決定性）</summary>
    private sealed class SegmentOutcome
    {
        public bool Completed { get; set; }
        public bool Incomplete { get; set; }
        public List<NetiqDiscoveredHost> Hosts { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>單輪掃描的結果：這一段新發現幾台、以及殘差是否已掃乾淨</summary>
    private sealed record RoundsOutcome(int NewHosts, bool Complete);

    /// <summary>
    /// 一次探索過程中的累積狀態：已見過哪些 IP（排除清單的來源）、各 IP 的候選主機名。
    /// 跨主掃描與補充掃描共用同一份——補充掃描要排除主掃描已見的主機，
    /// 兩段的結果也要聯集，都靠這裡。
    /// </summary>
    private sealed class ScanState
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _nameCandidates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次掃描**新發現**的 IP，依發現順序。刻意不從 <c>_seen</c> 推導——
        /// HashSet 的列舉順序不是契約，靠它「跳過前 N 筆」在雜湊重整後就會錯。</summary>
        private readonly List<string> _discovered = new();

        public ScanState(IReadOnlyList<string> alreadyKnown)
        {
            foreach (var ip in alreadyKnown) _seen.Add(ip);
        }

        /// <summary>目前已見過的全部 IP（唯讀檢視，供提早停頁觀察者判斷是否為新主機）</summary>
        public IReadOnlySet<string> SeenIps => _seen;

        /// <summary>
        /// 取得**這一段實際查詢範圍內**已見的 IP 清單（排除清單的來源）；
        /// 若超過子句上限則回傳 null，呼叫端據此停止並警告。
        ///
        /// <paramref name="segmentIps"/> 有值（/26 細粒度）時只取該 64 個位址中已見的，
        /// **不是整個 /24 已見的全部**——後者會讓 /26 段送出 64 個位址子句配上最多 254 個
        /// 排除子句（其中 190 個根本不在查詢範圍內），把細粒度想省下來的子句預算再灌爆一次。
        /// </summary>
        public IReadOnlyCollection<string>? SegmentExclusionOrNull(
            string segmentPrefix, IReadOnlyList<string>? segmentIps)
        {
            List<string> seenInSegment;
            if (segmentIps != null)
            {
                var scope = new HashSet<string>(segmentIps, StringComparer.OrdinalIgnoreCase);
                seenInSegment = _seen.Where(scope.Contains).ToList();
            }
            else
            {
                var prefixWithDot = segmentPrefix.EndsWith('.') ? segmentPrefix : segmentPrefix + ".";
                seenInSegment = _seen
                    .Where(ip => ip.StartsWith(prefixWithDot, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            return seenInSegment.Count > ExclusionClauseLimit ? null : seenInSegment;
        }

        /// <summary>取回的事件裡有沒有「本輪應該已被排除」的主機——有就代表 NOT 沒生效</summary>
        public bool ContainsExcluded(IReadOnlyList<SentinelEvent> events, IReadOnlyCollection<string> exclusion)
        {
            if (exclusion.Count == 0) return false;
            var exclusionSet = exclusion as HashSet<string> ?? new HashSet<string>(exclusion, StringComparer.OrdinalIgnoreCase);
            return events.Any(e =>
            {
                var ip = e.Fields.GetValueOrDefault(SentinelFieldMap.HostIp);
                return !string.IsNullOrEmpty(ip) && exclusionSet.Contains(ip);
            });
        }

        /// <summary>吸收一輪的事件，回傳這一輪新發現幾台主機</summary>
        public int Absorb(IReadOnlyList<SentinelEvent> events)
        {
            var added = 0;
            foreach (var evt in events)
            {
                var ip = evt.Fields.GetValueOrDefault(SentinelFieldMap.HostIp);
                if (string.IsNullOrEmpty(ip)) continue;

                if (_seen.Add(ip))
                {
                    _discovered.Add(ip);
                    added++;
                }

                var name = evt.Fields.GetValueOrDefault(SentinelFieldMap.HostName);
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!_nameCandidates.TryGetValue(ip, out var names))
                {
                    names = new List<string>();
                    _nameCandidates[ip] = names;
                }
                names.Add(name);
            }
            return added;
        }

        /// <summary>
        /// 本次掃描**新發現**的主機（不含建構時帶入的已登錄主機——那些由呼叫端合成，
        /// 名稱以主機清單為準比事件裡的 sn 更可靠）。
        /// 顯示名稱取該 IP 出現最多次的 sn 值：單一主機的事件不會每筆 sn 都不同，
        /// 取眾數比取第一筆更能防偶發的欄位缺席或雜訊值。
        /// </summary>
        public List<NetiqDiscoveredHost> DiscoveredHosts() =>
            _discovered
                .Select(ip => new NetiqDiscoveredHost(PickMostCommonName(ip) ?? ip, ip))
                .ToList();

        private string? PickMostCommonName(string ip) =>
            _nameCandidates.TryGetValue(ip, out var names)
                ? names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(g => g.Count())
                       .Select(g => g.Key)
                       .FirstOrDefault()
                : null;
    }

}
