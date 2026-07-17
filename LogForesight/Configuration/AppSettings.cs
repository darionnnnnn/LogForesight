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
    /// 一般（終端 JSON 較短）呼叫的 token 上限，用於每日總覽分析與前置掃描。0 = 不設上限。
    /// 這類回應正常應該只有幾百字元；上限故意抓緊，是因為觀察到模型退化重複輸出時會一路
    /// 生成到頂到上限才停——上限越大不會讓成功率變高，只會讓失敗的嘗試多跑幾十秒才觸頂、
    /// 白白浪費時間。如果模型的「思考/推理」長度本身沒有上限，一直調高這個值也只是把
    /// 截斷點往後延，不是根本解——請優先用 ExtraRequestFields 限制思考長度（見下方）。
    /// </summary>
    public int MaxTokens { get; set; } = 1536;

    /// <summary>
    /// 深入分析呼叫（RiskReportService 逐類別的問題分析）的 token 上限，獨立於 MaxTokens 之外。
    /// 這類回應天生就比終端摘要長得多（一次要分析多個問題的原因/影響/處置步驟），
    /// 用同一個上限會逼你在「精簡呼叫失敗時拖太久」和「深入分析被截斷」之間二選一，
    /// 所以拆開兩個設定各自調。
    /// </summary>
    public int DeepDiveMaxTokens { get; set; } = 8192;

    /// <summary>
    /// 原封不動合併進送給 AI 的請求 JSON 的額外欄位，最常見用途是限制模型「思考/推理」的長度。
    /// 這類參數沒有統一標準、依模型與 llama.cpp 版本而異，所以用透傳而非寫死的強型別欄位，
    /// 才能不改程式碼、只改設定檔就調整。
    ///
    /// 從實際 log 觀察到回覆內容混有 &lt;|channel|&gt; 特殊符號，這是 OpenAI Harmony 格式
    /// （gpt-oss 系列模型用來分隔 analysis/final 等輸出通道）外洩的痕跡，代表這個模型很可能
    /// 是 Harmony/gpt-oss 家族而非單純 Gemini 風格，預設同時帶上兩種慣例的欄位：
    /// - reasoning_effort：gpt-oss 在 llama.cpp 的官方文件化參數，值為 low/medium/high
    /// - chat_template_kwargs.thinking_budget：Gemini 風格聊天範本常見的數字預算慣例
    /// 伺服器通常會忽略不認得的欄位、不會報錯，所以兩種都送不會互相干擾，但**仍不保證兩者
    /// 之一對你的模型/伺服器組合一定有效**——請對照 llama.cpp server 啟動時印出的聊天範本
    /// 或模型文件確認實際支援的欄位名稱。
    /// </summary>
    public Dictionary<string, JsonElement>? ExtraRequestFields { get; set; } = new()
    {
        ["reasoning_effort"] = JsonSerializer.SerializeToElement("low"),
        ["chat_template_kwargs"] = JsonSerializer.SerializeToElement(new { thinking_budget = 512 })
    };

    /// <summary>
    /// 頻率懲罰：對已出現過的 token 依出現次數累加懲罰，抑制「同一段文字反覆重複」的退化輸出
    /// （實際觀察到的失敗模式，如摘要欄位塞滿重複的 "-1-1-1-1..." 或 "0 0 0 0..."）。
    /// 0 = 不懲罰，正值抑制重複；OpenAI 相容 API 的標準欄位，llama.cpp 也支援。
    /// 0.3 在實測中似乎不足以壓下這個模型的退化傾向，先調到 0.5；若仍常出現重複垃圾，
    /// 可以再往上調（llama.cpp 通常允許到 2.0），但過高可能影響正常內容的流暢度。
    /// </summary>
    public double? FrequencyPenalty { get; set; } = 0.5;

    /// <summary>
    /// 存在懲罰：對已出現過的 token（不論次數）給固定懲罰，鼓勵話題/用詞多樣性，
    /// 與 FrequencyPenalty 互補，一起抑制退化重複。0 = 不懲罰。
    /// </summary>
    public double? PresencePenalty { get; set; } = 0.5;
}

public class PermissionSettings
{
    /// <summary>
    /// 額外要監控權限異動的資料夾路徑（支援環境變數，如 %ProgramFiles%）。
    /// 執行檔自身所在目錄一律會被監控，不需加入此清單。
    /// </summary>
    public List<string> WatchedFolders { get; set; } = new();
}
