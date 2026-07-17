using System.Text.Json;

namespace LogForesight;

/// <summary>
/// 從執行檔目錄的 appsettings.json 載入設定。
/// 檔案不存在或格式錯誤時使用預設值並提示，不中斷執行。
/// </summary>
public class AppSettings
{
    public AiSettings Ai { get; set; } = new();
    public PermissionSettings Permissions { get; set; } = new();

    public static AppSettings Load()
    {
        // 讀執行檔所在目錄（排程執行時 CurrentDirectory 可能是 system32，不可靠）
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"找不到 {path}，使用預設設定。");
            return new AppSettings();
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), options) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"appsettings.json 格式錯誤（{ex.Message}），使用預設設定。");
            return new AppSettings();
        }
    }
}

public class AiSettings
{
    /// <summary>llama.cpp 的 OpenAI 相容 API 位址</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>單次 AI 呼叫的逾時秒數。本機 27B 級模型單次回應可能需數分鐘，預設 600 秒</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>失敗重試次數（Polly，連線失敗/HTTP 錯誤/逾時/空回應皆重試）</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>第一次重試的等待秒數，之後指數遞增（10 → 20 → 40）</summary>
    public int RetryDelaySeconds { get; set; } = 10;

    /// <summary>AI 回覆未通過 JSON 格式/內容檢查時的額外重試次數（不計入網路層 RetryCount）</summary>
    public int JsonRetryCount { get; set; } = 2;

    /// <summary>
    /// 單次回應的最大 token 數上限，避免模型異常時無限重複輸出拖垮回應品質與時間。0 = 不設上限。
    /// 注意：如果模型的「思考/推理」長度本身沒有上限，一直調高這個值只是把截斷點往後延，
    /// 不是根本解——先用 ExtraRequestFields 限制思考長度（見下方），這裡才不用一直往上加。
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// 原封不動合併進送給 AI 的請求 JSON 的額外欄位，最常見用途是限制模型「思考/推理」的長度。
    /// 這類參數沒有統一標準、依模型與 llama.cpp 版本而異，所以用透傳而非寫死的強型別欄位，
    /// 才能不改程式碼、只改設定檔就調整。預設值 chat_template_kwargs.thinking_budget=512
    /// 是 llama.cpp 對支援思考預算的 Jinja 聊天範本（如 Gemini 風格）常見的慣例欄位名稱，
    /// 但**不保證適用於所有模型/伺服器組合**——請對照 llama.cpp server 啟動時印出的聊天範本，
    /// 或該模型的文件確認正確欄位名稱，不對的話這個設定會被伺服器忽略、不會報錯。
    /// appsettings.json 範例：
    /// "ExtraRequestFields": { "chat_template_kwargs": { "thinking_budget": 512 } }
    /// </summary>
    public Dictionary<string, JsonElement>? ExtraRequestFields { get; set; } = new()
    {
        ["chat_template_kwargs"] = JsonSerializer.SerializeToElement(new { thinking_budget = 512 })
    };
}

public class PermissionSettings
{
    /// <summary>
    /// 額外要監控權限異動的資料夾路徑（支援環境變數，如 %ProgramFiles%）。
    /// 執行檔自身所在目錄一律會被監控，不需加入此清單。
    /// </summary>
    public List<string> WatchedFolders { get; set; } = new();
}
