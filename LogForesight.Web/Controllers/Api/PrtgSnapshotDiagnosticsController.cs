using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 快照最近完整小時的診斷，以及待寫檔停擺時的封存出口。</summary>
[ApiController]
[Route("api/prtg-snapshot-diagnostics")]
[Permission(Capability.Maintain)]
public sealed class PrtgSnapshotDiagnosticsController : ControllerBase
{
    private readonly PrtgSnapshotHostedService _snapshot;
    private readonly PrtgSnapshotDiagnosticsService _diagnostics;
    private readonly IAuditService? _audit;

    public PrtgSnapshotDiagnosticsController(PrtgSnapshotHostedService snapshot, IAuditService? audit = null)
    {
        _snapshot = snapshot;
        _diagnostics = snapshot.Diagnostics;
        _audit = audit;
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
        return ApiResponse<PrtgSnapshotDiagnosticsDto>.Ok(new PrtgSnapshotDiagnosticsDto(hours, PrtgSnapshotJournalDto.From(_snapshot.GetJournalPreview())));
    }

    /// <summary>
    /// 把停擺的待寫檔整個搬進封存夾並重新開始採集。只對「資料庫識別不符」與「PRTG 來源位址變更後舊樣本待處理」開放；
    /// 其他停止原因（checksum、容量、權限）回 409。不提供強制寫進目前資料庫。
    /// </summary>
    [HttpPost("archive-journal")]
    [RequireViewAll]
    public ApiResponse<PrtgSnapshotArchiveDto> ArchiveJournal()
    {
        var result = _snapshot.ArchiveJournalManually();
        _audit?.Record(
            action: AuditActions.PrtgSnapshotJournalArchive,
            summary: $"封存 PRTG 快照待寫檔（{result.Rows} 筆，原因 {result.StopCode}）",
            targetKind: "prtg_snapshot_journal",
            targetId: result.StopCode,
            detail: new { stopCode = result.StopCode, auto = false, rows = result.Rows, earliest = result.Earliest, latest = result.Latest, location = result.Location });
        return ApiResponse<PrtgSnapshotArchiveDto>.Ok(
            new PrtgSnapshotArchiveDto(result.Location, result.Rows, result.Earliest, result.Latest, result.StopCode));
    }

    internal static string StateName(PrtgSnapshotHourState state) => state switch
    {
        PrtgSnapshotHourState.Healthy => "coverage-evidence",
        PrtgSnapshotHourState.NoTargets => "no-targets",
        PrtgSnapshotHourState.OkCovered => "ok-covered",
        PrtgSnapshotHourState.Insufficient => "insufficient",
        PrtgSnapshotHourState.ReportedWriteCountMetTarget => "reported-write-count-met-target",
        PrtgSnapshotHourState.Stopped => "stopped",
        _ => "unknown"
    };
}

public sealed record PrtgSnapshotDiagnosticsDto(IReadOnlyList<PrtgSnapshotHourDto> Hours, PrtgSnapshotJournalDto? Journal = null);

/// <summary>待寫檔狀態與封存預覽。Stopped=false 時其餘欄位無意義。</summary>
public sealed record PrtgSnapshotJournalDto(bool Stopped, string? StopCode, string? Message, bool CanArchive,
    string? CannotArchiveReason, int Rows, int RowsInReplayWindow, DateTime? Earliest, DateTime? Latest, string? ArchiveLocation)
{
    internal static PrtgSnapshotJournalDto From(PrtgSnapshotJournalPreview p) => new(p.Stopped, p.StopCode, p.Message,
        p.CanArchive, p.CannotArchiveReason, p.Rows, p.RowsInReplayWindow, p.Earliest, p.Latest, p.ArchiveLocation);
}

public sealed record PrtgSnapshotArchiveDto(string Location, int Rows, DateTime? Earliest, DateTime? Latest, string StopCode);
public sealed record PrtgSnapshotHourDto(DateTime Hour, string State, int Targets, int AvailableValues,
    int Attempts, int Successes, int Skips, int WriteFailures, IReadOnlyDictionary<string, int> Reasons,
    int SampledUsableValues, int SampledLowCoverageValues, int OkValues, int DatabaseUsableValues);
