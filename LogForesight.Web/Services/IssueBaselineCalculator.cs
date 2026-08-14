using LogForesight.Core.Persistence;

namespace LogForesight.Web.Services;

/// <summary>
/// 機房級基準線（回饋十九輪批次G1）：重點問題卡與依問題視角共用同一份計算，兩頁的
/// 「vs 基準」數字才必然一致——與 <see cref="IssueRankingBuilder"/> 抽出共用投影同一個理由。
///
/// 基準＝過去 30 天（至查詢期間終點止，不是真實今天——同批次C「不另外抓一次真實時鐘」的既定原則）
/// 出現日台數的中位數；偏離倍數＝最近一次出現日台數 ÷ 基準。刻意只對**出現過的日子**取中位數，
/// 不把沒出現的日子補零——問題本來就是零星出現，用日曆天數當分母會讓偶發問題的基準恆趨近於零，
/// 偏離倍數因此永遠爆表，失去「這次是不是異常擴散」的判斷力。
/// </summary>
public static class IssueBaselineCalculator
{
    public const int WindowDays = 30;

    /// <summary>基準期出現不足這個天數＝「新問題，無基準」（規劃定案 N=3）：太少樣本的中位數不可靠，
    /// 且新問題本來就沒有「平常長什麼樣」可言。</summary>
    public const int MinOccurrenceDays = 3;

    /// <summary>基準期是查詢期間的終點往前推 30 天（含終點當天），與查詢視窗本身無關——
    /// 看「近 7 天」或歷史報表期間時，基準都是同一套「這個問題平常長什麼樣」。</summary>
    public static (DateTime From, DateTime To) Window(DateTime periodEnd) =>
        (periodEnd.Date.AddDays(-(WindowDays - 1)), periodEnd.Date);

    public readonly record struct Baseline(
        int OccurrenceDays, double? MedianHostCount, int? LatestHostCount, double? DeviationMultiplier);

    /// <summary>依 (Source, EventId) 分組計算基準；鍵正規化為大寫 Source，呼叫端查詢時也要
    /// 用 <c>ToUpperInvariant()</c>——與本檔其餘查詢方法的 wanted-normalization 慣例一致。</summary>
    public static Dictionary<(string SourceKey, int EventId), Baseline> Compute(IReadOnlyList<IssueDailyHostCount> days)
    {
        var result = new Dictionary<(string, int), Baseline>();

        foreach (var group in days.GroupBy(d => (SourceKey: d.Source.ToUpperInvariant(), d.EventId)))
        {
            var ordered = group.OrderBy(d => d.Date).ToList();

            if (ordered.Count < MinOccurrenceDays)
            {
                result[group.Key] = new Baseline(ordered.Count, null, null, null);
                continue;
            }

            var median = Median(ordered.Select(d => d.HostCount).OrderBy(x => x).ToList());
            var latest = ordered[^1].HostCount;
            // median 在 Count>=MinOccurrenceDays 時必然 >= 1（出現日的主機數恆為正整數），
            // 這裡的 0 防禦只是不假設上述前提永遠成立
            var multiplier = median > 0 ? latest / median : (double?)null;

            result[group.Key] = new Baseline(ordered.Count, median, latest, multiplier);
        }

        return result;
    }

    private static double Median(List<int> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
