namespace LogForesight.Core;

/// <summary>
/// 回應超過 <c>HttpClient.MaxResponseContentBufferSize</c> 的判定單一出口（PRTG、Sentinel、AI 共用）。
/// 以框架提供的錯誤分類判斷：超量回 <see cref="HttpRequestError.ConfigurationLimitExceeded"/>，
/// 連線被拒是 <see cref="HttpRequestError.ConnectionError"/>、逾時是 TaskCanceledException，都不會落進這裡。
/// 不比對訊息字串：訊息本地化或框架改版不會讓判定靜默失效。超量重試也不會變，呼叫端不得重試。
/// </summary>
internal static class HttpResponseLimit
{
    internal static bool IsExceeded(Exception ex) =>
        ex is HttpRequestException { HttpRequestError: HttpRequestError.ConfigurationLimitExceeded };

    internal static string MegabytesText(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
