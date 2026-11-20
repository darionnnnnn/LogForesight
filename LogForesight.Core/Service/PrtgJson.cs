using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 回應 JSON 的欄位讀取單一出口。PRTG 同一欄位在不同版本、不同端點會以字串或數字回傳，
/// 欄位缺席時一律回 null（由呼叫端決定預設值），不擲例外。
/// </summary>
public static class PrtgJson
{
    /// <summary>
    /// 讀字串欄位：字串回原值，數字與布林回原始文字，其餘（含欄位缺席、null、物件）回 null。
    /// </summary>
    public static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// 讀整數欄位：數字取 Int64，字串以不分文化的整數解析；欄位缺席或無法解析回 null。
    /// </summary>
    public static long? GetLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }
}
