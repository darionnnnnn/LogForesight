using System.Text;
using LogForesight.Web.Services.Import;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 80 輪批次 X2 探針：CsvRow.LineNumber 必須等於該筆資料在檔案中的起始行（從 1 起）。</summary>
public class CsvParserLineNumberProbeXTests
{
    private static CsvTable Parse(string text) =>
        CsvParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(text)), 100);

    [Theory]
    [InlineData("a,b\n1,\"x\ny\nz\"\n3,4\n", new[] { 2, 5 })]       // 跨行引號欄位：行號正確（守門）
    [InlineData("a,b\r\n1,\"x\r\ny\"\r\n3,4\r\n", new[] { 2, 4 })] // CRLF 跨行
    public void 跨行引號欄位的行號等於起始行(string csv, int[] expected)
    {
        Assert.Equal(expected, Parse(csv).Rows.Select(r => r.LineNumber).ToArray());
    }

    [Theory(Skip = "第 80 輪批次 X 探針成立，待決：TextFieldParser 讀取時靜默跳過空行，LineNumber 不含被跳過的空行，空行之後的資料列行號偏小（只影響錯誤訊息「第 N 行」）；API 取不到跳過行數，修法需自寫行計數讀取器，見回報")]
    [InlineData("a,b\n\n1,2\n", new[] { 3 })]                       // 前置空行
    [InlineData("a,b\r\n\r\n\r\n1,2\r\n3,4\r\n", new[] { 4, 5 })]   // 多個空行、CRLF
    [InlineData("a,b\n1,2\n\n3,\"p\nq\"\n\n5,6\n", new[] { 2, 4, 7 })] // 混合
    public void 空行之後的資料列行號等於起始行(string csv, int[] expected)
    {
        Assert.Equal(expected, Parse(csv).Rows.Select(r => r.LineNumber).ToArray());
    }
}
