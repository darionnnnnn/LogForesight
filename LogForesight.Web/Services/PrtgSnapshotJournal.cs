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
    // 每列只有數字、時間與固定 sampled 品質；512 bytes 同時預留累積器轉成值列的增量。
    internal const int ReservedBytesPerRow = 512;
    internal sealed record Batch(string Id, IReadOnlyList<PrtgValueRow> Rows);
    internal sealed record State(int Version, string DatabaseId, string SourceEndpoint,
        IReadOnlyList<PrtgSnapshotAccumulator.CheckpointRow> Accumulator, IReadOnlyList<Batch> Pending)
    {
        public string Checksum { get; init; } = "";
    }

    private readonly StorageBackend _backend;
    internal string FilePath { get; }
    internal long SavedBytes { get; private set; }
    private string? _databaseId;
    private FileStream? _ownership;
    private string? _expectedDigest;
    private bool _loaded;
    private readonly object _gate = new();

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
        var source = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get().SourceGeneration;
        if (source.Length == 0) return Endpoint(url); // 舊診斷模式，不能因此取得正式信任。
        var epoch = $"{Endpoint(url)}|{source}|{backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion()}|{backend.Blob("prtg_resource_generation_revision").ReadVersion()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(epoch)));
    }

    internal State? Load(string endpoint, DateTime now)
    {
        lock (_gate)
        {
        if (!File.Exists(FilePath)) { _loaded = true; _expectedDigest = null; return null; }
        if (new FileInfo(FilePath).Length > MaxBytes) throw new InvalidDataException("快照復原檔超過容量上限，停止採集並保留檔案");
        var bytes = File.ReadAllBytes(FilePath);
        var state = JsonSerializer.Deserialize<State>(bytes) ?? throw new InvalidDataException("快照復原檔為空");
        if (state.Version == 1) throw new InvalidDataException("舊版復原檔沒有完整性證明；保留原檔，請管理者備份並核對，不能自動重播或補簽 checksum。");
        if (string.IsNullOrEmpty(state.Checksum) || state.Checksum.Length != 64 || state.Checksum != Checksum(state))
            throw new InvalidDataException("快照復原檔 checksum 不符；停止採集並保留原檔。");
        Validate(state, endpoint, now);
        _loaded = true; _expectedDigest = Digest(bytes);
        SavedBytes = bytes.Length;
        return state;
        }
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
}
