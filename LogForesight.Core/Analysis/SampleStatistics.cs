namespace LogForesight;

/// <summary>
/// 樣本統計的單一出口：中位數與最近秩位法的索引只在這裡定義。
/// 偶數筆的中位數一律取中間兩值平均（與 TrendAnalyzer、PRTG 趨勢、問題基準、診斷匯出同一定義）。
/// </summary>
public static class SampleStatistics
{
    /// <summary>中位數；偶數筆取中間兩值平均，空集合回 null。</summary>
    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    /// <summary>整數樣本的中位數，定義同 <see cref="Median(IEnumerable{double})"/>。</summary>
    public static double? Median(IEnumerable<int> values) => Median(values.Select(v => (double)v));

    /// <summary>
    /// 最近秩位法（nearest-rank）的索引：第 ceil(fraction × N) 個值（0 起算後減 1），夾在 [0, N-1]；fraction 為 0～1。
    /// 呼叫端保證 count &gt; 0。
    /// </summary>
    public static int NearestRankIndex(int count, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * count);
        if (rank < 1) rank = 1;
        if (rank > count) rank = count;
        return rank - 1;
    }
}
