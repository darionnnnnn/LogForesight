using System.Globalization;
using LogForesight.Web.Models;

namespace LogForesight.Web.Controllers.Api;

/// <summary>
/// Controller 查詢字串解析的單一事實來源（docs/archive/HISTORY.md S9）。
/// 取代原本在 RecordsController／AiController／AuditController／DashboardController(含
/// ReportsController) 各自維護的四份逐字相同的 ParseDate/ParseLongs/ParseStrings。
/// </summary>
internal static class QueryStringParsing
{
    /// <summary>
    /// 逗號分隔的整數清單；空白／全空白回 null（表示「未指定」）。有任何一段不是整數就回 400：
    /// 默默丟掉壞的片段會讓「1,x,3」變成「1,3」，全壞時更會變成空清單或「未指定」——篩選條件靜默失效。
    /// </summary>
    public static List<long>? ParseLongs(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var values = new List<long>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw DomainException.Validation($"「{Truncate(part)}」不是有效的編號。");
            values.Add(value);
        }
        return values;
    }

    /// <summary>逗號分隔的字串清單；空白／全空白回 null（表示「未指定」）</summary>
    public static List<string>? ParseStrings(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? null
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>yyyy-MM-dd；解析失敗回 null（呼叫端自行決定要當「未指定」還是回錯誤）</summary>
    public static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>yyyy-MM-dd，解析失敗即丟 Validation——用於日期是路徑/必填參數的端點</summary>
    public static DateTime ParseRequiredDate(string? value) =>
        ParseDate(value) ?? throw DomainException.Validation("日期格式必須為 yyyy-MM-dd。");

    /// <summary>
    /// from/to 字串正規化為日期區間。
    /// <list type="bullet">
    /// <item>解析失敗或空字串時分別套用 <paramref name="fromDefault"/> / <paramref name="toDefault"/>。</item>
    /// <item>若 from &gt; to 且 <paramref name="throwOnReversed"/> 為 false（預設），自動交換；為 true 則丟驗證錯誤。</item>
    /// <item>若 <paramref name="maxDays"/> 有值且含頭尾天數超過上限，丟驗證錯誤。</item>
    /// </list>
    /// </summary>
    public static (DateTime? from, DateTime? to) ParseDateRange(
        string? fromStr,
        string? toStr,
        DateTime? fromDefault = null,
        DateTime? toDefault = null,
        bool throwOnReversed = false,
        int? maxDays = null)
    {
        // 空白＝未指定、套用預設；有填但格式不對就回 400，不能靜默當成未指定（使用者以為篩了日期，其實沒有）
        var from = ParseOptionalDate(fromStr) ?? fromDefault;
        var to   = ParseOptionalDate(toStr)   ?? toDefault;

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            if (throwOnReversed)
                throw DomainException.Validation("查詢起日不可大於迄日。");
            (from, to) = (to, from);
        }

        if (maxDays.HasValue && from.HasValue && to.HasValue)
        {
            var days = (to.Value - from.Value).Days + 1;
            if (days > maxDays.Value)
                throw DomainException.Validation(
                    $"查詢範圍過大（目前 {days} 天，上限 {maxDays.Value} 天），請縮小。");
        }

        return (from, to);
    }

    /// <summary>空白回 null（未指定）；有填但不是 yyyy-MM-dd 就丟 Validation</summary>
    public static DateTime? ParseOptionalDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseRequiredDate(value);

    private static string Truncate(string value) => value.Length <= 32 ? value : value[..32] + "…";
}
