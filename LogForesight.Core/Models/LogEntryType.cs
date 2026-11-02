namespace LogForesight.Core.Models;

/// <summary>
/// 事件等級／類別。成員名稱與整數值刻意完全同 <c>System.Diagnostics.EventLogEntryType</c>
/// （Error=1、Warning=2、Information=4、SuccessAudit=8、FailureAudit=16；另保留 classic API 的
/// Critical → 0 無名稱值慣例），因此 JSON（以名稱序列化）與 DB（整數欄位）契約不變。
/// 自訂此列舉的目的：<c>EventLogEntryType</c> 在 net8.0 組件層級標有 Windows 專屬，
/// 直接使用會讓整個專案無法在非 Windows 平台通過 CA1416。
/// </summary>
public enum LogEntryType
{
    Error = 1,
    Warning = 2,
    Information = 4,
    SuccessAudit = 8,
    FailureAudit = 16
}
