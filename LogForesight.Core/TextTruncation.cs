namespace LogForesight;

/// <summary>
/// 純文字截斷（供 AI prompt／報告輸出控制長度）。原本 LogAnalysisService／WeeklyCheckupService／
/// LogAggregator 各自寫一份逐字相同的版本，收斂到這裡。
///
/// SentinelClient.Truncate（截斷後綴「…(截斷)」、預設長度 300）與 AiInsightService.Truncate
/// （擴充方法，截斷後**不加任何後綴**）用途不同，刻意不歸併——各自的差異是實際需求而非疏漏。
/// </summary>
internal static class TextTruncation
{
    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
