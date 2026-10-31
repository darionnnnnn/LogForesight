using System.Text.Encodings.Web;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace LogForesight.Web.Controllers.Api;

/// <summary>PRTG 環境探測、歷史回填與結構同步的啟停與狀態端點（系統管理 > PRTG 維護）</summary>
[ApiController]
[Route("api/admin/settings")]
[Permission(Capability.Maintain)]
public class PrtgBackfillAdminController : ControllerBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IAuditService _audit;
    private readonly PrtgProbeService _prtgProbe;
    private readonly PrtgBackfillService _prtgBackfill;
    private readonly PrtgStructureSyncService _prtgStructureSync;
    private readonly IHostStore _hosts;
    private readonly IVisibilityService _visibility;
    private readonly ICurrentUser _currentUser;

    public PrtgBackfillAdminController(
        IAuditService audit,
        PrtgProbeService prtgProbe,
        PrtgBackfillService prtgBackfill,
        PrtgStructureSyncService prtgStructureSync,
        IHostStore hosts,
        IVisibilityService visibility,
        ICurrentUser currentUser)
    {
        _audit = audit;
        _prtgProbe = prtgProbe;
        _prtgBackfill = prtgBackfill;
        _prtgStructureSync = prtgStructureSync;
        _hosts = hosts;
        _visibility = visibility;
        _currentUser = currentUser;
    }

    // ── PRTG API 探測（probe，PRTG 第 1 輪批次B-3）──────────────────────────────────

    [HttpGet("prtg-probe/status")]
    public ApiResponse<PrtgProbeStatusDto> GetPrtgProbeStatus() =>
        ApiResponse<PrtgProbeStatusDto>.Ok(_prtgProbe.GetStatus());

    [HttpPost("prtg-probe/start")]
    public ApiResponse<StartPrtgProbeResultDto> StartPrtgProbe()
    {
        if (!_prtgProbe.TryStart(out var error, out var isConflict))
        {
            // 與回填、結構同步同一套：被另一個執行擋下是 409，設定不齊是 400（§7.2）
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 探測。");
        }

        _audit.Record(
            action: AuditActions.PrtgProbeRun,
            summary: "執行 PRTG API 環境探測",
            targetKind: "system_settings",
            targetId: "prtg_probe",
            detail: new { });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(new StartPrtgProbeResultDto { Started = true });
    }

    [HttpPost("prtg-probe/data-flow/start")]
    public ApiResponse<StartPrtgProbeResultDto> StartPrtgDataFlowProbe()
    {
        if (!_prtgProbe.TryStartDataFlow(out var error, out var isConflict))
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 小範圍驗證。");

        _audit.Record(
            action: AuditActions.PrtgProbeRun,
            summary: "執行 PRTG 小範圍資料流驗證",
            targetKind: "system_settings",
            targetId: "prtg_probe_data_flow",
            detail: new { Scope = "one_host_one_sensor_one_day" });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(new StartPrtgProbeResultDto { Started = true });
    }

    /// <summary>
    /// 停止進行中的環境探測。
    /// </summary>
    [HttpPost("prtg-probe/cancel")]
    public ApiResponse<StartPrtgProbeResultDto> CancelPrtgProbe()
    {
        if (!_prtgProbe.TryCancel())
            throw DomainException.Conflict("目前沒有進行中的環境探測。");

        _audit.Record(
            action: AuditActions.PrtgProbeCancel,
            summary: "停止環境探測",
            targetKind: "system_settings",
            targetId: "prtg_probe",
            detail: new { });

        return ApiResponse<StartPrtgProbeResultDto>.Ok(
            new StartPrtgProbeResultDto { Started = false });
    }

    // ── PRTG 歷史回填（PRTG 第 1 輪批次E）────────────────────────────────────────

    [HttpGet("prtg-backfill/status")]
    public ApiResponse<PrtgBackfillStatusDto> GetPrtgBackfillStatus()
    {
        var first = _prtgBackfill.GetAccessSnapshot();
        if (first.Status.IsRunning)
        {
            if (first.RunKind == "selected")
                RequireRecordedSelectedPrtgBackfillAccess(first.SelectedHostIds);
            else if (first.RunKind is "full" or "tail")
                RequireFullPrtgTransferAccess();
            else
                throw DomainException.Forbidden("無法確認進行中 PRTG 回填的範圍，拒絕讀取狀態。");
        }
        else if (!HasFullPrtgTransferAccess() &&
                 !(first.RunKind == "selected" && HasRecordedSelectedPrtgBackfillAccess(first.SelectedHostIds)))
        {
            // A limited maintainer may read only a completed selected run whose saved scope they can still see.
            return ApiResponse<PrtgBackfillStatusDto>.Ok(new PrtgBackfillStatusDto());
        }

        var latest = _prtgBackfill.GetAccessSnapshot();
        if (!SameBackfillRun(first, latest))
            throw DomainException.Conflict("PRTG 回填狀態在授權期間已切換，請重新讀取狀態。");
        if (latest.Status.IsRunning)
        {
            if (latest.RunKind == "selected") RequireRecordedSelectedPrtgBackfillAccess(latest.SelectedHostIds);
            else RequireFullPrtgTransferAccess();
        }
        else if (!HasFullPrtgTransferAccess() &&
                 !(latest.RunKind == "selected" && HasRecordedSelectedPrtgBackfillAccess(latest.SelectedHostIds)))
        {
            return ApiResponse<PrtgBackfillStatusDto>.Ok(new PrtgBackfillStatusDto());
        }
        return ApiResponse<PrtgBackfillStatusDto>.Ok(latest.Status);
    }

    [RequireViewAll]
    [HttpGet("prtg-backfill/preview")]
    public ApiResponse<PrtgFullBackfillPreviewDto> PreviewPrtgBackfill()
    {
        return ApiResponse<PrtgFullBackfillPreviewDto>.Ok(_prtgBackfill.PreviewFull());
    }

    [RequireViewAll]
    [HttpPost("prtg-backfill/start")]
    public ApiResponse<StartPrtgBackfillResultDto> StartPrtgBackfill([FromBody] PrtgFullBackfillStartRequest? request = null)
    {
        if (!_prtgBackfill.TryStartFull(request, out var error, out var isConflict))
        {
            // 被互斥擋下（探測／取數／結構同步／回填自己在跑）是狀態衝突，回 409；
            // 設定或前提不齊仍是 400。與結構同步端點同一套分支。
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 歷史回填。");
        }

        _audit.Record(
            action: AuditActions.PrtgBackfillRun,
            summary: "執行 PRTG 歷史資料回填",
            targetKind: "system_settings",
            targetId: "prtg_backfill",
            detail: new { });

        return ApiResponse<StartPrtgBackfillResultDto>.Ok(new StartPrtgBackfillResultDto { Started = true });
    }

    [SkipDataVersionBump]
    [HttpPost("prtg-backfill/selected/preview")]
    public ApiResponse<PrtgSelectedBackfillPreviewDto> PreviewSelectedPrtgBackfill(
        [FromBody] PrtgSelectedBackfillRequest request)
    {
        if (request == null)
            throw DomainException.Validation("請指定回填主機與日期範圍。");
        RequireSelectedPrtgBackfillAccess(request.HostIds);
        return ApiResponse<PrtgSelectedBackfillPreviewDto>.Ok(_prtgBackfill.PreviewSelected(request));
    }

    [HttpPost("prtg-backfill/selected/start")]
    public ApiResponse<StartPrtgSelectedBackfillResultDto> StartSelectedPrtgBackfill(
        [FromBody] PrtgSelectedBackfillRequest request)
    {
        if (request == null)
            throw DomainException.Validation("請指定回填主機與日期範圍。");
        RequireSelectedPrtgBackfillAccess(request.HostIds);
        if (!_prtgBackfill.TryStartSelected(request, out var preview, out var error, out var isConflict))
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動指定範圍的 PRTG 回填。");

        _audit.Record(
            action: AuditActions.PrtgBackfillRun,
            summary: $"執行指定主機 PRTG 數值回填（{preview.FromDate:yyyy-MM-dd}～{preview.ToDate:yyyy-MM-dd}，{preview.HostCount} 台主機）",
            targetKind: "system_settings",
            targetId: "prtg_backfill_selected",
            detail: new
            {
                preview.FromDate,
                preview.ToDate,
                preview.HostIds,
                preview.EstimatedRequests,
                preview.DaysWithTargets
            });

        return ApiResponse<StartPrtgSelectedBackfillResultDto>.Ok(
            new StartPrtgSelectedBackfillResultDto { Started = true, Preview = preview });
    }

    /// <summary>
    /// 停止進行中的歷史回填：回填逐日打 PRTG，天數多時可跑上數小時；
    /// 沒有這顆鈕時唯一的中止方式是重啟站台。
    /// </summary>
    [HttpPost("prtg-backfill/cancel")]
    public ApiResponse<StartPrtgBackfillResultDto> CancelPrtgBackfill(
        [FromBody] CancelPrtgSelectedBackfillRequest? request = null)
    {
        // The selected UI supplies its run ID; generic (null/empty-body) cancellation is only for full/tail runs.
        // Authorize the captured identity and then cancel that exact identity to prevent a scope-check race.
        var snapshot = _prtgBackfill.GetAccessSnapshot();
        var runId = request?.RunId;
        bool cancelled;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            if (snapshot.RunKind != "selected")
                throw DomainException.Conflict("指定回填已結束或執行識別不符，請重新讀取狀態。");
            RequireRecordedSelectedPrtgBackfillAccess(snapshot.SelectedHostIds);
            cancelled = _prtgBackfill.TryCancelIdentifiedRun("selected", runId);
        }
        else
        {
            if (snapshot.RunKind is not ("full" or "tail") || string.IsNullOrWhiteSpace(snapshot.RunId))
                throw DomainException.Conflict("目前沒有可識別的全量或接續回填。");
            RequireFullPrtgTransferAccess();
            cancelled = _prtgBackfill.TryCancelIdentifiedRun(snapshot.RunKind, snapshot.RunId);
        }
        if (!cancelled)
            throw DomainException.Conflict("目前沒有進行中的歷史回填。");

        _audit.Record(
            action: AuditActions.PrtgBackfillCancel,
            summary: "停止 PRTG 歷史回填",
            targetKind: "system_settings",
            targetId: "prtg_backfill",
            detail: new { request?.RunId });

        return ApiResponse<StartPrtgBackfillResultDto>.Ok(
            new StartPrtgBackfillResultDto { Started = false });
    }

    // ── PRTG 同步結構與對應（docs/PRTG-SPEC.md §5a）───────────────────────

    [HttpGet("prtg-structure-sync/status")]
    public ApiResponse<PrtgStructureSyncStatusDto> GetPrtgStructureSyncStatus() =>
        ApiResponse<PrtgStructureSyncStatusDto>.Ok(
            _prtgStructureSync.GetStatus());

    [HttpPost("prtg-structure-sync/start")]
    public ApiResponse<StartPrtgStructureSyncResultDto> StartPrtgStructureSync()
    {
        if (!_prtgStructureSync.TryStart(out var error, out var isConflict))
        {
            // 被互斥擋下（取數執行中、或同步已在跑）是狀態衝突而非輸入錯誤——
            // 回 409 讓呼叫端分得出「你送錯了」與「現在不行，等一下再試」。
            throw isConflict
                ? DomainException.Conflict(error!)
                : DomainException.Validation(error ?? "無法啟動 PRTG 結構同步。");
        }

        _audit.Record(
            action: AuditActions.PrtgStructureSyncRun,
            summary: "執行 PRTG 結構同步與主機對應",
            targetKind: "system_settings",
            targetId: "prtg_structure_sync",
            detail: new { });

        return ApiResponse<StartPrtgStructureSyncResultDto>.Ok(
            new StartPrtgStructureSyncResultDto { Started = true });
    }

    /// <summary>
    /// 中止進行中的結構同步（docs/PRTG-SPEC.md §5a）。這條路徑要對 PRTG 爬整棵樹，
    /// 大型環境會跑上數十分鐘；沒有這顆鈕時唯一的中止方式是重啟站台。
    /// </summary>
    [HttpPost("prtg-structure-sync/cancel")]
    public ApiResponse<StartPrtgStructureSyncResultDto> CancelPrtgStructureSync()
    {
        // 沒有執行中就不是「停止成功」——回 409 與 start 被互斥擋下時同一種語意。
        if (!_prtgStructureSync.TryCancel())
            throw DomainException.Conflict("目前沒有進行中的結構同步。");

        _audit.Record(
            action: AuditActions.PrtgStructureSyncCancel,
            summary: "中止 PRTG 結構同步",
            targetKind: "system_settings",
            targetId: "prtg_structure_sync",
            detail: new { });

        return ApiResponse<StartPrtgStructureSyncResultDto>.Ok(
            new StartPrtgStructureSyncResultDto { Started = false });
    }

    private void RequireFullPrtgTransferAccess() =>
        _currentUser.RequireViewAll(RequireViewAllAttribute.DefaultMessage);

    private bool HasFullPrtgTransferAccess() => _currentUser.HasViewAll();

    private void RequireSelectedPrtgBackfillAccess(IEnumerable<long>? requestedHostIds)
    {
        if (requestedHostIds == null) return; // Preserve the service's existing empty-selection validation.
        const int maxRequestIdsToAuthorize = 64;
        const int maxDistinctSelectedHosts = 5;
        var ids = new HashSet<long>();
        using (var enumerator = requestedHostIds.GetEnumerator())
        {
            var inspected = 0;
            while (enumerator.MoveNext())
            {
                if (++inspected > maxRequestIdsToAuthorize)
                    throw DomainException.Validation("指定回填主機清單過大，請重新選擇主機。");
                var id = enumerator.Current;
                if (id <= 0 || !ids.Add(id)) continue; // Preserve invalid/duplicate validation and normalization in the service.
                if (ids.Count > maxDistinctSelectedHosts)
                    throw DomainException.Validation("一次最多選擇 5 台主機。");
            }
        }
        if (ids.Count == 0) return; // The service returns the existing validation error for an empty selection.
        var visible = _visibility.GetVisibleHostIds();
        if (ids.Any(id => !visible.Contains(id) || _visibility.IsCaseGrantOnly(id)))
            throw DomainException.Forbidden("指定回填僅允許 Maintain 使用者對一般可見主機執行；案件授與主機不可用於回填。");
    }

    private void RequireRecordedSelectedPrtgBackfillAccess(IReadOnlySet<long>? selectedHostIds)
    {
        if (!HasRecordedSelectedPrtgBackfillAccess(selectedHostIds))
            throw DomainException.Forbidden("無法確認指定回填保存的主機範圍，拒絕讀取或停止。");
    }

    private bool HasRecordedSelectedPrtgBackfillAccess(IReadOnlySet<long>? selectedHostIds)
    {
        if (selectedHostIds == null || selectedHostIds.Count == 0 || selectedHostIds.Count > 5 ||
            selectedHostIds.Any(id => id <= 0))
            return false;
        var visible = _visibility.GetVisibleHostIds();
        return selectedHostIds.All(id => visible.Contains(id) && !_visibility.IsCaseGrantOnly(id));
    }

    private static bool SameBackfillRun(PrtgBackfillAccessSnapshot left, PrtgBackfillAccessSnapshot right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal) &&
        string.Equals(left.RunKind, right.RunKind, StringComparison.Ordinal) &&
        ((left.SelectedHostIds == null && right.SelectedHostIds == null) ||
         (left.SelectedHostIds != null && right.SelectedHostIds != null && left.SelectedHostIds.SetEquals(right.SelectedHostIds)));
}
