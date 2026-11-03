namespace LogForesight.Web.Services;

/// <summary>
/// 診斷匯出組裝服務需要的遮罩能力。<see cref="RunExportDeidentifier"/> 是唯一的正式實作；
/// 抽成介面是為了讓測試能換入「故意漏遮」的替身，驗證結果掃描真的會擋下檔案——
/// 正式碼的遮罩與掃描共用同一份字典，不這樣做就沒有辦法讓掃描路徑在測試裡出現殘留。
/// </summary>
public interface IRunExportMasker
{
    /// <summary>遮罩一段自由文字；null 或空字串回空字串</summary>
    string Mask(string? text);

    /// <summary>主機名稱欄位（執行站台）取代號</summary>
    string? MaskHost(string? hostText);

    /// <summary>堆疊處理（保留專案框架呼叫、框架層略過、路徑只留檔名）</summary>
    string MaskStack(string? stack);

    /// <summary>各類代號已配發的數量</summary>
    IReadOnlyDictionary<string, int> TokenCounts { get; }

    /// <summary>
    /// 結果掃描，失敗即停（丟 <see cref="RunExportDeidentificationException"/>）。
    /// <paramref name="scanPropertyNames"/> 為 false 時只掃值，見 <see cref="RunExportDeidentifier.ScanResidual"/>。
    /// </summary>
    void EnsureNoResidual(string json, IEnumerable<string>? allowedLiterals = null, bool scanPropertyNames = true);
}
