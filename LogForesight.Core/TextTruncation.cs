namespace LogForesight.Core;

/// <summary>
/// 純文字截斷的單一出口（供 AI prompt／報告輸出控制長度，也是資料庫欄位截斷的底層）。
/// 所有「依字元數截到上限」的需求一律經 <see cref="SafeCut"/>：切點落在高代理字元之後時再退一個字元，
/// 不會留下孤立代理字元（孤立代理在 JSON 序列化與 SQL Server 寫入都會出事）。
///
/// SentinelClient（後綴「…(截斷)」、預設長度 300）、AiInsightService（不加後綴）等的後綴與長度需求不同，
/// 差異只在 <paramref name="suffix"/> 與上限，切法統一。
/// </summary>
internal static class TextTruncation
{
    /// <summary>
    /// 超過 <paramref name="max"/> 字元時取前 <paramref name="max"/> 字元（切點落在代理對中間則少取一個字元），
    /// 再接 <paramref name="suffix"/>；未超長原樣回傳（不加後綴）。
    /// </summary>
    public static string SafeCut(string s, int max, string suffix = "")
    {
        if (s.Length <= max) return s;
        var cut = Math.Max(max, 0);
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + suffix;
    }

    /// <summary>同 <see cref="SafeCut"/>，被截掉時結尾補「…」（匯出用）。</summary>
    public static string SafeCutEllipsis(string s, int max) => SafeCut(s, max, "…");

    public static string Truncate(string s, int max) => SafeCut(s, max, "...");
}
