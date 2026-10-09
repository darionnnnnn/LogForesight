using LogForesight.Core.Persistence.Sql;

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
    public PrtgDiskVerificationResultStore(EfJsonBlobStore blob) : base(blob) { }

    internal PrtgDiskVerificationResultSnapshot CaptureSnapshot()
    {
        var read = ReadBoundedValueWithVersion(MaximumSnapshotCharacters);
        foreach (var (key, result) in read.Value)
            if (key <= 0 || result is null || result.SensorObjid != key ||
                result.DeviceObjid <= 0 || result.HostId <= 0 || string.IsNullOrWhiteSpace(result.SensorType) ||
                string.IsNullOrWhiteSpace(result.Status) || string.IsNullOrWhiteSpace(result.Summary) ||
                result.CheckedAtUtc == default || result.CheckedAtUtc.Kind != DateTimeKind.Utc ||
                result.DataDate == default || string.IsNullOrWhiteSpace(result.ParserSemanticVersion))
                throw new InvalidDataException("磁碟核驗結果 blob 含有空白或識別不一致的紀錄；拒絕部分使用。");
        return new(read.Value, read.Version, read.DeserializeCount, read.SourceCharacters);
    }

    internal bool IsSnapshotCurrent(PrtgDiskVerificationResultSnapshot snapshot) =>
        snapshot.Version == ReadCurrentBlobVersion();

    public IReadOnlyCollection<PrtgDiskVerificationResult> GetRecent(int take = 20) =>
        Get().Values.OrderByDescending(x => x.CheckedAtUtc).Take(Math.Clamp(take, 1, 50)).ToArray();
    public PrtgDiskVerificationResult? Get(long sensorObjid) => Get().GetValueOrDefault(sensorObjid);
    public void Save(PrtgDiskVerificationResult result) => Update(all => all[result.SensorObjid] = result);
}

internal sealed class PrtgDiskVerificationResultSnapshot
{
    private readonly Dictionary<long, PrtgDiskVerificationResult> _items;

    internal PrtgDiskVerificationResultSnapshot(Dictionary<long, PrtgDiskVerificationResult> items,
        long version, int deserializeCount, int sourceCharacters)
    { _items = items; Version = version; DeserializeCount = deserializeCount; SourceCharacters = sourceCharacters; }

    internal long Version { get; }
    internal int DeserializeCount { get; }
    internal int SourceCharacters { get; }
    internal int Count => _items.Count;
    internal PrtgDiskVerificationResult? Get(long sensorObjid) => _items.GetValueOrDefault(sensorObjid);
}
