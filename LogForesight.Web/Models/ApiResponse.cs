namespace LogForesight.Web.Models;

/// <summary>
/// 全站統一的 API 回應信封（docs/WEB-SPEC.md §7.2）。
/// 所有 API 一律回這個形狀，前端的解析與錯誤處理因此只需要寫一次（core/api.js）。
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public ApiError? Error { get; init; }

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };

    public static ApiResponse<T> Fail(string code, string message) =>
        new() { Success = false, Error = new ApiError { Code = code, Message = message } };
}

/// <summary>無資料回傳時使用（例如純粹的狀態更新）</summary>
public class ApiResponse : ApiResponse<object>
{
    public static ApiResponse Ok() => new() { Success = true };
}

public class ApiError
{
    /// <summary>固定的小寫 snake_case 錯誤碼，見 <see cref="ApiErrorCodes"/></summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// **可直接顯示給使用者的繁體中文**。前端不做錯誤碼→文案的對照表：
    /// 那張表會跟後端的錯誤情境慢慢脫節，而且維護兩份等於維護兩個事實來源。
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

public static class ApiErrorCodes
{
    public const string AuthExpired = "auth_expired";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string ValidationFailed = "validation_failed";
    public const string Conflict = "conflict";
    public const string ServerError = "server_error";
    public const string UpstreamTimeout = "upstream_timeout";
    public const string RateLimited = "rate_limited";
    public const string ServiceUnavailable = "service_unavailable";
    public const string AuthFailed = "auth_failed";
    public const string CaptureBusy = "capture_busy";

    // 專屬流程（PRTG 目錄／診斷傳輸／校準／排程預覽）的領域碼：前端不依碼分支，message 才是事實來源。
    // 傳輸儲存層（Core）自帶狀態碼，這裡登錄的狀態碼與其拋出點一致，供對帳與守門
    public const string ScopeLimitExceeded = "scope_limit_exceeded";
    public const string CatalogueChanged = "catalogue_changed";
    public const string EstimateRequired = "estimate_required";
    public const string SelectionChanged = "selection_changed";
    public const string InvalidCursor = "invalid_cursor";
    public const string InvalidToken = "invalid_token";
    public const string InvalidSelection = "invalid_selection";
    public const string UnsupportedCapacity = "unsupported_capacity";
    public const string LegacyImportRetired = "legacy_import_retired";
    public const string RequestCancelled = "request_cancelled";
    public const string ValidationCancelled = "validation_cancelled";
    public const string ChunkBodyTruncated = "chunk_body_truncated";
    public const string JsonInvalid = "json_invalid";
    public const string InvalidReason = "invalid_reason";
    public const string NetiqAttemptLedgerInvalid = "netiq_attempt_ledger_invalid";
    public const string CapacityExceeded = "capacity_exceeded";
    public const string ExportFailed = "export_failed";
    public const string PackageInvalid = "package_invalid";
    public const string TransferNotFound = "transfer_not_found";
    public const string CapacityUnverified = "capacity_unverified";
    public const string TransferOwnerMismatch = "transfer_owner_mismatch";
    public const string TransferBindingMismatch = "transfer_binding_mismatch";
    public const string TransferExpired = "transfer_expired";
    public const string TransferBindingChanged = "transfer_binding_changed";
    public const string TransferAccessUnverified = "transfer_access_unverified";
    public const string TransferFullVisibilityRequired = "transfer_full_visibility_required";
    public const string TransferContextChanged = "transfer_context_changed";
    public const string ExportSnapshotUnavailable = "export_snapshot_unavailable";
    public const string ExportProviderUnsupported = "export_provider_unsupported";
    public const string ExportLeaseInvalid = "export_lease_invalid";
    public const string ChunkSizeInvalid = "chunk_size_invalid";
    public const string ChunkShapeInvalid = "chunk_shape_invalid";
    public const string LeaseInvalid = "lease_invalid";
    public const string ManifestInvalid = "manifest_invalid";
    public const string FailureCodeInvalid = "failure_code_invalid";
    public const string CleanupRangeInvalid = "cleanup_range_invalid";
    public const string TransferIdInvalid = "transfer_id_invalid";
    public const string TransferSizeInvalid = "transfer_size_invalid";
    public const string ChunkCountInvalid = "chunk_count_invalid";
    public const string TransferOwnerInvalid = "transfer_owner_invalid";
    public const string Sha256Invalid = "sha256_invalid";
    public const string TransferIdConflict = "transfer_id_conflict";
    public const string ExportLeaseConflict = "export_lease_conflict";
    public const string TransferNotReceiving = "transfer_not_receiving";
    public const string ChunkContentConflict = "chunk_content_conflict";
    public const string ChunkWriteInProgress = "chunk_write_in_progress";
    public const string TransferBoundsExceeded = "transfer_bounds_exceeded";
    public const string TransferAlreadyComplete = "transfer_already_complete";
    public const string TransferNotValidatable = "transfer_not_validatable";
    public const string ValidationLeaseHeld = "validation_lease_held";
    public const string TransferIncomplete = "transfer_incomplete";
    public const string TransferChunksIncomplete = "transfer_chunks_incomplete";
    public const string ValidationLeaseStale = "validation_lease_stale";
    public const string ChunkIntegrityFailed = "chunk_integrity_failed";
    public const string PackageHashConflict = "package_hash_conflict";
    public const string PackageHashMismatch = "package_hash_mismatch";
    public const string TransferLeaseActive = "transfer_lease_active";
    public const string TransferRetentionNotElapsed = "transfer_retention_not_elapsed";
    public const string CleanupCursorConflict = "cleanup_cursor_conflict";
    public const string TransferVersionConflict = "transfer_version_conflict";
    public const string ChunkWriteLeaseStale = "chunk_write_lease_stale";

    /// <summary>錯誤碼 → HTTP 狀態碼的唯一登錄處；新增錯誤碼時只改這裡</summary>
    public static readonly IReadOnlyDictionary<string, int> Table = new Dictionary<string, int>
    {
        [NotFound] = 404,
        [Forbidden] = 403,
        [Conflict] = 409,
        [AuthExpired] = 401,
        [ServerError] = 500,
        [UpstreamTimeout] = 503,
        [RateLimited] = 429,
        [ServiceUnavailable] = 503,
        [AuthFailed] = 401,
        [CaptureBusy] = 503,
        [ScopeLimitExceeded] = 409,
        [CatalogueChanged] = 409,
        [EstimateRequired] = 409,
        [SelectionChanged] = 409,
        [InvalidCursor] = 400,
        [InvalidToken] = 400,
        [InvalidSelection] = 400,
        [UnsupportedCapacity] = 400,
        [LegacyImportRetired] = 410,
        [RequestCancelled] = 499,
        [ValidationCancelled] = 499,
        [ChunkBodyTruncated] = 400,
        [JsonInvalid] = 422,
        [InvalidReason] = 400,
        [NetiqAttemptLedgerInvalid] = 200,
        [CapacityExceeded] = 413,
        [ExportFailed] = 422,
        [PackageInvalid] = 422,
        [TransferNotFound] = 404,
        [CapacityUnverified] = 503,
        [TransferOwnerMismatch] = 403,
        [TransferBindingMismatch] = 403,
        [TransferExpired] = 410,
        [TransferBindingChanged] = 403,
        [TransferAccessUnverified] = 403,
        [TransferFullVisibilityRequired] = 403,
        [TransferContextChanged] = 409,
        [ExportSnapshotUnavailable] = 409,
        [ExportProviderUnsupported] = 409,
        [ExportLeaseInvalid] = 400,
        [ChunkSizeInvalid] = 400,
        [ChunkShapeInvalid] = 400,
        [LeaseInvalid] = 400,
        [ManifestInvalid] = 400,
        [FailureCodeInvalid] = 400,
        [CleanupRangeInvalid] = 400,
        [TransferIdInvalid] = 400,
        [TransferSizeInvalid] = 400,
        [ChunkCountInvalid] = 400,
        [TransferOwnerInvalid] = 400,
        [Sha256Invalid] = 400,
        [TransferIdConflict] = 409,
        [ExportLeaseConflict] = 409,
        [TransferNotReceiving] = 409,
        [ChunkContentConflict] = 409,
        [ChunkWriteInProgress] = 409,
        [TransferBoundsExceeded] = 409,
        [TransferAlreadyComplete] = 409,
        [TransferNotValidatable] = 409,
        [ValidationLeaseHeld] = 409,
        [TransferIncomplete] = 409,
        [TransferChunksIncomplete] = 409,
        [ValidationLeaseStale] = 409,
        [ChunkIntegrityFailed] = 409,
        [PackageHashConflict] = 409,
        [PackageHashMismatch] = 409,
        [TransferLeaseActive] = 409,
        [TransferRetentionNotElapsed] = 409,
        [CleanupCursorConflict] = 409,
        [TransferVersionConflict] = 409,
        [ChunkWriteLeaseStale] = 409
    };

    /// <summary>取錯誤碼對應的狀態碼；表外（含 validation_failed）一律 400</summary>
    public static int StatusOf(string code) => Table.TryGetValue(code, out var status) ? status : 400;
}
