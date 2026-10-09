using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;

namespace LogForesight.Web.Services;

/// <summary>Single bounded, source/scoped qualification capacity observation.</summary>
public sealed class PrtgQualificationCapacityPilotStore(StorageBackend backend)
{
    public const string BlobKey = "prtg_qualification_capacity_pilot_v1";
    public const int MaximumBytes = 8 * 1024;
    private readonly PilotBlobStore store = new(backend.Blob(BlobKey));

    public PrtgQualificationCapacityPilot? Read() => store.Read();

    public void Save(PrtgQualificationCapacityPilot pilot)
    {
        var json = JsonSerializer.Serialize(pilot);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("qualification-capacity-pilot-cap-exceeded");
        store.Write(json);
    }

    private sealed class PilotBlobStore(LogForesight.Core.Persistence.Sql.EfJsonBlobStore blob)
    {
        public PrtgQualificationCapacityPilot? Read()
        {
            var (json, _, length) = blob.ReadBoundedWithVersion(MaximumBytes);
            if (length > MaximumBytes || json is not null && Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidDataException("qualification-capacity-pilot-cap-exceeded");
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<PrtgQualificationCapacityPilot>(json)
                ?? throw new InvalidDataException("qualification-capacity-pilot-invalid");
        }
        public void Write(string json) => blob.MutateWithContext((_, current) => (json, true), MaximumBytes);
    }
}
