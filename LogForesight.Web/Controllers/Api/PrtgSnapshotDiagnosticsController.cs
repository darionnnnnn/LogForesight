using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 快照最近完整小時的唯讀診斷。</summary>
[ApiController]
[Route("api/prtg-snapshot-diagnostics")]
[Permission(Capability.Maintain)]
public sealed class PrtgSnapshotDiagnosticsController : ControllerBase
{
    private readonly PrtgSnapshotDiagnosticsService _diagnostics;

    public PrtgSnapshotDiagnosticsController(PrtgSnapshotHostedService snapshot)
    {
        _diagnostics = snapshot.Diagnostics;
    }

    [HttpGet]
    public ApiResponse<PrtgSnapshotDiagnosticsDto> Get()
    {
        var hours = _diagnostics.ReadRecent(DateTime.Now, 24).Select(x => new PrtgSnapshotHourDto(
            x.Hour,
            // ok 可用值可能覆蓋 sampled；它不能證明快照成功，因此 API 不將其回報為健康快照。
            StateName(x.State),
            x.Targets, x.AvailableValues, x.Attempts, x.Successes, x.Skips, x.WriteFailures,
            x.Reasons, x.SampledUsableValues, x.SampledLowCoverageValues, x.OkValues, x.DatabaseUsableValues)).ToArray();
        return ApiResponse<PrtgSnapshotDiagnosticsDto>.Ok(new PrtgSnapshotDiagnosticsDto(hours));
    }

    internal static string StateName(PrtgSnapshotHourState state) => state switch
    {
        PrtgSnapshotHourState.Healthy => "coverage-evidence",
        PrtgSnapshotHourState.NoTargets => "no-targets",
        PrtgSnapshotHourState.OkCovered => "ok-covered",
        PrtgSnapshotHourState.Insufficient => "insufficient",
        PrtgSnapshotHourState.ReportedWriteCountMetTarget => "reported-write-count-met-target",
        _ => "unknown"
    };
}

public sealed record PrtgSnapshotDiagnosticsDto(IReadOnlyList<PrtgSnapshotHourDto> Hours);
public sealed record PrtgSnapshotHourDto(DateTime Hour, string State, int Targets, int AvailableValues,
    int Attempts, int Successes, int Skips, int WriteFailures, IReadOnlyDictionary<string, int> Reasons,
    int SampledUsableValues, int SampledLowCoverageValues, int OkValues, int DatabaseUsableValues);
