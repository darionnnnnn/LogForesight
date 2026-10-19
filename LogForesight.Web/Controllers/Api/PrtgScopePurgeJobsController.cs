using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using NLog;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// Read/recovery endpoints for an accepted purge. Kept outside SettingsController's class-level
/// Maintain filter so an authenticated owner can still read progress after Maintain is revoked.
/// Every action performs the live, durable authorization and owner checks itself.
/// </summary>
[ApiController]
[Route("api/admin/settings/prtg-scope-purge/jobs")]
public sealed class PrtgScopePurgeJobsController : ControllerBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly StorageBackend _backend;
    private readonly ICurrentUser _currentUser;
    private readonly PrtgScopePurgeAuthorizationService _authorization;
    private readonly IAuditService _audit;
    private readonly PrtgBackfillService? _backfill;

    public PrtgScopePurgeJobsController(StorageBackend backend, ICurrentUser currentUser,
        PrtgScopePurgeAuthorizationService authorization, IAuditService audit, PrtgBackfillService? backfill = null)
    {
        _backend = backend;
        _currentUser = currentUser;
        _authorization = authorization;
        _audit = audit;
        _backfill = backfill;
    }

    [HttpPost("~/api/admin/settings/prtg-scope-purge/confirm")]
    public ApiResponse<PrtgScopePurgeResultDto> Confirm([FromBody] PrtgScopePurgeAcceptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PreviewId)) throw DomainException.Validation("請先預覽清除範圍。");
        var store = _backend.PrtgStore();
        var jobs = store.ScopePurgeJobs();
        var prior = jobs.Get(request.PreviewId);
        if (prior == null) throw DomainException.Validation("預覽不存在或已失效，請重新預覽。");
        if (prior.Source != "manual") throw DomainException.Forbidden("自動清除作業不提供人工確認／續跑路由。");
        if (!_currentUser.IsAuthenticated || prior.ActorId != _currentUser.UserId)
            throw DomainException.Forbidden("此清除預覽屬於其他操作者。");

        // An accepted preview is an idempotency key. Resolve it before current scope/expiry checks,
        // but only return the durable result after a fresh read-authorization check.
        if (prior.Status != "preview")
        {
            var read = _authorization.CheckRead(prior.ActorId, prior.AuthFingerprint);
            if (!read.Allowed) throw DomainException.Forbidden(read.DenialReason ?? "目前沒有讀取此作業的權限。");
            return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(prior));
        }

        var authorization = _authorization.Check(prior.ActorId, prior.AuthFingerprint);
        if (!authorization.Allowed) throw DomainException.Forbidden(authorization.DenialReason ?? "目前沒有維護權限。");
        if (_backfill == null)
            throw DomainException.Validation("PRTG 回填服務未啟用，無法判斷是否有其他 PRTG 作業執行中。");
        var conflict = _backfill.ScopePurgeConflict();
        if (conflict != null) throw DomainException.Conflict(conflict);

        var (scope, blocked) = ComputeTrustedScope(_backend);
        if (blocked != null) throw DomainException.Validation($"{blocked}，目前不能清除範圍外資料。");
        PrtgScopePurgeJobRow accepted;
        try
        {
            accepted = jobs.Accept(request.PreviewId, _currentUser.UserId, authorization.PermissionRevision,
                authorization.AuthFingerprint, PrtgScopePurge.ComputeFingerprint(store, scope!), DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("另一個範圍外清除作業", StringComparison.Ordinal))
        {
            throw DomainException.Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        _audit.Record(AuditActions.PrtgScopePurge,
            "已接受監看範圍外 PRTG 清除作業；資料將以耐久小批次逐步清除",
            "system_settings", "prtg_scope_purge", new { jobId = accepted.JobId, monitoredDevices = accepted.MonitoredDevices });
        return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(accepted));
    }

    [HttpGet("{jobId}")]
    public ApiResponse<PrtgScopePurgeResultDto> Get(string jobId)
    {
        var jobs = _backend.PrtgStore().ScopePurgeJobs();
        var job = jobs.Get(jobId) ?? throw DomainException.Validation("清除作業不存在。");
        EnsureOwner(job);
        var authorization = _authorization.CheckRead(job.ActorId, job.AuthFingerprint);
        if (!authorization.Allowed) throw DomainException.Forbidden(authorization.DenialReason ?? "目前沒有讀取此作業的權限。");
        return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(job));
    }

    [HttpPost("{jobId}/resume")]
    public ApiResponse<PrtgScopePurgeResultDto> Resume(string jobId,
        [FromBody] PrtgScopePurgeResumeRequest request, CancellationToken cancellationToken)
    {
        var store = _backend.PrtgStore();
        var jobs = store.ScopePurgeJobs();
        var job = jobs.Get(jobId) ?? throw DomainException.Validation("清除作業不存在。");
        EnsureOwner(job);

        var authorization = _authorization.Check(job.ActorId, job.AuthFingerprint);
        if (!authorization.Allowed)
        {
            if (job.Status is "accepted" or "running" or "paused")
                job = jobs.Stop(jobId, "permission-revoked", DateTimeOffset.UtcNow);
            var read = _authorization.CheckRead(job.ActorId, job.AuthFingerprint);
            if (!read.Allowed) throw DomainException.Forbidden(read.DenialReason ?? "目前沒有讀取此作業的權限。");
            return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(job));
        }

        if (request.ExpectedRevision < 1) throw DomainException.Validation("續跑版本識別無效。");
        var (scope, blocked) = ComputeTrustedScope(_backend);
        if (blocked != null)
        {
            job = jobs.Stop(jobId, "scope-untrusted", DateTimeOffset.UtcNow);
            return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(job));
        }

        try
        {
            job = jobs.RunBatch(jobId, request.ExpectedRevision,
                PrtgScopePurge.ComputeFingerprint(store, scope!), DateTimeOffset.UtcNow, cancellationToken,
                authorization.ToBatchFence());
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("另一個範圍外清除作業", StringComparison.Ordinal))
        {
            throw DomainException.Conflict(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw DomainException.Validation(ex.Message);
        }
        return ApiResponse<PrtgScopePurgeResultDto>.Ok(ToResult(job));
    }

    private void EnsureOwner(PrtgScopePurgeJobRow job)
    {
        if (job.Source != "manual" || !_currentUser.IsAuthenticated || job.ActorId != _currentUser.UserId)
            throw DomainException.Forbidden("此清除作業屬於其他操作者。");
    }

    private static (PrtgScopeResult? Scope, string? Blocked) ComputeTrustedScope(StorageBackend backend)
    {
        var store = backend.PrtgStore();
        PrtgScopeResult? scope = null;
        try
        {
            var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            scope = PrtgScopeDevices.Compute(store, new HostStore(backend.Blob("hosts")),
                new PrtgMirrorGuardSource(store), settings,
                new SentinelStore(backend.Blob("sentinels")).GetAll(), new SilentRunConsole(),
                new PrtgAddressResolver(), hostIds: null);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "清除續跑時監看範圍計算失敗");
        }
        return (scope, PrtgScopePurge.CheckScope(scope, store.GetMirrorSummary().DeviceCount > 0));
    }

    private static PrtgScopePurgeResultDto ToResult(PrtgScopePurgeJobRow job) => new()
    {
        JobId = job.JobId,
        Status = job.Status,
        Values = checked((int)job.DeletedValues),
        StateChanges = checked((int)job.DeletedStateChanges),
        MonitoredDevices = job.MonitoredDevices,
        OtherwiseRemovedValues = job.OtherwiseRemovedValues,
        OtherwiseRemovedStateChanges = job.OtherwiseRemovedStateChanges,
        RemainingValues = job.RemainingValues,
        RemainingStateChanges = job.RemainingStateChanges,
        CountsReconciled = job.CountsReconciled,
        StopReason = job.StopReason,
        Revision = job.Revision
    };

    private sealed class SilentRunConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
