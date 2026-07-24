namespace LogForesight;

/// <summary>
/// append-only 逐行紀錄的讀寫底層（稽核、執行紀錄、匯入紀錄、處理歷程…）。
///
/// 與 <see cref="IJsonBlobStore"/> 分開的理由：這些是**高頻附加**的資料，
/// 每次都重寫整份內容會隨資料量線性變慢。因此提供 O(1) 的 <see cref="AppendLine"/>——
/// DB 版對應 INSERT 一列。讀取一律回全部行，呼叫端逐行解析（單行損毀跳過）。
/// </summary>
public interface IJsonLogStore
{
    string Location { get; }

    /// <summary>全部行，依附加順序</summary>
    IReadOnlyList<string> ReadLines();

    /// <summary>附加一行（O(1)）</summary>
    void AppendLine(string line);
}
