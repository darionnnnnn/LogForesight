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

    /// <summary>規則儲存後端，目前只有 rules.json 一種實作；未來新增 DB 後端時在這裡加一個 case 即可</summary>
    public static IKnownIssueRuleStore CreateRuleStore(StorageSettings settings, string? filePath = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonKnownIssueRuleStore(filePath);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，規則庫改用預設的 Jsonl。");
                return new JsonKnownIssueRuleStore(filePath);
        }
    }

    /// <summary>抑制設定的儲存後端，目前只有 suppressions.json 一種實作</summary>
    public static ISuppressionStore CreateSuppressionStore(StorageSettings settings, string? filePath = null)
    {
        switch (settings.Type)
        {
            case "Jsonl":
                return new JsonSuppressionStore(filePath);
            default:
                Console.WriteLine($"未知的 Storage.Type「{settings.Type}」，抑制設定改用預設的 Jsonl。");
                return new JsonSuppressionStore(filePath);
        }
    }
}
