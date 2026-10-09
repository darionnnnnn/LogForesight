using LogForesight.Core.Persistence;

namespace LogForesight.Core.Service;

/// <summary>正式消費端共用的逐資源資格判斷；無權威世代的舊資料一律不追認。</summary>
public static class PrtgResourceQualification
{
    public static bool IsCurrent(PrtgSensorTimelineEvidence? proof, PrtgResourceIdentity? identity,
        string sourceGeneration, long sensorId, long deviceId, long hostId)
    {
        return proof is not null && identity is not null && identity.Active && !identity.PendingReconciliation && identity.Epoch > 0 &&
            proof.IdentityEpoch > 0 && !string.IsNullOrWhiteSpace(identity.Generation) &&
            !string.IsNullOrWhiteSpace(proof.ResourceGeneration) &&
            identity.SensorId == sensorId && proof.SensorId == sensorId && proof.HostId == hostId &&
            identity.DeviceId == deviceId && identity.HostId == hostId &&
            identity.SourceGeneration == sourceGeneration && proof.SourceGeneration == sourceGeneration &&
            proof.ResourceGeneration == identity.Generation && proof.IdentityEpoch == identity.Epoch;
    }

    public static bool IsChannelCurrent(PrtgDiskSemanticEvidence? evidence, PrtgDiskVerificationResult? result,
        PrtgResourceIdentity? identity)
    {
        return evidence is not null && result is not null && identity is { Active: true, PendingReconciliation: false, Epoch: > 0 } &&
            evidence.SensorObjid == identity.SensorId && result.SensorObjid == identity.SensorId &&
            evidence.DeviceObjid == identity.DeviceId && result.DeviceObjid == identity.DeviceId &&
            evidence.HostId == identity.HostId && result.HostId == identity.HostId &&
            evidence.ParserSemanticVersion == PrtgDiskAssessmentService.ParserSemanticVersion &&
            result.ParserSemanticVersion == PrtgDiskAssessmentService.ParserSemanticVersion &&
            !string.IsNullOrWhiteSpace(evidence.SourceGeneration) && !string.IsNullOrWhiteSpace(evidence.ResourceGeneration) &&
            evidence.IdentityEpoch == identity.Epoch && result.IdentityEpoch == identity.Epoch &&
            !string.IsNullOrWhiteSpace(evidence.ChannelGeneration) && result.SourceGeneration == identity.SourceGeneration &&
            result.ResourceGeneration == identity.Generation && result.ChannelGeneration == identity.ChannelGeneration &&
            evidence.SourceGeneration == identity.SourceGeneration && evidence.ResourceGeneration == identity.Generation &&
            evidence.ChannelGeneration == identity.ChannelGeneration;
    }
}
