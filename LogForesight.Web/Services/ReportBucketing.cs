using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 報表統計的時間分桶（單一出口，處理成效、類型趨勢共用）：
/// 期間 ≤ <see cref="DailyMaxDays"/> 天按日；≤ <see cref="WeeklyMaxDays"/> 天按週（週一起算）；更長按月。
/// 頭尾不足整週／整月的桶截在期間內，<c>Days</c> 為實際涵蓋天數。門檻為暫定值。
/// </summary>
public static class ReportBucketing
{
    /// <summary>暫定：期間不超過這個天數按日</summary>
    public const int DailyMaxDays = 14;

    /// <summary>暫定：期間不超過這個天數按週，超過按月</summary>
    public const int WeeklyMaxDays = 120;

    public readonly record struct Bucket(DateTime From, DateTime To)
    {
        public int Days => (To - From).Days + 1;
    }

    public static List<Bucket> Build(DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date;
        if (end < start) (start, end) = (end, start);

        var span = (end - start).Days + 1;
        var buckets = new List<Bucket>();
        for (var cursor = start; cursor <= end;)
        {
            DateTime bucketEnd;
            if (span <= DailyMaxDays)
            {
                bucketEnd = cursor;
            }
            else if (span <= WeeklyMaxDays)
            {
                // 到本週日為止（週一起算）：DayOfWeek 週日為 0，距週日的天數＝(7 - dow) % 7
                bucketEnd = cursor.AddDays((7 - (int)cursor.DayOfWeek) % 7);
            }
            else
            {
                bucketEnd = new DateTime(cursor.Year, cursor.Month, 1).AddMonths(1).AddDays(-1);
            }

            if (bucketEnd > end) bucketEnd = end;
            buckets.Add(new Bucket(cursor, bucketEnd));
            cursor = bucketEnd.AddDays(1);
        }

        return buckets;
    }

    public static List<ReportBucketDto> ToDtos(IReadOnlyList<Bucket> buckets) =>
        buckets.Select(b => new ReportBucketDto { From = Fmt(b.From), To = Fmt(b.To), Days = b.Days }).ToList();

    /// <summary>日期所屬的桶索引；不在任何桶內回 -1</summary>
    public static int IndexOf(IReadOnlyList<Bucket> buckets, DateTime date)
    {
        var d = date.Date;
        if (buckets.Count == 0 || d < buckets[0].From || d > buckets[^1].To) return -1;
        // 桶連續且依序，二分搜尋
        int lo = 0, hi = buckets.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (d < buckets[mid].From) hi = mid - 1;
            else if (d > buckets[mid].To) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    internal static string Fmt(DateTime d) => d.ToString("yyyy-MM-dd");
}
