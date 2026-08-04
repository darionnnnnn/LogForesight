namespace LogForesight.Web.Services;

/// <summary>
/// 處理狀態／案件相關的顯示文字對照，供日層級（DayHandlingCommandService）、問題層級
/// （IssueHandlingCommandService）、查詢（HandlingHistoryQueryService）三處共用。
/// </summary>
internal static class HandlingTextHelpers
{
    public static string StatusText(string status) => status switch
    {
        HandlingStatuses.Open => "未處理",
        HandlingStatuses.InProgress => "處理中",
        HandlingStatuses.Resolved => "已處理",
        HandlingStatuses.WontFix => "不處理",
        HandlingStatuses.FalsePositive => "誤報",
        HandlingStatuses.KnownNoise => "已知雜訊",
        _ => status
    };

    /// <summary>問題層級狀態文字（未標記＝未處理由呼叫端處理）</summary>
    public static string IssueStatusText(string status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        IssueHandlingStatuses.InProgress => "處理中",
        IssueHandlingStatuses.Observing => "觀察中",
        IssueHandlingStatuses.Open => "未處理",
        _ => status
    };

    public static string ActionText(string action) => action switch
    {
        HandlingActions.Assign => "指派處理人",
        HandlingActions.AutoAssign => "自動帶入處理人",
        HandlingActions.StatusChange => "變更狀態",
        HandlingActions.NoteUpdate => "更新說明",
        HandlingActions.IssueStatus => "標記問題",
        HandlingActions.IssueStatusCleared => "清除問題標記",
        HandlingActions.CaseAssign => "建立案件",
        HandlingActions.CaseSync => "案件同步",
        HandlingActions.CaseAttach => "排程掛接案件",
        _ => action
    };

    /// <summary>問題歷程列的顯示文字（「Source EventId」），反正規化存進 RecordHandlingLog.IssueLabel</summary>
    public static string IssueLabel(LogIssueSignature issue) => $"{issue.Source} {issue.EventId}";
}
