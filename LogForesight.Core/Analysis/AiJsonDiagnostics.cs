using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Core.Analysis;

/// <summary>
/// AI JSON 回覆失敗的診斷證據（第 63 輪批次 F）。<see cref="AIService.ChatJsonAsync{T}"/> 最終失敗時，
/// 以獨立 logger <see cref="LoggerName"/> 寫一行 Warn，訊息是單行精簡 JSON（<see cref="AiJsonDiagnosticsEntry"/>）。
///
/// 只蒐集證據，**不影響任何呼叫行為**：提示詞、解析容錯、重試次數與降級結果都不變；
/// 序列化或寫入失敗只記一般 Warn，絕不讓呼叫失敗。
/// 這個 logger 比照 <c>SqlPerformanceMonitor</c>：寫進執行紀錄、但不計入 WarnCount，
/// 也不顯示在執行詳情與異常彙總（見 <see cref="BatchRunRecorder.IsWarnCountExemptLogger"/>）——只給匯出用。
/// </summary>
public static class AiJsonDiagnostics
{
    /// <summary>
    /// 額外欄位裡會強制結構化輸出的鍵（暫定）。關掉文法重試時只拿掉 response_format 不夠：
    /// 站台若在 ExtraRequestFields 設了這些鍵，模型仍被文法擋成 []，那次重試就白費。
    /// </summary>
    internal static readonly IReadOnlySet<string> StructuredOutputKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "response_format", "grammar", "json_schema", "guided_json" };

    /// <summary>
    /// 診斷專用 logger 名稱。**不含句點**，所以 NLog 完整名稱與執行紀錄裡的短名稱（取最後一段）一致；
    /// 判定一律比對這個名稱，不比對訊息內容。
    /// </summary>
    public const string LoggerName = "AiJsonDiagnostics";

    // 失敗類別（值會寫進診斷 JSON，也是匯出契約，不得任意改字）
    public const string CategoryEmpty = "empty";
    public const string CategoryNoJson = "no_json";
    public const string CategoryTruncated = "truncated";
    public const string CategoryTypeMismatch = "type_mismatch";
    public const string CategoryParseOther = "parse_other";
    public const string CategoryValidation = "validation";

    // 空形狀（category=empty 的細分，第 63-2 輪 B-a-2）：回覆去空白後只剩括號——文法擋下思考時最短的合法輸出
    public const string EmptyShapeArray = "array";
    public const string EmptyShapeObject = "object";

    /// <summary>整次呼叫的結束方式：所有嘗試都是內容層失敗</summary>
    public const string EndedExhausted = "exhausted";
    /// <summary>以空回覆結束（空回覆已由 Polly 層重試過，不進 JSON 重試）</summary>
    public const string EndedEmpty = "empty";
    /// <summary>先有內容層失敗，後來遇到傳輸層失敗而結束</summary>
    public const string EndedTransport = "transport_error";

    /// <summary>回覆開頭保留的字元數</summary>
    public const int HeadChars = 1500;
    /// <summary>回覆結尾保留的字元數；總長不超過 <see cref="HeadChars"/>＋<see cref="TailChars"/> 時不放結尾</summary>
    public const int TailChars = 500;

    private static readonly Logger DiagnosticsLog = LogManager.GetLogger(LoggerName);
    // 一般警告（診斷本身出錯）刻意不用 GetCurrentClassLogger：本類別的短名稱就是 AiJsonDiagnostics，
    // 會被當成診斷行而免計、被執行詳情隱藏，出錯的訊息反而看不到
    private static readonly Logger GeneralLog = LogManager.GetLogger("LogForesight.Core.Analysis.AiJsonDiagnosticsWriter");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        // 診斷是給人讀的單行 JSON：中文不跳脫；換行與控制字元仍會被跳脫，所以一定是單行
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>把診斷 JSON（執行紀錄裡該行的訊息）還原成物件；格式不符回 null。</summary>
    public static AiJsonDiagnosticsEntry? TryRead(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiJsonDiagnosticsEntry>(message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判定一次內容層失敗的類別。優先序：空白回覆與空形狀（<c>[]</c>、<c>{}</c>）→ 解析得出（內容檢查沒過）→
    /// 完全沒有 '{' → 括號未閉合或 finish_reason=length（截斷）→ 語法合法卻無法繫結到型別 → 其他解析失敗。
    /// </summary>
    /// <param name="parsed">解析是否得出物件（得出卻失敗＝<c>validate</c> 回 false）</param>
    /// <param name="bindFailedOnValidSyntax">有候選語法合法、卻無法繫結到型別（<see cref="JsonException"/>）</param>
    internal static string Classify(string? content, string? finishReason, bool parsed, bool bindFailedOnValidSyntax)
    {
        if (string.IsNullOrWhiteSpace(content)) return CategoryEmpty;
        if (DetectEmptyShape(content) != null) return CategoryEmpty;
        if (parsed) return CategoryValidation;
        if (!content.Contains('{')) return CategoryNoJson;
        if (AiJson.HasUnclosedBrackets(content)
            || string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            return CategoryTruncated;
        return bindFailedOnValidSyntax ? CategoryTypeMismatch : CategoryParseOther;
    }

    /// <summary>
    /// 回覆是不是「空形狀」：去空白後只剩成對的 <c>[</c>、<c>]</c>、<c>{</c>、<c>}</c>（<c>[]</c>、<c>{}</c>、<c>[\n]</c>、
    /// <c>[{}]</c>…），且括號平衡。回傳 <see cref="EmptyShapeArray"/>（第一個括號是 [ ）或
    /// <see cref="EmptyShapeObject"/>（第一個括號是 { ）；不是空形狀回 null。空白回覆不在此列（由空回覆判定處理）。
    /// 括號不平衡的（例如只有 <c>{</c>）是被截斷，不算空形狀。
    /// </summary>
    internal static string? DetectEmptyShape(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string? shape = null;
        var stack = new Stack<char>();
        foreach (var c in content)
        {
            if (char.IsWhiteSpace(c)) continue;
            switch (c)
            {
                case '[': stack.Push(']'); shape ??= EmptyShapeArray; break;
                case '{': stack.Push('}'); shape ??= EmptyShapeObject; break;
                case ']' or '}':
                    if (stack.Count == 0 || stack.Pop() != c) return null;
                    break;
                default:
                    return null;
            }
        }

        return stack.Count == 0 ? shape : null;
    }

    /// <summary>
    /// 回覆的開頭與結尾。總長不超過 <see cref="HeadChars"/>＋<see cref="TailChars"/> 時整段放進開頭、結尾為 null
    /// （兩段加起來就是全文，不重複也不漏）；超過時開頭 1,500 字、結尾 500 字。
    /// 切點落在代理對中間時往內退一個字元，避免序列化出不合法的 UTF-16。
    /// </summary>
    internal static (string Head, string? Tail) SplitHeadTail(string content)
    {
        if (content.Length <= HeadChars + TailChars) return (content, null);

        var headLength = HeadChars;
        if (char.IsHighSurrogate(content[headLength - 1])) headLength--;
        var tailStart = content.Length - TailChars;
        if (char.IsLowSurrogate(content[tailStart])) tailStart++;
        return (content[..headLength], content[tailStart..]);
    }

    /// <summary>
    /// 解析得出時各欄字串長度：把解析物件序列化後走 <see cref="JsonDocument"/> 取第一層，
    /// 字串記長度、非字串記型別名（<see cref="JsonValueKind"/>）。解析不出或根節點不是物件回 null。
    /// </summary>
    internal static Dictionary<string, object>? MeasureFields<T>(T? parsed) where T : class
    {
        if (parsed == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(parsed));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var fields = new Dictionary<string, object>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!.Length
                    : property.Value.ValueKind.ToString();
            }
            return fields;
        }
        catch (Exception ex)
        {
            GeneralLog.Warn(ex, "AI JSON 診斷：各欄長度計算失敗（不影響呼叫結果）");
            return null;
        }
    }

    /// <summary>
    /// 組出一次內容層失敗的嘗試紀錄。診斷本身出任何錯都回 null（呼叫端略過這筆），不得讓呼叫失敗。
    /// </summary>
    internal static AiJsonAttemptDiagnostics? BuildAttempt<T>(int attempt, AiResponse response, string content,
        AiJsonParseOutcome<T>? outcome) where T : class
    {
        try
        {
            var parsed = outcome?.Value;
            var category = Classify(content, response.FinishReason, parsed != null,
                outcome?.LastSyntaxValidException != null);
            // 孤立代理字元換成 U+FFFD：否則序列化診斷 JSON 會擲例外，整行證據就丟了
            var (head, tail) = SplitHeadTail(AiJson.ReplaceLoneSurrogates(content));
            // 清洗前原文：與清洗後完全相同時不重複記（省日誌容量；讀者以 rawChars 為 null 與否判斷有沒有差別）
            var rawSame = response.RawChars == content.Length && response.RawHead == head && response.RawTail == tail;
            // 型別不符優先報語法合法候選的 Path（指到不符的欄位）；否則報最後一個例外的 Path
            var path = outcome?.LastSyntaxValidException?.Path ?? outcome?.LastException?.Path;
            return new AiJsonAttemptDiagnostics
            {
                Attempt = attempt,
                Category = category,
                FinishReason = response.FinishReason,
                PromptChars = response.PromptChars,
                SystemPromptChars = response.SystemPromptChars,
                ResponseChars = response.ResponseChars ?? content.Length,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens,
                ParseErrorPath = path,
                FieldLengths = MeasureFields(parsed),
                Head = head,
                Tail = tail,
                EmptyShape = DetectEmptyShape(content),
                RawChars = rawSame ? null : response.RawChars,
                RawHead = rawSame ? null : response.RawHead,
                RawTail = rawSame ? null : response.RawTail,
                ReasoningChars = response.ReasoningChars
            };
        }
        catch (Exception ex)
        {
            GeneralLog.Warn(ex, "AI JSON 診斷：第 {Attempt} 次嘗試的紀錄組裝失敗（不影響呼叫結果）", attempt);
            return null;
        }
    }

    /// <summary>
    /// 寫一行診斷。序列化或寫入失敗只記一般 Warn；嘗試紀錄為空（沒有內容層失敗）時什麼都不寫。
    /// </summary>
    internal static void Write(string label, string responseType, string? model, int maxTokens,
        int attemptsMade, string endedBy, IReadOnlyList<AiJsonAttemptDiagnostics> attempts)
    {
        if (attempts.Count == 0) return;
        try
        {
            var entry = new AiJsonDiagnosticsEntry
            {
                Label = label,
                ResponseType = responseType,
                Model = model,
                MaxTokens = maxTokens,
                Attempts = attemptsMade,
                EndedBy = endedBy,
                AttemptDetails = attempts.ToList()
            };
            var json = JsonSerializer.Serialize(entry, WriteOptions);
            // 以參數傳入而不是直接當訊息：JSON 裡的大括號不會被當成訊息範本解析
            DiagnosticsLog.Warn("{Diagnostics}", json);
        }
        catch (Exception ex)
        {
            GeneralLog.Warn(ex, "AI JSON 診斷寫入失敗（不影響呼叫結果）：label={Label}", label);
        }
    }
}

/// <summary>診斷 JSON 的最外層：一次 <c>ChatJsonAsync</c> 最終失敗的完整紀錄。欄位名稱是匯出契約。</summary>
public sealed class AiJsonDiagnosticsEntry
{
    /// <summary>呼叫端傳入的 label（主分析為 <c>daily-{yyyyMMdd}-h{主機 id}</c>），不含 <c>-aN</c> 後綴</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;

    /// <summary>期望的回覆型別名稱（<c>typeof(T).Name</c>）</summary>
    [JsonPropertyName("responseType")] public string ResponseType { get; set; } = string.Empty;

    [JsonPropertyName("model")] public string? Model { get; set; }

    /// <summary>實際生效的 max_tokens 上限（呼叫端覆寫值，否則設定值）</summary>
    [JsonPropertyName("maxTokens")] public int MaxTokens { get; set; }

    /// <summary>實際發出的 JSON 層嘗試次數（含最後遇到傳輸層失敗的那一次）</summary>
    [JsonPropertyName("attempts")] public int Attempts { get; set; }

    /// <summary>exhausted／empty／transport_error，見 <see cref="AiJsonDiagnostics.EndedExhausted"/></summary>
    [JsonPropertyName("endedBy")] public string EndedBy { get; set; } = string.Empty;

    [JsonPropertyName("attemptDetails")] public List<AiJsonAttemptDiagnostics> AttemptDetails { get; set; } = new();
}

/// <summary>一次內容層失敗的嘗試。沒有的資料一律為 null（不省略欄位，讀者才分得出「沒有」與「沒記」）。</summary>
public sealed class AiJsonAttemptDiagnostics
{
    [JsonPropertyName("attempt")] public int Attempt { get; set; }

    /// <summary>empty／no_json／truncated／type_mismatch／parse_other／validation</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;

    [JsonPropertyName("finishReason")] public string? FinishReason { get; set; }
    [JsonPropertyName("promptChars")] public int? PromptChars { get; set; }
    [JsonPropertyName("systemPromptChars")] public int? SystemPromptChars { get; set; }
    [JsonPropertyName("responseChars")] public int? ResponseChars { get; set; }
    [JsonPropertyName("promptTokens")] public int? PromptTokens { get; set; }
    [JsonPropertyName("completionTokens")] public int? CompletionTokens { get; set; }

    /// <summary>解析錯誤位置（<see cref="JsonException.Path"/>，不含內容），例如 <c>$.action</c></summary>
    [JsonPropertyName("parseErrorPath")] public string? ParseErrorPath { get; set; }

    /// <summary>解析得出時各欄：字串為長度（整數），非字串為型別名（字串）；解析不出為 null</summary>
    [JsonPropertyName("fieldLengths")] public Dictionary<string, object>? FieldLengths { get; set; }

    [JsonPropertyName("head")] public string Head { get; set; } = string.Empty;

    /// <summary>回覆結尾；總長不超過 2,000 字時為 null（全文已在 head）</summary>
    [JsonPropertyName("tail")] public string? Tail { get; set; }

    // 以下為第 63-2 輪 B-a-1／B-a-2 新增；舊版診斷行沒有這些欄位，讀回來一律是 null（相容）

    /// <summary>category=empty 且回覆是空形狀時：array（[]）或 object（{}）；其他為 null</summary>
    [JsonPropertyName("emptyShape")] public string? EmptyShape { get; set; }

    /// <summary>清洗前原文字元數（清洗去掉 channel 標記、思考段、簡轉繁之前）；與清洗後完全相同時為 null</summary>
    [JsonPropertyName("rawChars")] public int? RawChars { get; set; }

    /// <summary>清洗前原文的開頭（1,500 字）；與清洗後完全相同時為 null</summary>
    [JsonPropertyName("rawHead")] public string? RawHead { get; set; }

    /// <summary>清洗前原文的結尾（500 字）；總長不超過 2,000 字或與清洗後相同時為 null</summary>
    [JsonPropertyName("rawTail")] public string? RawTail { get; set; }

    /// <summary>伺服器另放的思考欄位（reasoning_content／reasoning）字元數；只記長度。沒有該欄位為 null</summary>
    [JsonPropertyName("reasoningChars")] public int? ReasoningChars { get; set; }
}
