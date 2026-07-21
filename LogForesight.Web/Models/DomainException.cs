namespace LogForesight.Web.Models;

/// <summary>
/// 業務規則錯誤（docs/WEB-SPEC.md §7.2）。Service 層拋出，由 ApiExceptionFilter
/// 統一轉成 4xx 信封——Controller 與 Service 因此都不需要寫 try-catch 樣板，
/// 錯誤處理只有一個地方，不會出現「這個端點忘了包裝錯誤」的漏洞。
///
/// <see cref="Message"/> 會直接顯示給使用者，所以要寫成看得懂的中文，
/// 不是給開發者看的技術訊息。
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    public static DomainException Validation(string message) => new(ApiErrorCodes.ValidationFailed, message);

    public static DomainException NotFound(string message) => new(ApiErrorCodes.NotFound, message);

    public static DomainException Conflict(string message) => new(ApiErrorCodes.Conflict, message);

    public static DomainException Forbidden(string message) => new(ApiErrorCodes.Forbidden, message);
}
