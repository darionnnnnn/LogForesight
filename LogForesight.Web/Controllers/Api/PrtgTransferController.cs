using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace LogForesight.Web.Controllers.Api;

[ApiController]
[Route("api/admin/settings/prtg-import-transfers")]
[Permission(Capability.Maintain)]
public sealed class PrtgTransferController : ControllerBase
{
    private static readonly TimeSpan ValidationLeaseDuration = TimeSpan.FromHours(1);
    private readonly StorageBackend _backend;
    private readonly IPrtgTransferCapacityProvider _capacity;
    private readonly IHostStore? _hosts;
    private readonly IVisibilityService? _visibility;
    private readonly ICurrentUser? _currentUser;
    private readonly ISystemSettingsStore? _settings;
    private readonly PrtgLegacyJsonTransferValidator _validator;

    public PrtgTransferController(StorageBackend? backend, IPrtgTransferCapacityProvider? capacity,
        IHostStore? hosts, IVisibilityService? visibility, ICurrentUser? currentUser,
        ISystemSettingsStore? settings, PrtgLegacyJsonTransferValidator? validator = null)
    {
        _backend = backend ?? throw new InvalidOperationException("PRTG 診斷傳輸儲存後端未設定。");
        _capacity = capacity ?? throw new InvalidOperationException("PRTG 診斷傳輸容量探測器未設定。");
        _hosts = hosts;
        _visibility = visibility;
        _currentUser = currentUser;
        _settings = settings;
        _validator = validator ?? new PrtgLegacyJsonTransferValidator();
    }

    [HttpPost]
    [RequestSizeLimit(16 * 1024)]
    public IActionResult Create([FromBody] PrtgTransferCreateRequestDto request)
    {
        try
        {
            var binding = CaptureBinding();
            var store = Store();
            var status = store.Create(new PrtgTransferCreateRequest(request.TransferId,
                request.DeclaredBytes, request.ChunkCount, request.PackageSha256, binding), DateTimeOffset.UtcNow);
            return Ok(ApiResponse<PrtgTransferStatus>.Ok(status));
        }
        catch (PrtgTransferStoreException ex) { return StoreError(ex); }
    }

    [HttpGet("{transferId:guid}")]
    public IActionResult GetStatus(Guid transferId)
    {
        try
        {
            var binding = CaptureBinding();
            return Ok(ApiResponse<PrtgTransferStatus>.Ok(Store().GetStatus(transferId, binding)));
        }
        catch (PrtgTransferStoreException ex) { return StoreError(ex); }
    }

    [HttpPost("{transferId:guid}/abandon")]
    [RequestSizeLimit(1024)]
    public IActionResult Abandon(Guid transferId)
    {
        try
        {
            var binding = CaptureBinding();
            Store().AbandonOwned(transferId, binding, DateTimeOffset.UtcNow);
            return Ok(ApiResponse<object>.Ok(new { TransferId = transferId, State = PrtgTransferStates.Abandoned }));
        }
        catch (PrtgTransferStoreException ex) { return StoreError(ex); }
    }

    [HttpPut("{transferId:guid}/chunks/{ordinal:int}")]
    [Consumes("application/octet-stream")]
    [RequestSizeLimit(EfPrtgTransferStore.MaxChunkBytes)]
    public async Task<IActionResult> PutChunk(Guid transferId, int ordinal, CancellationToken cancellationToken)
    {
        PrtgTransferChunkWriteLease? lease = null;
        PrtgTransferBinding? initialBinding = null;
        var acceptStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = CaptureBinding();
            initialBinding = binding;
            var store = Store();
            var status = store.GetStatus(transferId, binding);
            if (ordinal < 0 || ordinal >= status.ChunkCount)
                throw new PrtgTransferStoreException("chunk_shape_invalid", "片段序號超出宣告範圍。", 400);
            var expectedBytes = checked((int)Math.Min(EfPrtgTransferStore.MaxChunkBytes,
                status.DeclaredBytes - checked((long)ordinal * EfPrtgTransferStore.MaxChunkBytes)));
            if (!string.Equals(Request.ContentType?.Split(';', 2)[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                Request.ContentLength != expectedBytes)
                throw new PrtgTransferStoreException("chunk_size_invalid", "請以 application/octet-stream 傳送正確長度的單片原始位元組。", 400);

            // 先持久保留 buffer lease，通過後才配置並讀取最多 4 MiB request body。
            lease = store.BeginChunkWrite(transferId, ordinal, expectedBytes, binding, DateTimeOffset.UtcNow);
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new byte[expectedBytes];
            await Request.Body.ReadExactlyAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var currentBinding = CaptureBinding();
            if (currentBinding != binding)
            {
                store.ReleaseChunkWrite(lease, binding, DateTimeOffset.UtcNow);
                lease = null;
                throw new PrtgTransferStoreException("transfer_binding_changed", "片段接收期間全站主機範圍或來源設定已變更。", 403);
            }
            cancellationToken.ThrowIfCancellationRequested();
            acceptStarted = true;
            var receipt = store.AcceptChunk(lease, payload, currentBinding, DateTimeOffset.UtcNow);
            return Ok(ApiResponse<PrtgTransferChunkReceipt>.Ok(receipt));
        }
        catch (PrtgTransferStoreException ex) { return StoreError(ex); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, ApiResponse.Fail("request_cancelled", "片段讀取已取消；尚未接受的內容可安全重送。"));
        }
        catch (EndOfStreamException)
        {
            return BadRequest(ApiResponse.Fail("chunk_body_truncated", "片段本文長度不足，請查詢狀態後重送。"));
        }
        finally
        {
            if (lease != null && !acceptStarted)
            {
                try { Store().ReleaseChunkWrite(lease, initialBinding!, DateTimeOffset.UtcNow); }
                catch { /* 暫存 reservation 由既有 lease 到期回收。 */ }
            }
        }
    }

    [HttpPost("{transferId:guid}/complete")]
    [RequestSizeLimit(1024)]
    public async Task<IActionResult> Complete(Guid transferId, CancellationToken cancellationToken)
    {
        PrtgTransferValidationLease? lease = null;
        PrtgTransferBinding? initialBinding = null;
        var store = Store();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var binding = CaptureBinding();
            initialBinding = binding;
            var status = store.GetStatus(transferId, binding);
            if (status.State == PrtgTransferStates.Complete)
                return Ok(ApiResponse<PrtgTransferStatus>.Ok(status));
            var owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
            lease = store.AcquireValidationLease(transferId, binding, owner, ValidationLeaseDuration, DateTimeOffset.UtcNow);
            var validation = await _validator.ValidateAsync(status.ChunkCount, (ordinal, ct) =>
            {
                var currentBinding = CaptureBinding();
                if (currentBinding != binding)
                    throw new PrtgTransferStoreException("transfer_binding_changed", "驗證期間全站主機範圍或來源設定已變更。", 403);
                return Task.FromResult(store.ReadChunk(lease, ordinal, currentBinding, DateTimeOffset.UtcNow));
            }, cancellationToken).ConfigureAwait(false);

            var finalBinding = CaptureBinding();
            if (finalBinding != binding)
                throw new PrtgTransferStoreException("transfer_binding_changed", "驗證期間全站主機範圍或來源設定已變更。", 403);
            cancellationToken.ThrowIfCancellationRequested();
            var completed = store.CompleteValidation(lease, validation.PackageSha256,
                validation.ResultManifestJson, finalBinding, DateTimeOffset.UtcNow);
            return Ok(ApiResponse<PrtgTransferStatus>.Ok(completed));
        }
        catch (PrtgTransferStoreException ex)
        {
            if (lease != null && IsValidationFailure(ex.Code))
                TryFailValidation(store, lease, ex.Code);
            return StoreError(ex);
        }
        catch (InvalidDataException ex)
        {
            var code = FailureCode(ex.Message);
            if (lease != null) TryFailValidation(store, lease, code);
            return UnprocessableEntity(ApiResponse.Fail(code, SafeValidationMessage(code)));
        }
        catch (System.Text.Json.JsonException)
        {
            const string code = "json_invalid";
            if (lease != null) TryFailValidation(store, lease, code);
            return UnprocessableEntity(ApiResponse.Fail(code, "舊版診斷 JSON 格式無效或已截斷。"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, ApiResponse.Fail("validation_cancelled", "驗證已取消；片段仍保留，可重新查詢或續驗。"));
        }
        finally
        {
            // Reader 已結束才釋放 buffer reservation。未知提交時 CAS 不會退回 complete 或別人的新 lease。
            if (lease != null && initialBinding != null)
            {
                try { store.ReleaseValidationLease(lease, initialBinding, DateTimeOffset.UtcNow); }
                catch { /* 無法確認提交時保留原租約至到期，不重複授予記憶體額度。 */ }
            }
        }
    }

    private EfPrtgTransferStore Store() => new(_backend, _capacity);

    internal PrtgTransferBinding CaptureBinding()
    {
        if (_hosts == null || _visibility == null || _currentUser == null || _settings == null ||
            !_currentUser.IsAuthenticated || !_currentUser.Has(Capability.Maintain) ||
            string.IsNullOrWhiteSpace(_currentUser.Account) || (_currentUser.UserId <= 0 && !_currentUser.IsServerAdmin))
            throw new PrtgTransferStoreException("transfer_access_unverified", "無法確認登入者、主機可見範圍或 PRTG 來源，拒絕診斷傳輸。", 403);

        var snapshot = _hosts.CapturePrtgSnapshot();
        var allIds = snapshot.Hosts.Select(x => x.HostId).ToHashSet();
        var visibleIds = _currentUser.Has(Capability.ViewAll)
            ? allIds : _visibility.GetVisibleHostIds(snapshot);
        if (allIds.Count == 0 || visibleIds.Count == 0 || !allIds.SetEquals(visibleIds) ||
            allIds.Any(_visibility.IsCaseGrantOnly))
            throw new PrtgTransferStoreException("transfer_full_visibility_required", "診斷傳輸需要管理者可見全部主機；空範圍或案件授與不允許。", 403);

        var settings = _settings.Get();
        var scopeRevision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var orderedIds = allIds.Order().Select(x => x.ToString(CultureInfo.InvariantCulture));
        var scopeText = $"scope-revision:{scopeRevision}|{string.Join(",", orderedIds)}";
        var policyBlob = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey);
        var policyVersion = policyBlob.ReadVersion();
        var policy = new PrtgMonitoringPolicyStore(policyBlob).Get();
        if (policyBlob.ReadVersion() != policyVersion || _hosts.DataVersion != snapshot.Version)
            throw new PrtgTransferStoreException("transfer_context_changed", "來源或主機範圍在核對期間改變，請重試。", 409);
        var policyHosts = string.Join(",", policy.HostIds.Order().Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var policySensors = string.Join(",", policy.SensorIds.Order().Select(x => x.ToString(CultureInfo.InvariantCulture)));
        var sourceText = $"url:{settings.PrtgUrl?.Trim() ?? ""}|enabled:{settings.PrtgEnabled}|" +
                         $"policy-version:{policyVersion}|revision:{policy.Revision}|core:{policy.CoreSystemId}|" +
                         $"generation:{policy.SourceGeneration}|endpoint:{policy.EndpointHint}|" +
                         $"valid-from:{policy.ValidFrom.UtcDateTime.Ticks}|zone:{policy.SourceTimeZoneId}|culture:{policy.SourceCultureName}|" +
                         $"policy-hosts:{policyHosts}|policy-sensors:{policySensors}";
        var ownerText = $"user:{_currentUser.UserId}|server-admin:{_currentUser.IsServerAdmin}|account:{_currentUser.Account}";
        return new PrtgTransferBinding(Hash(Encoding.UTF8.GetBytes(ownerText)),
            Hash(Encoding.UTF8.GetBytes(scopeText)), Hash(Encoding.UTF8.GetBytes(sourceText)));
    }

    private void TryFailValidation(EfPrtgTransferStore store, PrtgTransferValidationLease lease, string code)
    {
        try { store.FailValidation(lease, code, CaptureBinding(), DateTimeOffset.UtcNow); }
        catch { /* 綁定改變或未知提交時保留 durable session，lease 到期後可重驗。 */ }
    }

    private static bool IsValidationFailure(string code) =>
        code is "package_hash_mismatch" or "chunk_integrity_failed" or "transfer_chunks_incomplete";

    private static string FailureCode(string message)
    {
        var separator = message.IndexOf(':');
        var candidate = separator > 0 ? message[..separator] : "package_invalid";
        return candidate.Length <= 64 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? candidate : "package_invalid";
    }

    private static string SafeValidationMessage(string code) => code switch
    {
        "purpose_rejected" => "匯入只接受 diagnostic-only 診斷包。",
        "format_version_unsupported" => "只支援 V1 或 V2 舊版診斷包。",
        "package_truncated" => "舊版診斷 JSON 格式無效或已截斷。",
        "json_row_too_large" or "json_token_too_large" => "單一診斷資料列或 JSON token 超過 4 MiB。",
        _ => "舊版診斷 JSON 未通過格式、欄位或完整性驗證。"
    };

    private IActionResult StoreError(PrtgTransferStoreException exception) =>
        StatusCode(exception.HttpStatusCode, ApiResponse.Fail(exception.Code, exception.Message));

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
}

public sealed record PrtgTransferCreateRequestDto(
    Guid TransferId,
    long DeclaredBytes,
    int ChunkCount,
    string PackageSha256);
