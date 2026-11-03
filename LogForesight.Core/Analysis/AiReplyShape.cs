namespace LogForesight.Core.Analysis;

/// <summary>
/// 回覆形狀（<see cref="AiReplyShape.Classify"/> 的結果）。<see cref="Shape"/> 為 object／fence／array／text／blank；
/// <see cref="CutOff"/> 表示保留下來的字數已達上限，**後面可能還有被截掉的內容，不推測它是什麼**。
/// </summary>
public sealed record AiReplyShapeResult(string Shape, bool CutOff);

/// <summary>
/// 「AI 回覆格式異常」案例的回覆類別判定（第 63 輪批次 F-4）。案例只保留回覆的前 N 字
/// （<c>TextTruncation.Truncate</c> 超過上限會截成上限字數再接「...」），所以只能從開頭判斷形狀，
/// 結尾是否完整無從得知——這裡只負責回報「看起來達到上限」，不猜被截掉的部分。
/// </summary>
public static class AiReplyShape
{
    public const string Object = "object";
    public const string Fence = "fence";
    public const string Array = "array";
    public const string Text = "text";
    public const string Blank = "blank";

    /// <summary>
    /// 判斷保留下來的回覆開頭是什麼形狀。略過開頭空白後：以 <c>{</c> 開頭為 object、
    /// 以 Markdown 圍欄（<c>```</c> 或 <c>~~~</c>）開頭為 fence、以 <c>[</c> 開頭為 array、其他文字為 text、
    /// 空字串或全空白為 blank。
    /// </summary>
    /// <param name="reply">保留下來的回覆（呼叫端已去掉降級摘要的說明前綴）</param>
    /// <param name="savedLimit">保存時的字數上限；回覆長度 <b>大於或等於</b>它就標 cut_off。
    /// 剛好等於上限也標：原文剛好這麼長與被截斷在無法區分，保守標記。blank 不標（沒有內容可截）</param>
    public static AiReplyShapeResult Classify(string? reply, int savedLimit)
    {
        if (string.IsNullOrWhiteSpace(reply)) return new AiReplyShapeResult(Blank, false);

        var cutOff = savedLimit > 0 && reply.Length >= savedLimit;
        var head = reply.AsSpan().TrimStart();
        string shape;
        if (head[0] == '{') shape = Object;
        else if (head[0] == '[') shape = Array;
        else if (head.StartsWith("```", StringComparison.Ordinal) || head.StartsWith("~~~", StringComparison.Ordinal)) shape = Fence;
        else shape = Text;
        return new AiReplyShapeResult(shape, cutOff);
    }
}
