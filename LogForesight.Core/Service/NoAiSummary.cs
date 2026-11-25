using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// 沒有 AI 摘要時全站同一個出口（批次 D：H8／O2）：標題顯示<b>當日最重的問題名稱與白話說明</b>，
/// 風險日詳情的「狀況」不再重複同一句。清單、儀表板、報表與詳情都經這裡，不各自寫佔位字。
/// <list type="bullet">
/// <item>名稱走 <see cref="LogIssueSignature.SourceEventLabel"/>（它的 Windows 格式出自 <see cref="IssueNames.For"/>，Linux 規則與 PRTG 感測器各有顯示規則），
/// 不在這裡另拼「來源＋事件編號」；白話說明取規則表的已知問題說明，沒有才寫「今日出現 N 次」。</item>
/// <item>最重＝重大旗標優先、再依嚴重度、再依次數；已被抑制的問題不參與（全部被抑制時才使用）。</item>
/// <item>當日沒有任何問題：寫「今日未偵測到需要處理的問題」。</item>
/// <item>寫入端（<see cref="LogAnalysisService"/> 的統計模式紀錄）與讀取端（舊紀錄、資料庫聚合）用同一個函式；
/// 讀取端對「空白或舊版佔位字」現場補（<see cref="IsPlaceholder"/>），不必搬資料。</item>
/// </list>
/// </summary>
public static class NoAiSummary
{
    /// <summary>當日沒有任何問題時的標題</summary>
    public const string NoIssueHeadline = "今日未偵測到需要處理的問題";

    /// <summary>
    /// 一個問題的排序與顯示素材（資料庫聚合列沒有完整簽章，用這個最小形狀）。
    /// <see cref="Name"/> 是 <see cref="LogIssueSignature.SourceEventLabel"/> 的結果；資料庫列用 <see cref="NameOf"/> 組。
    /// </summary>
    public readonly record struct Candidate(
        string Name, int SeverityRank, bool ElevatesDayRisk, int Count, string? KnownIssue, bool Suppressed);

    /// <summary>資料庫問題子列（沒有完整簽章）的名稱：只填顯示名稱用得到的欄位，仍走 <see cref="LogIssueSignature.SourceEventLabel"/>。</summary>
    public static string NameOf(string logName, string source, int eventId, string eventKey) =>
        new LogIssueSignature { LogName = logName, Source = source, EventId = eventId, EventKey = eventKey }.SourceEventLabel;

    /// <summary>
    /// 這段文字算不算「沒有摘要」：空白，或舊版統計模式寫進資料庫的佔位括號字
    /// （以「（統計模式…未呼叫 AI…）」的結構辨認，不比對整句，舊資料不必搬）。
    /// 「AI 分析排隊中」等待補狀態的文字不算——那是另一種狀態，畫面以徽章表達。
    /// </summary>
    public static bool IsPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        return t.StartsWith('（') && t.EndsWith('）') && t.Contains("統計模式", StringComparison.Ordinal)
            && t.Contains("未呼叫 AI", StringComparison.Ordinal);
    }

    /// <summary>已存的標題是佔位字或空白時，回當日最重問題的標題；否則原樣回傳。</summary>
    public static string ResolveHeadline(string? stored, IReadOnlyList<LogIssueSignature> issues) =>
        IsPlaceholder(stored) ? HeadlineFor(issues) : stored!;

    /// <summary>已存的摘要是佔位字時回空字串（標題已經說了，不重複）；否則原樣回傳。</summary>
    public static string ResolveSummary(string? stored) =>
        IsPlaceholder(stored) ? string.Empty : stored!;

    public static string HeadlineFor(IReadOnlyList<LogIssueSignature> issues) =>
        HeadlineFor(issues.Select(i => new Candidate(
            i.SourceEventLabel, (int)i.Severity, i.ElevatesDayRisk, i.Count, i.KnownIssue, i.Suppressed)).ToList());

    public static string HeadlineFor(IReadOnlyList<Candidate> issues)
    {
        if (issues.Count == 0) return NoIssueHeadline;

        var pool = issues.Where(i => !i.Suppressed).ToList();
        if (pool.Count == 0) pool = issues.ToList();
        var worst = pool
            .OrderByDescending(i => i.ElevatesDayRisk)
            .ThenByDescending(i => i.SeverityRank)
            .ThenByDescending(i => i.Count)
            .First();

        var name = worst.Name;
        var description = string.IsNullOrWhiteSpace(worst.KnownIssue) ? $"今日出現 {worst.Count} 次" : worst.KnownIssue.Trim();
        return AiDegradation.Clamp(string.IsNullOrWhiteSpace(name) ? description : $"{name}：{description}");
    }
}
