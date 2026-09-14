using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 即時快照數值累積器（Core，純類別、無 IO、執行緒安全）。
/// 責任：收集某一小時內每顆感測器的多個取樣值，整點時產出 <see cref="PrtgValueRow"/> 清單。
/// </summary>
public sealed class PrtgSnapshotAccumulator
{
    private readonly object _lock = new();
    private readonly Dictionary<DateTime, Dictionary<long, SensorAggregate>> _buckets = new();
    private int _sampleCount;

    /// <summary>目前累積器中的小時桶數量（供診斷與測試）。</summary>
    public int BucketCount
    {
        get
        {
            lock (_lock)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>目前累積器中的總樣本數（供診斷與測試）。</summary>
    public int SampleCount
    {
        get
        {
            lock (_lock)
            {
                return _sampleCount;
            }
        }
    }

    /// <summary>
    /// 累積一筆感測器取樣值到其時間所屬的小時桶。
    /// </summary>
    /// <param name="sensorObjid">PRTG 感測器 objid</param>
    /// <param name="sampleTime">取樣時間</param>
    /// <param name="value">量測數值</param>
    public void Add(long sensorObjid, DateTime sampleTime, double value)
    {
        var hour = new DateTime(sampleTime.Year, sampleTime.Month, sampleTime.Day, sampleTime.Hour, 0, 0, DateTimeKind.Unspecified);

        lock (_lock)
        {
            if (!_buckets.TryGetValue(hour, out var hourBucket))
            {
                hourBucket = new Dictionary<long, SensorAggregate>();
                _buckets[hour] = hourBucket;
            }

            if (!hourBucket.TryGetValue(sensorObjid, out var agg))
            {
                agg = new SensorAggregate();
                hourBucket[sensorObjid] = agg;
            }

            agg.Add(value);
            _sampleCount++;
        }
    }

    /// <summary>
    /// 取出所有早於 <paramref name="hourExclusive"/> 的小時桶，轉成 <see cref="PrtgValueRow"/> 並從累積器移除。
    /// </summary>
    /// <param name="hourExclusive">小時切齊邊界（不含此小時）</param>
    /// <param name="expectedSamplesPerHour">每小時期望取樣數（≤ 0 時 Coverage 為 null）</param>
    /// <param name="now">寫入時間（CreatedAt）</param>
    public IReadOnlyList<PrtgValueRow> DrainBefore(DateTime hourExclusive, int expectedSamplesPerHour, DateTime now)
    {
        lock (_lock)
        {
            var targetHours = _buckets.Keys.Where(h => h < hourExclusive).OrderBy(h => h).ToList();
            if (targetHours.Count == 0)
            {
                return Array.Empty<PrtgValueRow>();
            }

            var results = new List<PrtgValueRow>();
            foreach (var hour in targetHours)
            {
                var hourBucket = _buckets[hour];
                _buckets.Remove(hour);

                foreach (var (sensorObjid, agg) in hourBucket)
                {
                    _sampleCount -= agg.Count;
                    results.Add(ToRow(sensorObjid, hour, agg, expectedSamplesPerHour, now));
                }
            }

            return results;
        }
    }

    /// <summary>
    /// 取出全部小時桶（含當前小時），轉成 <see cref="PrtgValueRow"/> 並清空累積器（站台停止時用）。
    /// </summary>
    /// <param name="expectedSamplesPerHour">每小時期望取樣數（≤ 0 時 Coverage 為 null）</param>
    /// <param name="now">寫入時間（CreatedAt）</param>
    public IReadOnlyList<PrtgValueRow> DrainAll(int expectedSamplesPerHour, DateTime now)
    {
        lock (_lock)
        {
            if (_buckets.Count == 0)
            {
                return Array.Empty<PrtgValueRow>();
            }

            var targetHours = _buckets.Keys.OrderBy(h => h).ToList();
            var results = new List<PrtgValueRow>();

            foreach (var hour in targetHours)
            {
                var hourBucket = _buckets[hour];
                foreach (var (sensorObjid, agg) in hourBucket)
                {
                    results.Add(ToRow(sensorObjid, hour, agg, expectedSamplesPerHour, now));
                }
            }

            _buckets.Clear();
            _sampleCount = 0;
            return results;
        }
    }

    private static PrtgValueRow ToRow(long sensorObjid, DateTime hour, SensorAggregate agg, int expectedSamplesPerHour, DateTime now)
    {
        double? coverage = expectedSamplesPerHour <= 0
            ? null
            : Math.Min(100.0, agg.Count * 100.0 / expectedSamplesPerHour);

        return new PrtgValueRow
        {
            SensorObjid = sensorObjid,
            PeriodStart = hour,
            AvgValue = agg.Sum / agg.Count,
            MinValue = agg.Min,
            MaxValue = agg.Max,
            Coverage = coverage,
            Quality = PrtgDataQuality.Sampled,
            CreatedAt = now
        };
    }

    private sealed class SensorAggregate
    {
        public double Sum { get; private set; }
        public int Count { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }

        public void Add(double value)
        {
            Sum += value;
            if (Count == 0)
            {
                Min = value;
                Max = value;
            }
            else
            {
                if (value < Min) Min = value;
                if (value > Max) Max = value;
            }
            Count++;
        }
    }
}
