namespace LogForesight;

/// <summary>
/// 依設定選擇儲存後端（Strategy + Factory）。目前只有 Jsonl 一種實作；
/// 未來新增 DB 後端時，這裡是唯一需要新增 case 的地方，呼叫端（Program.cs／LogAnalysisService）不需修改。
/// </summary>
public static class StorageFactory
{
    public static IAnalysisRecordStore CreateRecordStore(StorageSettings settings, string? filePath = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonlAnalysisRecordStore(filePath);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，改用預設的 Jsonl。");
                return new JsonlAnalysisRecordStore(filePath);
        }
    }
}
