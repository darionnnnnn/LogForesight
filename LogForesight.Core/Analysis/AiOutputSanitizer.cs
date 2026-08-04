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
public static class AiOutputSanitizer
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
    /// 清洗＋簡轉繁。回 null＝清洗後無有效內容（例如整段皆思考、final 段被截斷）——
    /// 呼叫端據此視同空回應觸發既有重試／降級，不把半截思考當成回覆流進分析結果。
    /// </summary>
    public static string? Sanitize(string content)
    {
        var cleaned = StripChannels(content);
        cleaned = AnyToken.Replace(cleaned, "").Trim();

        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        // 轉換套用於清洗後的最終字串：JSON 模式輸出的鍵名是 ASCII 不受影響，
        // 只有值內中文被轉換；OpenCC 只映射中文字詞，不產生 "／\ 等 JSON 結構字元
        return TraditionalConverter.Value(cleaned);
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
