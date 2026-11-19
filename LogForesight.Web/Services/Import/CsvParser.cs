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
/// CSV 解析（docs/WEB-SPEC.md §9.9），以框架內建的 <see cref="Microsoft.VisualBasic.FileIO.TextFieldParser"/> 切欄：
/// 雙引號包覆、"" 跳脫、跨行的引號欄位（Excel 儲存格內換行）都由它處理，不再自己寫字元流。
/// 這裡只負責本專案的需求：UTF-8 BOM 容錯、行號對回原始檔案、欄位名不分大小寫、列數上限在讀取當下就擋。
/// </summary>
public static class CsvParser
{
    public static CsvTable Parse(Stream stream, int maxRows)
    {
        // detectEncodingFromByteOrderMarks：Excel 另存的 CSV 幾乎都帶 BOM，
        // 不處理的話第一個欄位名會多出一個看不見的字元，變成「找不到 account 欄位」
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(reader)
        {
            TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            // 不在這裡去空白：欄位值的空白由 CsvRow.Get 處理，引號內刻意保留的空白不能被吃掉
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        var headerFields = ReadRecord(parser, out _);
        if (headerFields == null)
            throw new CsvParseException("檔案是空的。");

        var headers = headerFields.Select(h => h.Trim()).ToList();
        if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace))
            throw new CsvParseException("找不到標題列（第一行必須是欄位名稱）。");

        var duplicated = headers.GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
            throw new CsvParseException($"標題列有重複的欄位名稱：{string.Join("、", duplicated)}。");

        var table = new CsvTable { Headers = headers };
        while (ReadRecord(parser, out var lineNumber) is { } fields)
        {
            if (fields.All(string.IsNullOrWhiteSpace)) continue;

            // 讀到第 maxRows + 1 列就停：不先把整份檔案讀進記憶體再檢查
            if (table.Rows.Count >= maxRows)
                throw new CsvParseException($"資料列數超過上限 {maxRows} 列，請分批匯入。");

            var row = new CsvRow { LineNumber = (int)lineNumber };
            for (var c = 0; c < headers.Count; c++)
                row.Values[headers[c]] = c < fields.Length ? fields[c] : string.Empty;
            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>讀下一筆紀錄；回傳 null 表示檔案結束。<paramref name="lineNumber"/> 是這筆紀錄起始的原始行號（從 1 起）。</summary>
    private static string[]? ReadRecord(Microsoft.VisualBasic.FileIO.TextFieldParser parser, out long lineNumber)
    {
        lineNumber = parser.LineNumber;
        if (parser.EndOfData) return null;
        try
        {
            lineNumber = parser.LineNumber;
            return parser.ReadFields();
        }
        catch (Microsoft.VisualBasic.FileIO.MalformedLineException ex)
        {
            throw new CsvParseException($"第 {ex.LineNumber} 行的引號沒有成對結束，請檢查該行的雙引號。");
        }
    }
}
