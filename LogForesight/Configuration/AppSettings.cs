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
    /// 部分模型（尤其 MoE）在輸出 JSON 前會先產生一段推理/前言文字，太小的上限會讓 JSON 本體
    /// 被攔腰截斷（保證解析失敗），預設值刻意抓寬鬆一些；地端呼叫不用省，寧可留餘裕。
    /// </summary>
    public int MaxTokens { get; set; } = 8192;
}

public class PermissionSettings
{
    /// <summary>
    /// 額外要監控權限異動的資料夾路徑（支援環境變數，如 %ProgramFiles%）。
    /// 執行檔自身所在目錄一律會被監控，不需加入此清單。
    /// </summary>
    public List<string> WatchedFolders { get; set; } = new();
}
