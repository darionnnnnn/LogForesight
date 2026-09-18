namespace LogForesight.Web.Services;

/// <summary>交辦單事件動作代碼的中文對照（唯一一份；未知代碼原樣回傳，由反射守門測試保證全數涵蓋）</summary>
internal static class WorkOrderTextHelpers
{
    public static string ActionText(string action) => action switch
    {
        WorkOrderEventActions.Created => "建立",
        WorkOrderEventActions.Appended => "追加主機",
        WorkOrderEventActions.MergedIn => "併入",
        WorkOrderEventActions.Reassigned => "改派",
        WorkOrderEventActions.SplitOut => "拆出",
        WorkOrderEventActions.SplitIn => "拆入",
        WorkOrderEventActions.Cancelled => "取消",
        WorkOrderEventActions.AdminClosed => "代為結案",
        WorkOrderEventActions.Closed => "結案",
        WorkOrderEventActions.Replied => "已回覆",
        _ => action
    };
}
