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

        var isPasswordMode = string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Password, StringComparison.Ordinal);

        string token = string.Empty;
        string username = string.Empty;
        string password = string.Empty;

        if (isPasswordMode)
        {
            username = settings.PrtgUsername ?? string.Empty;
            var enc = settings.PrtgPasswordEnc ?? string.Empty;
            password = CryptoHelper.IsEncrypted(enc) ? CryptoHelper.Decrypt(enc) : enc;
        }
        else
        {
            var enc = settings.PrtgApiTokenEnc ?? string.Empty;
            token = CryptoHelper.IsEncrypted(enc) ? CryptoHelper.Decrypt(enc) : enc;
        }

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
