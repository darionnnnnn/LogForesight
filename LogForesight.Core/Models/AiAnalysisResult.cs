using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogForesight.Core.Models;

/// <summary>
/// AI 回傳 JSON 的容錯解析。即使 response_format 已在 server 端強制 JSON，實務上仍會遇到：
/// (1) 模型在 JSON 前後加了前言/推理文字或 markdown 圍欄 → 用括號配對掃描抓出真正的 JSON 物件，
///     而不是天真地抓「第一個 { 到最後一個 }」（前言或雜訊中若混有其他大括號會直接抓錯範圍）；
/// (2) 回覆在 max_tokens 用盡時被攔腰截斷，JSON 物件缺收尾括號 → 嘗試自動補上括號後再解析一次。
/// </summary>
internal static class AiJson
{
    public static T? TryParse<T>(string raw) where T : class => TryParseDetailed<T>(raw).Value;

    /// <summary>
    /// <see cref="TryParse{T}"/> 的帶診斷版本（舊版直接委派到這裡），另外帶出失敗時的證據——最後一個 <see cref="JsonException"/>，
    /// 以及「哪個候選語法合法、卻無法繫結到型別」。診斷只在失敗路徑多做事，成功路徑沒有額外成本。
    /// <paramref name="accept"/> 是呼叫端的內容檢查：回傳第一個通過檢查的候選；都沒通過時回傳第一個解析得出的候選，
    /// 讓呼叫端照舊走內容檢查失敗路徑。這樣思考在前（<c>{"note":"x"}</c>＋答案）與答案在前（答案＋尾端小物件）兩種回覆都選得對。
    /// </summary>
    public static AiJsonParseOutcome<T> TryParseDetailed<T>(string raw, Func<T, bool>? accept = null) where T : class
    {
        T? longestParsed = null;
        var longestLength = -1;
        JsonException? lastException = null;
        JsonException? lastSyntaxValidException = null;

        // 孤立代理字元會讓 JsonSerializer／JsonDocument 擲 ArgumentException（不是 JsonException），
        // 例外一路冒到呼叫端；先換成 U+FFFD，讓它走一般的解析失敗路徑
        raw = ReplaceLoneSurrogates(raw);

        foreach (var candidate in Candidates(raw))
        {
            try
            {
                var result = JsonSerializer.Deserialize<T>(candidate);
                if (result != null)
                {
                    if (accept != null && accept(result))
                    {
                        return new AiJsonParseOutcome<T>(result, null, null);
                    }
                    // 沒有內容檢查（或都沒通過）時取最長的：答案通常遠大於思考裡的 {} 或尾端的小物件，
                    // 不論它排在前面或後面都選得對；只看順序的話總有一種排法會選到空欄位還判成功
                    if (candidate.Length > longestLength)
                    {
                        longestParsed = result;
                        longestLength = candidate.Length;
                    }
                }
            }
            catch (JsonException ex)
            {
                // 換下一個候選區段
                lastException = ex;
                if (IsSyntaxValid(candidate))
                {
                    lastSyntaxValidException = ex;
                }
            }
        }

        return longestParsed != null
            ? new AiJsonParseOutcome<T>(longestParsed, null, null)
            : new AiJsonParseOutcome<T>(null, lastException, lastSyntaxValidException);
    }

    /// <summary>
    /// 把孤立的高／低代理字元換成 U+FFFD（成對的代理字元原樣保留）。字串沒有孤立代理字元時回傳同一個實例。
    /// </summary>
    internal static string ReplaceLoneSurrogates(string raw)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            var lone = char.IsHighSurrogate(c)
                ? !(i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(raw[i - 1]));
            if (lone)
            {
                sb ??= new StringBuilder(raw, 0, i, raw.Length);
                sb.Append('\uFFFD');
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? raw;
    }

    /// <summary>候選字串本身是不是合法 JSON 語法（與能否繫結到目標型別無關）；只在解析失敗時呼叫。</summary>
    private static bool IsSyntaxValid(string candidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 回覆是否有「開了卻沒收尾」的跡象：從第一個 '{' 或 '[' 起算，括號（或字串）到結尾仍未閉合。
    /// 只供診斷分辨「被截斷」與其他格式問題；陣列開頭也要算（<c>[{"a":1}</c> 是被截斷，不是沒有 JSON），
    /// 所以不沿用只從 '{' 起算的 <see cref="TryRepairTruncated"/>，解析候選的順序與結果不受影響。
    /// </summary>
    public static bool HasUnclosedBrackets(string raw)
    {
        var start = raw.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return false;

        var depth = 0;
        bool inString = false, escape = false;
        foreach (var c in raw.AsSpan(start))
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
                case '{' or '[': depth++; break;
                case '}' or ']': if (depth > 0) depth--; break;
            }
        }

        return depth > 0 || inString;
    }

    /// <summary>
    /// 候選順序（第 63-2 輪 B-a-3）：原文 → 最後一個配對物件 → 其餘配對物件（原順序）→ 截斷修復 →
    /// 從每個 '{' 獨立掃描的物件（最後一個優先）。重複的候選只試一次。
    /// 最後一個優先，是因為會先思考的模型把思考寫在前、答案寫在後；思考文字裡的 <c>{}</c> 或範例物件
    /// 若排在前面，會被當成「全空欄位」的結果解析成功而蓋掉真正的答案。
    /// 思考文字含奇數個 <c>"</c> 時，連續追蹤字串狀態的配對掃描會整段翻轉、吞掉真正的物件，
    /// 所以掃描結束時仍卡在字串裡（而且沒有未閉合的括號）就改用獨立掃描的結果；
    /// 其餘情況獨立掃描只當最後的備援，排在截斷修復之後（截斷物件內層的小物件不會搶在修復之前被採用）。
    /// </summary>
    private static IEnumerable<string> Candidates(string raw)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        if (tried.Add(raw)) yield return raw;

        var balanced = ExtractBalancedObjects(raw, out var stringStateBroken);
        List<string>? independent = null;
        if (stringStateBroken)
        {
            independent = ExtractIndependentObjects(raw);
            balanced = independent;
        }

        foreach (var candidate in LastFirst(balanced))
        {
            if (tried.Add(candidate)) yield return candidate;
        }

        // 括號配對掃描到最後仍有未閉合的物件，代表輸出很可能被 max_tokens 截斷；
        // 嘗試補上缺少的收尾括號後再試一次解析，而不是直接放棄
        var repaired = TryRepairTruncated(raw);
        if (repaired != null && tried.Add(repaired))
        {
            yield return repaired;
        }

        independent ??= ExtractIndependentObjects(raw);
        foreach (var candidate in LastFirst(independent))
        {
            if (tried.Add(candidate)) yield return candidate;
        }
    }

    /// <summary>最後一個先，其餘維持原順序</summary>
    private static IEnumerable<string> LastFirst(List<string> items)
    {
        if (items.Count == 0) yield break;
        yield return items[^1];
        for (var i = 0; i < items.Count - 1; i++) yield return items[i];
    }

    /// <summary>獨立掃描時最多嘗試幾個起點；思考文字裡有大量未閉合的 '{' 時不讓掃描變成二次方</summary>
    private const int MaxIndependentScans = 64;

    /// <summary>
    /// 從每個 '{' 起<b>各自獨立</b>掃描（每個起點的字串狀態從頭算），回傳互不重疊的配對完整物件，依出現順序。
    /// 起點掃不到收尾時：該起點視為被截斷或被奇數引號干擾，只改從「掃描中落在字串裡的 '{'」繼續——
    /// 落在字串外、深度大於 0 的 '{' 是它的內層物件，不當成候選（否則截斷物件的內層小物件會被當答案）。
    /// </summary>
    private static List<string> ExtractIndependentObjects(string raw)
    {
        var results = new List<string>();
        var pos = 0;
        var scans = 0;
        while (scans++ < MaxIndependentScans)
        {
            var start = raw.IndexOf('{', pos);
            if (start < 0) break;

            var end = ScanBalancedEnd(raw, start, out var firstInStringBrace);
            if (end >= 0)
            {
                results.Add(raw[start..(end + 1)]);
                pos = end + 1;
            }
            else if (firstInStringBrace >= 0)
            {
                pos = firstInStringBrace;
            }
            else
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// 從 <paramref name="start"/>（必為 '{'）起、字串狀態從頭算，找配對完整的收尾位置；找不到回 -1。
    /// <paramref name="firstInStringBrace"/> 是掃描過程中第一個落在字串裡的 '{' 位置（沒有為 -1）。
    /// </summary>
    private static int ScanBalancedEnd(string raw, int start, out int firstInStringBrace)
    {
        firstInStringBrace = -1;
        var depth = 0;
        bool inString = false, escape = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (escape) { escape = false; }
                else if (c == '\\') { escape = true; }
                else if (c == '"') { inString = false; }
                else if (c == '{' && firstInStringBrace < 0) { firstInStringBrace = i; }
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// 掃描字串中所有「括號配對完整」的 {...} 區段（正確跳過字串內容中的括號），
    /// 依出現順序回傳。比天真的「第一個 { 到最後一個 }」更精準：
    /// 前言文字或多個 JSON 區段混雜時，仍能抓到真正配對完整的物件。
    /// </summary>
    private static List<string> ExtractBalancedObjects(string raw, out bool stringStateBroken)
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

        // 掃完仍卡在字串裡、括號卻都閉合了：物件之間（思考文字）有奇數個引號，字串狀態翻轉過，
        // 這份結果不可信（真正的物件可能被當成字串內容吞掉）。被截斷的物件則是 depth 大於 0，不在此列
        stringStateBroken = inString && depth == 0;
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
/// <see cref="AiJson.TryParseDetailed{T}"/> 的結果。<paramref name="Value"/> 與舊版 TryParse 的回傳值相同；
/// <paramref name="LastException"/> 是最後一個失敗候選的 <see cref="JsonException"/>；
/// <paramref name="LastSyntaxValidException"/> 是最後一個「語法合法、卻無法繫結到型別」的候選所擲的例外
/// （非 null 即代表型別不符，其 Path 指到不符的欄位）。
/// </summary>
internal readonly record struct AiJsonParseOutcome<T>(
    T? Value, JsonException? LastException, JsonException? LastSyntaxValidException) where T : class;

/// <summary>
/// 每日總覽分析（第一階段呼叫）的 JSON 契約。
/// 2026-07-20 AI 角色轉換：AI 不再是分析引擎，是把程式已算好的結論翻譯成白話的角色
/// （見 docs/archive/HISTORY.md）。risk_level 仍保留——AI 的判斷只能把風險等級往上拉，
/// 不能往下壓（RiskLevels.MoreSevere），是零成本的安全網，機制不變。
/// 結構化的目的：後續要接 Email / Telegram / webhook 等自動化動作時，
/// 直接取欄位使用，不需要再從自然語言文字裡撈資訊。
/// </summary>
internal class AiAnalysisResult
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
