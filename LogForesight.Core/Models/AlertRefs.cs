namespace LogForesight.Core.Models;

/// <summary>趨勢告警的種類（回饋十五輪 A-5／C-1）：詳情頁靠這個決定要不要提供頁內導航
/// （只有 signature 有對應的問題分節可捲動）與基準說明文字的用字。</summary>
public static class TrendAlertKinds
{
    /// <summary>首次出現／頻率上升——掛在單一問題簽章上，<see cref="TrendAlertRef.IssueKey"/> 非 null</summary>
    public const string Signature = "signature";

    /// <summary>整體錯誤量突增——不掛任何簽章</summary>
    public const string VolumeError = "volume-error";

    /// <summary>安全稽核事件量突增——不掛任何簽章</summary>
    public const string VolumeAudit = "volume-audit";
}

/// <summary>
/// 趨勢告警的結構化平行資料（回饋十五輪 A-5）：<see cref="DailyAnalysisRecord.TrendAlerts"/>
/// （純文字）維持不變供既有 prompt／報告／console 沿用，這裡是詳情頁做頁內導航與抑制掛載
/// 用的額外資訊。舊紀錄的 <see cref="DailyAnalysisRecord.TrendAlertRefs"/> 為空清單，
/// 前端據此降級回純文字顯示（零破壞）。
/// </summary>
public class TrendAlertRef
{
    /// <summary>與 <see cref="DailyAnalysisRecord.TrendAlerts"/> 對應項目逐字相同的文字</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Kind=Signature 時的問題簽章鍵（<see cref="IssueSignatureKey.For"/>），供跳轉到
    /// 詳情頁對應的問題分節；其餘 Kind 為 null</summary>
    public string? IssueKey { get; set; }

    /// <summary>見 <see cref="TrendAlertKinds"/></summary>
    public string Kind { get; set; } = TrendAlertKinds.Signature;
}

/// <summary>
/// 關聯告警的結構化平行資料（回饋十五輪 A-5），語意與 <see cref="TrendAlertRef"/> 對稱：
/// <see cref="DailyAnalysisRecord.CorrelationAlerts"/>（純文字）維持不變，這裡供詳情頁的
/// 模式說明 popover 與抑制掛載使用。
/// </summary>
public class CorrelationAlertRef
{
    /// <summary>與 <see cref="DailyAnalysisRecord.CorrelationAlerts"/> 對應項目逐字相同的文字</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>穩定模式識別碼，見 LogForesight.Core.Analysis.CorrelationPatternIds</summary>
    public string PatternId { get; set; } = string.Empty;
}
