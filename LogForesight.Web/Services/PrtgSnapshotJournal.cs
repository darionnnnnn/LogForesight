using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>單一快照工作者的復原檔。先落盤再提交 SQL；SQL 批次鍵處理提交後當機的重播。</summary>
internal sealed class PrtgSnapshotJournal : IDisposable
{
    internal const int MaxRows = 200_000;
    internal const long MaxBytes = 64 * 1024 * 1024;
    internal const int ReplayDays = 30;
    // 預留每列可信 slot proof 的上界；journal 總容量仍維持 64 MiB。
    internal const int ReservedBytesPerRow = 4096;
    internal sealed record Batch(string Id, IReadOnlyList<PrtgValueRow> Rows);
    internal sealed record State(int Version, string DatabaseId, string SourceEndpoint,
        IReadOnlyList<PrtgSnapshotAccumulator.CheckpointRow> Accumulator, IReadOnlyList<Batch> Pending)
    {
        public string Checksum { get; init; } = "";
    }

    private sealed record Manifest(int Version, string Generation, long Sequence, string LastChecksum, string Checksum);
    internal sealed record BindingPair(string Current, string Legacy, string SourceGeneration);
    private sealed record Segment(int Version, long Sequence, string PreviousChecksum, string DatabaseId,
        string SourceEndpoint, IReadOnlyList<PrtgSnapshotAccumulator.CheckpointRow> Upserts,
        IReadOnlyList<PrtgSnapshotAccumulator.CheckpointKey> Removals, IReadOnlyList<Batch> AddedBatches,
        IReadOnlyList<string> AckedBatchIds, State? Snapshot, string Checksum);

    private readonly StorageBackend _backend;
    internal string FilePath { get; }
    internal long SavedBytes { get; private set; }
    private string? _databaseId;
    private FileStream? _ownership;
    private string? _expectedDigest;
    private bool _loaded;
    private readonly object _gate = new();
    private string? _generation;
    private long _sequence;
    private string _lastSegmentChecksum = "";
    private string? _manifestDigest;
    private int _segmentCount;
    private long _generationBytes;
    private long _diskBytes;
    private long _manifestBytes;
    private int _liveAccumulatorRows;
    private long _pendingRows;
    private string? _generationEndpoint;
    // Set only by Load after a checksummed legacy journal exactly matches the
    // binding that the current configuration would have produced under the old
    // scope-revision scheme. It authorizes a one-time atomic format migration.
    private string? _verifiedLegacyBinding;
    private string? _verifiedLegacyTargetBinding;
    private readonly SortedDictionary<DateTime, int> _recordDates = new();
    private Dictionary<(DateTime Hour, long Sensor), PrtgSnapshotAccumulator.CheckpointRow> _currentAccumulator = new();
    private Dictionary<string, Batch> _currentPending = new(StringComparer.Ordinal);
    private const int MaxRowsPerSegment = 100_000;
    private const int CompactAfterSegments = 127;
    private const long GenerationQuotaBytes = MaxBytes / 2 - 64 * 1024;
    internal long BytesRead { get; private set; }
    internal long BytesWritten { get; private set; }
    internal int FilesRead { get; private set; }
    internal int FilesWritten { get; private set; }
    internal Action<string>? FaultPoint { get; set; }

    private string ManifestPath => FilePath + ".manifest";
    internal string ManifestFilePath => ManifestPath;

    /// <summary>工作者從恢復到停止持有同一把跨程序檔案鎖；不讓第二個工作者開新採集。</summary>
    internal void AcquireOwnership()
    {
        lock (_gate) _ownership ??= OpenWriterLock();
    }
    private FileStream OpenWriterLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        try { return new FileStream(FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException ex) { throw new IOException("已有快照工作者持有復原佇列；本工作者停止採集，勿刪除鎖定檔。", ex); }
    }
    public void Dispose() { lock (_gate) { _ownership?.Dispose(); _ownership = null; } }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Checksum(State state) => Digest(JsonSerializer.SerializeToUtf8Bytes(state with { Checksum = "" }));

    internal PrtgSnapshotJournal(StorageBackend backend)
    {
        _backend = backend;
        FilePath = Path.Combine(backend.DataRoot, "pending", "prtg-snapshot", "checkpoint.json");
    }

    private string DatabaseId => _databaseId ??= _backend.Blob("prtg_snapshot_database_id").Mutate(value =>
    {
        var id = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N")
            : JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("快照資料庫識別無效");
        return (JsonSerializer.Serialize(id), id);
    });

    // 位址只作恢復防錯綁定，不宣稱能辨識相同 URL 後的 PRTG Core 替換。
    internal static string Endpoint(string? url)
    {
        var value = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped).TrimEnd('/')
            : "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal static string Binding(StorageBackend backend, string? url)
    {
        return CaptureBindingPair(backend, url).Current;
    }

    /// <summary>Capture one immutable policy value and use it for both current and legacy bindings.</summary>
    internal static BindingPair CaptureBindingPair(StorageBackend backend, string? url)
    {
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var source = policy.SourceGeneration ?? "";
        var endpoint = Endpoint(url);
        if (source.Length == 0) return new BindingPair(endpoint, endpoint, source);
        var scopeRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var resourceRevision = backend.Blob("prtg_resource_generation_revision").ReadVersion();
        // Durable binding describes the source, not the selected-resource scope.
        // Scope/resource revisions remain per-operation cancellation fences, while
        // every trusted sample carries its own resource/channel generation and epoch.
        var current = $"v3|{endpoint}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(source))}";
        var epoch = $"{endpoint}|{source}|{scopeRevision}|{resourceRevision}";
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(epoch)));
        return new BindingPair(current, legacy, source);
    }

    /// <summary>Exact pre-v3 digest, used only to verify and migrate an untouched legacy journal.</summary>
    internal static string LegacyBinding(StorageBackend backend, string? url)
    {
        return CaptureBindingPair(backend, url).Legacy;
    }

    internal State? Load(string endpoint, DateTime now, string? currentLegacyBinding = null)
    {
        lock (_gate)
        {
        if (File.Exists(ManifestPath)) return LoadSegments(endpoint, now, currentLegacyBinding);
        if (!File.Exists(FilePath)) { _loaded = true; _expectedDigest = null; return null; }
        if (new FileInfo(FilePath).Length > MaxBytes) throw new InvalidDataException("快照復原檔超過容量上限，停止採集並保留檔案");
        var bytes = ReadFile(FilePath);
        var state = JsonSerializer.Deserialize<State>(bytes) ?? throw new InvalidDataException("快照復原檔為空");
        if (state.Version == 1) throw new InvalidDataException("舊版復原檔沒有完整性證明；保留原檔，請管理者備份並核對，不能自動重播或補簽 checksum。");
        if (string.IsNullOrEmpty(state.Checksum) || state.Checksum.Length != 64 || state.Checksum != Checksum(state))
            throw new InvalidDataException("快照復原檔 checksum 不符；停止採集並保留原檔。");
        Validate(state, state.SourceEndpoint, now);
        if (state.SourceEndpoint != endpoint && HasRows(state))
        {
            if (!IsExactLegacyBinding(state.SourceEndpoint, currentLegacyBinding))
                throw AmbiguousLegacyBinding();
            _verifiedLegacyBinding = state.SourceEndpoint;
            _verifiedLegacyTargetBinding = endpoint;
        }
        _loaded = true; _expectedDigest = Digest(bytes);
        SavedBytes = bytes.Length;
        return state;
        }
    }

    /// <summary>切換正式 consumer 至 segment journal；有效 V2 checkpoint 以一代快照先 durable 再切換。</summary>
    internal void EnableIncremental(string endpoint, DateTime now, string? currentLegacyBinding = null)
    {
        lock (_gate)
        {
            using var writeLock = _ownership == null ? OpenWriterLock() : null;
            if (File.Exists(ManifestPath))
            {
                var manifestBytes = ReadFile(ManifestPath);
                var manifest = ReadManifest(manifestBytes);
                if (Digest(manifestBytes) != _manifestDigest)
                    throw new InvalidDataException("快照 journal generation 已被其他寫入者變更；拒絕過期寫入。");
                if (_generationEndpoint != endpoint)
                {
                    if (_liveAccumulatorRows > 0 || _pendingRows > 0)
                    {
                        if (!IsExactLegacyBinding(_generationEndpoint, currentLegacyBinding) ||
                            _verifiedLegacyBinding != _generationEndpoint || _verifiedLegacyTargetBinding != endpoint)
                            throw AmbiguousLegacyBinding();
                        var migrated = new State(2, DatabaseId, endpoint, _currentAccumulator.Values.ToArray(),
                            _currentPending.Values.ToArray());
                        migrated = migrated with { Checksum = Checksum(migrated) };
                        // PublishGeneration writes and flushes every new snapshot segment before
                        // atomically replacing the manifest. The old generation remains authoritative
                        // if anything fails before that switch.
                        PublishGeneration(migrated, endpoint, now);
                        CleanupInactiveGenerations(_generation!);
                        _verifiedLegacyBinding = null;
                        _verifiedLegacyTargetBinding = null;
                        _diskBytes = MeasureJournalBytes();
                        SavedBytes = _diskBytes;
                        return;
                    }
                    var empty = new State(2, DatabaseId, endpoint, Array.Empty<PrtgSnapshotAccumulator.CheckpointRow>(), Array.Empty<Batch>());
                    empty = empty with { Checksum = Checksum(empty) };
                    PublishGeneration(empty, endpoint, now);
                }
                return;
            }

            State legacy;
            if (File.Exists(FilePath))
            {
                var bytes = ReadFile(FilePath);
                legacy = JsonSerializer.Deserialize<State>(bytes) ?? throw new InvalidDataException("快照復原檔為空");
                if (legacy.Version == 1) throw new InvalidDataException("舊版復原檔沒有完整性證明；保留原檔，不能自動重播或補簽 checksum。");
                if (legacy.Checksum != Checksum(legacy)) throw new InvalidDataException("快照復原檔 checksum 不符；保留原檔。");
                Validate(legacy, legacy.SourceEndpoint, now);
                if (legacy.SourceEndpoint != endpoint)
                {
                    if (HasRows(legacy) && (!IsExactLegacyBinding(legacy.SourceEndpoint, currentLegacyBinding) ||
                        _verifiedLegacyBinding != legacy.SourceEndpoint || _verifiedLegacyTargetBinding != endpoint))
                        throw AmbiguousLegacyBinding();
                    // The old checksum was verified and Load proved an exact old binding match.
                    // Re-sign only as part of the atomic generation switch below.
                    legacy = legacy with { SourceEndpoint = endpoint, Checksum = "" };
                    legacy = legacy with { Checksum = Checksum(legacy) };
                }
            }
            else
            {
                legacy = new State(2, DatabaseId, endpoint, Array.Empty<PrtgSnapshotAccumulator.CheckpointRow>(), Array.Empty<Batch>());
                legacy = legacy with { Checksum = Checksum(legacy) };
            }
            PublishGeneration(legacy, endpoint, now);
            // generation 指標原子發布後，新代已含完整資料，才可回收 V2 checkpoint。
            if (File.Exists(FilePath))
            {
                var oldLength = new FileInfo(FilePath).Length;
                File.Delete(FilePath);
                _diskBytes -= oldLength;
                SavedBytes = _diskBytes;
            }
            CleanupInactiveGenerations(_generation!);
            _diskBytes = MeasureJournalBytes();
            SavedBytes = _diskBytes;
            _loaded = true;
            _expectedDigest = null;
            _verifiedLegacyBinding = null;
            _verifiedLegacyTargetBinding = null;
        }
    }

    /// <summary>耐久保存 checkpoint delta，並可在同一段建立 SQL batch 或確認 batch ack。</summary>
    internal void AppendDelta(string endpoint, PrtgSnapshotAccumulator.CheckpointDelta delta,
        IReadOnlyList<Batch>? addedBatches, IReadOnlyList<string>? ackedBatchIds, DateTime now)
    {
        lock (_gate)
        {
            if (!File.Exists(ManifestPath)) EnableIncremental(endpoint, now);
            using var writeLock = _ownership == null ? OpenWriterLock() : null;
            EnsureIncrementalReady(endpoint, now);
            var segment = new Segment(1, _sequence + 1, _lastSegmentChecksum, DatabaseId, endpoint,
                delta.Upserts, delta.Removals, addedBatches ?? Array.Empty<Batch>(),
                ackedBatchIds ?? Array.Empty<string>(), null, "");
            AppendSegment(segment, now);
        }
    }

    internal void AppendAck(string endpoint, string batchId, DateTime now) =>
        AppendDelta(endpoint, new PrtgSnapshotAccumulator.CheckpointDelta(Array.Empty<PrtgSnapshotAccumulator.CheckpointRow>(),
            Array.Empty<PrtgSnapshotAccumulator.CheckpointKey>()), null, new[] { batchId }, now);

    private void EnsureIncrementalReady(string endpoint, DateTime now)
    {
        if (!File.Exists(ManifestPath)) throw new InvalidDataException("快照 journal 尚未完成 generation 初始化");
        var bytes = ReadFile(ManifestPath);
        if (_manifestDigest != Digest(bytes)) throw new InvalidDataException("快照 journal generation 已被其他寫入者變更；拒絕過期寫入。");
        var manifest = ReadManifest(bytes);
        if (manifest.Generation != _generation || manifest.Sequence != _sequence || manifest.LastChecksum != _lastSegmentChecksum)
            throw new InvalidDataException("快照 journal generation 已被其他寫入者變更；拒絕過期寫入。");
    }

    private void AppendSegment(Segment segment, DateTime now)
    {
        if (segment.Upserts.Count + segment.Removals.Count + segment.AddedBatches.Count + segment.AckedBatchIds.Count + segment.AddedBatches.Sum(b => (long)b.Rows.Count) > MaxRowsPerSegment)
            throw new InvalidDataException("快照單次 journal segment 超過 100000 列上限");
        var nextCounts = ValidateDelta(segment, now);
        var checksumSegment = segment with { Checksum = SegmentChecksum(segment) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checksumSegment);

        // 先完成rotation再發布本次delta，避免已提交資料被當成未提交回報。
        if (_segmentCount >= CompactAfterSegments || _generationBytes + bytes.LongLength > GenerationQuotaBytes)
        {
            Compact(segment.SourceEndpoint, now);
            segment = segment with { Sequence = _sequence + 1, PreviousChecksum = _lastSegmentChecksum };
            checksumSegment = segment with { Checksum = SegmentChecksum(segment) };
            bytes = JsonSerializer.SerializeToUtf8Bytes(checksumSegment);
        }

        var directory = Path.Combine(Path.GetDirectoryName(FilePath)!, _generation!, "segments");
        Directory.CreateDirectory(directory);
        var path = SegmentPath(_generation!, segment.Sequence);
        var temp = path + ".tmp";
        var manifestBytes = ManifestBytes(new Manifest(1, _generation!, segment.Sequence, checksumSegment.Checksum, ""));
        if (File.Exists(path) || File.Exists(temp)) CleanupUncommittedTail(segment.Sequence, path, temp);
        if (_generationBytes + bytes.LongLength > GenerationQuotaBytes || _diskBytes + bytes.LongLength + manifestBytes.LongLength > MaxBytes)
            throw new InvalidDataException("快照 journal 容量預留不足；保留已提交資料並停止採集。");
        var oldManifestTempBytes = File.Exists(ManifestPath + ".tmp") ? new FileInfo(ManifestPath + ".tmp").Length : 0;
        WriteDurable(temp, bytes);
        FaultPoint?.Invoke("after-segment-flush");
        File.Move(temp, path);
        var updated = new Manifest(1, _generation!, segment.Sequence, checksumSegment.Checksum, "");
        try { WriteManifest(updated); }
        catch
        {
            // manifest 仍指向舊序號時，尾端segment尚未提交；保留已提交prefix。
            throw;
        }
        FaultPoint?.Invoke("after-append-manifest");
        _sequence = segment.Sequence;
        _lastSegmentChecksum = checksumSegment.Checksum;
        _segmentCount++;
        _generationBytes += bytes.LongLength;
        _diskBytes += bytes.LongLength + manifestBytes.LongLength - _manifestBytes - oldManifestTempBytes;
        _manifestBytes = manifestBytes.LongLength;
        foreach (var key in checksumSegment.Removals)
            if (_currentAccumulator.TryGetValue((key.Hour, key.SensorObjid), out _)) RemoveRecordDate(key.Hour);
        foreach (var row in checksumSegment.Upserts)
            if (!_currentAccumulator.ContainsKey((row.Hour, row.SensorObjid))) AddRecordDate(row.Hour);
        foreach (var batch in checksumSegment.AddedBatches)
            foreach (var row in batch.Rows) AddRecordDate(row.PeriodStart);
        foreach (var id in checksumSegment.AckedBatchIds)
            foreach (var row in _currentPending[id].Rows) RemoveRecordDate(row.PeriodStart);
        Apply(checksumSegment, _currentAccumulator, _currentPending);
        _liveAccumulatorRows = nextCounts.LiveRows;
        _pendingRows = nextCounts.PendingRows;
        SavedBytes = _diskBytes;
    }

    private (int LiveRows, long PendingRows) ValidateDelta(Segment segment, DateTime now)
    {
        if (segment.Version != 1 || segment.DatabaseId != DatabaseId || segment.SourceEndpoint != _generationEndpoint ||
            segment.Upserts == null || segment.Removals == null || segment.AddedBatches == null || segment.AckedBatchIds == null)
            throw new InvalidDataException("快照 journal 增量版本或 binding 不符；保留資料並停止採集。");
        var upsertKeys = new HashSet<(DateTime, long)>();
        var removalKeys = new HashSet<(DateTime, long)>();
        foreach (var row in segment.Upserts)
        {
            ValidateAccumulatorRow(row, now);
            if (!upsertKeys.Add((row.Hour, row.SensorObjid))) throw new InvalidDataException("快照 journal delta 有重複 key");
        }
        foreach (var key in segment.Removals)
        {
            if (!removalKeys.Add((key.Hour, key.SensorObjid)) || upsertKeys.Contains((key.Hour, key.SensorObjid)))
                throw new InvalidDataException("快照 journal delta 的移除 key 重複或同時更新");
        }
        var addedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in segment.AddedBatches)
        {
            if (batch == null || !Guid.TryParseExact(batch.Id, "N", out _) || !addedIds.Add(batch.Id) || _currentPending.ContainsKey(batch.Id))
                throw new InvalidDataException("快照復原批次識別無效或重複");
            if (batch.Rows == null || batch.Rows.Count > MaxRowsPerSegment) throw new InvalidDataException("快照 batch 列數超過單段上限");
            foreach (var row in batch.Rows) ValidateValueRow(row, now);
        }
        var ackIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in segment.AckedBatchIds)
            if (!Guid.TryParseExact(id, "N", out _) || !ackIds.Add(id) || !_currentPending.ContainsKey(id))
                throw new InvalidDataException("快照 durable ack 沒有對應的 pending batch");

        var removedExisting = segment.Removals.Count(key => _currentAccumulator.ContainsKey((key.Hour, key.SensorObjid)));
        var newAccumulatorRows = _liveAccumulatorRows - removedExisting + segment.Upserts.Count(row => !_currentAccumulator.ContainsKey((row.Hour, row.SensorObjid)));
        var nextPendingRows = _pendingRows + segment.AddedBatches.Sum(b => (long)b.Rows.Count)
            - segment.AckedBatchIds.Sum(id => (long)_currentPending[id].Rows.Count);
        if (newAccumulatorRows + nextPendingRows > MaxRows)
            throw new InvalidDataException("快照待寫資料達 200000 列上限，保留資料並停止採集");
        if (_recordDates.Count > 0 && _recordDates.First().Key < now.AddDays(-ReplayDays))
            throw new InvalidDataException("快照 journal 已含超過 30 日安全重播期限的未清資料；保留原檔並停止採集");
        return (newAccumulatorRows, nextPendingRows);
    }

    private void AddRecordDate(DateTime date) => _recordDates[date] = _recordDates.GetValueOrDefault(date) + 1;
    private void RemoveRecordDate(DateTime date)
    {
        if (!_recordDates.TryGetValue(date, out var count)) return;
        if (count <= 1) _recordDates.Remove(date);
        else _recordDates[date] = count - 1;
    }
    private void RebuildRecordDates()
    {
        _recordDates.Clear();
        foreach (var row in _currentAccumulator.Values) AddRecordDate(row.Hour);
        foreach (var batch in _currentPending.Values)
            foreach (var row in batch.Rows) AddRecordDate(row.PeriodStart);
    }

    private void ValidateAccumulatorRow(PrtgSnapshotAccumulator.CheckpointRow row, DateTime now)
    {
        if (row == null || row.Count <= 0 || !double.IsFinite(row.Sum) || !double.IsFinite(row.Min) || !double.IsFinite(row.Max) ||
            row.Min > row.Max || row.Hour.Ticks % TimeSpan.TicksPerHour != 0 ||
            (row.Coverage.HasValue && (!double.IsFinite(row.Coverage.Value) || row.Coverage < 0)) ||
            row.Hour < now.AddDays(-ReplayDays))
            throw new InvalidDataException("快照增量樣本無效或超過 30 日安全重播期限");
        if (row.Trusted is { } proof && (!proof.IsStructurallyValid() || !proof.MatchesHour(row.Hour) ||
            proof.Slots.Count != row.Count || proof.Slots.Count == 0 || proof.Slots.Count > 12 ||
            proof.Slots.Select(s => s.Slot).Distinct().Count() != proof.Slots.Count ||
            proof.Slots.Select(s => s.PhysicalIdHash).Distinct(StringComparer.Ordinal).Count() != proof.Slots.Count ||
            proof.Slots.Any(s => s.Slot < 0 || s.Slot >= 60 / proof.StrategyMinutes || !double.IsFinite(s.Value))))
            throw new InvalidDataException("可信樣本 journal slot proof 無效");
    }

    private void ValidateValueRow(PrtgValueRow row, DateTime now)
    {
        if (row == null || row.Quality != PrtgDataQuality.Sampled || row.TrustVersion is < 0 or > 1 ||
            row.TrustVersion == 1 && string.IsNullOrWhiteSpace(row.TrustedProof) ||
            row.TrustVersion == 0 && row.TrustedProof != null ||
            (row.AvgValue.HasValue && !double.IsFinite(row.AvgValue.Value)) ||
            (row.MinValue.HasValue && !double.IsFinite(row.MinValue.Value)) ||
            (row.MaxValue.HasValue && !double.IsFinite(row.MaxValue.Value)) ||
            (row.Coverage.HasValue && (!double.IsFinite(row.Coverage.Value) || row.Coverage < 0 || row.Coverage > 100)) ||
            row.PeriodStart < now.AddDays(-ReplayDays))
            throw new InvalidDataException("快照待寫樣本無效或超過 30 日安全重播期限");
        if (row.TrustVersion == 1)
        {
            var proof = PrtgTrustedSampleProof.Deserialize(row.TrustedProof!);
            if (!proof.IsStructurallyValid() || !proof.MatchesHour(row.PeriodStart) ||
                proof.Slots.Count == 0 || proof.Slots.Count > 12 ||
                Math.Abs((row.Coverage ?? -1) - proof.Slots.Count * 100.0 / (60 / proof.StrategyMinutes)) > 1e-8 ||
                !row.AvgValue.HasValue || Math.Abs(row.AvgValue.Value - proof.Slots.Average(s => s.Value)) > 1e-8 ||
                !row.MinValue.HasValue || Math.Abs(row.MinValue.Value - proof.Slots.Min(s => s.Value)) > 1e-8 ||
                !row.MaxValue.HasValue || Math.Abs(row.MaxValue.Value - proof.Slots.Max(s => s.Value)) > 1e-8)
                throw new InvalidDataException("待寫可信樣本 proof 無效");
        }
    }

    private void CleanupUncommittedTail(long sequence, string path, string temp)
    {
        if (sequence != _sequence + 1) throw new InvalidDataException("拒絕覆寫非尾端的 journal segment");
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(temp)) File.Delete(temp);
        _diskBytes = MeasureJournalBytes();
    }

    private void Compact(string endpoint, DateTime now)
    {
        var state = new State(2, DatabaseId, endpoint, _currentAccumulator.Values.ToArray(), _currentPending.Values.ToArray());
        state = state with { Checksum = Checksum(state) };
        PublishGeneration(state, endpoint, now);
        // 新 generation 指標已耐久切換，舊 generation 才能回收。
        CleanupInactiveGenerations(_generation!);
        _diskBytes = MeasureJournalBytes();
    }

    private void CleanupInactiveGenerations(string activeGeneration)
    {
        var parent = Path.GetDirectoryName(FilePath)!;
        foreach (var dir in Directory.EnumerateDirectories(parent, "checkpoint.g.*"))
        {
            var generation = Path.GetFileName(dir);
            if (string.Equals(generation, activeGeneration, StringComparison.Ordinal)) continue;
            var checkedPath = GenerationPath(generation);
            try { Directory.Delete(checkedPath, recursive: true); }
            catch (IOException) { /* 舊代保留且仍計入容量上限 */ }
            catch (UnauthorizedAccessException) { /* 舊代保留且仍計入容量上限 */ }
        }
    }

    private void CleanupUncommittedSegments(string generation, long committedSequence)
    {
        var directory = Path.Combine(GenerationPath(generation), "segments");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.Ordinal) ||
                (name.EndsWith(".json", StringComparison.Ordinal) && long.TryParse(name[..^5], out var sequence) && sequence > committedSequence))
                File.Delete(path);
        }
    }

    private void PublishGeneration(State state, string endpoint, DateTime now)
    {
        Validate(state, endpoint, now);
        var generation = "checkpoint.g." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(GenerationPath(generation), "segments");
        Directory.CreateDirectory(directory);
        var parts = SnapshotChunks(state);
        var sequence = 0L;
        var previous = "";
        long generationBytes = 0;
        var segments = new List<(long Sequence, Segment Segment, byte[] Bytes)>();
        foreach (var part in parts)
        {
            sequence++;
            var snapshot = new Segment(1, sequence, previous, DatabaseId, endpoint, Array.Empty<PrtgSnapshotAccumulator.CheckpointRow>(),
                Array.Empty<PrtgSnapshotAccumulator.CheckpointKey>(), Array.Empty<Batch>(), Array.Empty<string>(), part, "");
            snapshot = snapshot with { Checksum = SegmentChecksum(snapshot) };
            var segmentBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            generationBytes += segmentBytes.LongLength;
            if (generationBytes > GenerationQuotaBytes)
                throw new InvalidDataException("快照 journal compacted snapshot 超過保留代配額；保留舊 generation 並停止採集。");
            segments.Add((sequence, snapshot, segmentBytes));
            previous = snapshot.Checksum;
        }
        var manifest = new Manifest(1, generation, sequence, previous, "");
        var manifestBytes = ManifestBytes(manifest);
        var currentDiskBytes = JournalDiskBytes();
        var previousManifestBytes = File.Exists(ManifestPath) ? new FileInfo(ManifestPath).Length : 0;
        if (currentDiskBytes + generationBytes + manifestBytes.LongLength > MaxBytes)
            throw new InvalidDataException("快照 journal 無法在 64 MiB 內保留新舊 generation；保留舊 generation 並停止採集。");
        foreach (var segment in segments)
            WriteDurable(Path.Combine(directory, SegmentName(segment.Sequence)), segment.Bytes);
        var tempManifest = ManifestPath + ".tmp";
        WriteDurable(tempManifest, manifestBytes);
        FaultPoint?.Invoke("before-generation-switch");
        if (File.Exists(ManifestPath)) File.Replace(tempManifest, ManifestPath, null);
        else File.Move(tempManifest, ManifestPath);
        FaultPoint?.Invoke("after-generation-switch");

        _generation = generation;
        _sequence = sequence;
        _lastSegmentChecksum = previous;
        _segmentCount = checked((int)sequence);
        _generationBytes = generationBytes;
        _currentAccumulator = state.Accumulator.ToDictionary(r => (r.Hour, r.SensorObjid));
        _currentPending = state.Pending.ToDictionary(b => b.Id, StringComparer.Ordinal);
        RebuildRecordDates();
        _manifestDigest = Digest(manifestBytes);
        _expectedDigest = _manifestDigest;
        _generationEndpoint = endpoint;
        _liveAccumulatorRows = state.Accumulator.Count;
        _pendingRows = state.Pending.Sum(batch => (long)batch.Rows.Count);
        _manifestBytes = manifestBytes.LongLength;
        _diskBytes = currentDiskBytes + generationBytes + manifestBytes.LongLength - previousManifestBytes;
        SavedBytes = _diskBytes;
    }

    private IReadOnlyList<State> SnapshotChunks(State state)
    {
        const int maxPoints = 100_000;
        var parts = new List<State>();
        var accumulatorIndex = 0;
        var batchIndex = 0;
        var batchRowIndex = 0;
        var includeEmptyBatch = false;
        while (accumulatorIndex < state.Accumulator.Count || batchIndex < state.Pending.Count || parts.Count == 0)
        {
            var accumulatorRows = new List<PrtgSnapshotAccumulator.CheckpointRow>();
            var batches = new List<Batch>();
            var count = 0;
            while (accumulatorIndex < state.Accumulator.Count && count < maxPoints)
            {
                accumulatorRows.Add(state.Accumulator[accumulatorIndex++]);
                count++;
            }
            while (batchIndex < state.Pending.Count && count < maxPoints)
            {
                var batch = state.Pending[batchIndex];
                var take = Math.Min(maxPoints - count, batch.Rows.Count - batchRowIndex);
                if (take > 0)
                {
                    batches.Add(new Batch(batch.Id, batch.Rows.Skip(batchRowIndex).Take(take).ToArray()));
                    batchRowIndex += take;
                    count += take;
                }
                else if (batch.Rows.Count == 0 && !includeEmptyBatch)
                {
                    batches.Add(new Batch(batch.Id, Array.Empty<PrtgValueRow>()));
                    includeEmptyBatch = true;
                }
                if (batchRowIndex >= batch.Rows.Count)
                {
                    batchIndex++;
                    batchRowIndex = 0;
                    includeEmptyBatch = false;
                }
            }
            var part = new State(2, state.DatabaseId, state.SourceEndpoint, accumulatorRows, batches);
            parts.Add(part with { Checksum = Checksum(part) });
        }
        return parts;
    }

    private State LoadSegments(string endpoint, DateTime now, string? currentLegacyBinding)
    {
        var manifestBytes = ReadFile(ManifestPath);
        var manifest = ReadManifest(manifestBytes);
        if (manifest.Sequence < 1) throw new InvalidDataException("快照 journal manifest sequence 無效");
        var accumulator = new Dictionary<(DateTime, long), PrtgSnapshotAccumulator.CheckpointRow>();
        var pending = new Dictionary<string, Batch>(StringComparer.Ordinal);
        var previous = "";
        string? generationEndpoint = null;
        var snapshotPhase = true;
        var sawSnapshot = false;
        long bytesTotal = 0;
        for (long seq = 1; seq <= manifest.Sequence; seq++)
        {
            var path = SegmentPath(manifest.Generation, seq);
            var segmentDirectory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(segmentDirectory) && (File.GetAttributes(segmentDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("快照 journal segments 不可指向資料目錄外的 reparse point");
            if (!File.Exists(path)) throw new InvalidDataException($"快照 journal committed segment {seq} 遺失；保留資料並停止採集。");
            var bytes = ReadFile(path);
            bytesTotal += bytes.LongLength;
            if (bytes.LongLength > MaxBytes || bytesTotal > MaxBytes) throw new InvalidDataException("快照 journal 超過 64 MiB 上限；保留資料並停止採集");
            var segment = JsonSerializer.Deserialize<Segment>(bytes) ?? throw new InvalidDataException("快照 journal segment 為空");
            if (segment.Upserts == null || segment.Removals == null || segment.AddedBatches == null || segment.AckedBatchIds == null)
                throw new InvalidDataException("快照 journal segment 欄位不完整");
            if (segment.Version != 1 || segment.Sequence != seq || segment.PreviousChecksum != previous || segment.Checksum != SegmentChecksum(segment))
                throw new InvalidDataException($"快照 journal committed segment {seq} checksum 或 sequence 不符；保留資料並停止採集。");
            if (segment.DatabaseId != DatabaseId)
                throw new InvalidDataException("快照 journal 的資料庫 binding 不符；保留資料並停止採集。");
            generationEndpoint ??= segment.SourceEndpoint;
            if (segment.SourceEndpoint != generationEndpoint)
                throw new InvalidDataException("快照 journal generation 內 source binding 不一致；保留資料並停止採集。");
            if (segment.Snapshot != null)
            {
                var snapshot = segment.Snapshot;
                var snapshotPoints = snapshot.Accumulator.Count + snapshot.Pending.Sum(batch => batch.Rows.Count);
                if (!snapshotPhase || snapshot.Version != 2 || snapshot.DatabaseId != segment.DatabaseId || snapshot.SourceEndpoint != segment.SourceEndpoint ||
                    snapshot.Accumulator == null || snapshot.Pending == null || snapshot.Pending.Any(batch => batch == null || batch.Rows == null) ||
                    snapshot.SourceEndpoint != segment.SourceEndpoint || snapshot.Checksum != Checksum(snapshot) ||
                    snapshotPoints > MaxRowsPerSegment)
                    throw new InvalidDataException("快照 journal snapshot checksum 不符；保留資料並停止採集。");
                sawSnapshot = true;
                foreach (var row in snapshot.Accumulator)
                    if (!accumulator.TryAdd((row.Hour, row.SensorObjid), row)) throw new InvalidDataException("snapshot accumulator key 重複");
                foreach (var batch in snapshot.Pending)
                {
                    if (pending.TryGetValue(batch.Id, out var prior))
                        pending[batch.Id] = prior with { Rows = prior.Rows.Concat(batch.Rows).ToArray() };
                    else pending.Add(batch.Id, batch);
                }
            }
            else
            {
                snapshotPhase = false;
                if (!sawSnapshot) throw new InvalidDataException("快照 journal generation 缺少起始 snapshot");
                if (segment.Upserts.Count + segment.Removals.Count + segment.AddedBatches.Count + segment.AckedBatchIds.Count + segment.AddedBatches.Sum(b => (long)b.Rows.Count) > MaxRowsPerSegment)
                    throw new InvalidDataException("快照 journal segment 超過 100000 列上限");
                Apply(segment, accumulator, pending);
            }
            previous = segment.Checksum;
        }
        if (!sawSnapshot || previous != manifest.LastChecksum) throw new InvalidDataException("快照 journal manifest 指向的 committed checksum 不符或缺少 snapshot；保留資料並停止採集。");
        var state = new State(2, DatabaseId, generationEndpoint!, accumulator.Values.ToArray(), pending.Values.ToArray());
        state = state with { Checksum = Checksum(state) };
        Validate(state, generationEndpoint!, now);
        if (generationEndpoint != endpoint && HasRows(state))
        {
            if (!IsExactLegacyBinding(generationEndpoint, currentLegacyBinding))
                throw AmbiguousLegacyBinding();
            _verifiedLegacyBinding = generationEndpoint;
            _verifiedLegacyTargetBinding = endpoint;
        }
        else if (generationEndpoint != endpoint && IsExactLegacyBinding(generationEndpoint, currentLegacyBinding))
        {
            _verifiedLegacyBinding = generationEndpoint;
            _verifiedLegacyTargetBinding = endpoint;
        }
        _generation = manifest.Generation; _sequence = manifest.Sequence; _lastSegmentChecksum = previous;
        _segmentCount = checked((int)manifest.Sequence);
        _generationBytes = Enumerable.Range(1, _segmentCount).Sum(i => new FileInfo(SegmentPath(manifest.Generation, i)).Length);
        _currentAccumulator = accumulator; _currentPending = pending;
        RebuildRecordDates();
        _manifestDigest = Digest(manifestBytes); _expectedDigest = _manifestDigest; _loaded = true;
        _generationEndpoint = generationEndpoint;
        _liveAccumulatorRows = accumulator.Count;
        _pendingRows = pending.Values.Sum(batch => (long)batch.Rows.Count);
        _manifestBytes = manifestBytes.LongLength;
        _diskBytes = MeasureJournalBytes();
        // 完整驗證新 generation 後，才回收原 V2 checkpoint。
        if (File.Exists(FilePath)) File.Delete(FilePath);
        CleanupUncommittedSegments(manifest.Generation, manifest.Sequence);
        CleanupInactiveGenerations(manifest.Generation);
        _diskBytes = MeasureJournalBytes();
        SavedBytes = _diskBytes;
        return state;
    }

    private static void Apply(Segment segment,
        Dictionary<(DateTime Hour, long Sensor), PrtgSnapshotAccumulator.CheckpointRow> accumulator,
        Dictionary<string, Batch> pending)
    {
        foreach (var key in segment.Removals) accumulator.Remove((key.Hour, key.SensorObjid));
        foreach (var row in segment.Upserts) accumulator[(row.Hour, row.SensorObjid)] = row;
        foreach (var batch in segment.AddedBatches) pending[batch.Id] = batch;
        foreach (var id in segment.AckedBatchIds) pending.Remove(id);
    }

    private string SegmentPath(string generation, long sequence) => Path.Combine(GenerationPath(generation), "segments", SegmentName(sequence));
    private string GenerationPath(string generation)
    {
        if (!generation.StartsWith("checkpoint.g.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(generation["checkpoint.g.".Length..], "N", out _))
            throw new InvalidDataException("快照 journal manifest generation 名稱無效");
        var root = Path.GetFullPath(Path.GetDirectoryName(FilePath)!);
        var full = Path.GetFullPath(Path.Combine(root, generation));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("快照 journal generation 路徑超出資料目錄");
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("快照 journal generation 不可指向資料目錄外的 reparse point");
        return full;
    }
    private static string SegmentName(long seq) => seq.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".json";
    private static string SegmentChecksum(Segment segment) => Digest(JsonSerializer.SerializeToUtf8Bytes(segment with { Checksum = "" }));
    private static byte[] ManifestBytes(Manifest manifest)
    {
        var bare = manifest with { Checksum = "" };
        return JsonSerializer.SerializeToUtf8Bytes(bare with { Checksum = Digest(JsonSerializer.SerializeToUtf8Bytes(bare)) });
    }
    private static Manifest ReadManifest(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<Manifest>(bytes) ?? throw new InvalidDataException("快照 journal manifest 為空");
        if (value.Version != 1 || value.Checksum != Digest(JsonSerializer.SerializeToUtf8Bytes(value with { Checksum = "" })))
            throw new InvalidDataException("快照 journal manifest checksum 不符；保留資料並停止採集。");
        if (value.Sequence < 1 || string.IsNullOrEmpty(value.Generation) || !value.Generation.StartsWith("checkpoint.g.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value.Generation["checkpoint.g.".Length..], "N", out _))
            throw new InvalidDataException("快照 journal manifest generation 或 sequence 無效。");
        return value;
    }
    private void WriteManifest(Manifest manifest)
    {
        var bytes = ManifestBytes(manifest);
        var temp = ManifestPath + ".tmp";
        WriteDurable(temp, bytes);
        File.Replace(temp, ManifestPath, null);
        _manifestDigest = Digest(bytes);
    }
    private byte[] ReadFile(string path)
    {
        if (new FileInfo(path).Length > MaxBytes)
            throw new InvalidDataException("快照 journal 檔案超過 64 MiB 上限；拒絕讀入記憶體並保留檔案");
        var bytes = File.ReadAllBytes(path);
        BytesRead += bytes.LongLength;
        FilesRead++;
        return bytes;
    }
    private long JournalDiskBytes() => MeasureJournalBytes();
    private long MeasureJournalBytes()
    {
        var parent = Path.GetDirectoryName(FilePath)!;
        return Directory.Exists(parent)
            ? Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
    }
    private void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
        BytesWritten += bytes.LongLength;
        FilesWritten++;
    }

    internal void Save(string endpoint, IReadOnlyList<PrtgSnapshotAccumulator.CheckpointRow> accumulator,
        IReadOnlyList<Batch> pending, DateTime now)
    {
        lock (_gate)
        {
        using var writeLock = _ownership == null ? OpenWriterLock() : null;
        if (!_loaded && File.Exists(FilePath)) throw new InvalidDataException("儲存前必須先恢復目前 checkpoint；拒絕盲目覆寫。");
        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
            throw new InvalidDataException("快照復原檔超過容量上限，停止採集並保留檔案");
        var currentDigest = File.Exists(FilePath) ? Digest(File.ReadAllBytes(FilePath)) : null;
        if (currentDigest != _expectedDigest) throw new InvalidDataException("checkpoint 已由其他寫入者變更；拒絕過期儲存並停止採集。");
        var state = new State(2, DatabaseId, endpoint, accumulator, pending);
        Validate(state, endpoint, now);
        state = state with { Checksum = Checksum(state) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        if (bytes.LongLength > MaxBytes) throw new InvalidDataException("快照待寫資料達容量上限，停止採集");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        // 只在完整寫入並 flush 後替換；意外中斷保留上一份有效 checkpoint。
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
        else File.Move(temp, FilePath);
        SavedBytes = bytes.LongLength;
        _loaded = true; _expectedDigest = Digest(bytes);
        }
    }

    private void Validate(State state, string endpoint, DateTime now)
    {
        if (state.Version != 2 || state.DatabaseId != DatabaseId)
            throw new InvalidDataException("快照復原檔的版本、資料庫或來源位址不符；保留資料並停止採集");
        if (state.Accumulator == null || state.Pending == null || state.Pending.Any(b => b == null || b.Rows == null) ||
            state.Accumulator.Count + state.Pending.Sum(b => (long)b.Rows.Count) > MaxRows)
            throw new InvalidDataException("快照待寫資料達列數上限，停止採集");
        new PrtgSnapshotAccumulator().Restore(state.Accumulator);
        if (state.Pending.SelectMany(b => b.Rows).Any(r => r == null || r.Quality != PrtgDataQuality.Sampled ||
            (r.AvgValue.HasValue && !double.IsFinite(r.AvgValue.Value)) ||
            (r.MinValue.HasValue && !double.IsFinite(r.MinValue.Value)) ||
            (r.MaxValue.HasValue && !double.IsFinite(r.MaxValue.Value)) ||
            (r.Coverage.HasValue && (!double.IsFinite(r.Coverage.Value) || r.Coverage < 0 || r.Coverage > 100))))
            throw new InvalidDataException("快照待寫樣本內容無效，停止採集並保留檔案");
        if (state.SourceEndpoint != endpoint && (state.Accumulator.Count > 0 || state.Pending.Count > 0))
            throw new InvalidDataException("PRTG 來源位址與待寫資料不符；保留資料並停止採集");
        if (state.Pending.Any(b => !Guid.TryParseExact(b.Id, "N", out _)) ||
            state.Pending.Select(b => b.Id).Distinct().Count() != state.Pending.Count)
            throw new InvalidDataException("快照復原批次識別無效");
        if (state.Accumulator.Any(r => r.Hour < now.AddDays(-ReplayDays)) ||
            state.Pending.SelectMany(b => b.Rows).Any(r => r.PeriodStart < now.AddDays(-ReplayDays)))
            throw new InvalidDataException("快照復原資料超過 30 日安全重播期限；保留資料待管理者處理");
    }

    private static bool HasRows(State state) => state.Accumulator.Count > 0 || state.Pending.Count > 0;

    private static bool IsExactLegacyBinding(string? stored, string? currentLegacyBinding) =>
        stored is { Length: 64 } && stored.All(Uri.IsHexDigit) &&
        string.Equals(stored, currentLegacyBinding, StringComparison.Ordinal);

    private static InvalidDataException AmbiguousLegacyBinding() => new(
        "快照 journal 的舊版來源 binding 已變更且無法由保存的 opaque hash 判定原因；原資料已保留，需核對後復原，拒絕重標或自動重播。");
}
