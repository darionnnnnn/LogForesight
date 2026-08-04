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
    public static List<T> Parse<T>(IEnumerable<string> lines, JsonSerializerOptions options) where T : class
    {
        var result = new List<T>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, options);
                if (item != null) result.Add(item);
            }
            catch (JsonException)
            {
                // 逐行獨立：單行損毀只跳過該行
            }
        }
        return result;
    }
}
