namespace LogForesight;

/// <summary>
/// 每日、全主機、確定性的慢速趨勢偵測（2026-07-20 新增，見 docs/PLAN.md「核心設計決策 B」）。
/// 取代原「週六全量體檢找慢速斜線」的偵測職責：<see cref="TrendAnalyzer"/> 只比對「今日 vs 歷史平均」，
/// 若攻擊者或硬體劣化採取每天小幅加量的節奏，會一直躲在單日 2 倍門檻之下，但拉長到「近一週 vs 前一週」
/// 的總量比較就看得出斜線。原本這件事交給每週一次的 AI 體檢判讀，偵測延遲最壞達 7 天且依賴 AI 召回率；
/// 現在改為每日純算術比對，可單元測試、進 --selftest，偵測延遲縮到 1 天，AI 體檢（見
/// <see cref="WeeklyCheckupService"/>）只需要負責「講這段期間的故事」，不再是慢速訊號能否被發現的關鍵。
/// </summary>
public static class SlowTrendAnalyzer
{
    /// <summary>單一比較窗口的天數。近期窗口＝今日＋前 6 天，前期窗口＝再往前 7 天，兩側等長</summary>
    public const int WindowDays = 7;

    /// <summary>近期（含今日）累計次數需達此值才可能觸發，避免零星雜訊被兩期比較放大成假警報</summary>
    private const int MinRecentCount = 10;

    /// <summary>近期累計達前期累計的幾倍視為慢速惡化</summary>
    private const double RisingFactor = 1.5;

    /// <summary>兩側等長的便利多載（不需要知道是否實際完成比對時使用）</summary>
    public static List<string> Apply(List<LogIssueSignature> issues, List<DailyAnalysisRecord> history, DateTime targetDate)
        => Apply(issues, history, targetDate, out _);

    /// <summary>
    /// 比對每個當日事件簽章的「近 7 天（今日＋前 6 天）總量」與「前 7 天總量」，回傳命中的告警說明。
    /// 兩個窗口刻意等長——長度不一致會讓平穩訊號也產生系統性倍率偏差，把門檻實質放寬。
    /// 只在有完整前一期資料（滿 7 天可靠歷史）時才比對，資料不足時保守略過而非用不完整的
    /// 期間硬算，避免主機剛接入前兩週產生誤判。
    /// </summary>
    /// <param name="evaluated">
    /// false = 前期資料不足，本次完全沒有比對（不是「比對過但沒發現」）。呼叫端據此申報偵測缺口——
    /// 靜默跳過會讓「沒告警」被誤讀成「沒問題」，與專案的覆蓋率誠實申報原則相違。
    /// </param>
    public static List<string> Apply(List<LogIssueSignature> issues, List<DailyAnalysisRecord> history, DateTime targetDate,
        out bool evaluated)
    {
        var alerts = new List<string>();
        evaluated = false;

        // DataIncomplete 的日子排除在兩個窗口外，理由與 TrendAnalyzer 相同：
        // 不完整的一天會讓窗口總量偏低，把正常量誤判成「大幅下降後又回升」的假斜線
        var reliableHistory = history.Where(h => !h.DataIncomplete).ToList();

        // 邊界皆為「不含」：WindowDays=7 時 recentWindowStart=targetDate-7，近期窗口取
        // targetDate-6 ~ targetDate-1 共 6 天，加上今日（sig.Count，尚未寫入歷史）剛好 7 天；
        // 前期窗口 targetDate-13 ~ targetDate-7 也是 7 天，兩側等長。
        var recentWindowStart = targetDate.Date.AddDays(-WindowDays);          // 不含
        var priorWindowStart = targetDate.Date.AddDays(-2 * WindowDays);       // 不含

        var recentDays = reliableHistory.Where(h => h.Date.Date > recentWindowStart && h.Date.Date < targetDate.Date).ToList();
        var priorDays = reliableHistory.Where(h => h.Date.Date > priorWindowStart && h.Date.Date <= recentWindowStart).ToList();

        // 前一期資料不足七天（主機剛接入不到兩週、或該區間有 DataIncomplete 的日子），無法可靠比對，整批略過
        if (priorDays.Count < WindowDays)
        {
            return alerts;
        }

        evaluated = true;

        foreach (var sig in issues)
        {
            // 只用「當天實際讀取了該頻道」的歷史日當基準，避免假性零把前期總量墊低造成假斜線——
            // 與 TrendAnalyzer 同一套排除規則（ChannelCoverage.WasRead 一般化了原本的 Security 特例）。
            // 新頻道（Defender/RDP）上線後前期窗口天然不足 WindowDays，會被下方守門整批略過，不需另設暖身。
            var relevantRecent = recentDays.Where(h => ChannelCoverage.WasRead(h, sig.LogName)).ToList();
            var relevantPrior = priorDays.Where(h => ChannelCoverage.WasRead(h, sig.LogName)).ToList();

            if (relevantPrior.Count < WindowDays)
            {
                continue; // 該簽章可用的前期資料不足七天（如 Security 因權限問題長期缺漏），個別略過
            }

            int recentTotal = sig.Count + SumForSignature(relevantRecent, sig);
            int priorTotal = SumForSignature(relevantPrior, sig);

            // priorTotal > 0 是刻意的：這裡要抓的是「已存在、正在惡化」的訊號，不是「本週才出現」——
            // 後者屬於 TrendAnalyzer 的 New 分支職責，兩者不重疊
            if (priorTotal > 0 && recentTotal >= MinRecentCount && recentTotal >= priorTotal * RisingFactor)
            {
                alerts.Add($"慢速惡化：{sig.Source} EventId {sig.EventId} 近 {WindowDays} 天累計 x{recentTotal}" +
                           $"（含今日），前 {WindowDays} 天累計 x{priorTotal}");
            }
        }

        return alerts;
    }

    private static int SumForSignature(List<DailyAnalysisRecord> days, LogIssueSignature sig) =>
        days.Sum(h => h.TopIssues.FirstOrDefault(i => SameIssue(i, sig))?.Count ?? 0);

    private static bool SameIssue(LogIssueSignature a, LogIssueSignature b) =>
        a.LogName == b.LogName && a.Source == b.Source && a.EventId == b.EventId && a.EntryType == b.EntryType;
}
