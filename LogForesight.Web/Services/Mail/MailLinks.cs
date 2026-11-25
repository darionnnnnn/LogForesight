using LogForesight.Core.Models;

namespace LogForesight.Web.Services.Mail;

/// <summary>
/// 信件連結的唯一組裝點：站台網址只取自設定 <c>SiteBaseUrl</c>（不取請求標頭），信件服務內不得自行以字串相接網址。
/// 站台可能掛在 IIS 子應用程式下，所以設定值可含子路徑（例 https://x.example/lf），組出的連結保留該子路徑。
/// </summary>
public static class MailLinks
{
    // 頁面名稱與 wwwroot/js/core/layout.js 的導覽文字一致（守門測試比對）
    public const string MyWorkOrdersName = "我的交辦";
    public const string WorkOrdersName = "交辦總覽";
    public const string RecordsName = "問題查詢";

    // 路徑對齊 PagesController 的頁面路由
    public const string WorkOrdersPath = "work-orders";
    public const string RecordsPath = "records";
    public static string MyWorkOrdersPath(long userId) => $"handlers/{userId}";

    /// <summary>「我的交辦」連結帶標記篩選的查詢參數名（值是 <see cref="WorkOrderQueries.Flags"/> 之一，與清單端點的 <c>flag</c> 同名同值）</summary>
    public const string FlagQueryName = "flag";

    /// <summary>
    /// 帶標記篩選的「我的交辦」路徑（例 <c>handlers/3?flag=returned</c>：只看被退回的單）。
    /// <paramref name="flag"/> 為 null 或不是已知標記時退回不帶篩選的路徑（不把未知值塞進信件連結）。
    /// 站台網址與相對路徑的相接仍只在 <see cref="Build"/>。
    /// </summary>
    public static string MyWorkOrdersPath(long userId, string? flag) =>
        flag != null && WorkOrderQueries.Flags.Contains(flag)
            ? $"{MyWorkOrdersPath(userId)}?{FlagQueryName}={Uri.EscapeDataString(flag)}"
            : MyWorkOrdersPath(userId);
    public static string WorkOrderPath(long workOrderId) => $"work-orders/{workOrderId}";

    /// <summary>格式說明（儲存被拒時給使用者的訊息）</summary>
    public const string FormatHint = "站台網址必須是以 http:// 或 https:// 開頭的完整網址，不可含空白，例如 https://lf.example.com 或 https://srv.example.com/lf（站台掛在子應用程式時要含子路徑）。";

    /// <summary>
    /// 組出連結：站台網址沒填或不合法時回 null（呼叫端就不附連結，信件與未設定時完全相同）。
    /// 去掉站台網址結尾斜線、相對路徑開頭斜線，相接後不會出現雙斜線，子路徑保留。
    /// </summary>
    public static string? Build(string? baseUrl, string relativePath)
    {
        if (!TryNormalize(baseUrl, out var normalized, out _) || normalized.Length == 0) return null;
        return normalized + "/" + (relativePath ?? "").TrimStart('/');
    }

    /// <summary>
    /// 驗證並正規化站台網址（去頭尾空白與結尾斜線）。空白視為「沒填」（合法、結果為空字串）。
    /// 必須是 http／https 的完整絕對網址；含空白字元、ftp://、只有主機名（x.example）、帶查詢字串、片段或內嵌帳密（user@host）都拒絕。
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return true;

        if (text.Any(char.IsWhiteSpace)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !text.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase)
            || text.Contains('?') || text.Contains('#'))
        {
            error = FormatHint;
            return false;
        }

        normalized = text.TrimEnd('/');
        return true;
    }
}
