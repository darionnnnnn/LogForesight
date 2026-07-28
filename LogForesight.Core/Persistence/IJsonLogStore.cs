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

    /// <summary>
    /// 刪除附加時間早於 cutoff 的行，回傳刪除筆數（docs/OPS-HARDENING-PLAN.md P0-3）。
    /// 附加時間是**寫入時的插入時間戳記**，不是行內容解析出的業務日期——這批資料多是
    /// append-only 執行歷程，插入時間就是事件發生時間，不必逐行解析 JSON。
    /// schema 升級前寫入的既存列沒有時間戳記，一律保留（無法判斷年代，寧可留著）。
    /// </summary>
    int Prune(DateTime cutoff);

    /// <summary>
    /// 依附加時間範圍讀取（不分頁，依附加順序），供呼叫端先在 SQL 端窄化候選集再於記憶體精確過濾
    /// （docs/OPS-HARDENING-PLAN.md P1-2）。from/to 為 null 代表不限；沒有時間戳記的既存列
    /// （schema 升級前寫入）一律視為在範圍內——**這裡的窄化不保證絕對精確**，呼叫端仍須對
    /// 精確欄位做最終判斷（見 <c>AuditLogStore.Count</c> 的用法）。
    /// </summary>
    IReadOnlyList<string> ReadLines(DateTime? from, DateTime? to);

    /// <summary>
    /// 全表分頁讀取（依附加順序新到舊），SQL 端 OFFSET/FETCH，不必先讀全表。
    /// 僅適合「完全不篩選、只是要分頁瀏覽全部」的情境——若還有其他篩選條件，
    /// 呼叫端應改用 <see cref="ReadLines(DateTime?,DateTime?)"/> 撈候選集後在記憶體精確過濾與分頁。
    /// </summary>
    (IReadOnlyList<string> Items, int Total) ReadPage(int skip, int take);
}
