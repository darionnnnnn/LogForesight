using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NLog;

namespace LogForesight.Core;

/// <summary>
/// PRTG 連線／存取例外。訊息保證不含敏感的 token 資訊，可直接顯示給操作者或寫入 log。
/// </summary>
public class PrtgClientException : Exception
{
    public PrtgClientException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// PRTG 監控系統 HTTP 存取客戶端（PRTG 第 1 輪批次B）。
/// 負責與 PRTG API 進行 HTTP 通訊，自動在 Query String 附加 API Token。
/// 不抽介面、不加重試、不加節流、不加分頁。
/// </summary>
public sealed class PrtgClient : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _baseUrl;
    private readonly string _token;
    private readonly HttpClient _http;
    private bool _disposed;

    internal TimeSpan Timeout => _http.Timeout;
    internal HttpMessageHandler Handler { get; }

    public PrtgClient(string baseUrl, string token, int timeoutSeconds, bool ignoreSslErrors, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new PrtgClientException("PRTG 未設定連線位址。");

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(parsed.Host))
        {
            throw new PrtgClientException(
                $"PRTG 的連線位址格式不正確：「{baseUrl}」。" +
                "請填寫完整網址，含 https:// 或 http://（例如 https://prtg.corp.local）。");
        }

        _baseUrl = baseUrl.TrimEnd('/');
        _token = token ?? string.Empty;

        var ownsHandler = handler == null;
        var actualHandler = handler ?? CreateDefaultHandler(ignoreSslErrors);
        Handler = actualHandler;

        _http = new HttpClient(actualHandler, disposeHandler: ownsHandler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 1))
        };

        if (ownsHandler && ignoreSslErrors)
        {
            Log.Warn("[PRTG] PrtgIgnoreSslErrors 已啟用，本連線不驗證 PRTG 憑證（自簽憑證環境的逃生門，正式環境建議改安裝 CA 憑證）");
        }
    }

    private static HttpMessageHandler CreateDefaultHandler(bool ignoreSslErrors)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (ignoreSslErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }

    /// <summary>
    /// 測試 PRTG 連線：呼叫 sensors 端點取得最少資料以驗證連線位址與 token 有效性。
    /// 判定成功條件：HTTP 狀態成功、回應不以 &lt; 開頭（非 HTML 登入頁）、能解析為 JSON 物件。
    /// 成功回傳耗時，失敗擲 <see cref="PrtgClientException"/>。
    /// </summary>
    public async Task<TimeSpan> TestConnectionAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var json = await GetJsonAsync("/api/table.json?content=sensors&columns=objid&count=1", ct);

        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            throw new PrtgClientException("PRTG 回傳 HTML 內容而非 JSON，請確認連線位址是否正確或 API token 是否有效。");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrtgClientException("PRTG 回傳內容非預期的 JSON 物件格式。");
            }
        }
        catch (JsonException ex)
        {
            throw new PrtgClientException("PRTG 回傳內容非合法的 JSON 格式。", ex);
        }

        sw.Stop();
        return sw.Elapsed;
    }

    /// <summary>
    /// 呼叫 PRTG API GET 端點並取得原始 JSON 字串。
    /// </summary>
    public async Task<string> GetJsonAsync(string relativePathAndQuery, CancellationToken ct = default)
    {
        var uri = BuildUri(relativePathAndQuery);
        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            resp = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var sanitized = StripToken(ex.Message);
            throw new PrtgClientException($"連線 PRTG 伺服器失敗：{sanitized}");
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PrtgClientException("PRTG API token 無效或權限不足。");
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new PrtgClientException($"PRTG 伺服器回應錯誤：HTTP {(int)resp.StatusCode}");
            }

            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    private Uri BuildUri(string relativePathAndQuery)
    {
        var path = relativePathAndQuery.TrimStart('/');
        var separator = path.Contains('?') ? '&' : '?';
        var url = $"{_baseUrl}/{path}{separator}apitoken={Uri.EscapeDataString(_token)}";
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// 把 token 從對外訊息中遮蔽。原文與 URL 編碼後的形式都要遮——底層例外訊息帶的是請求 URL，
    /// token 在那裡是編碼過的，只比對原文會讓含特殊字元的 token 原樣外流。
    /// </summary>
    private string StripToken(string text)
    {
        if (string.IsNullOrEmpty(_token) || string.IsNullOrEmpty(text)) return text;

        var result = text.Replace(_token, "***");
        var escaped = Uri.EscapeDataString(_token);
        if (!string.Equals(escaped, _token, StringComparison.Ordinal))
        {
            result = result.Replace(escaped, "***");
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
