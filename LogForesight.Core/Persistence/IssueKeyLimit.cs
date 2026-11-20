using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Persistence;

/// <summary>
/// 問題鍵（issue_key）長度上限的唯一判定：案件（<c>lf_issue_cases</c>）與逐日處理列（<c>lf_issue_handling</c>）
/// 兩張表的 <c>HasMaxLength</c> 取較小者。問題鍵是比對鍵，截斷到中間會與其他問題撞鍵或永遠對不上，
/// 所以超長一律「不寫」而不是截斷；兩個 store 各自仍有同一條防線，這裡讓上游流程在寫任何衍生資料
/// （處理紀錄、成員數、事件）之前就排除該筆。
/// </summary>
internal static class IssueKeyLimit
{
    /// <summary>問題鍵長度上限；兩張表都沒設上限時為 <see cref="int.MaxValue"/></summary>
    public static int Max { get; } = new[]
    {
        ColumnClamp.For<IssueCaseRow>(nameof(IssueCaseRow.IssueKey)),
        ColumnClamp.For<IssueHandlingRow>(nameof(IssueHandlingRow.IssueKey))
    }.Where(m => m.HasValue).Select(m => m!.Value).DefaultIfEmpty(int.MaxValue).Min();

    /// <summary>鍵是否超過上限；超過時回傳該上限供訊息使用</summary>
    public static bool Exceeds(string issueKey, out int maxLength)
    {
        maxLength = Max;
        return issueKey.Length > maxLength;
    }
}
