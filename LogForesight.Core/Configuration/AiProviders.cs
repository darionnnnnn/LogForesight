using NLog;

namespace LogForesight.Core.Configuration;

/// <summary>
/// AI 服務提供者的識別字與共用判定（回饋二十輪終檢收斂）。三個字面值原本散在七個檔案，
/// 「算不算已設定」的三路判定在 Core 與 Web 各有一份、大小寫比對方式還不一致——
/// appsettings 寫 <c>"openai"</c> 時，一份判定會落到本機分支、另一份卻組出 OpenAI 官方 URL，
/// 正是「首頁說停用、實際打了外網」那種漂移。所有比對一律經過這裡。
/// </summary>
public static class AiProviders
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>本機 OpenAI 相容端點（llama.cpp／KoboldCpp 等）。預設值＝升級前的既有行為。</summary>
    public const string Local = "Local";
    public const string OpenAi = "OpenAi";
    public const string AzureOpenAi = "AzureOpenAi";

    public static readonly IReadOnlyList<string> All = new[] { Local, OpenAi, AzureOpenAi };

    /// <summary>把任意來源（DB／appsettings／表單）的值正規化成上面三個標準寫法之一；
    /// 空白或無法辨識一律視為 <see cref="Local"/>。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Local;
        var trimmed = value.Trim();
        foreach (var known in All)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase)) return known;
        }
        Log.Warn("無法辨識的 AI Provider 設定值「{0}」，已退回 {1}", value, Local);
        return Local;
    }

    public static bool Is(string? value, string provider) =>
        string.Equals(Normalize(value), provider, StringComparison.Ordinal);

    /// <summary>模型名稱留空時的預設：本機端點沿用歷來的 <c>local-model</c>，其他提供者必填、不給預設。</summary>
    public static string DefaultModel(string? provider) =>
        Is(provider, Local) ? "local-model" : string.Empty;

    /// <summary>
    /// 「AI 是否算設定完成」的唯一定義——本機看位址、OpenAI 官方看金鑰、Azure 要位址＋部署名稱。
    /// <c>AiSettings.IsConfigured</c>（批次）與 <c>WebAiService</c>（互動）都呼叫這裡，
    /// 分開寫遲早漂移成「首頁說可用、實際建不出客戶端」。
    /// </summary>
    public static bool IsConfigured(string? provider, string? baseUrl, string? apiKeyOrEnc, string? azureDeployment)
    {
        var p = Normalize(provider);
        if (p == OpenAi) return !string.IsNullOrWhiteSpace(apiKeyOrEnc);
        if (p == AzureOpenAi) return !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(azureDeployment);
        return !string.IsNullOrWhiteSpace(baseUrl);
    }
}
