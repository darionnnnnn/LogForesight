using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>管理者確認的 Core 身分與試點；端點摘要只用於防止設定換站後繼續沿用確認。</summary>
public sealed class PrtgMonitoringPolicy
{
    public string Revision { get; set; } = string.Empty;
    public string CoreSystemId { get; set; } = string.Empty;
    public string SourceGeneration { get; set; } = string.Empty;
    public string EndpointHint { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    public List<long> HostIds { get; set; } = [];
    public List<long> SensorIds { get; set; } = [];
    public string ConfirmedBy { get; set; } = string.Empty;
    public string SourceTimeZoneId { get; set; } = "";
    public string SourceCultureName { get; set; } = "";
    public bool Ready(string url) => CoreSystemId.Length > 0 && SourceGeneration.Length > 0 &&
        ValidFrom != default && EndpointHint == EfPrtgObservationStore.SourceHintFor(url) &&
        HostIds.Count > 0 && SensorIds.Count > 0 && SourceTimeZoneId.Length > 0 && SourceCultureName.Length > 0;
}

public sealed class PrtgMonitoringPolicyStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgMonitoringPolicy>(blob)
{
    public const string BlobKey = "prtg_monitoring_policy";
}

/// <summary>逐 sensor 保存，避免所有感測器歷史集中在單一大 blob；每個試點最多保存 31 天。</summary>
public sealed class PrtgSensorTimelineEvidence
{
    public long SensorId { get; set; }
    public long HostId { get; set; }
    public string SourceGeneration { get; set; } = string.Empty;
    public string ResourceGeneration { get; set; } = string.Empty;
    public string IdentityFingerprint { get; set; } = string.Empty;
    public long MappingRevision { get; set; }
    public string DiskSemanticFingerprint { get; set; } = "";
    public DateTimeOffset? DiskSemanticValidFrom { get; set; }
    public DateTimeOffset? DiskSemanticCheckedAt { get; set; }
    public DateTimeOffset? DiskIncidentStartedAt { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public string QualityReason { get; set; } = "not-observed";
    public List<PrtgSensorCoverage> Coverage { get; set; } = [];
    public List<PrtgTimedState> States { get; set; } = [];

    public void Bind(long sensorId, long hostId, string sourceGeneration, string fingerprint, DateTimeOffset observedAt)
    {
        if (SensorId == sensorId && HostId == hostId && SourceGeneration == sourceGeneration &&
            IdentityFingerprint == fingerprint) return;
        SensorId = sensorId; HostId = hostId; SourceGeneration = sourceGeneration;
        ResourceGeneration = Guid.NewGuid().ToString("N"); IdentityFingerprint = fingerprint;
        DiskSemanticFingerprint = ""; DiskSemanticValidFrom = null; DiskSemanticCheckedAt = null; DiskIncidentStartedAt = null;
        ValidFrom = observedAt; Coverage.Clear(); States.Clear(); QualityReason = "identity-warmup";
    }

    public IReadOnlyList<PrtgCoveredStatePeriod> Periods(DateTimeOffset from, DateTimeOffset through) =>
        PrtgCoveredStateTimeline.Build(SensorId, SourceGeneration, ResourceGeneration, Coverage, States, from, through);

    public void Accept(DateTimeOffset from, DateTimeOffset through, IEnumerable<PrtgTimedState> states)
    {
        if (from < ValidFrom || through <= from) throw new ArgumentException("涵蓋不可早於已確認身分。");
        var incoming = states.ToArray();
        if (incoming.Any(s => s.SensorObjid != SensorId || s.SourceGeneration != SourceGeneration ||
            s.ResourceGeneration != ResourceGeneration || s.At < from || s.At > through))
            throw new ArgumentException("狀態與涵蓋身分或時間不一致。");
        States = States.Concat(incoming).Distinct().OrderBy(s => s.At).ToList();
        var spans = Coverage.Append(new(SensorId, from, through, SourceGeneration, ResourceGeneration))
            .OrderBy(c => c.From).ToArray();
        Coverage = [];
        foreach (var span in spans)
            if (Coverage.Count > 0 && Coverage[^1].Through >= span.From)
                Coverage[^1] = Coverage[^1] with { Through = Coverage[^1].Through > span.Through ? Coverage[^1].Through : span.Through };
            else Coverage.Add(span);
        var cutoff = through.AddDays(-31);
        // 保留涵蓋起點及其前導狀態，不能裁掉前導後誤認恢復。
        Coverage.RemoveAll(c => c.Through < cutoff);
        var anchor = States.LastOrDefault(s => s.At <= cutoff);
        var first = anchor?.At ?? cutoff;
        Coverage = Coverage.Select(c => c with { From = c.From < first ? first : c.From }).ToList();
        States.RemoveAll(s => s.At < first);
        if (States.Count > 20000) throw new InvalidOperationException("timeline-capacity-exceeded");
        QualityReason = "covered";
        LastAttemptAt = through;
    }
}

public sealed class PrtgSensorTimelineStore(EfJsonBlobStore blob) : JsonBlobSingleton<PrtgSensorTimelineEvidence>(blob)
{
    public const string Prefix = "prtg_timeline_";
}
