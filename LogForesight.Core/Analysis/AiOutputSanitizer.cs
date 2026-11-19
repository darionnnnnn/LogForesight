using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LogForesight.Core.Analysis;

/// <summary>
/// AI 原始回覆的清洗（docs/archive/FEEDBACK-3-PLAN.md #7）：地端推理型模型偶爾漏出內部的
/// channel 分段標記（思考過程／最終回覆），且思考與回覆常夾雜簡體中文——
/// PromptGuidelines 的提示詞約束擋不住模型的內在行為，這裡是輸出端的第二道防線。
///
/// <see cref="AIService.ChatAsync"/> 是唯一呼叫點：批次五層分析、Web 互動卡、
/// 詳情頁對話全部經此清洗，不需要各自處理。
/// </summary>
internal static class AiOutputSanitizer
{
    // 實測看過 <|channel|>thought 與缺結尾豎線的 <|channel>thought 兩種變體，
    // 第二個豎線設為可選（\|?）才能同時接住兩種格式
    private static readonly Regex ChannelMarker = new(@"<\|channel\|?>\s*([a-zA-Z_]+)", RegexOptions.Compiled);
    private static readonly Regex MessageMarker = new(@"<\|message\|?>", RegexOptions.Compiled);

    // 上面兩者之外殘留的任何 <|...|> / <|...> token（如 <|start|>、<|end|>）一律視為雜訊剝除
    private static readonly Regex AnyToken = new(@"<\|[^>]*\|?>", RegexOptions.Compiled);

    private static readonly Lazy<Func<string, string>> TraditionalConverter =
        new(() => OpenCC.OpenCC.Converter("cn", "twp"));

    /// <summary>
    /// 清洗＋簡轉繁（只轉敘述文字，回顯欄位原樣，見 <see cref="ConvertNarrative"/>）。回 null＝清洗後無有效內容（例如整段皆思考、final 段被截斷）——
    /// 呼叫端據此視同空回應觸發既有重試／降級，不把半截思考當成回覆流進分析結果。
    /// </summary>
    public static string? Sanitize(string content)
    {
        var cleaned = StripChannels(content);
        cleaned = AnyToken.Replace(cleaned, "").Trim();

        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        return ConvertNarrative(cleaned);
    }

    /// <summary>
    /// 回顯欄位的鍵名（帳號、主機名、事件原文等 AI 照抄輸入的值）：這些值必須與輸入逐字相同，
    /// 簡轉繁會把「后」「发」之類的字改掉，帳號與主機名就對不上原始資料。比對不分大小寫。
    /// </summary>
    internal static readonly IReadOnlySet<string> VerbatimKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "host", "hosts", "hostname", "host_name", "account", "accounts", "account_name", "user", "users",
        "username", "user_name", "ip", "ip_address", "source", "event", "events", "event_text", "event_message",
        "message", "messages", "raw", "raw_message", "sample", "samples", "sample_message", "log", "logs",
    };

    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // 行內程式碼（`...`）：自由文字回覆裡模型慣用它標出帳號、主機名與指令，整段原樣保留
    private static readonly Regex CodeSpan = new(@"`[^`\r\n]+`", RegexOptions.Compiled);

    /// <summary>
    /// 簡轉繁只作用於敘述文字：JSON 回覆逐欄走訪，字串值轉換，<see cref="VerbatimKeys"/> 下的值（含巢狀）原樣保留；
    /// 非 JSON 的自由文字整段轉換，但 `行內程式碼` 原樣保留。JSON 前後夾帶的前言／結語視為敘述一併轉換。
    /// 轉換只改字，不產生 JSON 結構字元，所以走訪後重新序列化的結果仍是合法 JSON。
    /// </summary>
    internal static string ConvertNarrative(string cleaned)
    {
        var start = cleaned.IndexOfAny(['{', '[']);
        var end = cleaned.LastIndexOfAny(['}', ']']);
        if (start >= 0 && end > start)
        {
            try
            {
                var node = JsonNode.Parse(cleaned[start..(end + 1)]);
                if (node != null)
                {
                    var converted = ConvertNode(node, verbatim: false);
                    return ConvertText(cleaned[..start]) + converted.ToJsonString(JsonOut) + ConvertText(cleaned[(end + 1)..]);
                }
            }
            catch (JsonException)
            {
                // 不是合法 JSON（截斷、重複鍵…）：落到自由文字路徑，交給下游的解析與重試處理
            }
        }

        return ConvertText(cleaned);
    }

    private static JsonNode ConvertNode(JsonNode node, bool verbatim)
    {
        switch (node)
        {
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = value == null ? null : ConvertNode(value, verbatim || VerbatimKeys.Contains(key));
                }
                return result;
            case JsonArray arr:
                var list = new JsonArray();
                foreach (var item in arr)
                {
                    list.Add(item == null ? null : ConvertNode(item, verbatim));
                }
                return list;
            case JsonValue v when !verbatim && v.TryGetValue<string>(out var text):
                return JsonValue.Create(TraditionalConverter.Value(text))!;
            default:
                return node.DeepClone();
        }
    }

    private static string ConvertText(string text)
    {
        if (text.Length == 0) return text;
        var sb = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in CodeSpan.Matches(text))
        {
            sb.Append(TraditionalConverter.Value(text[last..m.Index])).Append(m.Value);
            last = m.Index + m.Length;
        }
        sb.Append(TraditionalConverter.Value(text[last..]));
        return sb.ToString();
    }

    /// <summary>
    /// 只保留最後一個 final 段（channel 標記後緊接 message 標記之後、下一個 channel 標記
    /// 之前的文字）；沒有 channel 標記時原樣回傳（下游 AnyToken 仍會掃一次殘留 token）；
    /// 有 channel 標記但沒有 final 段（例如 final 段被 max_tokens 截斷前根本沒生成到）
    /// 視為整段皆思考，回空字串。
    /// </summary>
    private static string StripChannels(string content)
    {
        var matches = ChannelMarker.Matches(content);
        if (matches.Count == 0) return content;

        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var marker = matches[i];
            if (!string.Equals(marker.Groups[1].Value, "final", StringComparison.OrdinalIgnoreCase)) continue;

            var afterMarker = content[(marker.Index + marker.Length)..];
            var messageMatch = MessageMarker.Match(afterMarker);
            var start = messageMatch.Success ? messageMatch.Index + messageMatch.Length : 0;

            // 下一個 channel 標記的位置（若有）是這個 final 段的結尾；沒有下一段就到字尾
            var end = i + 1 < matches.Count
                ? Math.Max(start, matches[i + 1].Index - (marker.Index + marker.Length))
                : afterMarker.Length;

            return afterMarker[start..Math.Min(end, afterMarker.Length)];
        }

        return "";
    }
}
