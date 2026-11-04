using LogForesight.Core.Configuration;
using LogForesight.Core.Models;

namespace LogForesight.Core.Service;

/// <summary>
/// 一筆密鑰清單項目：名稱、密文，以及「這一趟取數用不用得到」。
/// </summary>
/// <param name="Name">中文名稱（AI 金鑰、SMTP 密碼、PRTG API token、PRTG 密碼、PRTG passhash、Sentinel 密碼）。不含任何值</param>
/// <param name="Ciphertext">儲存的密文（可能是空字串＝沒填，也可能是舊資料的明文）</param>
/// <param name="NeededThisRun">這一趟取數會用到（啟用中且有主機的 Sentinel、已啟用 PRTG 目前認證模式的憑證、已設定的 AI）</param>
/// <param name="Subject">同名項目的區分（Sentinel 的名稱）；其他項目為 null</param>
/// <param name="Retired">
/// PRTG 目前認證模式以外的殘留（例如改用 passhash 後留下的舊密碼）。系統用不到它，畫面上也沒有欄位能重新輸入或清除，
/// 所以它解不開不能算「憑證有問題」，否則金鑰警示永遠解除不了；切回該模式時 <c>PrtgClientFactory.CredentialState</c> 會照實回報。
/// </param>
public sealed record SecretEntry(string Name, string? Ciphertext, bool NeededThisRun, string? Subject = null, bool Retired = false)
{
    /// <summary>給人看的名稱：有區分對象時附上（例如「Sentinel 密碼「SENT-A」」）。不含任何值</summary>
    public string DisplayName => string.IsNullOrEmpty(Subject) ? Name : $"{Name}「{Subject}」";
}

/// <summary>
/// 站內所有密文欄位的唯一清單來源：診斷匯出的去識別化字典、啟動時的解密探測、取數趟的前置檢查共用同一份，
/// 新增密文欄位只改這裡，三處不會各漏一個。
/// 清單只有名稱與密文，不解密、不記錄；解密判定由呼叫端決定。
/// </summary>
public static class SecretInventory
{
    public const string AiKeyName = "AI 金鑰";
    public const string SmtpPasswordName = "SMTP 密碼";
    public const string PrtgTokenName = "PRTG API token";
    public const string PrtgPasswordName = "PRTG 密碼";
    public const string PrtgPasshashName = "PRTG passhash";
    public const string SentinelPasswordName = "Sentinel 密碼";

    /// <summary>
    /// 建立清單，順序固定：AI、SMTP、PRTG token、PRTG 密碼、PRTG passhash、各 Sentinel。
    /// </summary>
    /// <param name="settings">系統設定</param>
    /// <param name="sentinels">所有 Sentinel</param>
    /// <param name="sentinelNamesWithHosts">
    /// 這趟有主機要查的 Sentinel 名稱（<c>HostListSelection</c> 的分組鍵）；null＝不另外篩，啟用中的 Sentinel 都算用得到。
    /// </param>
    public static IReadOnlyList<SecretEntry> Build(
        SystemSettings settings, IEnumerable<Sentinel> sentinels, IReadOnlySet<string>? sentinelNamesWithHosts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sentinels);

        // 與 PrtgClientFactory 的認證模式分支一致：不是 password／passhash 就是 token
        var prtgMode = settings.PrtgAuthMode;
        var passhashMode = string.Equals(prtgMode, PrtgAuthModes.Passhash, StringComparison.Ordinal);
        var passwordMode = string.Equals(prtgMode, PrtgAuthModes.Password, StringComparison.Ordinal);
        var tokenMode = !passhashMode && !passwordMode;

        // AI「已設定」沿用唯一定義；用密文欄判定，解不開的金鑰不能讓 OpenAI 官方端點被誤判成沒設定而漏檢
        var aiConfigured = AiProviders.IsConfigured(
            settings.AiProvider, settings.AiBaseUrl, settings.AiApiKeyEnc, settings.AiAzureDeployment);

        var list = new List<SecretEntry>
        {
            new(AiKeyName, settings.AiApiKeyEnc, aiConfigured),
            // SMTP 只在寄信時用，不屬於取數趟
            new(SmtpPasswordName, settings.SmtpPasswordEnc, false),
            new(PrtgTokenName, settings.PrtgApiTokenEnc, settings.PrtgEnabled && tokenMode, Retired: !tokenMode),
            new(PrtgPasswordName, settings.PrtgPasswordEnc, settings.PrtgEnabled && passwordMode, Retired: !passwordMode),
            new(PrtgPasshashName, settings.PrtgPasshashEnc, settings.PrtgEnabled && passhashMode, Retired: !passhashMode)
        };

        foreach (var sentinel in sentinels)
        {
            var needed = sentinel.Active &&
                         (sentinelNamesWithHosts == null || sentinelNamesWithHosts.Contains(sentinel.Name));
            list.Add(new SecretEntry(SentinelPasswordName, sentinel.PasswordEnc, needed, sentinel.Name));
        }

        return list;
    }
}
