using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LogForesight.Core;

/// <summary>SentinelClient 的認證與已認證請求（token 生命週期、重試包裝）。</summary>
public sealed partial class SentinelClient
{
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

    /// <summary>原廠文件範例回應為 <c>{"Token":"…"}</c>（見 docs/NETIQ-API-REFERENCE.md §1.1），
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
}
