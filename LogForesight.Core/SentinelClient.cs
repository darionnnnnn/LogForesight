using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using NLog;
using Polly;
using Polly.Retry;

namespace LogForesight.Core;

/// <summary>
/// event-search job 的狀態（Sentinel REST API 文件定義，見 docs/NETIQ-API-REFERENCE.md §3）。
/// 數值與原廠文件的 <c>status</c> 欄位一一對應。
/// </summary>
public enum SentinelJobState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Unavailable = 4,
    Canceled = 5,
    AccessDenied = 6
}

/// <summary>單筆事件的投影結果：欄位名（Sentinel schema 短名）→ 值。欄位對應交由呼叫端解讀
/// （docs/NETIQ-API-REFERENCE.md §4，已由三輪 probe 實測定案，見 <see cref="SentinelFieldMap"/>）</summary>
public sealed record SentinelEvent(IReadOnlyDictionary<string, string> Fields)
{
    private static readonly IReadOnlyDictionary<string, SentinelFieldShape> EmptyShapes =
        new Dictionary<string, SentinelFieldShape>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, SentinelFieldShape> Shapes { get; init; } = EmptyShapes;
}

/// <summary>Optional per-client hard limits; absent means the established client behavior is unchanged.</summary>
public sealed record SentinelClientReadLimits(int MaxHttpRequests, int MaxResponseBytes)
{
    public void Validate()
    {
        if (MaxHttpRequests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(MaxHttpRequests));
        if (MaxResponseBytes is < 1 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
    }
}

internal sealed record SentinelFieldShape(string JsonKind, int? ValueLength, bool? ParsesAsTimestamp, bool? HasExplicitOffset);

/// <summary>建立一個 event-search job 的查詢條件</summary>
public sealed record SentinelSearchRequest(
    string Filter,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string>? Fields = null,
    int? PageSize = null,
    int? MaxResults = null,
    string Type = "USER",
    /// <summary>每取回一頁就呼叫一次，回傳 false 代表呼叫端已取得所需資訊、可停止翻頁。
    /// 用於主機探索：只需要相異主機 IP，不必把事件翻完。null＝不提早停止（預設）。
    /// 提早停止會讓取回數小於 Found，因此結果的 Truncated 為 true——語意正確：
    /// 沒翻完就是可能還有沒看到的東西。</summary>
    Func<IReadOnlyList<SentinelEvent>, bool>? PageObserver = null,
    /// <summary>true＝串流模式（回饋三十四輪 A3）：取回的分頁只交給 <see cref="PageObserver"/>，
    /// **不累積進結果的 Events**。單一 job 上限 10 萬筆、最多 12 個 job 同時在跑，
    /// 全量緩衝是排程執行占用 22GB 的主因之一。必須搭配 PageObserver 使用。
    /// 預設 false＝維持既有全量行為（診斷探測等呼叫端要看完整結果）。</summary>
    bool StreamOnly = false,
    /// <summary>true＝解析時不做欄位過濾，保留回應的全部屬性（回饋三十五輪體檢輪）：
    /// **僅供 NetIQ probe 診斷使用**——probe 的價值正是逐筆印出全欄位、算欄位聯集來發現
    /// 未知欄位（obssvcname、estz 都是這樣找到的），過濾後它再也看不到投影清單以外的東西。
    /// 分析主線一律走預設 false（欄位過濾，見 SentinelFieldMap.ParseKeepFields）。</summary>
    bool RawFields = false,
    bool ShapeOnly = false,
    int? MaxPages = null,
    int? MaxShapeFieldKeys = null);

public sealed class SentinelSearchResult
{
    public required IReadOnlyList<SentinelEvent> Events { get; init; }

    /// <summary>此 job 符合條件的總事件數（Sentinel 回報的 <c>found</c>，可能大於實際取回筆數）</summary>
    public required int Found { get; init; }

    public required SentinelJobState State { get; init; }

    private readonly int? _retrieved;

    /// <summary>本次實際取回的事件筆數。串流模式下 <see cref="Events"/> 是空的，
    /// 截斷判定仍必須以實際取回筆數為準，故獨立成一個欄位；
    /// 未指定時退回 <c>Events.Count</c>——非串流模式下兩者本來就相同。</summary>
    public int Retrieved
    {
        get => _retrieved ?? Events.Count;
        init => _retrieved = value;
    }

    /// <summary>實際取回筆數少於 Found——受 max-results 上限或分頁提前中止影響，
    /// 呼叫端應比照 DataIncomplete 的基準排除邏輯處理（docs/archive/HISTORY.md）</summary>
    public bool Truncated => Retrieved < Found;

    /// <summary>Sentinel 回報有資料（Found &gt; 0）卻一筆都沒取回、且並非 max-results 上限為 0——
    /// 屬於結果頁讀不到／解析不出（avail 為 0、首頁為空、非陣列），不是真正的筆數截斷。
    /// 呼叫端不得用二分重查或高流量旗標處理（重查只會得到同樣的空結果）。</summary>
    public bool Unreadable { get; init; }
}

/// <summary>連線／查詢過程中的錯誤。訊息不含密碼，可直接顯示給操作者或寫入 log。</summary>
public class SentinelClientException : Exception
{
    public SentinelClientException(string message, Exception? inner = null, string safeCode = "query-failed") : base(message, inner) => SafeCode = safeCode;
    public string SafeCode { get; }

    /// <summary>位址未設定或格式不正確：訊息只含使用者自己填的位址與修正方式，可直接顯示給操作者</summary>
    public const string InvalidBaseUrlCode = "invalid-base-url";

    /// <summary>伺服器憑證被拒（指紋不符或不被信任）；訊息只含本系統的說明與伺服器出示的指紋，可直接顯示</summary>
    public const string CertificateRejectedCode = "certificate-rejected";
}

/// <summary>
/// <see cref="SentinelClient"/> 對外唯一用到的能力（docs/archive/FEEDBACK-12-PLAN.md §3.8-2）：抽出來
/// 讓 <c>NetiqPipelineService</c> 的 pipeline 測試能注入假搜尋結果，不必真的連 Sentinel。
/// <see cref="SentinelClient"/> 是唯一實作；簽章原封不動照搬既有 <c>SearchAsync</c>。
/// </summary>
public interface ISentinelSearchClient : IAsyncDisposable
{
    Task<SentinelSearchResult> SearchAsync(SentinelSearchRequest request, CancellationToken ct = default);
}

/// <summary>
/// Sentinel REST API 封裝：SAML token 認證生命週期＋event-search job 生命週期
/// （docs/NETIQ-API-REFERENCE.md §2、§3）。單一職責——只懂 REST 協定，不懂 watchlist／欄位對應等業務語意。
///
/// 單一 instance＝單一併發佇列（同 <see cref="AIService"/> 慣例）：同一時間只有一個 job 在跑，
/// 跨 Sentinel 平行由呼叫端各自建立一個 client 實例達成。token 整個 instance 生命週期內重用，
/// <see cref="DisposeAsync"/> 時登出。
///
/// 建立 job → 輪詢至終態 → 逐頁取回結果 → 刪除 job 的完整生命週期（<see cref="SearchAsync"/>）。
/// 任何中途失敗（含呼叫端取消）都保證嘗試刪除已建立的 job（docs/NETIQ-API-REFERENCE.md §6 #7「job 用完即刪」）。
///
/// 依關注點分 4 個 partial 檔：本檔（共用狀態／建構／連線資源）、
/// <c>SentinelClient.Auth.cs</c>（token 認證與已認證請求）、
/// <c>SentinelClient.Search.cs</c>（event-search job 生命週期）、
/// <c>SentinelClient.Paging.cs</c>（查詢結果分頁與解析）。方法共享同一組 HTTP／session 狀態，
/// 不拆成獨立可注入的類別。
/// </summary>
public sealed partial class SentinelClient : ISentinelSearchClient
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SentinelServer _server;
    private readonly Uri _baseUri;
    private readonly NetiqOptions _settings;
    private readonly HttpClient _http;
    private readonly SentinelClientReadLimits? _readLimits;
    private int _httpRequestCount;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly ResiliencePipeline _retryPipeline;
    private readonly ResiliencePipeline _createJobPipeline;
    private string? _token;
    private bool _disposed;

    public SentinelClient(SentinelServer server, NetiqOptions settings, HttpMessageHandler? handler = null,
        SentinelClientReadLimits? readLimits = null)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new SentinelClientException($"Sentinel「{server.Name}」未設定 BaseUrl。", safeCode: SentinelClientException.InvalidBaseUrlCode);

        // 連線位址格式在這裡就擋下，而不是等送出請求時才炸：BaseUrl 是自由文字欄位，
        // 少打 https://（「sentinel.corp.local:8443」）之類的輸入極常見，而 HttpClient 對它們擲的是
        // InvalidOperationException／NotSupportedException——**不是** SentinelClientException，
        // 會穿透所有呼叫端「連線失敗就顯示訊息」的 catch，變成 500 或中斷整趟 probe。
        // 統一轉成本類別的例外，訊息直接可顯示給操作者。
        if (!Uri.TryCreate(server.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(parsed.Host))
        {
            throw new SentinelClientException(
                $"Sentinel「{server.Name}」的連線位址格式不正確：「{server.BaseUrl}」。" +
                "請填寫完整網址，含 https:// 或 http://（例如 https://sentinel.corp.local:8443）。",
                safeCode: SentinelClientException.InvalidBaseUrlCode);
        }

        _server = server;
        _baseUri = parsed;
        _settings = settings;
        readLimits?.Validate();
        _readLimits = readLimits;

        var ownsHandler = handler == null;
        if (!ServerCertificateTrust.TryNormalize(settings.TrustedCertificateSha256, out _))
            throw new SentinelClientException("Sentinel 信任的憑證指紋格式不正確，應為 64 個十六進位字元的 SHA-256。",
                safeCode: "invalid-certificate-pin");
        // 注入的 handler（測試）不經憑證判定；自建時一律掛上，探測才拿得到伺服器出示的指紋
        _certificateTrust = handler == null
            ? new ServerCertificateTrust(settings.TrustedCertificateSha256, settings.AllowInvalidCertificates)
            : null;
        var actualHandler = handler ?? OutboundHttp.CreateHandler(TimeSpan.FromMinutes(5), _certificateTrust!.Validate);
        if (_readLimits is not null && actualHandler is SocketsHttpHandler boundedHandler)
            boundedHandler.AllowAutoRedirect = false;
        _http = new HttpClient(actualHandler, disposeHandler: ownsHandler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(settings.TimeoutSeconds, 1)),
            // 整包讀取的回應上限：異常回應或代理錯誤頁不能一次吃掉站台記憶體（探測模式另有更小的有界讀取）
            MaxResponseContentBufferSize = MaxResponseBytes
        };

        // AllowInvalidCertificates 是顯式逃生門而非靜默放行（AppSettings 註解的承諾）：
        // 每個 client 建立時記一筆 Info（不是每次連線——那會洗版），讓 log 看得出這台是誰在裸奔
        if (ownsHandler && settings.AllowInvalidCertificates && _certificateTrust!.PinnedSha256 is null)
        {
            if (_readLimits is null)
                Log.Info("[{Server}] AllowInvalidCertificates 已啟用，本連線不驗證 Sentinel 憑證（自簽憑證環境的逃生門，正式環境建議改安裝 CA 憑證）", server.Name);
            else
                Log.Warn("Bounded metadata probe uses the configured certificate-validation policy.");
        }

        // 重試：連線失敗／逾時／5xx（含 503）皆重試，指數退避；4xx 不重試（打錯就是打錯，
        // 401/403 另有 SendAuthenticatedAsync 的一次性重新認證重放機制，不走這裡）。
        //
        // RetryCount <= 0 時**完全不加重試策略**，而不是把它夾到 1：Polly 的 RetryStrategyOptions
        // 要求 MaxRetryAttempts >= 1，傳 0 會在建構子就擲 ValidationException（「NetIQ 維護」頁的
        // 失敗重試次數 min=0，管理員填 0 是合理的「不要重試」，卻會讓每一次 SentinelClient 建構
        // 直接爆掉）；夾到 1 則是靜默違背設定值。不加策略才真的等於「不重試」。
        _retryPipeline = BuildRetryPipeline(settings, retryTimeouts: true);
        // 建立搜尋工作（POST）不是冪等的：逾時代表伺服器可能已經建了工作，重送會多一個殘留工作佔用名額。
        // 只重試確定沒建成的（連線失敗、5xx），逾時不重送
        _createJobPipeline = BuildRetryPipeline(settings, retryTimeouts: false);
    }

    private ResiliencePipeline BuildRetryPipeline(NetiqOptions settings, bool retryTimeouts)
    {
        var pipelineBuilder = new ResiliencePipelineBuilder();
        if (settings.RetryCount <= 0) return pipelineBuilder.Build();
        // 憑證被拒（SecureConnectionError）重試也不會變，不重試
        var predicate = new PredicateBuilder()
            .Handle<HttpRequestException>(ex => ex.HttpRequestError != HttpRequestError.SecureConnectionError)
            .Handle<SentinelTransientException>();
        if (retryTimeouts) predicate = predicate.Handle<TaskCanceledException>();
        pipelineBuilder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = settings.RetryCount,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            // 多個主機分批同時失敗時錯開重試時間
            UseJitter = true,
            ShouldHandle = predicate,
            OnRetry = args =>
            {
                if (_readLimits is null)
                    Log.Warn(args.Outcome.Exception, "[{Server}] Sentinel 呼叫失敗，第 {Attempt}/{Total} 次重試",
                        _server.Name, args.AttemptNumber + 1, settings.RetryCount);
                else
                    Log.Warn("Bounded metadata probe retry {Attempt}/{Total}; details withheld",
                        args.AttemptNumber + 1, settings.RetryCount);
                return default;
            }
        });
        return pipelineBuilder.Build();
    }

    // Sentinel 常見以自簽憑證部署；啟用略過驗證時的 WARN 由建構子負責（每 client 一筆，不在這裡重複）
    /// <summary>整包讀取的回應上限（Sentinel 一頁事件數百 KB 到數 MB）</summary>
    internal const long MaxResponseBytes = 128L * 1024 * 1024;

    private readonly ServerCertificateTrust? _certificateTrust;

    /// <summary>最近一次連線時 Sentinel 出示的憑證指紋（SHA-256；http 連線或還沒連過為 null）</summary>
    public string? PresentedCertificateSha256 => _certificateTrust?.LastPresentedSha256;

    /// <summary>憑證被拒時可直接顯示的原因；不是憑證問題回 null</summary>
    private string? CertificateRejection(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            ? _certificateTrust?.LastRejection
            : null;

    private string BaseUrl => _server.BaseUrl.TrimEnd('/');
    private string AuthTokensUrl => $"{BaseUrl}/SentinelAuthServices/auth/tokens";
    private string EventSearchCollectionUrl => $"{BaseUrl}/SentinelRESTServices/objects/event-search";

    private static string Truncate(string s, int max = 300) => TextTruncation.SafeCut(s, max, "…(截斷)");

    /// <summary>僅用於觸發 Polly 重試的內部標記例外，不對外流出</summary>
    private sealed class SentinelTransientException : Exception;

    public async ValueTask DisposeAsync()
    {
        if (_readLimits is not null)
        {
            using var boundedCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DisposeCoreAsync(boundedCleanup.Token);
            return;
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await DisposeCoreAsync(cleanup.Token);
    }

    public ValueTask DisposeAsync(CancellationToken cleanupToken) => DisposeCoreAsync(cleanupToken);

    private async ValueTask DisposeCoreAsync(CancellationToken cleanupToken)
    {
        if (_disposed) return;
        _disposed = true;

        if (_token != null)
        {
            try
            {
                await DeleteTokenAsync(_token, cleanupToken);
            }
            catch (Exception ex)
            {
                if (_readLimits is null) Log.Warn(ex, "[{Server}] 登出 Sentinel token 失敗（非致命）", _server.Name);
                else Log.Warn("Bounded metadata probe token cleanup failed; details withheld");
            }
        }

        _http.Dispose();
        _queue.Dispose();
    }
}
