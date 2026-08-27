namespace LogForesight.Web.Services;

/// <summary>
/// 問題優先度分數最小版（回饋十九輪批次G3，規劃 §使用者定案：常數寫死、無設定、無接口）。
///
/// 取代單純的「嚴重度→主機數→總次數」排序：那套排序看不出「這個問題今天特別該優先處理」——
/// 一個天天都有、影響很廣的低嚴重度背景值（如 DCOM 10016）會恆占高名次，而一個剛從 3 台
/// 擴散到 30 台的中嚴重度問題（真正的異常訊號）卻可能排在後面。分數綜合六個維度後排序，
/// 才答得出「為什麼是這個問題該先看」。
///
/// 公式（常數係使用者定案，微調需重新走定案流程，不是這裡能單方面調整的參數）：
/// <code>
/// score = 100 × severityW × hostRatioFactor × spreadW × noveltyW × openW × tierW
///   severityW      高=1.0 / 中=0.6 / 低=0.3（去重後最高嚴重度）
///   hostRatioFactor 0.5 + 0.5×hostRatio（影響率越高分數越高，但不是線性到零——1 台也有基本分）
///   spreadW        基準偏離倍數 d → clamp(0.6 + 0.2×log2(max(d,1)), 0.6, 1.6)；無基準=1.2（新問題）
///   noveltyW       fleet first-seen ≤7 天=1.3，≤30 天=1.1，否則 1.0
///   openW          0.5 + 0.5×(OpenHostCount / HostCount)（全處理完→折半，呼應 §10.6 不霸佔版面的精神）
///   tierW          受影響主機最高分級 核心=1.2 / 一般=1.0 / 測試=0.7
/// </code>
/// </summary>
public static class IssuePriorityScorer
{
    public readonly record struct ScoreInput(
        IssueSeverity MaxSeverity,
        double HostRatio,
        double? BaselineDeviationMultiplier,
        int FleetFirstSeenDaysAgo,
        int OpenHostCount,
        int HostCount,
        string HighestAffectedTier);

    /// <summary>六個成分權重＋合成後的總分——展開列「為什麼是 N 分」直接顯示這組數字，
    /// 不必呼叫端重算一次來拆解總分怎麼來的。</summary>
    public readonly record struct ScoreResult(
        double Total,
        double SeverityWeight,
        double HostRatioFactor,
        double SpreadWeight,
        double NoveltyWeight,
        double OpenWeight,
        double TierWeight);

    public static ScoreResult Score(ScoreInput input)
    {
        var severityW = input.MaxSeverity switch
        {
            IssueSeverity.High or IssueSeverity.Critical => 1.0,
            IssueSeverity.Medium => 0.6,
            _ => 0.3
        };

        var hostRatioFactor = 0.5 + 0.5 * input.HostRatio;

        var spreadW = input.BaselineDeviationMultiplier is { } d
            ? Math.Clamp(0.6 + 0.2 * Math.Log2(Math.Max(d, 1)), 0.6, 1.6)
            : 1.2;

        var noveltyW = input.FleetFirstSeenDaysAgo <= 7 ? 1.3
            : input.FleetFirstSeenDaysAgo <= 30 ? 1.1
            : 1.0;

        // HostCount=0 理論上不會發生（沒有主機的問題不會出現在排行裡），這裡的防禦
        // 只是不假設上述前提永遠成立——退回折半（同「全處理完」的既有語意）
        var openW = input.HostCount > 0
            ? 0.5 + 0.5 * ((double)input.OpenHostCount / input.HostCount)
            : 0.5;

        var tierW = input.HighestAffectedTier switch
        {
            WebHost.TierCore => 1.2,
            WebHost.TierTest => 0.7,
            _ => 1.0
        };

        var total = 100 * severityW * hostRatioFactor * spreadW * noveltyW * openW * tierW;

        return new ScoreResult(total, severityW, hostRatioFactor, spreadW, noveltyW, openW, tierW);
    }
}
