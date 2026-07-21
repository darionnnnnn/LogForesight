namespace LogForesight.Web.Services.Import;

/// <summary>
/// 單一種類 CSV 匯入的邏輯（docs/WEB-SPEC.md §9.9）。
///
/// 三種匯入（使用者／主機／群組授權）各一個實作，共用同一套流程：
/// 上傳 → <see cref="BuildPlan"/>（驗證、判定逐列動作，**不寫入**）→ 預覽 → <see cref="Apply"/>。
/// 新增第四種匯入時只要多一個實作並註冊，ImportService 與 Controller 都不需要改（OCP）。
/// </summary>
public interface ICsvImporter
{
    ImportKind Kind { get; }

    /// <summary>必要欄位；缺少時整檔拒絕（並列出缺哪些，順便抓拼錯字）</summary>
    string[] RequiredHeaders { get; }

    /// <summary>可辨識的全部欄位；出現未知欄位時提醒（多半是拼錯字）</summary>
    string[] KnownHeaders { get; }

    /// <summary>範本內容（含範例列）。以 UTF-8 BOM 輸出，Excel 開啟不會亂碼</summary>
    string BuildTemplate();

    /// <summary>驗證並判定每一列的動作，不寫入任何資料</summary>
    ImportPlan BuildPlan(CsvTable table, string fileName);

    /// <summary>執行計畫。呼叫端已確認 <see cref="ImportPlan.CanApply"/></summary>
    ImportResult Apply(ImportPlan plan, CsvTable table);
}
