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

    public sealed record CheckpointDelta(IReadOnlyList<CheckpointRow> Upserts, IReadOnlyList<CheckpointKey> Removals);
    public sealed record CheckpointKey(DateTime Hour, long SensorObjid);

    private readonly HashSet<CheckpointKey> _dirty = new();
    private readonly HashSet<CheckpointKey> _removed = new();

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
            _entryCount = rows.Count;
            _dirty.Clear();
            _removed.Clear();
        }
    }

    /// <summary>只擷取自上次 durable 確認以來變更的 key，不複製整個 accumulator。</summary>
    public CheckpointDelta CaptureDelta(IReadOnlyCollection<CheckpointKey>? removeAfterCommit = null)
    {
        lock (_lock)
        {
            var removals = new HashSet<CheckpointKey>(_removed);
            if (removeAfterCommit != null) removals.UnionWith(removeAfterCommit);
            var upserts = _dirty.Where(key => !removals.Contains(key) &&
                    _buckets.TryGetValue(key.Hour, out var bucket) && bucket.ContainsKey(key.SensorObjid))
                .Select(key =>
                {
                    var agg = _buckets[key.Hour][key.SensorObjid];
                    return new CheckpointRow(key.Hour, key.SensorObjid, agg.Sum, agg.Count, agg.Min, agg.Max, agg.Coverage);
                }).ToArray();
            return new CheckpointDelta(upserts, removals.ToArray());
        }
    }

    /// <summary>在 delta 已 durable 後確認它，並套用同一筆 durable drain 的局部移除。</summary>
    public void CommitDelta(CheckpointDelta delta)
    {
        lock (_lock)
        {
            foreach (var row in delta.Upserts)
            {
                var key = new CheckpointKey(row.Hour, row.SensorObjid);
                _dirty.Remove(key);
                _removed.Remove(key);
            }
            foreach (var key in delta.Removals)
            {
                if (_buckets.TryGetValue(key.Hour, out var bucket) && bucket.Remove(key.SensorObjid, out var agg))
                {
                    _sampleCount -= agg.Count;
                    _entryCount--;
                    if (bucket.Count == 0) _buckets.Remove(key.Hour);
                }
                _dirty.Remove(key);
                _removed.Remove(key);
            }
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<DateTime, Dictionary<long, SensorAggregate>> _buckets = new();
    private int _sampleCount;
    private int _entryCount;

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

    /// <summary>目前小時／感測器 checkpoint key 數量，供容量守門常數時間查詢。</summary>
    public int EntryCount
    {
        get { lock (_lock) return _entryCount; }
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
                _entryCount++;
            }

            agg.Add(value, sampleCoverage);
            _sampleCount++;
            var key = new CheckpointKey(hour, sensorObjid);
            _dirty.Add(key);
            _removed.Remove(key);
        }
    }

    /// <summary>預覽將結算的小時列，不改 accumulator；呼叫端 durable 提交後再 CommitDelta。</summary>
    public IReadOnlyList<PrtgValueRow> PreviewDrainBefore(DateTime hourExclusive, int expectedSamplesPerHour, DateTime now) =>
        PreviewDrain(hour => hour < hourExclusive, expectedSamplesPerHour, now);

    public IReadOnlyList<PrtgValueRow> PreviewDrainAll(int expectedSamplesPerHour, DateTime now) =>
        PreviewDrain(_ => true, expectedSamplesPerHour, now);

    public IReadOnlyList<CheckpointKey> KeysForDrainBefore(DateTime hourExclusive)
    {
        lock (_lock) return _buckets.Where(b => b.Key < hourExclusive)
            .SelectMany(b => b.Value.Keys.Select(id => new CheckpointKey(b.Key, id))).ToArray();
    }

    public IReadOnlyList<CheckpointKey> KeysForDrainAll()
    {
        lock (_lock) return _buckets.SelectMany(b => b.Value.Keys.Select(id => new CheckpointKey(b.Key, id))).ToArray();
    }

    private IReadOnlyList<PrtgValueRow> PreviewDrain(Func<DateTime, bool> include, int expectedSamplesPerHour, DateTime now)
    {
        lock (_lock)
            return _buckets.Where(b => include(b.Key)).OrderBy(b => b.Key)
                .SelectMany(b => b.Value.Select(s => ToRow(s.Key, b.Key, s.Value, expectedSamplesPerHour, now))).ToArray();
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
                    _entryCount--;
                    var key = new CheckpointKey(hour, sensorObjid);
                    _dirty.Remove(key);
                    _removed.Add(key);
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
                    var key = new CheckpointKey(hour, sensorObjid);
                    _dirty.Remove(key);
                    _removed.Add(key);
                    results.Add(ToRow(sensorObjid, hour, agg, expectedSamplesPerHour, now));
                }
            }

            _buckets.Clear();
            _sampleCount = 0;
            _entryCount = 0;
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
