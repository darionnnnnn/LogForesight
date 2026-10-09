using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 即時快照數值累積器（Core，純類別、無 IO、執行緒安全）。
/// 責任：收集某一小時內每顆感測器的多個取樣值，整點時產出 <see cref="PrtgValueRow"/> 清單。
/// </summary>
public sealed class PrtgSnapshotAccumulator
{
    public sealed record CheckpointRow(DateTime Hour, long SensorObjid, double Sum, int Count, double Min, double Max,
        double? Coverage = null, PrtgTrustedSampleProof? Trusted = null);

    public sealed record CheckpointDelta(IReadOnlyList<CheckpointRow> Upserts, IReadOnlyList<CheckpointKey> Removals);
    public sealed record CheckpointKey(DateTime Hour, long SensorObjid);

    private readonly HashSet<CheckpointKey> _dirty = new();
    private readonly HashSet<CheckpointKey> _removed = new();

    public IReadOnlyList<CheckpointRow> Capture()
    {
        lock (_lock)
            return _buckets.SelectMany(b => b.Value.Select(s =>
                new CheckpointRow(b.Key, s.Key, s.Value.Sum, s.Value.Count, s.Value.Min, s.Value.Max, s.Value.Coverage, s.Value.Trusted))).ToArray();
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
                    return new CheckpointRow(key.Hour, key.SensorObjid, agg.Sum, agg.Count, agg.Min, agg.Max, agg.Coverage, agg.Trusted);
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

    /// <summary>診斷 fallback 不可抹除同桶已取得的可信 slot；兩種資格不可相互混用。</summary>
    public bool TryAddDiagnostic(long sensorObjid, DateTime sampleTime, double value, double? sampleCoverage = null)
    {
        var hour = new DateTime(sampleTime.Year, sampleTime.Month, sampleTime.Day, sampleTime.Hour, 0, 0, DateTimeKind.Unspecified);
        lock (_lock)
        {
            if (_buckets.TryGetValue(hour, out var bucket) && bucket.TryGetValue(sensorObjid, out var prior) && prior.Trusted != null) return false;
            Add(sensorObjid, sampleTime, value, sampleCoverage);
            return true;
        }
    }

    /// <summary>接收具來源時間及物理樣本身分的正式樣本；不把舊 Add 的診斷資料升級為可信資料。</summary>
    public PrtgTrustedSampleDisposition AddTrusted(PrtgTrustedSample sample, DateTime asOf)
        => AddTrusted(sample, asOf, out _);

    public PrtgTrustedSampleDisposition AddTrusted(PrtgTrustedSample sample, DateTime asOf, out string? rejectionReason)
    {
        rejectionReason = null;
        if (sample == null || sample.SensorObjid <= 0 || !double.IsFinite(sample.Value) ||
            asOf.Kind != DateTimeKind.Utc || sample.MeasuredAt.Kind != DateTimeKind.Utc || sample.ReceivedAt.Kind != DateTimeKind.Utc ||
            sample.Quality != PrtgTrustedSampleQuality.Good ||
            !ValidText(sample.SourceGeneration, 256) || !ValidText(sample.ResourceGeneration, 256) ||
            !ValidText(sample.ChannelGeneration, 256) || !ValidText(sample.ResourceEpoch, 256) ||
            !ValidText(sample.SemanticVersion, 128) || !ValidText(sample.StrategyVersion, 128) ||
            !ValidText(sample.PhysicalMeasurementId, 1024) || !ValidText(sample.RawTimestampTimeZoneId, 128) ||
            !ValidText(sample.AnalysisTimeZoneId, 128) || sample.StrategyMinutes is not (5 or 15) ||
            sample.ConfirmedScanInterval <= TimeSpan.Zero || sample.ConfirmedScanInterval > TimeSpan.FromHours(1))
            return PrtgTrustedSampleDisposition.Rejected;
        if (!TryZone(sample.RawTimestampTimeZoneId, out _) || !TryZone(sample.AnalysisTimeZoneId, out var analysisZone) ||
            !TryAnalysisBucket(sample.MeasuredAt, analysisZone, out var localMeasured) ||
            !IsAnalysisHourInstant(sample.StrategyEffectiveFromHour, analysisZone))
            return PrtgTrustedSampleDisposition.Rejected;
        if (sample.MeasuredAt > asOf && sample.MeasuredAt - asOf <= TimeSpan.FromMinutes(2))
            return PrtgTrustedSampleDisposition.DeferredFuture;
        if (
            sample.MeasuredAt > asOf || sample.ReceivedAt > asOf || sample.ReceivedAt < sample.MeasuredAt ||
            asOf - sample.MeasuredAt > TimeSpan.FromTicks(sample.ConfirmedScanInterval.Ticks * 2) + TimeSpan.FromSeconds(30) ||
            sample.ReceivedAt - sample.MeasuredAt > TimeSpan.FromTicks(sample.ConfirmedScanInterval.Ticks * 2) + TimeSpan.FromSeconds(30))
            return PrtgTrustedSampleDisposition.Rejected;

        if (sample.StrategyEffectiveFromHour > sample.MeasuredAt)
            return PrtgTrustedSampleDisposition.Rejected;
        var hour = new DateTime(localMeasured.Year, localMeasured.Month, localMeasured.Day,
            localMeasured.Hour, 0, 0, DateTimeKind.Unspecified);
        var slot = localMeasured.Minute / sample.StrategyMinutes;
        var context = PrtgTrustedSampleProof.From(sample, Array.Empty<PrtgTrustedSampleSlot>());
        var slotValue = new PrtgTrustedSampleSlot(slot, PhysicalIdHash(sample.PhysicalMeasurementId),
            sample.MeasuredAt, sample.ReceivedAt, sample.Value);

        lock (_lock)
        {
            if (!_buckets.TryGetValue(hour, out var hourBucket))
                _buckets[hour] = hourBucket = new Dictionary<long, SensorAggregate>();
            if (!hourBucket.TryGetValue(sample.SensorObjid, out var agg))
            {
                agg = new SensorAggregate();
                hourBucket.Add(sample.SensorObjid, agg);
                _entryCount++;
            }
            if (agg.Count > 0 && agg.Trusted == null) return PrtgTrustedSampleDisposition.Rejected;
            var previous = agg.Trusted;
            if (previous != null && previous.ContextHash != context.ContextHash)
                return PrtgTrustedSampleDisposition.Rejected;
            var selected = previous?.Slots ?? Array.Empty<PrtgTrustedSampleSlot>();
            var samePhysical = selected.FirstOrDefault(s => string.Equals(s.PhysicalIdHash, slotValue.PhysicalIdHash, StringComparison.OrdinalIgnoreCase));
            if (samePhysical != null)
            {
                if (samePhysical.Value != slotValue.Value || samePhysical.MeasuredAt != slotValue.MeasuredAt)
                {
                    rejectionReason = "physical_measurement_conflict";
                    return PrtgTrustedSampleDisposition.Rejected;
                }
                return PrtgTrustedSampleDisposition.Duplicate;
            }
            var priorSlot = selected.FirstOrDefault(s => s.Slot == slot);
            if (priorSlot != null && (priorSlot.MeasuredAt > slotValue.MeasuredAt ||
                priorSlot.MeasuredAt == slotValue.MeasuredAt && priorSlot.ReceivedAt >= slotValue.ReceivedAt))
                return PrtgTrustedSampleDisposition.Duplicate;
            var slots = selected.Where(s => s.Slot != slot).Append(slotValue).OrderBy(s => s.Slot).ToArray();
            agg.SetTrusted(context with { Slots = slots });
            _sampleCount = _sampleCount - (previous?.Slots.Count ?? 0) + slots.Length;
            var key = new CheckpointKey(hour, sample.SensorObjid);
            _dirty.Add(key);
            _removed.Remove(key);
            return priorSlot == null ? PrtgTrustedSampleDisposition.Accepted : PrtgTrustedSampleDisposition.Replaced;
        }
    }

    private static bool ValidText(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool TryZone(string id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static bool TryAnalysisBucket(DateTime instant, TimeZoneInfo zone, out DateTime wall)
    {
        wall = default;
        if (instant.Kind != DateTimeKind.Utc) return false;
        wall = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
        return !zone.IsAmbiguousTime(wall) && !zone.IsInvalidTime(wall);
    }

    private static bool IsAnalysisHourInstant(DateTime instant, TimeZoneInfo zone)
    {
        if (!TryAnalysisBucket(instant, zone, out var wall)) return false;
        return wall.Minute == 0 && wall.Second == 0 && wall.Millisecond == 0 &&
            wall.Ticks % TimeSpan.TicksPerHour == 0;
    }
    private static string PhysicalIdHash(string id) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))[..16]);

    /// <summary>預覽將結算的小時列，不改 accumulator；呼叫端 durable 提交後再 CommitDelta。</summary>
    public IReadOnlyList<PrtgValueRow> PreviewDrainBefore(DateTime hourExclusive, int expectedSamplesPerHour, DateTime now) =>
        PreviewDrain(hour => hour < hourExclusive, expectedSamplesPerHour, now);

    public IReadOnlyList<PrtgValueRow> PreviewDrainAll(int expectedSamplesPerHour, DateTime now) =>
        PreviewDrain(_ => true, expectedSamplesPerHour, now);

    public IReadOnlyList<PrtgValueRow> PreviewDrainBeforeSources(DateTime legacyWallHourExclusive,
        DateTime asOfUtc, int expectedSamplesPerHour, DateTime now)
    {
        if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("來源結算時間必須是 UTC。", nameof(asOfUtc));
        lock (_lock) return _buckets.OrderBy(b => b.Key)
            .SelectMany(b => b.Value.Where(s => IsCompleteHour(b.Key, s.Value, legacyWallHourExclusive, asOfUtc))
                .Select(s => ToRow(s.Key, b.Key, s.Value, expectedSamplesPerHour, now))).ToArray();
    }

    public IReadOnlyList<CheckpointKey> KeysForDrainBeforeSources(DateTime legacyWallHourExclusive, DateTime asOfUtc)
    {
        if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("來源結算時間必須是 UTC。", nameof(asOfUtc));
        lock (_lock) return _buckets.SelectMany(b => b.Value
            .Where(s => IsCompleteHour(b.Key, s.Value, legacyWallHourExclusive, asOfUtc))
            .Select(s => new CheckpointKey(b.Key, s.Key))).ToArray();
    }

    private static bool IsCompleteHour(DateTime wallHour, SensorAggregate aggregate, DateTime legacyCutoff, DateTime asOfUtc)
    {
        if (aggregate.Trusted is null) return wallHour < legacyCutoff;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(aggregate.Trusted.AnalysisTimeZoneId);
        var wall = TimeZoneInfo.ConvertTimeFromUtc(asOfUtc, zone);
        var cutoff = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, 0, 0, DateTimeKind.Unspecified);
        return wallHour < cutoff;
    }

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
        if (agg.Trusted is { } proof)
        {
            var slots = proof.Slots;
            return new PrtgValueRow
            {
                SensorObjid = sensorObjid, PeriodStart = hour,
                AvgValue = slots.Average(s => s.Value), MinValue = slots.Min(s => s.Value), MaxValue = slots.Max(s => s.Value),
                Coverage = slots.Count * 100.0 / (60 / proof.StrategyMinutes), Quality = PrtgDataQuality.Sampled,
                CreatedAt = now, TrustVersion = 1, TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
            };
        }
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
            Trusted = row.Trusted;
            if (Trusted is { } proof)
            {
                if (!proof.IsStructurallyValid() || !proof.MatchesHour(row.Hour) || proof.StrategyMinutes is not (5 or 15) || proof.Slots.Count == 0 ||
                    proof.Slots.Any(s => s.Slot < 0 || s.Slot >= 60 / proof.StrategyMinutes || !double.IsFinite(s.Value)) ||
                    proof.Slots.Select(s => s.Slot).Distinct().Count() != proof.Slots.Count ||
                    row.Count != proof.Slots.Count || Math.Abs(row.Sum - proof.Slots.Sum(s => s.Value)) > 1e-8 ||
                    Math.Abs(row.Min - proof.Slots.Min(s => s.Value)) > 1e-8 ||
                    Math.Abs(row.Max - proof.Slots.Max(s => s.Value)) > 1e-8)
                    throw new InvalidDataException("可信樣本 checkpoint slots 無效");
                SetTrusted(proof);
            }
            else
            {
                Sum = row.Sum;
                Count = row.Count;
                Min = row.Min;
                Max = row.Max;
                Coverage = row.Coverage;
            }
            UnknownRestoredCoverage = !row.Coverage.HasValue;
        }
        public double Sum { get; private set; }
        public int Count { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public double? Coverage { get; private set; } = 0;
        public bool UnknownRestoredCoverage { get; }
        public PrtgTrustedSampleProof? Trusted { get; private set; }

        public void Add(double value, double? coverage)
        {
            // 舊入口只供診斷。與可信 slot 混入時整個桶退回未驗證狀態。
            Trusted = null;
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

        public void SetTrusted(PrtgTrustedSampleProof proof)
        {
            Trusted = proof;
            Count = proof.Slots.Count;
            Sum = proof.Slots.Sum(s => s.Value);
            Min = proof.Slots.Min(s => s.Value);
            Max = proof.Slots.Max(s => s.Value);
            Coverage = Count * 100.0 / (60 / proof.StrategyMinutes);
        }
    }
}
