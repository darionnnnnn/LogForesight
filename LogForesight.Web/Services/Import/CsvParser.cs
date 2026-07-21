using System.Text;

namespace LogForesight.Web.Services.Import;

/// <summary>解析後的 CSV：標題列 ＋ 資料列（每列已對應成 欄位名→值）</summary>
public class CsvTable
{
    public List<string> Headers { get; init; } = new();

    public List<CsvRow> Rows { get; init; } = new();
}

public class CsvRow
{
    /// <summary>檔案中的實際行號（含標題列），錯誤訊息要指得出是哪一行</summary>
    public int LineNumber { get; init; }

    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string header) => Values.TryGetValue(header, out var value) ? value.Trim() : string.Empty;

    /// <summary>多值欄位（groups / owners）：以分號分隔，避開 CSV 本身的逗號</summary>
    public List<string> GetMultiple(string header)
    {
        var raw = Get(header);
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>欄位是否存在且有值（區分「沒填 = 不變」與「填空 = 清空」的關鍵）</summary>
    public bool HasValue(string header) => !string.IsNullOrWhiteSpace(Get(header));

    /// <summary>1/0/true/false；空白回傳 defaultValue</summary>
    public bool? GetBool(string header)
    {
        var raw = Get(header);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return raw.ToLowerInvariant() switch
        {
            "1" or "true" or "y" or "yes" => true,
            "0" or "false" or "n" or "no" => false,
            _ => null
        };
    }
}

public class CsvParseException : Exception
{
    public CsvParseException(string message) : base(message) { }
}

/// <summary>
/// CSV 解析（docs/WEB-SPEC.md §9.9）。
///
/// 自己寫而不引套件：規格很小（逗號分隔、雙引號包覆、標題列），
/// 但需求很具體——UTF-8 BOM 容錯、行號要能對回原始檔案、欄位名不分大小寫。
/// 引入套件再包一層轉接的成本並不比這幾十行低。
/// </summary>
public static class CsvParser
{
    public static CsvTable Parse(Stream stream, int maxRows)
    {
        // detectEncodingFromByteOrderMarks：Excel 另存的 CSV 幾乎都帶 BOM，
        // 不處理的話第一個欄位名會多出一個看不見的字元，變成「找不到 account 欄位」
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);

        if (lines.Count == 0)
            throw new CsvParseException("檔案是空的。");

        var headers = SplitLine(lines[0]).Select(h => h.Trim()).ToList();
        if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace))
            throw new CsvParseException("找不到標題列（第一行必須是欄位名稱）。");

        var duplicated = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
            throw new CsvParseException($"標題列有重複的欄位名稱：{string.Join("、", duplicated)}。");

        var table = new CsvTable { Headers = headers };

        for (var i = 1; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            if (table.Rows.Count >= maxRows)
                throw new CsvParseException($"資料列數超過上限 {maxRows} 列，請分批匯入。");

            var fields = SplitLine(lines[i]);
            var row = new CsvRow { LineNumber = i + 1 };

            for (var c = 0; c < headers.Count; c++)
                row.Values[headers[c]] = c < fields.Count ? fields[c] : string.Empty;

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>
    /// 單行切欄。支援雙引號包覆（欄位值可含逗號）與 "" 跳脫的雙引號本身。
    /// 不支援跨行的引號欄位——Event Log 訊息不會進 CSV，實務上用不到，
    /// 支援它要改成字元流解析，複雜度不成比例。
    /// </summary>
    private static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
