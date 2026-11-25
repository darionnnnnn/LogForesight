using LogForesight.Core.Analysis;

namespace LogForesight.Core.Models;

/// <summary>
/// 預設期限的唯一一份常數來源（程式常數，不是設定）：依問題嚴重度自「建立日（站台時鐘的日期）」起算——
/// 高 <see cref="High"/> 天、中 <see cref="Medium"/> 天，低不帶期限（回 null）。
/// 夜間派工新建的單、案件與逐日列，以及人工交辦預覽的建議期限（<c>SuggestedDueDate</c>）都從這裡算，不得各寫一份。
/// 只影響新建的；既有案件不回填。舊資料的 Critical 經 <see cref="LegacySeverityRank"/> 正規化為 High。
/// </summary>
public static class DefaultDueDays
{
    /// <summary>嚴重度高：建立日起 3 天</summary>
    public const int High = 3;

    /// <summary>嚴重度中：建立日起 7 天</summary>
    public const int Medium = 7;

    /// <summary>該嚴重度的預設天數；低（不適用）回 null</summary>
    public static int? ForSeverity(IssueSeverity severity) => LegacySeverityRank.ToSeverity((int)severity) switch
    {
        IssueSeverity.High => High,
        IssueSeverity.Medium => Medium,
        _ => null
    };

    /// <summary>以 severity_rank 原始值取預設天數（先經 <see cref="LegacySeverityRank"/> 正規化）</summary>
    public static int? ForRank(int rank) => ForSeverity((IssueSeverity)rank);

    /// <summary>
    /// 預設期限日期：<paramref name="createdOn"/> 只取日期部分（不帶時間）再加天數；低嚴重度回 null。
    /// <paramref name="createdOn"/> 一律來自站台時鐘（注入的 <c>TimeProvider</c> 或呼叫端傳入的發生時間），不在這裡讀系統時鐘。
    /// </summary>
    public static DateTime? DueDateFor(IssueSeverity severity, DateTime createdOn) =>
        ForSeverity(severity) is { } days ? createdOn.Date.AddDays(days) : null;
}
