using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

public sealed record PrtgDiskVerificationResult(
    long SensorObjid, long DeviceObjid, long HostId, string SensorType,
    string Status, string Summary, string? ChannelIdentifier, string? ChannelName,
    string? Unit, double? Scale, string? Direction, int ComparedPointCount,
    bool? ValuesMatch, DateTime CheckedAtUtc, DateTime DataDate,
    string ParserSemanticVersion, bool Cancelled = false);

/// <summary>Stores only typed verification output; never raw PRTG response bodies.</summary>
public sealed class PrtgDiskVerificationResultStore : JsonBlobSingleton<Dictionary<long, PrtgDiskVerificationResult>>
{
    public const string BlobKey = "prtg_disk_verification_results";
    public PrtgDiskVerificationResultStore(EfJsonBlobStore blob) : base(blob) { }
    public IReadOnlyCollection<PrtgDiskVerificationResult> GetRecent(int take = 20) =>
        Get().Values.OrderByDescending(x => x.CheckedAtUtc).Take(Math.Clamp(take, 1, 50)).ToArray();
    public PrtgDiskVerificationResult? Get(long sensorObjid) => Get().GetValueOrDefault(sensorObjid);
    public void Save(PrtgDiskVerificationResult result) => Update(all => all[result.SensorObjid] = result);
}
