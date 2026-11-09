using System.Text.Json;

namespace LogForesight.Core.Persistence;

/// <summary>
/// append-only log（<see cref="EfJsonLogStore"/>）逐行反序列化的共用邏輯：略過空白行、
/// 單行損毀只跳過該行不中斷整批（與 history.txt 時代的容錯策略一致）。
/// 原本 5 個 store（稽核／執行紀錄／匯入紀錄／權限異動／處理歷程）各自寫一份幾乎相同的迴圈，
/// 收斂到這裡；ID 播種（各 store 播種欄位名稱不同）維持各自實作，不在此一併抽象。
/// </summary>
internal static class JsonLogParser
{
    public static List<T> Parse<T>(IEnumerable<string> lines, JsonSerializerOptions options) where T : class =>
        ParseStream<T>(lines, options).ToList();

    /// <summary>
    /// 惰性逐行反序列化（容錯規則同 <see cref="Parse{T}"/>）：只在列舉到才解析那一行，
    /// 呼叫端邊讀邊篩選時不必先把整批解析結果放進記憶體。
    /// </summary>
    public static IEnumerable<T> ParseStream<T>(IEnumerable<string> lines, JsonSerializerOptions options) where T : class
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            T? item;
            try
            {
                item = JsonSerializer.Deserialize<T>(line, options);
            }
            catch (JsonException)
            {
                // 逐行獨立：單行損毀只跳過該行
                continue;
            }
            if (item != null) yield return item;
        }
    }
}
