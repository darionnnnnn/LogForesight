using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Web.Configuration;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// Web 的 AI 加值層（docs/SCALE-2000-PLAN.md §6）。原則：**程式能確定性算的不交給 AI**，
/// AI 只做「幫人看懂、幫人排序」。輸入是已彙總的結構化統計（prompt 小），輸出短。
///
/// 三條鐵律：
///   1. 任何 AI 失敗都**靜默降級**回 null——呼叫端據此隱藏卡片/按鈕，頁面功能不受影響。
///   2. 逾時獨立設短（互動情境，10 秒），與批次的 600 秒不同。
///   3. 輸出永遠由呼叫端以 textContent 呈現；AI 回傳的下鑽參數必須過白名單驗證才組連結。
///
/// AI 位址沿用「批次 appsettings 唯一事實來源、Web 唯讀」的既有決策（同 Sentinel 名單）。
/// </summary>
public interface IWebAiService
{
    /// <summary>AI 是否已設定（BaseUrl 有值）。false 時呼叫端可直接略過、不必發請求</summary>
    bool Available { get; }

    /// <summary>
    /// 帶快取地產生 JSON 結果。cacheKey 相同直接回快取（同日多人瀏覽只有第一人觸發呼叫）；
    /// 任何失敗（未設定、逾時、非 JSON、驗證未過）回 null。
    /// </summary>
    Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class;
}

public class WebAiService : IWebAiService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAiCacheStore _cache;
    private readonly Lazy<AIService?> _ai;
    private readonly bool _available;

    public WebAiService(WebAppSettings settings, IAiCacheStore cache)
    {
        _cache = cache;

        var aiSettings = LoadBatchAiSettings(settings.Storage.ResolveDataRoot());
        _available = aiSettings != null && !string.IsNullOrWhiteSpace(aiSettings.BaseUrl);

        // 延遲建立：沒人用 AI 時不必建 HttpClient。互動情境的參數覆寫——**不重試**：
        // 互動情境下重試只會把「失敗」拖成數十秒（逾時×嘗試＋退避），使用者早就不等了。
        // 一次打不到就降級，讓卡片安靜消失比讓人盯著轉圈更好。
        _ai = new Lazy<AIService?>(() =>
        {
            if (aiSettings == null) return null;
            aiSettings.TimeoutSeconds = 8;
            aiSettings.MaxTokens = 256;
            aiSettings.RetryCount = 1;          // Polly 要求 ≥1；退避設 0 讓失敗不再被拖長
            aiSettings.RetryDelaySeconds = 0;
            aiSettings.JsonRetryCount = 0;
            return new AIService(aiSettings);
        });
    }

    public bool Available => _available;

    public async Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class
    {
        if (!_available) return null;

        var cached = _cache.Get(cacheKey);
        if (cached != null)
        {
            var hit = TryParse<T>(cached);
            if (hit != null) return hit;   // 壞快取（舊格式）就當 miss，重新產生
        }

        try
        {
            var ai = _ai.Value;
            if (ai == null) return null;

            var result = await ai.ChatJsonAsync<T>(userPrompt, systemPrompt);
            if (!result.Success || result.Value == null) return null;

            _cache.Put(cacheKey, result.RawContent);
            return result.Value;
        }
        catch (Exception ex)
        {
            // 靜默降級：AI 是純加值層，掛掉不該讓任何頁面出錯
            Log.Debug(ex, "Web AI 呼叫失敗（靜默降級）：{0}", ex.Message);
            return null;
        }
    }

    /// <summary>輸入雜湊：讓「資料沒變就不重算」成立。用 SHA-256 前 16 碼，夠短又幾乎不撞</summary>
    public static string HashInput(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static T? TryParse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 讀批次 appsettings.json 的 Ai 區段（唯一事實來源）。讀不到就回 null（＝AI 不可用），
    /// 不讓站台掛掉——AI 是加值層，設定缺失只該讓加值功能消失，不影響其餘。
    /// </summary>
    private static AiSettings? LoadBatchAiSettings(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "appsettings.json");
        if (!File.Exists(path)) return null;

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (!doc.RootElement.TryGetProperty("Ai", out var ai)) return null;

            return JsonSerializer.Deserialize<AiSettings>(ai.GetRawText(), options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn(ex, "解析批次 appsettings 的 Ai 區段失敗，Web AI 加值功能停用：{0}", ex.Message);
            return null;
        }
    }
}
