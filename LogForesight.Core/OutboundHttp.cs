using System.Net.Security;

namespace LogForesight.Core;

/// <summary>
/// 對外連線（PRTG、Sentinel、AI）共用的 HTTP 處理常式設定，三個用戶端各自一份時連線逾時與轉址行為已經分岔：
/// - 連線逾時：連不上的位址在這段時間內失敗，不必等整個請求逾時再乘上重試次數；
/// - 不跟隨轉址：請求帶認證（token、帳密、API key），轉址到別處就是把認證送給對方；
/// - 憑證驗證：呼叫端傳入（null＝系統預設驗證）。
/// </summary>
public static class OutboundHttp
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static SocketsHttpHandler CreateHandler(TimeSpan pooledConnectionLifetime,
        RemoteCertificateValidationCallback? certificateValidation = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledConnectionLifetime,
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = false
        };
        if (certificateValidation != null)
            handler.SslOptions.RemoteCertificateValidationCallback = certificateValidation;
        return handler;
    }
}
