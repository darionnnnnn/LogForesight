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
    public AnalysisSettings Analysis { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

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
    /// 原封不動合併進送給 AI 的請求 JSON 的額外欄位。
    ///
    /// 實際伺服器是 **KoboldCpp**（非原生 llama.cpp server），已從對方的啟動設定檔確認以下
    /// 關鍵事實，取代先前的猜測：
    /// - `"jinja_kwargs": "{\"enable_thinking\": true}"` ── 這個模型的聊天範本認的是
    ///   **布林開關** `enable_thinking`，不是數字預算；先前猜的 `thinking_budget` 這個 key
    ///   範本根本不認得，等於完全沒作用。伺服器目前預設整台開著（true），所以預設一律會思考。
    ///   直接關閉（false）才是目前唯一有實證支援的思考控制手段。
    /// - `"chatcompletionsadapter": "AutoGuess"` ── KoboldCpp 用這個機制自動猜測聊天/輸出
    ///   格式；如果它誤判了這個客製化 Gemma-MoE checkpoint 的格式，觀察到的 `&lt;|channel|&gt;`
    ///   （Harmony 格式的輸出通道分隔符號）外洩到內容裡就说得通了——不一定是模型本身的問題，
    ///   也可能是 adapter 沒有正確解析/拆分模型輸出。
    /// - 重複懲罰的 KoboldCpp **原生**參數名稱是 `rep_pen`（KoboldAI 系譜命名），不是原生
    ///   llama.cpp server 的 `repeat_penalty`——先前那個 key 這台伺服器很可能根本不認得。
    ///
    /// `reasoning_effort` 保留但降低權重：KoboldCpp 有自己的 `reasoningeffort` 啟動參數，
    /// 不確定 OpenAI 相容層是否支援逐請求覆寫；`enable_thinking:false` 才是目前的主力手段。
    /// </summary>
    public Dictionary<string, JsonElement>? ExtraRequestFields { get; set; } = new()
    {
        ["chat_template_kwargs"] = JsonSerializer.SerializeToElement(new { enable_thinking = false }),
        ["rep_pen"] = JsonSerializer.SerializeToElement(1.3)
    };

    /// <summary>
    /// 頻率懲罰：對已出現過的 token 依出現次數累加懲罰，抑制「同一段文字反覆重複」的退化輸出
    /// （實際觀察到的失敗模式，如摘要欄位塞滿重複的 "-1-1-1-1..."、"0 0 0 0..."、
    /// "process 45312 process 45312..." 這類反覆片語）。0 = 不懲罰，正值抑制重複；
    /// OpenAI 相容 API 的標準欄位，llama.cpp 也支援。
    /// 0.3 實測不足以壓下這個模型的退化傾向，調到 0.5 後從 log 觀察似乎依然明顯，
    /// 這裡先調到 0.8 作為下一步嘗試；若持續無效，可能代表這個 server/模型組合下
    /// OpenAI 相容層的 frequency_penalty 沒有確實生效，改靠上面的 repeat_penalty
    /// 或請維護伺服器的人確認實際支援的取樣參數。
    /// </summary>
    public double? FrequencyPenalty { get; set; } = 0.8;

    /// <summary>
    /// 存在懲罰：對已出現過的 token（不論次數）給固定懲罰，鼓勵話題/用詞多樣性，
    /// 與 FrequencyPenalty 互補，一起抑制退化重複。0 = 不懲罰。
    /// </summary>
    public double? PresencePenalty { get; set; } = 0.8;
}

public class PermissionSettings
{
    /// <summary>
    /// 額外要監控權限異動的資料夾路徑（支援環境變數，如 %ProgramFiles%）。
    /// 執行檔自身所在目錄一律會被監控，不需加入此清單。
    /// </summary>
    public List<string> WatchedFolders { get; set; } = new();
}

public class AnalysisSettings
{
    /// <summary>
    /// 伺服器角色描述（如「公司機房的 AD 網域控制站」），會帶入 prompt 讓 AI 依環境判讀
    /// （同一事件在不同角色的機器上嚴重性不同）。留空則略過。原為 Program.cs 的常數，
    /// 搬進設定檔是為了未來多主機化時每台主機能各自設定角色描述做準備。
    /// </summary>
    public string ServerDescription { get; set; } = "";

    /// <summary>
    /// 體檢間隔天數（2026-07-20 重設計，取代原「固定星期六全量」，見 docs/PLAN.md「核心設計決策 B」）。
    /// 「發現慢速斜線」已改由每日確定性的 <see cref="SlowTrendAnalyzer"/> 負責，體檢只剩「講這段期間的
    /// 故事」——採 due-date 輪巡：每台主機距上次體檢達此天數即到期，不綁固定星期幾、不需要 cohort 分桶，
    /// 首次接觸主機時錯峰虛擬回填上次體檢日，多主機規模下自然攤平、不會在某一天集中尖峰。
    /// 錯過（機器關機、排程失敗）時會在下次執行自動補跑，機制不變。單機情境下等同「每 N 天做一次」。
    /// </summary>
    public int CheckupIntervalDays { get; set; } = 7;
}

public class StorageSettings
{
    /// <summary>
    /// 分析紀錄的儲存後端。目前只有 "Jsonl"（預設，現行檔案格式）；
    /// 未來要接 DB 時新增對應實作並在這裡加一個 case 即可，分析邏輯不需改動。
    /// </summary>
    public string Type { get; set; } = "Jsonl";
}
