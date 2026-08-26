namespace LogForesight.Core.Models;

/// <summary>
/// 重新分析模式：依主機日的處理狀態決定哪些已有紀錄的舊日子需要重新分析。
/// 判準用「該主機日在 lf_issue_handling 有沒有列／有沒有結案類列」，不走日層級狀態推導，確定性較高。
/// 結案類判定由 <see cref="IssueHandlingStatuses.IsClosed"/> 決定。
/// </summary>
public enum RerunMode
{
    /// <summary>不重跑（現行行為）：無既有主機日會被重跑，只補缺漏日。</summary>
    None = 0,

    /// <summary>只重跑完全沒被處理過的日子：該主機日在 lf_issue_handling 沒有任何列。</summary>
    Unhandled = 1,

    /// <summary>加上「已指派但還沒處理完」的日子：該主機日沒有任何結案類列（可以有非結案類列）。</summary>
    UnhandledAndAssigned = 2,

    /// <summary>全部重跑，含已處理完的：窗口內全部既有日，不過濾。</summary>
    All = 3
}
