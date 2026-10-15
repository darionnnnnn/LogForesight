using LogForesight.Core.Persistence.Sql;
using System.Text.Json;

namespace LogForesight.Core.Persistence;

public sealed record PrtgDiskVerificationResult(
    long SensorObjid, long DeviceObjid, long HostId, string SensorType,
    string Status, string Summary, string? ChannelIdentifier, string? ChannelName,
    string? Unit, double? Scale, string? Direction, int ComparedPointCount,
    bool? ValuesMatch, DateTime CheckedAtUtc, DateTime DataDate,
    string ParserSemanticVersion, bool Cancelled = false,
    string SourceGeneration = "", string ResourceGeneration = "", string ChannelGeneration = "",
    long IdentityEpoch = 0);

/// <summary>Stores only typed verification output; never raw PRTG response bodies.</summary>
public sealed class PrtgDiskVerificationResultStore : JsonBlobSingleton<Dictionary<long, PrtgDiskVerificationResult>>
{
    public const string BlobKey = "prtg_disk_verification_results";
    internal const int MaximumSnapshotCharacters = 32 * 1024 * 1024;
    private readonly EfJsonBlobStore _blob;
    public PrtgDiskVerificationResultStore(EfJsonBlobStore blob) : base(blob) => _blob = blob;

    internal PrtgDiskVerificationResultSnapshot CaptureSnapshot()
    {
        var read = _blob.ReadBoundedWithVersion(MaximumSnapshotCharacters);
        if (read.Version == 0 && read.Prefix is null)
            return new([], 0, 0, 0);
        if (read.ReportedLength > MaximumSnapshotCharacters || read.Prefix is null ||
            read.Prefix.Length > MaximumSnapshotCharacters)
            throw new InvalidDataException("Disk verification results exceed their bounded read limit.");
        StrictAuthorityJsonDocument.Validate<Dictionary<long, PrtgDiskVerificationResult>>(read.Prefix);
        var value = JsonSerializer.Deserialize<Dictionary<long, PrtgDiskVerificationResult>>(read.Prefix,
            LfJsonOptions.Pretty) ?? throw new JsonException("Disk verification document is null.");
        ValidateDocument(value);
        return new(value, read.Version, 1, read.Prefix.Length);
    }

    internal static void ValidateRepairDocument(string raw)
    {
        if (raw.Length > MaximumSnapshotCharacters)
            throw new InvalidDataException("Disk verification repair candidate exceeds its formal limit.");
        StrictAuthorityJsonDocument.Validate<Dictionary<long, PrtgDiskVerificationResult>>(raw);
        var document = JsonSerializer.Deserialize<Dictionary<long, PrtgDiskVerificationResult>>(
            raw, LfJsonOptions.Pretty) ?? throw new JsonException("Disk verification document is null.");
        ValidateDocument(document);
    }

    public new Dictionary<long, PrtgDiskVerificationResult> Update(Action<Dictionary<long, PrtgDiskVerificationResult>> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return _blob.MutateWithContext((_, raw) =>
        {
            var value = DeserializeForMutation(raw);
            mutation(value);
            ValidateDocument(value);
            var serialized = JsonSerializer.Serialize(value, LfJsonOptions.Pretty);
            var content = StrictAuthorityJsonDocument.PreserveUnknownDictionaryEntryProperties<PrtgDiskVerificationResult>(
                raw, serialized);
            if (content.Length > MaximumSnapshotCharacters)
                throw new InvalidDataException("Disk verification results exceed their bounded write limit.");
            return (content, value);
        }, maxCurrentCharacters: MaximumSnapshotCharacters, skipUnchangedContent: true,
            enforceCurrentUtf8ByteCap: false);
    }

    private static Dictionary<long, PrtgDiskVerificationResult> DeserializeForMutation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        StrictAuthorityJsonDocument.Validate<Dictionary<long, PrtgDiskVerificationResult>>(raw);
        var value = JsonSerializer.Deserialize<Dictionary<long, PrtgDiskVerificationResult>>(raw, LfJsonOptions.Pretty)
            ?? throw new JsonException("Disk verification document is null.");
        ValidateDocument(value);
        return value;
    }

    private static void ValidateDocument(Dictionary<long, PrtgDiskVerificationResult> document)
    {
        foreach (var (key, result) in document)
            if (key <= 0 || result is null || result.SensorObjid != key ||
                result.DeviceObjid <= 0 || result.HostId <= 0 || string.IsNullOrWhiteSpace(result.SensorType) ||
                string.IsNullOrWhiteSpace(result.Status) || string.IsNullOrWhiteSpace(result.Summary) ||
                result.CheckedAtUtc == default || result.CheckedAtUtc.Kind != DateTimeKind.Utc ||
                result.DataDate == default || string.IsNullOrWhiteSpace(result.ParserSemanticVersion))
                throw new InvalidDataException("磁碟核驗結果 blob 含有空白或識別不一致的紀錄；拒絕部分使用。");
    }

    internal bool IsSnapshotCurrent(PrtgDiskVerificationResultSnapshot snapshot) =>
        snapshot.Version == ReadCurrentBlobVersion();

    public new Dictionary<long, PrtgDiskVerificationResult> Get() => CaptureSnapshot().CopyItems();
    public IReadOnlyCollection<PrtgDiskVerificationResult> GetRecent(int take = 20) =>
        Get().Values.OrderByDescending(x => x.CheckedAtUtc).Take(Math.Clamp(take, 1, 50)).ToArray();
    public PrtgDiskVerificationResult? Get(long sensorObjid) => CaptureSnapshot().Get(sensorObjid);
    public void Save(PrtgDiskVerificationResult result) => Update(all => all[result.SensorObjid] = result);
}

internal sealed class PrtgDiskVerificationResultSnapshot
{
    private readonly Dictionary<long, PrtgDiskVerificationResult> _items;

    internal PrtgDiskVerificationResultSnapshot(Dictionary<long, PrtgDiskVerificationResult> items,
        long version, int deserializeCount, int sourceCharacters)
    { _items = items; Version = version; DeserializeCount = deserializeCount; SourceCharacters = sourceCharacters; }

    internal long Version { get; }
    /// <summary>Number of typed dictionary deserializations; the raw structural guard is a separate parse.</summary>
    internal int DeserializeCount { get; }
    internal int SourceCharacters { get; }
    internal int Count => _items.Count;
    internal Dictionary<long, PrtgDiskVerificationResult> CopyItems() => new(_items);
    internal PrtgDiskVerificationResult[] Values() => _items.Values.ToArray();
    internal PrtgDiskVerificationResult? Get(long sensorObjid) => _items.GetValueOrDefault(sensorObjid);
}
