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
    /// <summary>trigger 為 null 代表沒有執行過／已閒置；未知字串原樣回傳（不吞掉線索）</summary>
    public static string Of(string? trigger, IUserDisplayNameService displayNames, IUserStore users) => trigger switch
    {
        null => "閒置",
        "schedule" => "排程",
        _ when trigger.StartsWith("manual:", StringComparison.Ordinal) =>
            $"手動（{displayNames.OfAccount(users, trigger["manual:".Length..])}）",
        _ => trigger
    };
}
