using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight;

/// <summary>
/// AI 回傳 JSON 的容錯解析。即使 response_format 已在 server 端強制 JSON，實務上仍會遇到：
/// (1) 模型在 JSON 前後加了前言/推理文字或 markdown 圍欄 → 用括號配對掃描抓出真正的 JSON 物件，
///     而不是天真地抓「第一個 { 到最後一個 }」（前言或雜訊中若混有其他大括號會直接抓錯範圍）；
/// (2) 回覆在 max_tokens 用盡時被攔腰截斷，JSON 物件缺收尾括號 → 嘗試自動補上括號後再解析一次。
/// </summary>
public static class AiJson
{
    public static T? TryParse<T>(string raw) where T : class
    {
        foreach (var candidate in Candidates(raw))
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(candidate);
                if (result != null)
                {
                    return result;
                }
            }
            catch (JsonException)
            {
                // 換下一個候選區段
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string raw)
    {
        yield return raw;

        var balanced = ExtractBalancedObjects(raw);
        foreach (var candidate in balanced)
        {
            yield return candidate;
        }

        // 括號配對掃描到最後仍有未閉合的物件，代表輸出很可能被 max_tokens 截斷；
        // 嘗試補上缺少的收尾括號後再試一次解析，而不是直接放棄
        var repaired = TryRepairTruncated(raw);
        if (repaired != null)
        {
            yield return repaired;
        }
    }

    /// <summary>
    /// 掃描字串中所有「括號配對完整」的 {...} 區段（正確跳過字串內容中的括號），
    /// 依出現順序回傳。比天真的「第一個 { 到最後一個 }」更精準：
    /// 前言文字或多個 JSON 區段混雜時，仍能抓到真正配對完整的物件。
    /// </summary>
    private static List<string> ExtractBalancedObjects(string raw)
    {
        var results = new List<string>();
        int depth = 0, start = -1;
        bool inString = false, escape = false;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (inString)
            {
                if (escape) { escape = false; }
                else if (c == '\\') { escape = true; }
                else if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) { start = i; }
                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            results.Add(raw[start..(i + 1)]);
                            start = -1;
                        }
                    }
                    break;
            }
        }

        return results;
    }

    /// <summary>
    /// 從第一個 '{' 開始，用堆疊追蹤 {}/[] 的巢狀順序，依正確的後進先出順序補上缺少的收尾符號
    /// （只統計深度、不記順序的話，物件裡包陣列時會補錯括號種類，產生語法仍不合法的「修復」）。
    /// 回傳 null 代表沒有截斷跡象（本來就平衡，或根本找不到 JSON 起點）。
    /// </summary>
    private static string? TryRepairTruncated(string raw)
    {
        int start = raw.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var content = raw[start..];
        var stack = new Stack<char>();
        bool inString = false, escape = false;

        foreach (char c in content)
        {
            if (inString)
            {
                if (escape) { escape = false; }
                else if (c == '\\') { escape = true; }
                else if (c == '"') { inString = false; }
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' when stack.Count > 0 && stack.Peek() == '}': stack.Pop(); break;
                case ']' when stack.Count > 0 && stack.Peek() == ']': stack.Pop(); break;
            }
        }

        if (stack.Count == 0 && !inString)
        {
            return null; // 已平衡，不需修補
        }

        var sb = new StringBuilder(content);
        if (inString)
        {
            sb.Append('"'); // 截斷發生在字串值中間，先收尾引號
        }
        TrimTrailingComma(sb); // 截斷點若剛好在逗號後（如陣列元素間），先移除懸空的逗號再補括號
        while (stack.Count > 0)
        {
            sb.Append(stack.Pop());
        }
        return sb.ToString();
    }

    private static void TrimTrailingComma(StringBuilder sb)
    {
        int i = sb.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) { i--; }
        if (i >= 0 && sb[i] == ',')
        {
            sb.Length = i;
        }
    }
}

/// <summary>
/// 每日總覽分析（第一階段呼叫）的 JSON 契約。
/// 2026-07-20 AI 角色轉換：AI 不再是分析引擎，是把程式已算好的結論翻譯成白話的角色
/// （見 docs/AI-ROLE-PLAN.md）。risk_level 仍保留——AI 的判斷只能把風險等級往上拉，
/// 不能往下壓（LogAnalysisService.MoreSevere），是零成本的安全網，機制不變。
/// 結構化的目的：後續要接 Email / Telegram / webhook 等自動化動作時，
/// 直接取欄位使用，不需要再從自然語言文字裡撈資訊。
/// </summary>
public class AiAnalysisResult
{
    [JsonPropertyName("risk_level")]
    public string RiskLevel { get; set; } = string.Empty;

    /// <summary>一句話標題，讓不懂 Event Log 的人一眼看懂今天的狀況</summary>
    [JsonPropertyName("headline")]
    public string Headline { get; set; } = string.Empty;

    /// <summary>今天發生什麼的白話敘述，禁用 Event ID 與程式碼層級術語</summary>
    [JsonPropertyName("story")]
    public string Story { get; set; } = string.Empty;

    /// <summary>這是新問題、正在惡化、還是延續中的已知問題——接續前幾天脈絡講</summary>
    [JsonPropertyName("trend_story")]
    public string TrendStory { get; set; } = string.Empty;

    /// <summary>現在該做什麼、多急迫（取代原本的多項 recommendations 清單）</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    public static AiAnalysisResult? TryParse(string raw)
    {
        var result = AiJson.TryParse<AiAnalysisResult>(raw);
        return result != null && (result.Story.Length > 0 || result.Headline.Length > 0 || result.RiskLevel.Length > 0) ? result : null;
    }
}
