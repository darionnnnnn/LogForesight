using LogForesight.Core.Models;
using State = LogForesight.Core.Models.CredentialState;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 用戶端工廠。
/// 全專案唯一的 PRTG 憑證解密與憑證完整性檢查收斂點。
/// </summary>
public static class PrtgClientFactory
{
    /// <summary>
    /// 依 SystemSettings 建立配置完成的 PrtgClient 實例。
    /// 內部依 PrtgAuthMode 自動解密所需憑證。
    /// </summary>
    public static PrtgClient Create(SystemSettings settings, HttpMessageHandler? handler = null,
        PrtgRequestBudget? budget = null, TimeProvider? timeProvider = null) =>
        CreateCore(settings, handler, budget, timeProvider, null);

    private static PrtgClient CreateCore(SystemSettings settings, HttpMessageHandler? handler,
        PrtgRequestBudget? budget, TimeProvider? timeProvider, int? timeoutSecondsOverride)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var (token, username, password, passhash) = ResolveCredentials(settings);

        return new PrtgClient(
            baseUrl: settings.PrtgUrl ?? string.Empty,
            tokenOrEmpty: token,
            timeoutSeconds: timeoutSecondsOverride ?? settings.PrtgTimeoutSeconds,
            ignoreSslErrors: settings.PrtgIgnoreSslErrors,
            handler: handler,
            authMode: settings.PrtgAuthMode,
            usernameOrEmpty: username,
            passwordOrEmpty: password,
            passhashOrEmpty: passhash,
            budget: budget ?? PrtgRequestBudget.Shared,
            timeProvider: timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// 環境探測用的 client：單次請求逾時不超過 <see cref="PrtgProbeRunner.RequestTimeout"/>，
    /// 一個卡住的請求不會吃掉整輪探測的時間上限。其他 PRTG client 沿用設定的逾時。
    /// </summary>
    internal static PrtgClient CreateForEnvironmentProbe(SystemSettings settings,
        HttpMessageHandler? handler = null, PrtgRequestBudget? budget = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuredTimeout = Math.Max(settings.PrtgTimeoutSeconds, 1);
        var probeTimeout = Math.Min(configuredTimeout, (int)PrtgProbeRunner.RequestTimeout.TotalSeconds);
        return CreateCore(settings, handler, budget, timeProvider, probeTimeout);
    }

    /// <summary>
    /// 依認證方式取出（解密後的）憑證。internal 供測試直接斷言「解密真的發生、分支沒有接反」——
    /// 工廠的主要職責就是這一段，只斷言 Create 不擲例外是恆真測試，擋不住把 Decrypt 漏掉的回歸。
    /// </summary>
    internal static (string Token, string Username, string Password, string Passhash) ResolveCredentials(SystemSettings settings)
    {
        if (string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Passhash, StringComparison.Ordinal))
        {
            var enc = settings.PrtgPasshashEnc ?? string.Empty;
            var passhash = CryptoHelper.TryDecrypt(enc, out var plainPasshash) ? plainPasshash : string.Empty;
            return (string.Empty, settings.PrtgUsername ?? string.Empty, string.Empty, passhash);
        }

        if (string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Password, StringComparison.Ordinal))
        {
            var enc = settings.PrtgPasswordEnc ?? string.Empty;
            var password = CryptoHelper.TryDecrypt(enc, out var plainPassword) ? plainPassword : string.Empty;
            return (string.Empty, settings.PrtgUsername ?? string.Empty, password, string.Empty);
        }

        var tokenEnc = settings.PrtgApiTokenEnc ?? string.Empty;
        // TryDecrypt：明文原樣回傳；解不開（金鑰不符）當成未設定，不讓取數整趟失敗
        var token = CryptoHelper.TryDecrypt(tokenEnc, out var plainToken) ? plainToken : string.Empty;
        return (token, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>
    /// 檢查 SystemSettings 是否已具備可發動請求的有效認證資訊，等同 <see cref="CredentialState"/> 為 Ok。
    /// token 模式：PrtgApiTokenEnc 非空且解得開。
    /// password 模式：PrtgUsername 非空，且 PrtgPasswordEnc 非空且解得開。
    /// passhash 模式：PrtgUsername 非空，且 PrtgPasshashEnc 非空且解得開。
    /// 解不開（金鑰不符）時回 false——否則各取數路徑會拿空憑證去打 PRTG，而不是在閘門處就停下。
    /// 要分辨「沒填」和「解不開」請讀 <see cref="CredentialState"/>。
    /// </summary>
    public static bool HasUsableCredentials(SystemSettings settings) => CredentialState(settings) == State.Ok;

    /// <summary>
    /// 憑證不可用時給人看的原因（可用時 null）。回填、探測、結構同步三個手動入口共用這一句，
    /// 不再各自寫「尚未設定」而把「解不開」也說成沒設定——那會讓人回頭去檢查根本沒問題的設定欄。
    /// </summary>
    public static string? CredentialProblem(SystemSettings settings) => CredentialState(settings) switch
    {
        State.Undecryptable => "PRTG 認證資訊無法解密（金鑰檔與資料庫不相符），請到 PRTG 維護頁「連線」重新輸入並儲存",
        State.NotConfigured => "尚未設定 PRTG 認證資訊（API token 或帳號密碼）",
        _ => null
    };

    /// <summary>
    /// 目前認證模式的憑證狀態——PRTG 憑證可用性的唯一判定出口。
    /// 只看目前認證模式用到的欄位；其他模式殘留的舊密文不影響結果。
    /// 密文欄有值但解不開＝<see cref="State.Undecryptable"/>（需重新輸入）；欄位空白或缺帳號＝<see cref="State.NotConfigured"/>。
    /// </summary>
    public static State CredentialState(SystemSettings settings)
    {
        if (settings == null) return State.NotConfigured;

        if (string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Passhash, StringComparison.Ordinal))
            return SecretState(settings.PrtgPasshashEnc, requireUsername: true, settings.PrtgUsername);

        if (string.Equals(settings.PrtgAuthMode, PrtgAuthModes.Password, StringComparison.Ordinal))
            return SecretState(settings.PrtgPasswordEnc, requireUsername: true, settings.PrtgUsername);

        return SecretState(settings.PrtgApiTokenEnc, requireUsername: false, null);
    }

    /// <summary>
    /// 密文欄的狀態：空白＝未設定；解不開＝Undecryptable（先於帳號判斷，使用者重填時才會一併補帳號）；
    /// 明文原樣視為可用。
    /// </summary>
    private static State SecretState(string? value, bool requireUsername, string? username)
    {
        if (string.IsNullOrWhiteSpace(value)) return State.NotConfigured;
        if (!CryptoHelper.TryDecrypt(value, out var plain)) return State.Undecryptable;
        if (string.IsNullOrWhiteSpace(plain)) return State.NotConfigured;
        if (requireUsername && string.IsNullOrWhiteSpace(username)) return State.NotConfigured;
        return State.Ok;
    }
}
