using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NLog;
using Polly;
using Polly.Retry;

namespace LogForesight;

/// <summary>
/// event-search job 的狀態（Sentinel REST API 文件定義，見 docs/NETIQ-API-PLAN.md §1.2）。
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
/// （docs/NETIQ-API-PLAN.md §3.3，待 --netiq-probe 定案）</summary>
public sealed record SentinelEvent(IReadOnlyDictionary<string, string> Fields);

/// <summary>建立一個 event-search job 的查詢條件</summary>
public sealed record SentinelSearchRequest(
    string Filter,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string>? Fields = null,
    int? PageSize = null,
    int? MaxResults = null,
    string Type = "USER");

public sealed class SentinelSearchResult
{
    public required IReadOnlyList<SentinelEvent> Events { get; init; }

    /// <summary>此 job 符合條件的總事件數（Sentinel 回報的 <c>found</c>，可能大於實際取回筆數）</summary>
    public required int Found { get; init; }

    public required SentinelJobState State { get; init; }

    /// <summary>實際取回筆數少於 Found——受 max-results 上限或分頁提前中止影響，
    /// 呼叫端應比照 DataIncomplete 的基準排除邏輯處理（docs/PLAN.md）</summary>
    public bool Truncated => Events.Count < Found;
}

/// <summary>連線／查詢過程中的錯誤。訊息不含密碼，可直接顯示給操作者或寫入 log。</summary>
public class SentinelClientException : Exception
{
    public SentinelClientException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ISentinelClient : IAsyncDisposable
{
    /// <summary>
    /// 建立 job → 輪詢至終態 → 逐頁取回結果 → 刪除 job 的完整生命週期。
    /// 任何中途失敗（含呼叫端取消）都保證嘗試刪除已建立的 job（docs/NETIQ-API-PLAN.md §5「job 用完即刪」）。
    /// </summary>
    Task<SentinelSearchResult> SearchAsync(SentinelSearchRequest request, CancellationToken ct = default);
}

/// <summary>
/// Sentinel REST API 封裝：SAML token 認證生命週期＋event-search job 生命週期
/// （docs/NETIQ-API-PLAN.md §1、§3.1）。單一職責——只懂 REST 協定，不懂 watchlist／欄位對應等業務語意。
///
/// 單一 instance＝單一併發佇列（同 <see cref="AIService"/> 慣例）：同一時間只有一個 job 在跑，
/// 跨 Sentinel 平行由呼叫端各自建立一個 client 實例達成。token 整個 instance 生命週期內重用，
/// <see cref="DisposeAsync"/> 時登出。
/// </summary>
public sealed class SentinelClient : ISentinelClient
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SentinelServer _server;
    private readonly NetIqSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly ResiliencePipeline _retryPipeline;
    private string? _token;
    private bool _disposed;

    public SentinelClient(SentinelServer server, NetIqSettings settings, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(server.BaseUrl))
            throw new SentinelClientException($"Sentinel「{server.Name}」未設定 BaseUrl。");

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
        // 401/403 另有 SendAuthenticatedAsync 的一次性重新認證重放機制，不走這裡）
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = Math.Max(settings.RetryCount, 0),
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
            })
            .Build();
    }

    private static HttpMessageHandler CreateDefaultHandler(NetIqSettings settings)
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

    public async Task<SentinelSearchResult> SearchAsync(SentinelSearchRequest request, CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        string? jobHref = null;
        try
        {
            await ThrottleAsync(ct);
            jobHref = await CreateJobAsync(request, ct);

            var status = await PollUntilTerminalAsync(jobHref, ct);
            if (status.State is SentinelJobState.Unavailable or SentinelJobState.Canceled or SentinelJobState.AccessDenied)
            {
                throw new SentinelClientException($"Sentinel「{_server.Name}」查詢工作結束於非成功狀態：{status.State}");
            }

            var events = await FetchAllPagesAsync(status, request, ct);
            return new SentinelSearchResult { Events = events, Found = status.Found, State = status.State };
        }
        finally
        {
            if (jobHref != null)
            {
                // 清理不受呼叫端取消影響——即使呼叫端放棄等待，殘留的 job 仍佔用 server 資源，
                // 用一個獨立、有限時的 token 確保「用完即刪」的承諾在取消路徑下仍然成立。
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await TryDeleteJobAsync(jobHref, cleanupCts.Token);
            }
            _queue.Release();
        }
    }

    // ── 認證 ─────────────────────────────────────────────────────────

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_token != null) return;
        await AuthenticateAsync(ct);
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_server.Username}:{_server.Password}"));

        HttpResponseMessage resp;
        try
        {
            resp = await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var attempt = new HttpRequestMessage(HttpMethod.Post, AuthTokensUrl);
                attempt.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                var r = await _http.SendAsync(attempt, innerCt);
                if (IsTransientStatus(r.StatusCode))
                {
                    r.Dispose();
                    throw new SentinelTransientException();
                }
                return r;
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SentinelTransientException)
        {
            throw new SentinelClientException($"連線 Sentinel「{_server.Name}」認證失敗（重試已耗盡）：{ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp);
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」認證失敗：HTTP {(int)resp.StatusCode}" +
                    (resp.StatusCode == HttpStatusCode.Unauthorized ? "（帳號或密碼錯誤）" : "") +
                    $"｜{Truncate(body)}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var token = TryExtractToken(json);
            if (string.IsNullOrWhiteSpace(token))
                throw new SentinelClientException($"Sentinel「{_server.Name}」認證回應未包含可辨識的 Token 欄位：{Truncate(json)}");

            _token = token;
            Log.Info("[{Server}] Sentinel 認證成功", _server.Name);
        }
    }

    /// <summary>原廠文件範例回應為 <c>{"Token":"…"}</c>（見 docs/NETIQ-API-PLAN.md §1.1），
    /// 大小寫容忍以防實際環境版本差異</summary>
    private static string? TryExtractToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("Token", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
            if (root.TryGetProperty("token", out var t2) && t2.ValueKind == JsonValueKind.String) return t2.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>認證過的請求：token 過期（401/403）時清快取重新認證一次並重放，仍失敗才放棄。</summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct, bool allowReauth = true)
    {
        await EnsureAuthenticatedAsync(ct);
        var token = _token!;

        HttpResponseMessage resp;
        try
        {
            resp = await _retryPipeline.ExecuteAsync(async innerCt =>
            {
                using var attempt = requestFactory();
                attempt.Headers.Authorization = new AuthenticationHeaderValue("X-SAML", token);
                var r = await _http.SendAsync(attempt, innerCt);
                if (IsTransientStatus(r.StatusCode))
                {
                    r.Dispose();
                    throw new SentinelTransientException();
                }
                return r;
            }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SentinelTransientException)
        {
            throw new SentinelClientException($"連線 Sentinel「{_server.Name}」失敗（重試已耗盡）：{ex.Message}", ex);
        }

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            resp.Dispose();
            if (!allowReauth)
                throw new SentinelClientException($"Sentinel「{_server.Name}」驗證被拒（重新認證後仍失敗），請確認帳密與權限是否有效。");

            Log.Info("[{Server}] token 可能已過期，重新認證後重放請求", _server.Name);
            _token = null;
            return await SendAuthenticatedAsync(requestFactory, ct, allowReauth: false);
        }

        return resp;
    }

    private static bool IsTransientStatus(HttpStatusCode code) =>
        code == HttpStatusCode.ServiceUnavailable || code == HttpStatusCode.RequestTimeout || (int)code >= 500;

    // ── Job 生命週期 ─────────────────────────────────────────────────

    private async Task<string> CreateJobAsync(SentinelSearchRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["filter"] = request.Filter,
            ["start"] = request.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["end"] = request.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["pgsize"] = request.PageSize ?? _settings.PageSize,
            ["max-results"] = request.MaxResults ?? _settings.MaxResultsPerJob,
            ["type"] = request.Type,
            ["init-user"] = _server.Username,
            ["InitiatingHostName"] = Environment.MachineName
        };
        if (request.Fields is { Count: > 0 })
        {
            body["fields"] = string.Join(",", request.Fields);
        }
        var json = body.ToJsonString();

        var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Post, EventSearchCollectionUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }, ct);

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await SafeReadBodyAsync(resp);
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」建立查詢工作失敗：HTTP {(int)resp.StatusCode}｜{Truncate(errBody)}");
            }

            if (resp.Headers.Location != null)
                return resp.Headers.Location.ToString();

            var raw = await resp.Content.ReadAsStringAsync(ct);
            var href = TryExtractHrefFromBody(raw);
            if (href == null)
            {
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」建立查詢工作成功，但回應中找不到工作位址" +
                    $"（Location 標頭與回應本文皆無 @href）。原始回應：{Truncate(raw)}");
            }
            return href;
        }
    }

    private static string? TryExtractHrefFromBody(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("@href", out var h1) && h1.ValueKind == JsonValueKind.String) return h1.GetString();
            if (root.TryGetProperty("href", out var h2) && h2.ValueKind == JsonValueKind.String) return h2.GetString();
            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("@href", out var mh1) && mh1.ValueKind == JsonValueKind.String) return mh1.GetString();
                if (meta.TryGetProperty("href", out var mh2) && mh2.ValueKind == JsonValueKind.String) return mh2.GetString();
            }
        }
        catch (JsonException)
        {
            // 交給呼叫端把原始內容顯示出來，這裡不吞例外訊息、只是判定「解不出來」
        }
        return null;
    }

    internal sealed record JobStatus(SentinelJobState State, int Found, int Avail, string? ResultsHref);

    private async Task<JobStatus> PollUntilTerminalAsync(string jobHref, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(_settings.TimeoutSeconds, 1));
        var delay = TimeSpan.FromMilliseconds(500);

        while (true)
        {
            await ThrottleAsync(ct);
            var status = await GetJobStatusAsync(jobHref, ct);

            if (status.State is SentinelJobState.Completed or SentinelJobState.CompletedWithErrors
                or SentinelJobState.Unavailable or SentinelJobState.Canceled or SentinelJobState.AccessDenied)
            {
                return status;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」查詢逾時（{_settings.TimeoutSeconds} 秒），工作狀態仍為 {status.State}");
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
        }
    }

    private async Task<JobStatus> GetJobStatusAsync(string jobHref, CancellationToken ct)
    {
        var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, jobHref), ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp);
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」查詢工作狀態失敗：HTTP {(int)resp.StatusCode}｜{Truncate(body)}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseJobStatus(json);
        }
    }

    internal static JobStatus ParseJobStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var state = SentinelJobState.Running; // 未知/缺席一律當作仍在跑，靠逾時收斂，不誤判成失敗
            if (root.TryGetProperty("status", out var s))
            {
                if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) && Enum.IsDefined(typeof(SentinelJobState), n))
                    state = (SentinelJobState)n;
                else if (s.ValueKind == JsonValueKind.String && Enum.TryParse<SentinelJobState>(s.GetString(), true, out var parsed))
                    state = parsed;
            }

            // ValueKind 先驗再取值：TryGetInt32 在非 Number 型別上會擲 InvalidOperationException
            // （不是 JsonException，下面的 catch 接不到），欄位型別與預期不符時寧可當 0 也不炸
            var found = ReadInt(root, "found");
            var avail = ReadInt(root, "avail");

            string? resultsHref = null;
            if (root.TryGetProperty("results", out var r))
            {
                resultsHref = r.ValueKind switch
                {
                    JsonValueKind.String => r.GetString(),
                    JsonValueKind.Object when r.TryGetProperty("@href", out var rh) && rh.ValueKind == JsonValueKind.String => rh.GetString(),
                    JsonValueKind.Object when r.TryGetProperty("href", out var rh2) && rh2.ValueKind == JsonValueKind.String => rh2.GetString(),
                    _ => null
                };
            }

            return new JobStatus(state, found, avail, resultsHref);
        }
        catch (JsonException ex)
        {
            throw new SentinelClientException("Sentinel 查詢工作狀態回應不是合法 JSON", ex);
        }
    }

    /// <summary>數字欄位的容錯讀取：Number 直讀，String 嘗試解析（防 API 版本差異把數字加引號），其餘當 0</summary>
    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(el.GetString(), out var s) => s,
            _ => 0
        };
    }

    private async Task TryDeleteJobAsync(string jobHref, CancellationToken ct)
    {
        try
        {
            var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Delete, jobHref), ct);
            resp.Dispose();
        }
        catch (Exception ex)
        {
            // 用完即刪是負擔控制措施，失敗不該讓已經拿到的查詢結果報廢——記 WARN 讓人知道
            // server 端可能殘留一個 job，不中斷主流程。
            Log.Warn(ex, "[{Server}] 清理查詢工作失敗（可能造成 server 端殘留 job，非致命）：{Href}", _server.Name, jobHref);
        }
    }

    // ── 結果分頁 ─────────────────────────────────────────────────────

    private async Task<List<SentinelEvent>> FetchAllPagesAsync(JobStatus status, SentinelSearchRequest request, CancellationToken ct)
    {
        var events = new List<SentinelEvent>();
        if (status.ResultsHref == null || status.Avail == 0)
            return events;

        var pageSize = request.PageSize ?? _settings.PageSize;
        var targetCount = Math.Min(status.Found, request.MaxResults ?? _settings.MaxResultsPerJob);
        var page = 1;

        while (events.Count < targetCount)
        {
            await ThrottleAsync(ct);
            var href = SetPageQueryParam(status.ResultsHref, page);
            var pageEvents = await FetchPageAsync(href, ct);
            if (pageEvents.Count == 0) break; // 沒有更多資料，即使數字對不上也停止，避免無窮迴圈

            events.AddRange(pageEvents);
            if (pageEvents.Count < pageSize) break; // 這頁不足一頁筆數，視為最後一頁

            page++;
        }

        return events;
    }

    /// <summary>把 results 連結的 <c>page=</c> 參數改成指定頁碼。避免依賴未文件化的「下一頁連結」
    /// 慣例（docs/NETIQ-API-PLAN.md §9 未決事項）——已知第一頁 href 帶 <c>page=1</c>，往後頁碼自己算。</summary>
    internal static string SetPageQueryParam(string href, int page)
    {
        if (Regex.IsMatch(href, @"([?&])page=\d+"))
            return Regex.Replace(href, @"([?&])page=\d+", $"$1page={page}");

        var separator = href.Contains('?') ? "&" : "?";
        return $"{href}{separator}page={page}";
    }

    private async Task<List<SentinelEvent>> FetchPageAsync(string href, CancellationToken ct)
    {
        var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, href), ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp);
                throw new SentinelClientException(
                    $"Sentinel「{_server.Name}」讀取查詢結果失敗：HTTP {(int)resp.StatusCode}｜{Truncate(body)}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseEventsPage(json);
        }
    }

    private static readonly string[] CandidateArrayKeys =
        { "items", "event", "events", "results", "data", "records", "rows", "entry", "entries" };

    /// <summary>
    /// **best-effort 通用解析**：官方文件未提供結果頁的確切 JSON 結構範例
    /// （docs/NETIQ-API-PLAN.md §9 未決事項）。依序嘗試常見形狀：純陣列、或物件中常見鍵名的陣列屬性，
    /// 找不到時保底取第一個陣列型別的屬性。解析失敗回傳空清單而非擲例外——呼叫端
    /// （尤其 --netiq-probe）應另外印出原始 body 供人工核對真實結構，不能讓「格式猜錯」
    /// 讓整條查詢鏈斷裂。真實環境的 probe 輸出定案欄位對應後，這裡再依實測結果收斂。
    /// </summary>
    internal static List<SentinelEvent> ParseEventsPage(string json)
    {
        var events = new List<SentinelEvent>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return events;
        }

        using (doc)
        {
            var array = FindEventArray(doc.RootElement);
            if (array == null) return events;

            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in item.EnumerateObject())
                {
                    fields[prop.Name] = ElementToString(prop.Value);
                }
                events.Add(new SentinelEvent(fields));
            }
        }
        return events;
    }

    private static JsonElement? FindEventArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var key in CandidateArrayKeys)
        {
            if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Array)
                return val;
        }
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array) return prop.Value;
        }
        return null;
    }

    private static string ElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => value.GetRawText() // 物件／陣列型別的欄位值原樣保留 JSON 文字，不遺失資訊
    };

    // ── 共用小工具 ───────────────────────────────────────────────────

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (_settings.QueryDelayMs > 0)
            await Task.Delay(_settings.QueryDelayMs, ct);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return "(無法讀取回應內容)"; }
    }

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
