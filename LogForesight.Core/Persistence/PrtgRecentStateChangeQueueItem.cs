namespace LogForesight.Core.Persistence;

/// <summary>每台裝置一筆持久化交接項目，連接感測器鏡像提交與近期狀態變更擷取。</summary>
public sealed record PrtgRecentStateChangeQueueItem(
    long DeviceObjid,
    DateTime FromLocalDate,
    DateTime ToLocalDate,
    string SourceIdentityHash,
    string BusinessScopeVersion,
    DateTime FirstEnqueuedAtUtc,
    DateTime NextAttemptAtUtc,
    int Attempts,
    string? LeaseOwner,
    DateTime? LeaseExpiresAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>以底層 BlobRow 的樂觀版本保護佇列租約。</summary>
public sealed record PrtgRecentStateChangeLease(
    PrtgRecentStateChangeQueueItem Item,
    string Owner,
    long Version);

/// <summary>有界的作業紀錄，說明因範圍或容量變更而明確停止的待辦工作。</summary>
public sealed record PrtgRecentStateChangeQueueStopSummary(
    DateTime AtUtc,
    string Reason,
    string BusinessScopeVersion,
    int StoppedCount,
    IReadOnlyList<long> StoppedDeviceObjidSample,
    int RejectedDeviceCount,
    IReadOnlyList<long> RejectedDeviceObjidSample);
