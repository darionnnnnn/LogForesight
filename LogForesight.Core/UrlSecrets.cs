using System.Text.RegularExpressions;

namespace LogForesight.Core;

/// <summary>
/// 網址查詢字串中的機敏參數偵測與遮罩。
///
/// 為什麼要有：位址欄位（PRTG／Sentinel／AI）會被寫進稽核、例外訊息與回應，
/// 使用者若把 passhash／token 直接貼在網址後面，這些值就會沿著「顯示位址」的路徑外流。
/// 儲存時以 <see cref="ContainsSecretQuery"/> 拒收，對外顯示時一律經 <see cref="Mask"/>。
///
/// 與 <c>PrtgClient.StripSecrets</c> 的差別：那邊是「已知憑證值」的替換（例外訊息裡可能以任何形式出現），
/// 這裡是「依參數名」遮值，不需要知道憑證內容——兩者語意不同，各自保留。
/// </summary>
public static class UrlSecrets
{
    private static readonly string[] SecretNames =
        { "passhash", "password", "apitoken", "api_key", "apikey", "token", "secret" };

    // 參數名必須位於 ? 或 & 之後、= 之前，避免把路徑或值中的片段誤判成參數名
    private static readonly Regex SecretParam = new(
        @"(?<prefix>[?&](?:" + string.Join("|", SecretNames.Select(Regex.Escape)) + @")=)(?<value>[^&#]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>查詢字串是否含機敏參數（參數名不分大小寫）</summary>
    public static bool ContainsSecretQuery(string? url)
        => !string.IsNullOrEmpty(url) && SecretParam.IsMatch(url);

    /// <summary>把機敏參數的值換成 ***，其餘原樣</summary>
    public static string Mask(string? url)
        => string.IsNullOrEmpty(url) ? url ?? "" : SecretParam.Replace(url, m => m.Groups["prefix"].Value + "***");
}
