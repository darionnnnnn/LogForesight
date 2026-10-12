using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>AI 探活狀態列舉</summary>
public enum AiProbeStatus
{
    NotConfigured,
    NotProbed,
    Ready,
    Failed
}

/// <summary>AI 探活結果</summary>
public sealed record AiProbeResult(
    bool IsReady,
    AiProbeStatus Status,
    string Detail,
    DateTime ProbedAt,
    double? HttpElapsedMilliseconds = null,
    int? TotalTokens = null);

/// <summary>
/// AI 服務探活介面（五十輪批次 F-1b）。
/// 提供同步讀取快取狀態（供就緒判定）與非同步刷新入口（供精靈狀態 API 在取狀態前刷新）。
/// </summary>
public interface IAiProbeService
{
    /// <summary>最近一次快取探活結果。若尚未探活過，回傳 NotProbed</summary>
    AiProbeResult LatestResult { get; }

    /// <summary>
    /// 刷新探活狀態。若已有 60 秒內之快取結果且設定未變動，直接回傳快取；
    /// 逾期或無快取時執行實際探活（最長 10 秒）。同一時間多個並發請求共用同一次實際探活。
    /// </summary>
    Task<AiProbeResult> RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// AI 探活服務實作（五十輪批次 F-1b）。
/// 複用現有 AI HTTP 設定與認證組裝，探活有逾時與 60 秒成功／失敗快取。
/// </summary>
public class AiProbeService : IAiProbeService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(10);

    private readonly ISystemSettingsStore _settingsStore;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AiProbeResult? _latestResult;
    private DateTime _latestProbedAt = DateTime.MinValue;
    private string _cachedFingerprint = string.Empty;

    public AiProbeService(ISystemSettingsStore settingsStore, HttpClient httpClient)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public AiProbeResult LatestResult =>
        _latestResult ?? new AiProbeResult(false, AiProbeStatus.NotProbed, "AI 服務尚未探活。", DateTime.MinValue);

    public async Task<AiProbeResult> RefreshAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();
        var fingerprint = GetFingerprint(settings);
        var now = DateTime.UtcNow;

        if (_latestResult != null &&
            _cachedFingerprint == fingerprint &&
            now - _latestProbedAt < DefaultCacheDuration)
        {
            return _latestResult;
        }

        await _gate.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (_latestResult != null &&
                _cachedFingerprint == fingerprint &&
                now - _latestProbedAt < DefaultCacheDuration)
            {
                return _latestResult;
            }

            var result = await ExecuteProbeAsync(settings, ct);
            _latestResult = result;
            _latestProbedAt = DateTime.UtcNow;
            _cachedFingerprint = fingerprint;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetFingerprint(SystemSettings s) =>
        $"{s.AiProvider}|{s.AiBaseUrl}|{s.AiApiKeyEnc}|{s.AiModel}|{s.AiAzureDeployment}|{s.AiAzureApiVersion}";

    private async Task<AiProbeResult> ExecuteProbeAsync(SystemSettings db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var aiSettings = AIService.BuildSettingsFromDb(db);

        if (!aiSettings.IsConfigured)
        {
            return new AiProbeResult(
                false,
                AiProbeStatus.NotConfigured,
                "尚未設定 AI 服務位址，分析將以統計模式執行（可正常運作，僅缺白話摘要）。",
                now);
        }

        var (requestUrl, applyAuth) = AIService.ResolveEndpointAndAuth(aiSettings);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        applyAuth(request.Headers);

        var isAzure = AiProviders.Is(aiSettings.Provider, AiProviders.AzureOpenAi);
        var effectiveModel = isAzure
            ? null
            : (!string.IsNullOrWhiteSpace(aiSettings.Model) ? aiSettings.Model : AiProviders.DefaultModel(aiSettings.Provider));

        var payload = new
        {
            model = effectiveModel,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var effectiveTimeout = _httpClient.Timeout > TimeSpan.Zero && _httpClient.Timeout <= MaxTimeout
            ? _httpClient.Timeout
            : MaxTimeout;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var timer = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                const int maximumBytes = 64 * 1024;
                if (response.Content.Headers.ContentLength > maximumBytes)
                    throw new InvalidDataException("AI probe response is oversized.");
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var buffer = new MemoryStream();
                var block = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(block, timeoutCts.Token)) > 0)
                {
                    if (buffer.Length + read > maximumBytes) throw new InvalidDataException("AI probe response is oversized.");
                    buffer.Write(block, 0, read);
                }
                using var body = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                var root = body.RootElement;
                if (!UniqueProperties(root) || root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
                    !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                    choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var role) ||
                    role.ValueKind != JsonValueKind.String || role.GetString() != "assistant" ||
                    !message.TryGetProperty("content", out var content) ||
                    content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    (content.ValueKind == JsonValueKind.Null &&
                     (!choices[0].TryGetProperty("finish_reason", out var finish) ||
                      finish.ValueKind != JsonValueKind.String || finish.GetString() != "length")))
                    throw new InvalidDataException("AI probe chat response shape is invalid.");
                int? tokens = null;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var tokenCount) && tokenCount >= 0)
                    tokens = tokenCount;
                return new AiProbeResult(
                    true,
                    AiProbeStatus.Ready,
                    "AI 服務探活成功，小請求回應格式相容；正式摘要仍由分析作業產生。",
                    now, Math.Round(timer.Elapsed.TotalMilliseconds, 3), tokens);
            }

            Log.Warn("AI 探活收到非成功狀態碼：{0}", (int)response.StatusCode);
            return new AiProbeResult(
                false,
                AiProbeStatus.Failed,
                "AI 服務探活失敗，服務回應非成功狀態碼（分析將以統計模式執行）。",
                now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("AI 探活逾時（上限 {0} 秒）", effectiveTimeout.TotalSeconds);
            return new AiProbeResult(
                false,
                AiProbeStatus.Failed,
                "AI 服務探活逾時，請檢查服務位址與網路連線（分析將以統計模式執行）。",
                now);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "AI 探活連線失敗：{0}", ex.Message);
            return new AiProbeResult(
                false,
                AiProbeStatus.Failed,
                "AI 服務探活失敗，請檢查服務位址與連線狀態（分析將以統計模式執行）。",
                now);
        }
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!seen.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
