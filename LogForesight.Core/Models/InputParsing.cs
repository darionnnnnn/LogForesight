using System.Globalization;

namespace LogForesight.Core.Models;

/// <summary>
/// 設定與請求輸入的嚴格解析（驗證端與消費端共用同一份，判定才不會分岔）。
/// </summary>
public static class InputParsing
{
    /// <summary>
    /// 一天中的時刻，只接受 <c>HH:mm</c>（00:00～23:59）。<see cref="TimeSpan.TryParse(string?, out TimeSpan)"/>
    /// 會把 "90" 當成 90 天、"1.02:00" 當成 1 天 2 小時，存進去之後「現在時刻 ≥ 設定」永遠不成立，摘要靜默不寄。
    /// </summary>
    public static bool TryParseClockTime(string? value, out TimeSpan time) =>
        TimeSpan.TryParseExact(value?.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);

    /// <summary>
    /// 列舉名稱解析，只接受已定義的成員名稱。<see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> 也接受數字字串（"99"）
    /// 與逗號組合（"A,B"），會把未定義的值存進去。
    /// </summary>
    public static bool TryParseDefinedEnum<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(',') || char.IsDigit(text[0]) || text[0] is '-' or '+') return false;
        return Enum.TryParse(text, ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    /// <summary>
    /// 「到某日（含）」的上界：當天最後一個 tick。<c>AddDays(1).AddSeconds(-1)</c> 會漏掉 23:59:59 之後的那一秒內的紀錄。
    /// 儲存端以 ≤ 比對時，這與「小於次日零時」的半開區間等價（DateTime 與 SQL Server datetime2 的精度都是 100 奈秒）。
    /// </summary>
    public static DateTime EndOfDay(DateTime date) => date.Date.AddDays(1).AddTicks(-1);
}
