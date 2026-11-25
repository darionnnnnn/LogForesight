using System.Globalization;
using System.Text;

namespace LogForesight.Web.Services;

/// <summary>一張可匯出的報表表格：欄名與列（儲存格為字串、數字或 null）</summary>
public sealed record ReportCsvTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// 報表 CSV 輸出（F20-4）。規則只在這裡：
/// <list type="bullet">
/// <item>UTF-8 並寫 BOM（Excel 才認得中文）；CRLF 換行。</item>
/// <item>文字儲存格含逗號、雙引號、換行時以雙引號包起來，內部雙引號重複一次。</item>
/// <item>公式注入：文字儲存格以 <c>=</c>、<c>+</c>、<c>-</c>、<c>@</c>（或 Tab、CR）開頭時前置單引號，Excel 不會當公式執行；
/// 數字儲存格不處理（以不分文化的格式輸出，負數不會被誤加單引號）。外部系統字串（問題名稱、主機狀況等）一律當文字原樣輸出。</item>
/// <item>列數上限 <see cref="MaxRows"/>，由 <c>ReportService.GetCsvTable</c> 把關。</item>
/// </list>
/// </summary>
public static class ReportCsv
{
    /// <summary>單張表匯出的列數上限（不含標題列）；超過回明確訊息請縮小期間</summary>
    public const int MaxRows = 50_000;

    public const string TableTrend = "trend";
    public const string TableHosts = "hosts";
    public const string TableIssues = "issues";

    /// <summary>列數（不含標題列）超過上限擲驗證錯誤並要求縮小期間；恰為上限放行。開始串流寫出之前呼叫</summary>
    public static void RequireWithinLimit(int rowCount)
    {
        if (rowCount > MaxRows)
            throw Models.DomainException.Validation(
                $"這張表有 {rowCount} 列，超過匯出上限 {MaxRows} 列，請縮小期間或調整顯示範圍後再匯出。");
    }

    public static string Cell(object? value)
    {
        switch (value)
        {
            case null: return string.Empty;
            case string text: return Quote(Neutralize(text));
            case IFormattable number: return number.ToString(null, CultureInfo.InvariantCulture);
            default: return Quote(Neutralize(value.ToString() ?? string.Empty));
        }
    }

    /// <summary>以 = + - @ Tab CR 開頭的文字前置單引號（公式注入防護）</summary>
    private static string Neutralize(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string Quote(string text) =>
        text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    /// <summary>逐列串流寫出（先寫 BOM 與標題列）。呼叫端須在寫出前就把上限檢查做完——開始寫之後無法再改回應狀態碼。</summary>
    public static async Task WriteAsync(Stream output, ReportCsvTable table, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', table.Columns.Select(c => Cell(c))));
        foreach (var row in table.Rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', row.Select(Cell)));
        }
        await writer.FlushAsync(ct);
    }
}
