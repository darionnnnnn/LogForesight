using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 用戶端工廠（PRTG 第 2 輪批次A）。
/// 全專案唯一的 PRTG 憑證解密與憑證完整性檢查收斂點。
/// </summary>
public static class PrtgClientFactory
{
    /// <summary>
    /// 依 SystemSettings 建立配置完成的 PrtgClient 實例。
    /// 內部依 PrtgAuthMode 自動解密所需憑證。
    /// </summary>
    public static PrtgClient Create(SystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (token, username, password) = ResolveCredentials(settings);

        return new PrtgClient(
            baseUrl: settings.PrtgUrl ?? string.Empty,
            tokenOrEmpty: token,
            timeoutSeconds: settings.PrtgTimeoutSeconds,
            ignoreSslErrors: settings.PrtgIgnoreSslErrors,
            handler: null,
            authMode: settings.PrtgAuthMode,
            usernameOrEmpty: username,
            passwordOrEmpty: password);
    }

    /// <summary>
    /// 依認證方式取出（解密後的）憑證。internal 供測試直接斷言「解密真的發生、分支沒有接反」——
    /// 工廠的主要職責就是這一段，只斷言 Create 不擲例外是恆真測試，擋不住把 Decrypt 漏掉的回歸。
    /// </summary>
    internal static (string Token, string Username, string Password) ResolveCredentials(SystemSettings settings)
    {
        var isPasswordMode = string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Password, StringComparison.Ordinal);

        if (isPasswordMode)
        {
            var enc = settings.PrtgPasswordEnc ?? string.Empty;
            var password = CryptoHelper.IsEncrypted(enc) ? CryptoHelper.Decrypt(enc) : enc;
            return (string.Empty, settings.PrtgUsername ?? string.Empty, password);
        }

        var tokenEnc = settings.PrtgApiTokenEnc ?? string.Empty;
        var token = CryptoHelper.IsEncrypted(tokenEnc) ? CryptoHelper.Decrypt(tokenEnc) : tokenEnc;
        return (token, string.Empty, string.Empty);
    }

    /// <summary>
    /// 檢查 SystemSettings 是否已具備可發動請求的有效認證資訊。
    /// token 模式：PrtgApiTokenEnc 非空。
    /// password 模式：PrtgUsername 與 PrtgPasswordEnc 皆非空。
    /// </summary>
    public static bool HasUsableCredentials(SystemSettings settings)
    {
        if (settings == null) return false;

        if (string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Password, StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(settings.PrtgUsername) &&
                   !string.IsNullOrWhiteSpace(settings.PrtgPasswordEnc);
        }

        return !string.IsNullOrWhiteSpace(settings.PrtgApiTokenEnc);
    }
}
