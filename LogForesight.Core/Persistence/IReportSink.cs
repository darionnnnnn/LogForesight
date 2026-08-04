namespace LogForesight.Core.Persistence;

/// <summary>報告種類，供後端分類儲存（如檔案系統的子目錄、或未來 DB 的分類欄位），不影響內容本身</summary>
public enum ReportKind
{
    DailyRisk,
    WeeklyCheckup,
    Permission
}

/// <summary>
/// 報告內容的輸出目的地。呼叫端負責組好報告文字內容，sink 只負責「寫到哪裡、回傳什麼參照」——
/// 內容組裝與輸出目的地分離，才能在不改動報告組裝邏輯的前提下換成 DB 後端（OCP/DIP）。
/// </summary>
public interface IReportSink
{
    /// <param name="host">主機識別（單機情境可傳空字串）；有值時實作可依此分子目錄或加分類欄位</param>
    /// <param name="fileName">建議檔名（含副檔名），DB 後端可忽略此參數、僅作為顯示用途保留</param>
    /// <param name="content">已組好的完整報告文字</param>
    /// <returns>報告參照：目前是檔案完整路徑，未來 DB 後端可以是資料表主鍵或查詢 URL</returns>
    Task<string> WriteAsync(ReportKind kind, string host, string fileName, string content);
}
