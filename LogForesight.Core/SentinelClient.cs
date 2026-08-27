using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using NLog;
using Polly;
using Polly.Retry;

namespace LogForesight.Core;

/// <summary>
/// event-search job 的狀態（Sentinel REST API 文件定義，見 docs/NETIQ-API-REFERENCE.md §1.2）。
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
/// （docs/NETIQ-API-REFERENCE.md §3.3，已由三輪 probe 實測定案，見 <see cref="SentinelFieldMap"/>）</summary>
public sealed record SentinelEvent(IReadOnlyDictionary<string, string> Fields);

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
    Func<IReadOnlyList<SentinelEvent>, bool>? PageObserver = null);

public sealed class SentinelSearchResult
{
    public required IReadOnlyList<SentinelEvent> Events { get; init; }

    /// <summary>此 job 符合條件的總事件數（Sentinel 回報的 <c>found</c>，可能大於實際取回筆數）</summary>
    public required int Found { get; init; }

    public required SentinelJobState State { get; init; }

    /// <summary>實際取回筆數少於 Found——受 max-results 上限或分頁提前中止影響，
    /// 呼叫端應比照 DataIncomplete 的基準排除邏輯處理（docs/archive/HISTORY.md）</summary>
    public bool Truncated => Events.Count < Found;
}

/// <summary>連線／查詢過程中的錯誤。訊息不含密碼，可直接顯示給操作者或寫入 log。</summary>
public class SentinelClientException : Exception
{
    public SentinelClientException(string message, Exception? inner = null) : base(message, inner) { }
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
/// （docs/NETIQ-API-REFERENCE.md §1、§3.1）。單一職責——只懂 REST 協定，不懂 watchlist／欄位對應等業務語意。
///
/// 單一 instance＝單一併發佇列（同 <see cref="AIService"/> 慣例）：同一時間只有一個 job 在跑，
/// 跨 Sentinel 平行由呼叫端各自建立一個 client 實例達成。token 整個 instance 生命週期內重用，
/// <see cref="DisposeAsync"/> 時登出。
///
/// 建立 job → 輪詢至終態 → 逐頁取回結果 → 刪除 job 的完整生命週期（<see cref="SearchAsync"/>）。
/// 任何中途失敗（含呼叫端取消）都保證嘗試刪除已建立的 job（docs/NETIQ-API-REFERENCE.md §5「job 用完即刪」）。
///
/// 依關注點分 3 個 partial 檔：本檔（共用狀態／建構／連線資源）、
/// <c>SentinelClient.Auth.cs</c>（token 認證與已認證請求）、
/// <c>SentinelClient.Search.cs</c>（event-search job 生命週期）、
/// <c>SentinelClient.Paging.cs</c>（查詢結果分頁與解析）。方法共享同一組 HTTP／session 狀態，
/// 不拆成獨立可注入的類別。
/// </summary>
public sealed partial class SentinelClient : ISentinelSearchClient
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SentinelServer _server;
    private readonly NetiqOptions _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly ResiliencePipeline _retryPipeline;
    private string? _token;
    private bool _disposed;

    public SentinelClient(SentinelServer server, NetiqOptions settings, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new SentinelClientException($"Sentinel「{server.Name}」未設定 BaseUrl。");

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
                "請填寫完整網址，含 https:// 或 http://（例如 https://sentinel.corp.local:8443）。");
        }

        _server = server;
        _settings = settings;

        var ownsHandler = handler == null;
        var actualHandler = handler ?? CreateDefaultHandler(settings);
        _http = new HttpClient(actualHandler, disposeHandler: ownsHandler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(settings.TimeoutSeconds, 1))
        };

        // AllowInvalidCertificates 是顯式逃生門而非靜默放行（AppSettings 註解的承諾）：
        // 每個 client 建立時記一筆 WARN（不是每次連線——那會洗版），讓 log 看得出這台是誰在裸奔
        if (ownsHandler && settings.AllowInvalidCertificates)
        {
            Log.Warn("[{Server}] AllowInvalidCertificates 已啟用，本連線不驗證 Sentinel 憑證（自簽憑證環境的逃生門，正式環境建議改安裝 CA 憑證）", server.Name);
        }

        // 重試：連線失敗／逾時／5xx（含 503）皆重試，指數退避；4xx 不重試（打錯就是打錯，
        // 401/403 另有 SendAuthenticatedAsync 的一次性重新認證重放機制，不走這裡）。
        //
        // RetryCount <= 0 時**完全不加重試策略**，而不是把它夾到 1：Polly 的 RetryStrategyOptions
        // 要求 MaxRetryAttempts >= 1，傳 0 會在建構子就擲 ValidationException（「NetIQ 維護」頁的
        // 失敗重試次數 min=0，管理員填 0 是合理的「不要重試」，卻會讓每一次 SentinelClient 建構
        // 直接爆掉）；夾到 1 則是靜默違背設定值。不加策略才真的等於「不重試」。
        var pipelineBuilder = new ResiliencePipelineBuilder();
        if (settings.RetryCount > 0)
        {
            pipelineBuilder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = settings.RetryCount,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<SentinelTransientException>(),
                OnRetry = args =>
                {
                    Log.Warn(args.Outcome.Exception, "[{Server}] Sentinel 呼叫失敗，第 {Attempt}/{Total} 次重試",
                        _server.Name, args.AttemptNumber + 1, settings.RetryCount);
                    return default;
                }
            });
        }
        _retryPipeline = pipelineBuilder.Build();
    }

    private static HttpMessageHandler CreateDefaultHandler(NetiqOptions settings)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (settings.AllowInvalidCertificates)
        {
            // Sentinel 常見以自簽憑證部署；啟用時的 WARN 由建構子負責（每 client 一筆，不在這裡重複）
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }

    private string BaseUrl => _server.BaseUrl.TrimEnd('/');
    private string AuthTokensUrl => $"{BaseUrl}/SentinelAuthServices/auth/tokens";
    private string EventSearchCollectionUrl => $"{BaseUrl}/SentinelRESTServices/objects/event-search";

    private static string Truncate(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…(截斷)";

    /// <summary>僅用於觸發 Polly 重試的內部標記例外，不對外流出</summary>
    private sealed class SentinelTransientException : Exception;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_token != null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{AuthTokensUrl}/{Uri.EscapeDataString(_token)}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_server.Username}:{_server.Password}")));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await _http.SendAsync(req, cts.Token);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[{Server}] 登出 Sentinel token 失敗（非致命）", _server.Name);
            }
        }

        _http.Dispose();
        _queue.Dispose();
    }
}
