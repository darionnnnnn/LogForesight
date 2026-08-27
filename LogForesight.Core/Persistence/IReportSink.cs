namespace LogForesight.Core.Persistence;

/// <summary>報告種類，供後端分類儲存（`lf_reports.kind`），不影響內容本身</summary>
public enum ReportKind
{
    DailyRisk,
    WeeklyCheckup,
    Permission
}

/// <summary>
/// <see cref="ReportKind"/> 的持久化字面值（`lf_reports.kind`）。
/// 不直接存 enum 的 ToString()／序號：前者改名就靜默漂移，後者插入新成員就整批錯位，
/// 而這一欄要跨版本讀得回來。
/// </summary>
public static class ReportKinds
{
    public const string DailyRisk = "daily_risk";
    public const string WeeklyCheckup = "weekly_checkup";
    public const string Permission = "permission";

    public static string Of(ReportKind kind) => kind switch
    {
        ReportKind.DailyRisk => DailyRisk,
        ReportKind.WeeklyCheckup => WeeklyCheckup,
        ReportKind.Permission => Permission,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的報告種類")
    };

    /// <summary>顯示用中文名稱（報告檢視畫面的標題與查無資料的訊息共用）</summary>
    public static string Zh(string kind) => kind switch
    {
        DailyRisk => "風險報告",
        WeeklyCheckup => "體檢報告",
        Permission => "權限異動報告",
        _ => "報告"
    };
}

/// <summary>
/// 報告的分類中繼資料，供清單與檢視畫面的標題列使用（只有風險報告有值）。
/// 內容本身已在 <c>content</c> 裡，這裡存的是不解全文就能顯示的那幾個欄位。
/// </summary>
/// <param name="RiskLevel">風險等級</param>
/// <param name="Categories">當日發現的類別串，如「儲存裝置+安全」</param>
public record ReportMeta(string? RiskLevel = null, string? Categories = null);

/// <summary>
/// 報告內容的輸出目的地。呼叫端負責組好報告文字內容，sink 只負責「寫到哪裡、回傳什麼參照」——
/// 內容組裝與輸出目的地分離，報告組裝邏輯不需要知道自己被存到哪。
/// </summary>
public interface IReportSink
{
    /// <param name="host">主機識別。<see cref="HostKey.HostId"/> 為 0（主機尚未登記成功）時
    /// 以 <see cref="HostKey.HostName"/> 歸戶，語意同全站的 <c>HostIdentity</c> 慣例</param>
    /// <param name="fileName">既有檔名格式（含副檔名），存為顯示與下載檔名</param>
    /// <param name="content">已組好的完整報告文字</param>
    /// <param name="meta">分類中繼資料，非風險報告可省略</param>
    /// <returns>報告參照（`lf_reports.report_id` 的字串形式），存進
    /// <c>DailyAnalysisRecord.ReportFile</c> 供讀取端反查</returns>
    Task<string> WriteAsync(ReportKind kind, HostKey host, string fileName, string content, ReportMeta? meta = null);
}
