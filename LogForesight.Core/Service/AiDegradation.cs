using LogForesight.Core.Analysis;

namespace LogForesight.Core.Service;

/// <summary>
/// AI 回覆格式異常降級的單一規則：重試上限、規則層標題（結論）與後綴的組字。
/// 排程寫回（<c>EfAnalysisRecordStore.AttachAiResult</c>）與 <see cref="LogAnalysisService"/> 共用這一份，
/// 次數只在寫入端的交易裡才知道，所以結論由分析端產生、後綴由寫入端依次數補上。
/// </summary>
public static class AiDegradation
{
    /// <summary>同一主機日連續幾次格式異常後放棄（不再列入待補）</summary>
    public const int MaxAttempts = 3;

    /// <summary>規則層結論的長度上限：規則說明可由使用者自訂，超長會把儀表板的標題欄撐開</summary>
    public const int MaxConclusionChars = 80;

    /// <summary>降級後綴（還會再試）的前後段，中間夾次數</summary>
    public const string PendingSuffixHead = "（AI 摘要待補，第 ";
    public const string PendingSuffixTail = " 次）";

    /// <summary>放棄後綴（已達上限不再重試）</summary>
    public static readonly string AbandonedSuffix = $"（AI 摘要已放棄：連續 {MaxAttempts} 次格式異常）";

    /// <summary>AI 服務完全沒回應時的後綴：這種主機日維持待補，前端徽章是「AI 分析中」，失敗要靠標題講清楚</summary>
    public const string ServiceFailedSuffix = "（AI 服務未回應，AI 待補）";

    /// <summary>次數是否已達放棄上限</summary>
    public static bool IsAbandoned(int attempts) => attempts >= MaxAttempts;

    /// <summary>
    /// 規則層結論（沒有 AI 白話時給人看的一句話）：命中規則取規則的白話說明；沒有命中規則取最嚴重問題的
    /// 來源與次數；連問題都沒有時退回「今日偵測到 N 個問題」。已被抑制的問題不參與挑選（只在全部被抑制時才使用）。
    /// </summary>
    public static string BuildRuleLayerHeadline(IReadOnlyList<LogIssueSignature> issues)
    {
        if (issues.Count == 0) return "今日偵測到 0 個問題，請查看規則層明細";

        var pool = issues.Where(i => !i.Suppressed).ToList();
        if (pool.Count == 0) pool = issues.ToList();

        static IOrderedEnumerable<LogIssueSignature> Rank(IEnumerable<LogIssueSignature> source) => source
            .OrderByDescending(i => i.ElevatesDayRisk)
            .ThenByDescending(i => i.Severity)
            .ThenByDescending(i => i.Count);

        var ruleHit = Rank(pool.Where(i => !string.IsNullOrWhiteSpace(i.KnownIssue))).FirstOrDefault();
        if (ruleHit != null) return Clamp(ruleHit.KnownIssue!.Trim());

        var worst = Rank(pool).First();
        var label = worst.SourceEventLabel;
        return Clamp(string.IsNullOrWhiteSpace(label)
            ? $"今日偵測到 {issues.Count} 個問題"
            : $"{label} 今日出現 {worst.Count} 次");
    }

    /// <summary>AI 服務完全失敗的標題：有問題時與降級同一份規則層結論；沒有問題時沒有結論可講，只說摘要暫缺。</summary>
    public static string BuildServiceFailedHeadline(IReadOnlyList<LogIssueSignature> issues) =>
        (issues.Count == 0 ? "今日分析摘要暫缺" : BuildRuleLayerHeadline(issues)) + ServiceFailedSuffix;

    /// <summary>結論加後綴：未達上限為「第 N 次」、達上限為放棄字樣。結論先截斷，後綴一定完整保留。</summary>
    public static string ComposeHeadline(string conclusion, int attempts)
    {
        var suffix = IsAbandoned(attempts) ? AbandonedSuffix : $"{PendingSuffixHead}{attempts}{PendingSuffixTail}";
        return Clamp(conclusion) + suffix;
    }

    private static string Clamp(string s) =>
        s.Length <= MaxConclusionChars ? s : s[..(MaxConclusionChars - 1)] + "…";
}
