using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Web.Configuration;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// Web 的 AI 加值層（docs/archive/HISTORY.md §6）。原則：**程式能確定性算的不交給 AI**，
/// AI 只做「幫人看懂、幫人排序」。輸入是已彙總的結構化統計（prompt 小），輸出短。
///
/// 三條鐵律：
///   1. 任何 AI 失敗都**靜默降級**回 null——呼叫端據此隱藏卡片/按鈕，頁面功能不受影響。
///   2. 逾時獨立設短（互動情境，10 秒），與批次的 600 秒不同。
///   3. 輸出永遠由呼叫端以 textContent 呈現；AI 回傳的下鑽參數必須過白名單驗證才組連結。
///
/// AI 位址／金鑰／進階參數（Timeout/Retry/Tokens/penalties/ExtraRequestFields）的事實來源
/// **一律是「系統管理 > 設定」頁（DB）**——§12（回饋第九輪）起 appsettings 的 Ai 區段已退役，
/// 互動情境與排程路徑共用同一份 DB 值（進階參數的解讀共用
/// <see cref="RuntimeSettingsResolver.ApplyAiAdvanced"/>，不各寫一份會漂移的版本）。
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

    /// <summary>
    /// 詳情頁對話（R7 精簡版）一輪回覆：純文字、不走快取（每輪內容都不同，快取沒有意義）。
    /// 任何失敗回 null，呼叫端顯示「AI 目前忙碌或無法回應」。
    /// </summary>
    Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt);
}

public class WebAiService : IWebAiService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AiCacheStore _cache;
    private readonly ISystemSettingsStore _systemSettings;

    /// <summary>
    /// 目前 DB 設定推導出的 AI 進階參數（§12）。與批次路徑共用同一份解讀
    /// （<see cref="RuntimeSettingsResolver.ApplyAiAdvanced"/>），不各寫一份會漂移的版本。
    /// </summary>
    private AiSettings AdvancedFromDb()
    {
        var ai = new AiSettings();
        RuntimeSettingsResolver.ApplyAiAdvanced(ai, _systemSettings.Get());
        return ai;
    }

    /// <summary>
    /// 互動情境會用到的進階參數指紋（§12）：進快照參與比對，設定頁改了這些值也會觸發重建、
    /// 即時生效——快照只比對 (BaseUrl, KeyEnc) 的話，進階參數要等位址變更或重啟才生效，
    /// 與「設定頁存檔即生效」的既有行為矛盾。只涵蓋互動客戶端實際使用的欄位
    /// （DeepDive/penalty/ExtraFields；逾時與重試在互動情境是固定覆寫值，不受設定頁影響）。
    /// </summary>
    private static string AdvancedFingerprint(SystemSettings db) =>
        $"{db.AiDeepDiveMaxTokens}|{db.AiFrequencyPenalty}|{db.AiPresencePenalty}|{db.AiExtraRequestFieldsJson}";

    // 本類別是 Singleton，但 AI 位址／金鑰／進階參數可在設定頁隨時改：每次取用時比對 DB 目前值，
    // 變了就重建 AIService（設定頁存檔即生效，不必重啟站台）——SettingsBoundClient（S8）
    // 統一處理快照比對與重建，取代原本互動／對話情境各自寫一份幾乎逐字相同的 lock+比對邏輯。
    //
    // 對話用獨立的第二個 AIService 實例（各自的請求佇列）：對話輪次的逾時/token 上限與
    // 其他互動情境（判讀單一問題、AI 歸納）不同，且不希望一輪對話卡住佇列讓其他 AI 卡片跟著等。
    // 兩個實例仍打同一個 KoboldCpp，實際併發上限由對方序列化，這裡最多讓 Web 端同時有 2 個請求在飛。
    private readonly SettingsBoundClient<(string BaseUrl, string KeyEnc, string Advanced), AIService> _interactiveClient;
    private readonly SettingsBoundClient<(string BaseUrl, string KeyEnc, string Advanced), AIService> _chatClient;

    public WebAiService(AiCacheStore cache, ISystemSettingsStore systemSettings)
    {
        _cache = cache;
        _systemSettings = systemSettings;

        _interactiveClient = new SettingsBoundClient<(string, string, string), AIService>(snapshot =>
        {
            var (baseUrl, keyEnc, _) = snapshot;   // 指紋只參與比對，factory 內直接重讀 DB
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;

            var advanced = AdvancedFromDb();
            return new AIService(new AiSettings
            {
                BaseUrl = baseUrl,
                ApiKey = CryptoHelper.IsEncrypted(keyEnc) ? CryptoHelper.Decrypt(keyEnc) : "",
                // 互動情境的參數覆寫——**不重試**：互動情境下重試只會把「失敗」拖成數十秒
                // （逾時×嘗試＋退避），使用者早就不等了。一次打不到就降級，
                // 讓卡片安靜消失比讓人盯著轉圈更好。
                TimeoutSeconds = 8,
                MaxTokens = 256,
                RetryCount = 1,          // Polly 要求 ≥1；退避設 0 讓失敗不再被拖長
                RetryDelaySeconds = 0,
                JsonRetryCount = 0,
                DeepDiveMaxTokens = advanced.DeepDiveMaxTokens,
                FrequencyPenalty = advanced.FrequencyPenalty,
                PresencePenalty = advanced.PresencePenalty,
                ExtraRequestFields = advanced.ExtraRequestFields
            });
        });

        _chatClient = new SettingsBoundClient<(string, string, string), AIService>(snapshot =>
        {
            var (baseUrl, keyEnc, _) = snapshot;   // 指紋只參與比對，factory 內直接重讀 DB
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;

            var advanced = AdvancedFromDb();
            return new AIService(new AiSettings
            {
                BaseUrl = baseUrl,
                ApiKey = CryptoHelper.IsEncrypted(keyEnc) ? CryptoHelper.Decrypt(keyEnc) : "",
                // 對話回覆比單一問題判讀長，token 上限拉高；地端模型回應快（實測 50~80 t/s），
                // 逾時不必抓得像批次那麼保守，但仍給足餘裕避免正常對話被腰斬。不重試——
                // 互動情境下重試只會把「失敗」拖得更久，一次打不到就讓使用者自己重問。
                TimeoutSeconds = 60,
                MaxTokens = 768,
                RetryCount = 1,
                RetryDelaySeconds = 0,
                JsonRetryCount = 0,
                DeepDiveMaxTokens = advanced.DeepDiveMaxTokens,
                FrequencyPenalty = advanced.FrequencyPenalty,
                PresencePenalty = advanced.PresencePenalty,
                ExtraRequestFields = advanced.ExtraRequestFields
            });
        });
    }

    /// <summary>
    /// 目前生效的 AI 位址（§12：唯一來源是 DB）。空字串＝刻意停用 AI——設定頁明講「留空會停用」，
    /// 不能被任何退路悄悄接手；從未存過時 <see cref="SystemSettings.AiBaseUrl"/> 本身就是
    /// 出廠預設（http://localhost:8080），與退役前的 appsettings 退路值相同，升級後行為不變。
    /// </summary>
    private static string EffectiveBaseUrl(SystemSettings db) => db.AiBaseUrl.Trim();

    public bool Available => !string.IsNullOrWhiteSpace(EffectiveBaseUrl(_systemSettings.Get()));

    /// <summary>依 DB 目前的位址／金鑰／進階參數取（或重建）互動情境的 AI 客戶端；未設定位址回 null</summary>
    private AIService? GetClient()
    {
        var db = _systemSettings.Get();
        return _interactiveClient.Get((EffectiveBaseUrl(db), db.AiApiKeyEnc, AdvancedFingerprint(db)));
    }

    /// <summary>依 DB 目前的位址／金鑰／進階參數取（或重建）對話情境的 AI 客戶端；未設定位址回 null</summary>
    private AIService? GetChatClient()
    {
        var db = _systemSettings.Get();
        return _chatClient.Get((EffectiveBaseUrl(db), db.AiApiKeyEnc, AdvancedFingerprint(db)));
    }

    public async Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt)
    {
        try
        {
            var ai = GetChatClient();
            if (ai == null) return null;

            var result = await ai.ChatAsync(userPrompt, systemPrompt, jsonMode: false, label: "record-detail-chat");
            return result.Success && !string.IsNullOrWhiteSpace(result.Content) ? result.Content.Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "詳情頁對話呼叫失敗（靜默降級）：{0}", ex.Message);
            return null;
        }
    }

    public async Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class
    {
        var cached = _cache.Get(cacheKey);
        if (cached != null)
        {
            var hit = TryParse<T>(cached);
            if (hit != null) return hit;   // 壞快取（舊格式）就當 miss，重新產生
        }

        try
        {
            var ai = GetClient();
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

}
