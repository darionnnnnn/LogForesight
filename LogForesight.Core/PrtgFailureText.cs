using System.Text.RegularExpressions;

namespace LogForesight;

/// <summary>
/// PRTG 失敗給使用者看的中文說明——全站「失敗種類 → 說明文字」只有這一份對照。
/// 說明在前、底層原文附在後面（截斷到 <see cref="MaxOriginalLength"/>）；原文是英文例外訊息或 PRTG 回的錯誤文字，
/// 呼叫端要先自行遮蔽憑證。使用者按停止（OCE 且 ct 已取消）不經過這裡，維持取消語意。
/// </summary>
public static class PrtgFailureText
{
    /// <summary>附在說明後面的原文長度上限（字元）。</summary>
    public const int MaxOriginalLength = 500;

    public const string Timeout = "等候 PRTG 回應逾時，請稍後再試，或確認 PRTG 伺服器負載與逾時設定。";
    public const string Unreachable = "連不上 PRTG 伺服器，請確認連線位址、連接埠與網路。";
    public const string CredentialToken = "PRTG 認證失敗：API token 無效或權限不足。";
    public const string CredentialAccount = "PRTG 認證失敗：帳號或密碼錯誤，或帳號憑證無效、權限不足。";
    public const string ServerGaveUp =
        "PRTG 回應 OK 但沒有回資料：PRTG 伺服器放棄處理這個查詢（忙碌、處理逾時或核心服務異常），不是認證錯誤。請稍後再試；持續發生請檢查 PRTG 核心伺服器的負載與狀態。";
    public const string BadRequest = "PRTG 拒絕了這個查詢（HTTP 400）";
    public const string InitFailed = "無法建立 PRTG 連線，請檢查連線位址與認證設定。";

    /// <summary>傳輸層失敗的說明：逾時與連不上各一種。</summary>
    public static string ForTransport(PrtgFailureKind kind) => kind == PrtgFailureKind.Timeout ? Timeout : Unreachable;

    /// <summary>憑證失敗的說明：帳號類與 token 各一種。</summary>
    public static string ForCredential(bool usesUsernameAuth) => usesUsernameAuth ? CredentialAccount : CredentialToken;

    /// <summary>HTTP 狀態碼的說明：400 帶 PRTG 的錯誤文字（<paramref name="detail"/>），其餘維持狀態碼。</summary>
    public static string ForHttpStatus(int statusCode, string? detail = null)
    {
        var head = statusCode switch
        {
            400 => BadRequest,
            // 連線不跟隨轉址（OutboundHttp）：只給狀態碼的話操作者不知道要改位址
            301 or 302 or 303 or 307 or 308 =>
                $"PRTG 伺服器要求轉址（HTTP {statusCode}），本系統不跟隨：請把連線位址改成最終網址，常見是 http:// 要改成 https://",
            _ => $"PRTG 伺服器回應錯誤：HTTP {statusCode}"
        };
        return WithOriginal(head, detail);
    }

    /// <summary>說明在前、原文附後（原文空白則只回說明；超過上限截斷並加「…」）。</summary>
    public static string WithOriginal(string description, string? original)
    {
        var text = Clip(original);
        return text.Length == 0 ? description : $"{description}（原文：{text}）";
    }

    /// <summary>單行化並截斷到 <see cref="MaxOriginalLength"/>。</summary>
    public static string Clip(string? original)
    {
        if (string.IsNullOrWhiteSpace(original)) return string.Empty;
        var text = Regex.Replace(original, @"\s+", " ").Trim();
        return text.Length <= MaxOriginalLength ? text : text[..MaxOriginalLength] + "…";
    }

    /// <summary>
    /// 從 PRTG 400 的回應本文取出錯誤文字：優先取 XML 的 &lt;error&gt; 元素，其餘去掉標籤後取文字。
    /// </summary>
    public static string ExtractServerError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var match = Regex.Match(body, @"<error[^>]*>(.*?)</error>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = match.Success ? match.Groups[1].Value : Regex.Replace(body, @"<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(text);
    }

    /// <summary>
    /// 初始化 PRTG 連線失敗的說明：PRTG 用戶端自己的例外訊息已是中文說明；其餘例外只給固定說明，原文只進記錄檔。
    /// </summary>
    public static string ForInit(Exception ex)
        => ex is PrtgClientException ? ex.Message : InitFailed;

    /// <summary>
    /// 存進維護頁狀態欄位的失敗說明：PRTG 用戶端自己的例外訊息保證不含憑證，是管理者判斷帳密、網路、逾時要用的資訊；
    /// 其餘例外只給固定說明與型別名（形狀比照 MigrationFailureText），原文（可能含資料表名、路徑）只進主控台與記錄檔。
    /// </summary>
    public static string ForStatus(Exception ex)
        => ex is PrtgClientException
            ? ex.Message
            : $"發生非預期的錯誤（{ex.GetType().Name}），詳細原因見記錄檔。";
}
