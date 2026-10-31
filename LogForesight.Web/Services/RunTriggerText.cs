using LogForesight.Core.Persistence;

namespace LogForesight.Web.Services;

/// <summary>
/// 「誰觸發的」文字（<c>manual:帳號</c>／<c>schedule</c>／其他內部觸發字串轉成中文）。
///
/// 抽成共用 helper 而不是每個呼叫端各寫一份：排程頁狀態與全站執行中告示講的是同一趟執行，
/// 兩邊文字若不一致，使用者會以為是兩件不同的事（回饋四十五輪批次A3）。
/// </summary>
public static class RunTriggerText
{
    /// <summary>
    /// 觸發字串轉成給人看的文字。trigger 必須非 null：「閒置」與「工作排程器」的 null 處理由呼叫端決定
    /// （即時狀態講閒置、歷史紀錄講工作排程器），這裡不替它猜。
    /// 舊紀錄的 <c>console</c>／空字串統一顯示「工作排程器」；未知字串原樣回傳（不吞掉線索）。
    /// </summary>
    public static string Of(string trigger, IUserDisplayNameService displayNames, IUserStore users) => trigger switch
    {
        "" or "console" => "工作排程器",
        BatchRunTriggers.Schedule => "排程",
        BatchRunTriggers.FetchFollowUp => "AI 跟隨取數",
        _ when trigger.StartsWith("manual:", StringComparison.Ordinal) =>
            $"手動（{displayNames.OfAccount(users, trigger["manual:".Length..])}）",
        _ => trigger
    };
}
