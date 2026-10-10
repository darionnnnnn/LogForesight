using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>Enforces the same response contract used by native snapshot diagnostics.</summary>
public static class PrtgSnapshotCapacityResponseValidator
{
    public static void Validate(string json, IReadOnlyCollection<long> requestedSensorIds)
    {
        var result = PrtgSnapshotResponseContract.Validate(json, requestedSensorIds);
        if (!result.Compatible) throw new InvalidDataException(result.Reason);
    }
}
