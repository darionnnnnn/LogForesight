namespace LogForesight.Web.Models;

/// <summary>
/// 「哪些例外的訊息可以給使用者看」的單一規則（docs/WEB-SPEC.md §7.2）。
///
/// 只有領域例外——<see cref="DomainException"/> 與 <see cref="ApiExceptionFilter"/> 已登錄的專屬例外型別——
/// 的訊息是寫給使用者看的中文；其他例外（IO、網路、JSON、SQL、HttpClient……）的原文可能含檔案路徑、
/// 網址、連線字串，只進記錄檔，畫面與 API 回應給寫好的中文說明。
///
/// 呼叫端自己負責把原例外寫進 NLog（<c>Log.Warn(ex, ...)</c>）；這裡不寫記錄，避免同一個例外被記兩次。
/// </summary>
public static class UserFacingMessage
{
    /// <summary>已登錄、訊息可原樣給使用者的專屬例外型別（與 <c>ApiExceptionFilter</c> 的對照一致）。</summary>
    private static readonly Type[] RegisteredTypes =
    {
        typeof(DomainException),
        typeof(LogForesight.Core.CryptoKeyUnavailableException),
        typeof(LogForesight.Core.Persistence.ConcurrentUpdateException),
        typeof(LogForesight.Core.Persistence.PrtgScopePurgeBusyException),
        typeof(LogForesight.Core.Persistence.WorkOrderRuleException),
        typeof(LogForesight.Core.Persistence.Sql.PrtgTransferStoreException),
        typeof(LogForesight.Core.Persistence.Sql.PrtgScopeConflictException),
        typeof(LogForesight.Web.Services.RunExportDeidentificationException),
        typeof(LogForesight.Web.Services.NetiqDiscoveryException),
    };

    /// <summary>此例外的訊息是否可以直接給使用者。</summary>
    public static bool IsUserFacing(Exception ex)
    {
        var type = ex.GetType();
        foreach (var registered in RegisteredTypes)
            if (registered.IsAssignableFrom(type)) return true;
        return false;
    }

    /// <summary>領域例外回原訊息，其餘回 <paramref name="fallback"/>（寫好的中文說明）。</summary>
    public static string For(Exception ex, string fallback)
        => IsUserFacing(ex) && !string.IsNullOrWhiteSpace(ex.Message) ? ex.Message : fallback;
}
