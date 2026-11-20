using System.Text.RegularExpressions;

namespace LogForesight.Core;

/// <summary>
/// 網址中的憑證偵測與遮罩：查詢字串的機敏參數，以及 <c>user:pass@</c> 形式的使用者資訊。
///
/// 為什麼要有：位址欄位（PRTG／Sentinel／AI）會被寫進稽核、例外訊息與回應，
/// 使用者若把 passhash／token 直接貼在網址後面，這些值就會沿著「顯示位址」的路徑外流。
/// 儲存時以 <see cref="ContainsSecret"/> 拒收，對外顯示時一律經 <see cref="Mask"/>。
///
/// <see cref="Mask"/> 也被執行紀錄去識別化拿來掃整段自由文字（網址夾在句子中間），
/// 所以以「在文字中找參數」的方式比對，不要求整段是一個可解析的網址；參數名先做百分比解碼再比對名單，
/// <c>pass%68ash=</c> 這種寫法不會漏掉。
///
/// 機敏參數名只有 <see cref="SecretNames"/> 一份，執行紀錄去識別化的「名稱＝值」樣式也由它組成。
///
/// 與 <c>PrtgClient.StripSecrets</c> 的差別：那邊是「已知憑證值」的替換（例外訊息裡可能以任何形式出現），
/// 這裡是「依參數名」遮值，不需要知道憑證內容——兩者語意不同，各自保留。
/// </summary>
public static class UrlSecrets
{
    /// <summary>機敏參數名（比對不分大小寫）</summary>
    public static readonly IReadOnlyList<string> SecretNames =
    [
        "password", "passwd", "pwd", "passphrase", "passhash", "apitoken", "api_key", "api-key", "apikey",
        "access_token", "refresh_token", "client_secret", "token", "secret"
    ];

    private static readonly System.Collections.Frozen.FrozenSet<string> SecretNameSet =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(SecretNames, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // 參數名必須位於 ? 或 & 之後、= 之前，避免把路徑或值中的片段誤判成參數名
    private static readonly Regex QueryParam = new(
        @"(?<prefix>[?&](?<name>[^=&#?\s]{1,64})=)(?<value>[^&#\s]*)",
        RegexOptions.CultureInvariant, RegexTimeout);

    // scheme://使用者資訊@主機：使用者資訊裡有冒號才是帳密（只有帳號沒有密碼的不遮）。
    // 使用者資訊取到主機段（第一個 / ? # 之前）的最後一個 @：密碼本身含 @ 時不能只遮到第一個
    private static readonly Regex UserInfo = new(
        @"(?<prefix>\b[A-Za-z][A-Za-z0-9+.\-]*://)(?<info>[^/?#\s]*:[^/?#\s]*)@",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>網址是否夾帶憑證：機敏查詢參數，或 <c>user:pass@</c></summary>
    public static bool ContainsSecret(string? url)
        => !string.IsNullOrEmpty(url) &&
           (UserInfo.IsMatch(url) || QueryParam.Matches(url).Any(m => IsSecretName(m.Groups["name"].Value)));

    /// <summary>把機敏參數的值與使用者資訊換成 ***，其餘原樣</summary>
    public static string Mask(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url ?? "";
        var masked = QueryParam.Replace(url,
            m => IsSecretName(m.Groups["name"].Value) ? m.Groups["prefix"].Value + "***" : m.Value);
        return UserInfo.Replace(masked, m => m.Groups["prefix"].Value + "***@");
    }

    /// <summary>
    /// 給「位址格式不正確」這類訊息回顯使用者填的位址：先 <see cref="Mask"/>，仍含 <c>@</c> 時
    /// （沒寫 scheme 的 <c>user:pass@host</c>，<see cref="Mask"/> 認不得）把最後一個 @ 之前整段換成 ***。
    /// 只用在整段就是一個位址欄位值的地方；自由文字不適用（信箱會被遮掉）。
    /// </summary>
    public static string MaskAddressInput(string? address)
    {
        var masked = Mask(address);
        var at = masked.LastIndexOf('@');
        if (at < 0 || masked.Contains("***@", StringComparison.Ordinal)) return masked;
        return string.Concat("***", masked.AsSpan(at));
    }

    private static bool IsSecretName(string rawName)
    {
        string name;
        try { name = Uri.UnescapeDataString(rawName.Replace('+', ' ')); }
        catch (UriFormatException) { name = rawName; }
        return SecretNameSet.Contains(name.Trim());
    }
}
