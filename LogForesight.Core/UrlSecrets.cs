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
    private const string QueryParamPattern = @"(?<prefix>[?&](?<name>[^=&#?\s]{1,64})=)(?<value>[^&#\s]*)";

    private static readonly Regex QueryParam = new(QueryParamPattern, RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>逾時時 <see cref="Mask"/> 回傳的固定字串（不含輸入原文）</summary>
    internal const string TimeoutMask = "***（位址過長或格式異常，內容已整段遮蔽）***";

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>網址是否夾帶憑證：機敏查詢參數，或 <c>user:pass@</c>。比對逾時一律視為含憑證（寧可拒收）。</summary>
    public static bool ContainsSecret(string? url) => ContainsSecret(url, QueryParam);

    /// <summary>把機敏參數的值與使用者資訊換成 ***，其餘原樣。比對逾時回固定整段遮蔽字串，不回原文。</summary>
    public static string Mask(string? url) => Mask(url, QueryParam);

    /// <summary>測試用：以指定逾時重建查詢參數正規式，讓防線可在不依賴機器速度的情況下驗證</summary>
    internal static bool ContainsSecret(string? url, TimeSpan timeout) => ContainsSecret(url, NewQueryParam(timeout));

    internal static string Mask(string? url, TimeSpan timeout) => Mask(url, NewQueryParam(timeout));

    private static Regex NewQueryParam(TimeSpan timeout) => new(QueryParamPattern, RegexOptions.CultureInvariant, timeout);

    private static bool ContainsSecret(string? url, Regex queryParam)
    {
        if (string.IsNullOrEmpty(url)) return false;
        try
        {
            return HasUserInfo(url) || queryParam.Matches(url).Any(m => IsSecretName(m.Groups["name"].Value));
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warn("網址憑證偵測逾時（長度 {0}），視為含憑證", url.Length);
            return true;
        }
    }

    private static string Mask(string? url, Regex queryParam)
    {
        if (string.IsNullOrEmpty(url)) return url ?? "";
        try
        {
            var masked = queryParam.Replace(url,
                m => IsSecretName(m.Groups["name"].Value) ? m.Groups["prefix"].Value + "***" : m.Value);
            return MaskUserInfo(masked);
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warn("網址憑證遮罩逾時（長度 {0}），整段改為固定遮蔽字串", url.Length);
            return TimeoutMask;
        }
    }

    // ── scheme://使用者資訊@主機 ─────────────────────────────────────────────
    // 語意：scheme:// 之後、到第一個 / ? # 或空白之前的範圍內，取最後一個 @ 之前的部分為使用者資訊；
    // 其中有冒號才是帳密（只有帳號沒有密碼的不遮）。密碼本身含 @ 時不能只遮到第一個。
    // 以字串掃描實作而不用單一正規式：「info 內含冒號、後接 @」的巢狀量詞在 scheme:// 後接大量「a:」時
    // 會平方級回溯。這裡每個 :// 只掃一次、每段只走一遍，整體線性。
    // scheme 的判定與原正規式一致：[A-Za-z][A-Za-z0-9+.\-]* 且起點在字界上（前一字元不是 \w）。

    private static bool HasUserInfo(string text) => FindUserInfo(text, 0, out _, out _);

    private static string MaskUserInfo(string text)
    {
        if (!FindUserInfo(text, 0, out var start, out var end)) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        var copied = 0;
        do
        {
            sb.Append(text, copied, start - copied).Append("***");
            copied = end;
        }
        while (FindUserInfo(text, end + 1, out start, out end));
        return sb.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// 自 <paramref name="from"/> 起找下一段使用者資訊，回傳其範圍 [infoStart, infoEnd)（infoEnd 即 @ 的位置）。
    /// 下一次搜尋應從 @ 之後開始（與正規式逐段不重疊比對相同）。
    /// </summary>
    private static bool FindUserInfo(string text, int from, out int infoStart, out int infoEnd)
    {
        infoStart = infoEnd = 0;
        var search = from;
        while (search < text.Length)
        {
            var sep = text.IndexOf("://", search, StringComparison.Ordinal);
            if (sep < 0) return false;
            search = sep + 3;
            if (!HasSchemeStart(text, sep, from)) continue;

            var i = sep + 3;
            var lastAt = -1;
            var colonBeforeLastAt = false;
            var sawColon = false;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (c is '/' or '?' or '#' || char.IsWhiteSpace(c)) break;
                if (c == ':') sawColon = true;
                else if (c == '@')
                {
                    lastAt = i;
                    colonBeforeLastAt = sawColon;
                }
            }
            if (lastAt >= 0 && colonBeforeLastAt)
            {
                infoStart = sep + 3;
                infoEnd = lastAt;
                return true;
            }
            // 掃過的這段沒有帳密：從段尾往回一個字元續找——段尾若是「:」後接「//」，那個冒號正是下一個 scheme 的分隔
            // （a://b://user:pw@host），直接跳到 i 會漏掉它
            search = Math.Max(search, i - 1);
        }
        return false;
    }

    /// <summary>
    /// <paramref name="sep"/>（「://」的位置）之前是否有合法的 scheme 起點：落在 [<paramref name="floor"/>, sep) 內、
    /// 全部是 scheme 字元，起點是 ASCII 字母且前一字元不是 \w。
    /// </summary>
    private static bool HasSchemeStart(string text, int sep, int floor)
    {
        var runStart = sep;
        while (runStart > floor && IsSchemeChar(text[runStart - 1])) runStart--;
        for (var s = runStart; s < sep; s++)
        {
            if (!IsAsciiLetter(text[s])) continue;
            if (s == 0 || !IsWordChar(text[s - 1])) return true;
        }
        return false;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsSchemeChar(char c) => IsAsciiLetter(c) || c is >= '0' and <= '9' or '+' or '.' or '-';

    /// <summary>與 Regex 的 \w 相同：字母、數字、組合記號、連接標點（含底線）</summary>
    private static bool IsWordChar(char c) => char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.UppercaseLetter or System.Globalization.UnicodeCategory.LowercaseLetter
        or System.Globalization.UnicodeCategory.TitlecaseLetter or System.Globalization.UnicodeCategory.ModifierLetter
        or System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.NonSpacingMark
        or System.Globalization.UnicodeCategory.DecimalDigitNumber or System.Globalization.UnicodeCategory.ConnectorPunctuation;

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
