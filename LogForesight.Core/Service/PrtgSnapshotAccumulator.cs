using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 即時快照數值累積器（Core，純類別、無 IO、執行緒安全）。
/// 責任：收集某一小時內每顆感測器的多個取樣值，整點時產出 <see cref="PrtgValueRow"/> 清單。
/// </summary>
public sealed class PrtgSnapshotAccumulator
{
    public sealed record CheckpointRow(DateTime Hour, long SensorObjid, double Sum, int Count, double Min, double Max,
        double? Coverage = null);

    public IReadOnlyList<CheckpointRow> Capture()
    {
        lock (_lock)
            return _buckets.SelectMany(b => b.Value.Select(s =>
                new CheckpointRow(b.Key, s.Key, s.Value.Sum, s.Value.Count, s.Value.Min, s.Value.Max, s.Value.Coverage))).ToArray();
    }

    public void Restore(IReadOnlyList<CheckpointRow> rows)
    {
        // 先驗證完整份資料，錯誤時不得留下半份還原狀態。
        var buckets = new Dictionary<DateTime, Dictionary<long, SensorAggregate>>();
        long count = 0;
        foreach (var row in rows)
        {
            if (row == null || row.Count <= 0 || !double.IsFinite(row.Sum) || !double.IsFinite(row.Min) ||
                !double.IsFinite(row.Max) || row.Min > row.Max || row.Hour.Ticks % TimeSpan.TicksPerHour != 0 ||
                (row.Coverage.HasValue && (!double.IsFinite(row.Coverage.Value) || row.Coverage < 0)))
                throw new InvalidDataException("PRTG 快照恢復資料無效");
            if (!buckets.TryGetValue(row.Hour, out var bucket)) buckets[row.Hour] = bucket = new();
            if (!bucket.TryAdd(row.SensorObjid, new SensorAggregate(row)))
                throw new InvalidDataException("PRTG 快照恢復資料有重複感測器小時");
            count += row.Count;
            if (count > int.MaxValue) throw new InvalidDataException("PRTG 快照恢復樣本數超出上限");
        }
        lock (_lock)
        {
            _buckets.Clear();
            foreach (var bucket in buckets) _buckets.Add(bucket.Key, bucket.Value);
            _sampleCount = (int)count;
        }
    }

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
    /// <param name="sampleCoverage">採集當時一筆樣本的涵蓋率貢獻；不依重啟後的新策略重算。</param>
    public void Add(long sensorObjid, DateTime sampleTime, double value, double? sampleCoverage = null)
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

            agg.Add(value, sampleCoverage);
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
        double? coverage = agg.Coverage.HasValue ? Math.Min(100, agg.Coverage.Value) : agg.UnknownRestoredCoverage || expectedSamplesPerHour <= 0
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
        public SensorAggregate() { }
        public SensorAggregate(CheckpointRow row)
        {
            Sum = row.Sum;
            Count = row.Count;
            Min = row.Min;
            Max = row.Max;
            Coverage = row.Coverage;
            UnknownRestoredCoverage = !row.Coverage.HasValue;
        }
        public double Sum { get; private set; }
        public int Count { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public double? Coverage { get; private set; } = 0;
        public bool UnknownRestoredCoverage { get; }

        public void Add(double value, double? coverage)
        {
            Sum += value;
            Coverage = Coverage.HasValue && coverage.HasValue ? Coverage.Value + coverage.Value : null;
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
