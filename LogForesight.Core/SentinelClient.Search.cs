using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogForesight.Core;

/// <summary>SentinelClient 的 event-search job 生命週期：建立→輪詢→刪除，以及不經 job 的單次 GET。</summary>
public sealed partial class SentinelClient
{
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

    /// <summary>
    /// 只做認證（取 token），不建立任何 event-search job——「網址／帳密是否正確」的最小驗證
    /// （項目 6，docs/WEB-SPEC.md：Sentinel 維護頁的「測試連線」按鈕）。
    /// 成功回傳耗時；失敗一律擲 <see cref="SentinelClientException"/>（訊息可直接顯示，
    /// 錯誤分類沿用 <see cref="AuthenticateAsync"/> 既有的 401/其他 HTTP／連線失敗區分）。
    /// </summary>
    public async Task<TimeSpan> TestConnectionAsync(CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        try
        {
            await ThrottleAsync(ct);
            var sw = Stopwatch.StartNew();
            await EnsureAuthenticatedAsync(ct);
            sw.Stop();
            return sw.Elapsed;
        }
        finally
        {
            _queue.Release();
        }
    }

    /// <summary>
    /// 建立 job 的請求本文序列化選項。**必須用不轉義的編碼器**：`System.Text.Json` 預設會把
    /// 雙引號寫成 <c>"</c>、非 ASCII 字元寫成 <c>\uXXXX</c>，而 **Sentinel 的 JSON 解析器
    /// 不接受 <c>\uXXXX</c> 轉義序列**，會回 400「invalid JSON value」——
    /// 2026-07-29 第二輪 probe 實測：片語查詢 <c>obssvcname:"Microsoft-Windows-Security-Auditing"</c>
    /// 整個被拒，錯誤指向 filter 值的起始位置。片語查詢（帶引號）是規則來源下推 Lucene 的必要語法，
    /// 沒有這個設定整條取數管線只要用到片語就會失敗。
    /// <c>UnsafeRelaxedJsonEscaping</c> 只做 JSON 規格強制的最小轉義（<c>"</c> → <c>\"</c>），
    /// 名稱裡的 Unsafe 指的是「不對 HTML 做額外防禦轉義」，這裡的輸出送往 REST API 不是網頁，
    /// 且本文只由本程式組出（filter 內容非使用者自由輸入的 HTML），無 XSS 面。
    /// </summary>
    private static readonly JsonSerializerOptions JobBodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
        var json = body.ToJsonString(JobBodyJsonOptions);

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

    /// <summary>
    /// 對任意 REST 資源做認證過的 GET，不走 event-search job 生命週期
    /// （docs/archive/HISTORY.md 2026-07-29 第二輪 probe：探索 ESM <c>/objects/eventsource</c> 等
    /// 一般資源用，這些端點是單次讀取，沒有 job/輪詢/刪除的概念）。
    /// 回傳原始 JSON 字串——解析交由呼叫端，不同端點形狀不一，這裡不假設任何結構。
    /// </summary>
    /// <param name="path">相對路徑（如 <c>/SentinelRESTServices/objects/eventsource</c>）或完整 URL</param>
    public async Task<string> RawGetAsync(string path, CancellationToken ct = default)
    {
        await _queue.WaitAsync(ct);
        try
        {
            await ThrottleAsync(ct);
            var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : $"{BaseUrl}{(path.StartsWith('/') ? "" : "/")}{path}";

            var resp = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new SentinelClientException(
                        $"Sentinel「{_server.Name}」讀取「{path}」失敗：HTTP {(int)resp.StatusCode}｜{Truncate(body)}");
                }
                return body;
            }
        }
        finally
        {
            _queue.Release();
        }
    }
}
