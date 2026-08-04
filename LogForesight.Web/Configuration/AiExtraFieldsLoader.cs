using System.Text.Json;
using NLog;

namespace LogForesight.Web.Configuration;

/// <summary>
/// 繞過 <c>IConfiguration</c> binder，直接讀 appsettings.json 的 <c>Ai:ExtraRequestFields</c> 節點。
///
/// 背景（docs/FEEDBACK-7-PLAN.md）：<c>ConfigurationBinder</c> 綁不出
/// <c>Dictionary&lt;string, JsonElement&gt;</c>——<see cref="JsonElement"/> 沒有可寫屬性、也沒有
/// <c>TypeConverter</c>，binder 對這個型別的欄位只能產生 <c>default(JsonElement)</c>
/// （<c>ValueKind=Undefined</c>）。<see cref="AIService"/> 建構子對這種空值呼叫
/// <c>GetRawText()</c> 會丟 <see cref="InvalidOperationException"/>，導致 Web 觸發的排程／立即
/// 執行必定在分析開始前就中止。改用 <see cref="JsonSerializer"/> 直接反序列化該節點即可正確
/// 產生 JsonElement——與批次 exe（<c>AppSettings.Load</c>）、<c>WebAiService.LoadBatchAiSettings</c>
/// 走的是同一種正確路徑。
/// </summary>
public static class AiExtraFieldsLoader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 依序讀 <c>appsettings.json</c> 與 <c>appsettings.{environmentName}.json</c> 的
    /// <c>Ai:ExtraRequestFields</c>，環境檔有節點時覆蓋基礎檔（與 ASP.NET Core 組態優先序一致）。
    /// 兩者皆無節點回 null——呼叫端應據此回復型別預設值，而不是沿用 binder 產生的壞值。
    /// </summary>
    public static Dictionary<string, JsonElement>? Load(string contentRootPath, string environmentName)
    {
        Dictionary<string, JsonElement>? result = null;

        var fromBase = LoadFromFile(Path.Combine(contentRootPath, "appsettings.json"));
        if (fromBase != null) result = fromBase;

        var fromEnv = LoadFromFile(Path.Combine(contentRootPath, $"appsettings.{environmentName}.json"));
        if (fromEnv != null) result = fromEnv;

        return result;
    }

    private static Dictionary<string, JsonElement>? LoadFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (!doc.RootElement.TryGetProperty("Ai", out var ai)) return null;
            if (!ai.TryGetProperty("ExtraRequestFields", out var extra)) return null;

            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extra.GetRawText(), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn(ex, "解析 {Path} 的 Ai:ExtraRequestFields 失敗，將回復預設值：{Message}", path, ex.Message);
            return null;
        }
    }
}
