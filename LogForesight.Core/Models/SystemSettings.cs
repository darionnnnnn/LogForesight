namespace LogForesight;

/// <summary>
/// 全站系統設定（↔ webdata blob，key=system_settings）。**單一物件，非清單**——全站只有一份。
///
/// 涵蓋原本分散在批次 appsettings.json（AI 位址）與程式碼寫死常數（未處理等級門檻、
/// 補充／留存天數）的可調整項目，改由「系統管理 > 設定」頁維護。所有欄位的預設值
/// 沿用原本的寫死行為，既有部署升級後行為不變，直到管理者主動調整。
/// </summary>
public class SystemSettings
{
    /// <summary>
    /// 未處理計算納入哪些嚴重度（<see cref="IssueSeverity"/> 名稱：Critical/High/Medium/Low）。
    /// 未列在此清單的嚴重度，若問題未被明確標記，視同「不處理（預設）」，不計入未處理／待辦統計
    /// （一般化原本寫死「Low 一律預設不處理」的規則）。
    /// </summary>
    public List<string> UnhandledSeverities { get; set; } = new() { "Critical", "High", "Medium" };

    /// <summary>
    /// 層級顯示模式，決定 <see cref="UnhandledSeverities"/> 以外的嚴重度在畫面上如何呈現：
    /// <c>DefaultHidden</c>（預設隱藏但可手動開啟）／<c>Locked</c>（完全隱藏、無法開啟）／
    /// <c>GlobalFilter</c>（後端查詢層直接排除，全站統計數字只計入已勾選層級）。
    /// 不影響風險等級判定與報告全文——那是批次時已算定的證據層，不受顯示設定影響。
    /// </summary>
    public string SeverityDisplayMode { get; set; } = "DefaultHidden";

    /// <summary>AI（llama.cpp／OpenAI 相容端點）位址。空字串＝AI 加值層與批次 AI 分析停用</summary>
    public string AiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// AI API 金鑰的密文（<see cref="CryptoHelper.Encrypt"/> 產生）。地端無驗證的端點留空即可；
    /// 需驗證的雲端／內部代理端點才需要設定，發送時以 <c>Authorization: Bearer</c> 帶入。
    /// </summary>
    public string AiApiKeyEnc { get; set; } = "";

    /// <summary>首次執行（歷史資料庫全空）時回補歷史的天數</summary>
    public int InitialHistoryDays { get; set; } = 120;

    /// <summary>歷史資料庫保留天數（需 &gt;= InitialHistoryDays）</summary>
    public int RetentionDays { get; set; } = 120;

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }

    /// <summary>
    /// <see cref="UnhandledSeverities"/> 解析成 <see cref="IssueSeverity"/> 集合，供
    /// <c>DayHandlingDerivation.Derive</c> 與問題明細的預設不處理判定共用。無法解析的字串（設定損毀）
    /// 靜默略過，不讓整個未處理計算因為一個壞字串而掛掉。
    /// </summary>
    public HashSet<IssueSeverity> ParseUnhandledSeverities() =>
        UnhandledSeverities
            .Select(s => Enum.TryParse<IssueSeverity>(s, ignoreCase: true, out var severity) ? severity : (IssueSeverity?)null)
            .Where(s => s.HasValue)
            .Select(s => s!.Value)
            .ToHashSet();
}
