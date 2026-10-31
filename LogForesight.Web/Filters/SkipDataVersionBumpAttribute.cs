namespace LogForesight.Web.Filters;

/// <summary>
/// 宣告端點不改變分析資料：成功的非 GET 請求結束後不推進資料版本戳
/// （<c>DataVersionStampPolicy</c>），儀表板／報表的整包快取不會因它失效。
/// 仍是排除法——沒有貼這個標記的端點一律照常推進，所以新端點自動涵蓋；
/// 只有確認「不寫分析資料」才貼，有疑慮就不要貼（多貼＝使用者看到過期資料）。
/// 可貼在 class（整個控制器）或 action 上，宣告跟著端點走，不會像路徑清單那樣漂移。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SkipDataVersionBumpAttribute : Attribute
{
}
