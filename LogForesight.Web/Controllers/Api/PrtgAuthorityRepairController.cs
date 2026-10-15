using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

public sealed record PrtgAuthorityRepairStatusDto(
    bool CanReadDetails, int ModeDocumentCount, bool HasDiskSemanticDocument, bool HasDiskVerificationDocument);

public sealed record PrtgAuthorityRepairDocumentDto(
    string Kind, long? HostId, long Version, int FormalLimitCharacters, int RepairBudgetCharacters,
    string State, int? ContentCharacters, string? Content);

public sealed record PrtgAuthorityRepairRequest(
    long? HostId, long ExpectedVersion,
    IReadOnlyList<PrtgAuthorityJsonRemoval>? RemoveJsonPaths,
    IReadOnlyList<PrtgAuthorityTextPatch>? TextPatches);

[ApiController]
[Route("api/admin/settings/prtg-authority-repair")]
[Permission(Capability.Maintain)]
public sealed class PrtgAuthorityRepairController : ControllerBase
{
    private const string ModePrefix = "prtg_resource_pressure_modes_";
    private readonly StorageBackend _backend;
    private readonly PrtgAuthorityRepairStore _repair;
    private readonly ICurrentUser _currentUser;
    private readonly IVisibilityService _visibility;
    private readonly IAuditService _audit;

    public PrtgAuthorityRepairController(StorageBackend backend, ICurrentUser currentUser,
        IVisibilityService visibility, IAuditService audit)
    {
        _backend = backend;
        _repair = new PrtgAuthorityRepairStore(backend);
        _currentUser = currentUser;
        _visibility = visibility;
        _audit = audit;
    }

    [HttpGet("status")]
    public ApiResponse<PrtgAuthorityRepairStatusDto> GetStatus()
    {
        using var ctx = _backend.CreateContext();
        var modeCount = ctx.Blobs.AsNoTracking().Count(row => row.BlobKey.StartsWith(ModePrefix));
        var hasSemantic = ctx.Blobs.AsNoTracking().Any(row => row.BlobKey == PrtgDiskSemanticEvidenceStore.BlobKey);
        var hasVerification = ctx.Blobs.AsNoTracking().Any(row => row.BlobKey == PrtgDiskVerificationResultStore.BlobKey);
        return ApiResponse<PrtgAuthorityRepairStatusDto>.Ok(new(_currentUser.Has(Capability.ViewAll),
            modeCount, hasSemantic, hasVerification));
    }

    [HttpGet("{kind}/document")]
    public IActionResult ReadDocument(string kind, [FromQuery] long? hostId = null)
    {
        if (!RequireBusinessDetails()) return Forbid();
        if (!TryParseKind(kind, out var documentKind)) return BadRequest(ApiResponse.Fail("invalid_repair_kind", "修復文件種類不支援。"));
        if (!CanAccessHost(documentKind, hostId)) return NotFound();
        try
        {
            var snapshot = _repair.Inspect(documentKind, hostId);
            return Ok(ApiResponse<PrtgAuthorityRepairDocumentDto>.Ok(new(
                documentKind.ToString(), snapshot.HostId, snapshot.Version, snapshot.FormalLimitCharacters,
                snapshot.RepairBudgetCharacters, snapshot.State.ToString(), snapshot.ContentCharacters, snapshot.Content)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(ApiResponse.Fail("repair_read_rejected", "此文件無法在固定維修讀取預算內安全檢視。"));
        }
    }

    [HttpPost("{kind}/repair")]
    public IActionResult Repair(string kind, [FromBody] PrtgAuthorityRepairRequest request)
    {
        if (!RequireBusinessDetails()) return Forbid();
        if (!TryParseKind(kind, out var documentKind)) return BadRequest(ApiResponse.Fail("invalid_repair_kind", "修復文件種類不支援。"));
        if (!CanAccessHost(documentKind, request.HostId)) return NotFound();
        if (request.ExpectedVersion <= 0) return BadRequest(ApiResponse.Fail("repair_version_required", "必須提供讀取時的文件版本。"));
        var jsonRemovals = request.RemoveJsonPaths;
        var patches = request.TextPatches;
        if ((jsonRemovals is null) == (patches is null) ||
            (jsonRemovals?.Count ?? 0) == 0 && (patches?.Count ?? 0) == 0)
            return BadRequest(ApiResponse.Fail("repair_edit_required", "請提供一組明確的 JSON 路徑移除或文字修補。"));

        try
        {
            var result = jsonRemovals is not null
                ? _repair.RemoveJsonPaths(documentKind, request.ExpectedVersion, jsonRemovals, request.HostId)
                : _repair.ApplyTextPatches(documentKind, request.ExpectedVersion, patches!, request.HostId);
            _audit.Record("prtg_authority_repair",
                $"PRTG authority 文件已依版本 CAS 修復（{documentKind}；版本 {result.PreviousVersion}→{result.NewVersion}；{result.EditCount} 項）",
                targetKind: "prtg_authority_blob",
                targetId: documentKind == PrtgAuthorityRepairDocumentKind.ResourcePressureMode ? request.HostId!.Value.ToString() : documentKind.ToString(),
                detail: new { Kind = documentKind.ToString(), HostId = request.HostId, result.PreviousVersion,
                    result.NewVersion, result.PreviousCharacters, result.NewCharacters, result.EditCount, result.ContentSha256 });
            return Ok(ApiResponse<PrtgAuthorityRepairResult>.Ok(result));
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Conflict(ApiResponse.Fail("repair_stale_version", "文件已被其他維護操作更新；請重新讀取後再修復。"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
        {
            _audit.Record("prtg_authority_repair_rejected", $"PRTG authority 修復未提交（{documentKind}；拒絕類別 {SafeReason(ex)}）",
                targetKind: "prtg_authority_blob", targetId: documentKind.ToString(), result: AuditResult.Denied);
            return BadRequest(ApiResponse.Fail("repair_rejected", "修復未提交：文件、修改或完整性檢查未通過。請重新讀取狀態。"));
        }
    }

    private bool RequireBusinessDetails()
    {
        if (_currentUser.Has(Capability.ViewAll)) return true;
        _audit.Record("access_denied", "PRTG authority 文件內容維修需要現有業務資料檢視能力。",
            targetKind: "auth", targetId: "ViewAll", result: AuditResult.Denied);
        return false;
    }

    private bool CanAccessHost(PrtgAuthorityRepairDocumentKind kind, long? hostId)
    {
        if (kind == PrtgAuthorityRepairDocumentKind.ResourcePressureMode)
            return hostId is > 0 && _visibility.GetVisibleHostIds().Contains(hostId.Value);
        return hostId is null;
    }

    private static bool TryParseKind(string kind, out PrtgAuthorityRepairDocumentKind parsed) =>
        Enum.TryParse(kind, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    private static string SafeReason(Exception exception) => exception switch
    {
        Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => "stale-version",
        InvalidDataException => "invalid-content",
        System.Text.Json.JsonException => "invalid-json",
        ArgumentException => "invalid-request",
        _ when exception.Message.StartsWith("repair-", StringComparison.Ordinal) => exception.Message,
        _ => "validation-failed"
    };
}
