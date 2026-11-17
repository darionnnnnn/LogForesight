namespace LogForesight.Core.Persistence;

/// <summary>
/// 交辦單／案件協調器自己判定的規則違反（資料不符、已結案、缺原因、成員不屬於本單……）。
/// 訊息是協調器寫好的中文，可以原樣給使用者看；Web 層的 Guard 只轉換這個型別，
/// 框架層（EF／LINQ）擲出的 <see cref="InvalidOperationException"/> 與 <see cref="ArgumentException"/> 原文可能是英文內部細節，
/// 不會被當成規則訊息（記錄後交給 <c>ApiExceptionFilter</c> 回 500 與固定說明）。
///
/// 繼承 <see cref="InvalidOperationException"/> 是刻意的：既有 <c>catch (InvalidOperationException)</c> 的呼叫端行為不變。
/// 只在協調器對「使用者操作」的規則檢查使用；程式錯誤（夜間派工不支援的步驟、建構參數）維持原例外型別。
/// </summary>
public sealed class WorkOrderRuleException : InvalidOperationException
{
    public WorkOrderRuleException(string message) : base(message)
    {
    }
}
