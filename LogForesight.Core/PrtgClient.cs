using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Core;

/// <summary>
/// PRTG 連線／存取例外。訊息保證不含敏感的 token、密碼或 passhash 資訊，可直接顯示給操作者或寫入 log。
/// </summary>
public class PrtgClientException : Exception
{
    public PrtgClientException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// PRTG 監控系統 HTTP 存取客戶端（PRTG 第 1 輪批次B、第 2 輪批次A）。
/// 負責與 PRTG API 進行 HTTP 通訊，自動在 Query String 附加 API Token 或帳號與 passhash。
/// 不抽介面、不加重試、不加節流、不加分頁。
/// </summary>
public sealed class PrtgClient : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _baseUrl;
    private readonly bool _usesUsernameAuth;
    private readonly bool _needsPasshashExchange;
    private readonly string _token;
    private readonly string _username;
    private readonly string _password;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _passhashLock = new(1, 1);
    private string? _cachedPasshash;
    private string? _credentialFailure;
    private bool _disposed;

    internal TimeSpan Timeout => _http.Timeout;
    internal HttpMessageHandler Handler { get; }

    public PrtgClient(
        string baseUrl,
        string tokenOrEmpty,
        int timeoutSeconds,
        bool ignoreSslErrors,
        HttpMessageHandler? handler = null,
        string authMode = PrtgAuthModes.Token,
        string usernameOrEmpty = "",
        string passwordOrEmpty = "",
        string passhashOrEmpty = "")
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

        _usesUsernameAuth = string.Equals(authMode, PrtgAuthModes.Password, StringComparison.Ordinal) ||
                            string.Equals(authMode, PrtgAuthModes.Passhash, StringComparison.Ordinal);
        _needsPasshashExchange = string.Equals(authMode, PrtgAuthModes.Password, StringComparison.Ordinal);

        if (_usesUsernameAuth && string.IsNullOrWhiteSpace(usernameOrEmpty))
        {
            throw new PrtgClientException("PRTG 帳號不可為空。");
        }

        if (string.Equals(authMode, PrtgAuthModes.Passhash, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(passhashOrEmpty))
            {
                throw new PrtgClientException("PRTG passhash 不可為空。");
            }
            _cachedPasshash = passhashOrEmpty.Trim();
        }

        _baseUrl = baseUrl.TrimEnd('/');
        _token = tokenOrEmpty ?? string.Empty;
        _username = usernameOrEmpty ?? string.Empty;
        _password = passwordOrEmpty ?? string.Empty;

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
    /// 測試 PRTG 連線：呼叫 sensors 端點取得最少資料以驗證連線位址與認證資訊有效性。
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
            throw new PrtgClientException("PRTG 回傳 HTML 內容而非 JSON，請確認連線位址是否正確或認證資訊是否有效。");
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
    /// 帳密模式下確保已取得 passhash（同實例只換取一次）。
    /// token 模式不會被呼叫到，若被呼叫直接回傳空字串。
    /// passhash 模式在建構期已預填快取，直接回傳快取值。
    /// </summary>
    private async Task<string> EnsurePasshashAsync(CancellationToken ct)
    {
        if (!_usesUsernameAuth) return string.Empty;

        if (_cachedPasshash != null) return _cachedPasshash;

        // passhash 模式絕不呼叫 getpasshash.htm，走到這裡代表快取沒被正確預填，是程式錯誤而不是使用者輸入問題
        if (!_needsPasshashExchange)
        {
            throw new PrtgClientException("passhash 模式絕不呼叫 getpasshash.htm，走到這裡代表快取未被正確預填。");
        }

        // 憑證錯誤要黏住：每個 sensor 的數值擷取各自呼叫一次 GetJsonAsync，密碼打錯時
        // 若讓每次都重打 getpasshash，三千個 sensor 就是三千次登入失敗——足以觸發
        // PRTG 端的帳號鎖定。傳輸類失敗（連不上、逾時）不黏，那種重試是有意義的。
        if (_credentialFailure != null) throw new PrtgClientException(_credentialFailure);

        // 併發保護：FetchValuesAsync 會併發呼叫 GetJsonAsync，
        // 用 SemaphoreSlim(1,1) 確保多個併發請求同時到達時只向 PRTG 請求一次 passhash
        await _passhashLock.WaitAsync(ct);
        try
        {
            if (_cachedPasshash != null) return _cachedPasshash;
            if (_credentialFailure != null) throw new PrtgClientException(_credentialFailure);

            // 這是唯一會帶 password 的請求
            var query = $"username={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}";
            var uri = new Uri($"{_baseUrl}/api/getpasshash.htm?{query}", UriKind.Absolute);

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
                var sanitized = StripSecrets(ex.Message);
                throw new PrtgClientException($"連線 PRTG 伺服器失敗：{sanitized}");
            }

            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw FailCredentials("PRTG 帳號或密碼錯誤。");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    throw new PrtgClientException($"PRTG 伺服器回應錯誤：HTTP {(int)resp.StatusCode}");
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                var trimmed = text.Trim();

                if (trimmed.StartsWith('<'))
                {
                    // 帳密錯時 PRTG 回登入頁，但反向代理維護頁／WAF 阻擋頁同樣是 200+HTML——
                    // 訊息不能一口咬定帳密錯，否則使用者會去改本來對的密碼（真的因此打錯反而觸發鎖定）
                    throw FailCredentials("PRTG 回傳登入頁或 HTML 內容，請確認帳號密碼與連線位址。");
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    throw FailCredentials("PRTG 未回傳有效的 passhash。");
                }

                _cachedPasshash = trimmed;
                return _cachedPasshash;
            }
        }
        finally
        {
            _passhashLock.Release();
        }
    }

    /// <summary>
    /// 記下憑證層級的失敗並回傳要擲出的例外——同一個 client 實例之後不再重打 getpasshash。
    /// 只用於「帳密本身不對」這類重試也不會成功的失敗；傳輸類失敗不記，那種重試有意義。
    /// </summary>
    private PrtgClientException FailCredentials(string message)
    {
        _credentialFailure = message;
        return new PrtgClientException(message);
    }

    /// <summary>
    /// 呼叫 PRTG API GET 端點並取得原始 JSON 字串。
    /// </summary>
    public async Task<string> GetJsonAsync(string relativePathAndQuery, CancellationToken ct = default)
    {
        var passhash = await EnsurePasshashAsync(ct);
        var uri = BuildUri(relativePathAndQuery, passhash);
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
            var sanitized = StripSecrets(ex.Message);
            throw new PrtgClientException($"連線 PRTG 伺服器失敗：{sanitized}");
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new PrtgClientException(_usesUsernameAuth
                    ? "PRTG 帳號憑證無效或權限不足。"
                    : "PRTG API token 無效或權限不足。");
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new PrtgClientException($"PRTG 伺服器回應錯誤：HTTP {(int)resp.StatusCode}");
            }

            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    private Uri BuildUri(string relativePathAndQuery, string? passhash = null)
    {
        var path = relativePathAndQuery.TrimStart('/');
        var separator = path.Contains('?') ? '&' : '?';
        string authQuery;
        if (_usesUsernameAuth)
        {
            authQuery = $"username={Uri.EscapeDataString(_username)}&passhash={Uri.EscapeDataString(passhash ?? string.Empty)}";
        }
        else
        {
            authQuery = $"apitoken={Uri.EscapeDataString(_token)}";
        }
        var url = $"{_baseUrl}/{path}{separator}{authQuery}";
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// 把 token、password、passhash 從對外訊息中遮蔽。原文與 URL 編碼後的形式都要遮——底層例外訊息帶的是請求 URL，
    /// 參數在那裡是編碼過的，只比對原文會讓含特殊字元的憑證原樣外流。
    /// </summary>
    private string StripSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = MaskSecret(text, _token);
        text = MaskSecret(text, _password);
        text = MaskSecret(text, _cachedPasshash);
        return text;
    }

    private static string MaskSecret(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(text)) return text;

        var result = text.Replace(secret, "***");
        var escaped = Uri.EscapeDataString(secret);
        if (!string.Equals(escaped, secret, StringComparison.Ordinal))
        {
            result = result.Replace(escaped, "***");
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _passhashLock.Dispose();
        _http.Dispose();
    }
}
