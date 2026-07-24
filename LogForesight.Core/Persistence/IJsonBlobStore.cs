namespace LogForesight;

/// <summary>
/// 「一團 JSON 文字」的原子讀寫（webdata 各 store 的儲存底層，統一走 SQL：SQLite 測試／
/// SqlServer 正式）。把「文字放哪裡、怎麼原子更新」與「store 的業務邏輯」分開，
/// 同一份 store 邏輯不因後端改變（docs/SCALE-2000-PLAN.md §4）。
///
/// <see cref="Mutate{TResult}"/> 是讀→改→寫的原子單位，以交易實作：呼叫端拿到目前內容、
/// 算出新內容，底層保證中途不被別人插入寫入（避免更新遺失——hosts 是批次與 Web 共同
/// 寫入的資料，這點是正確性關鍵）。
/// </summary>
public interface IJsonBlobStore
{
    /// <summary>供 log／Location 顯示（如「sqlserver:users」）</summary>
    string Location { get; }

    /// <summary>目前內容；不存在回 null（首次執行的正常情況）</summary>
    string? Read();

    /// <summary>讀→改→寫的原子操作。mutation 收目前內容、回 (新內容, 結果)</summary>
    TResult Mutate<TResult>(Func<string?, (string content, TResult result)> mutation);
}
