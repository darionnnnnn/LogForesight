using System.Text.RegularExpressions;

namespace LogForesight.Core.Service;

/// <summary>
/// 診斷訊息正規化的唯一實作（異常彙總畫面與診斷匯出共用，兩邊分組才會一致）。
/// 訊息裡的日期、數字、GUID 會讓「同一個錯誤」看起來各不相同；不正規化的話異常彙總會退化成
/// 一筆一組，完全失去「這是通案嗎」的判斷價值。
/// </summary>
public static class RunMessageNormalizer
{
    private static readonly Regex DatePattern = new(@"\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?)?");
    private static readonly Regex GuidPattern = new(@"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b");
    private static readonly Regex NumberPattern = new(@"\d+");

    public static string Normalize(string message)
    {
        var normalized = DatePattern.Replace(message, "{date}");
        normalized = GuidPattern.Replace(normalized, "{guid}");
        return NumberPattern.Replace(normalized, "{n}");
    }
}
