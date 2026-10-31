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

    /// <summary>錯誤碼 → HTTP 狀態碼的唯一登錄處；新增錯誤碼時只改這裡</summary>
    public static readonly IReadOnlyDictionary<string, int> Table = new Dictionary<string, int>
    {
        [NotFound] = 404,
        [Forbidden] = 403,
        [Conflict] = 409,
        [AuthExpired] = 401,
        [ServerError] = 500,
        [UpstreamTimeout] = 503
    };

    /// <summary>取錯誤碼對應的狀態碼；表外（含 validation_failed）一律 400</summary>
    public static int StatusOf(string code) => Table.TryGetValue(code, out var status) ? status : 400;
}
