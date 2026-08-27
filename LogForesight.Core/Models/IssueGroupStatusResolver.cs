namespace LogForesight.Core.Models;

/// <summary>單一主機對單一問題的處理概況三態</summary>
public enum HostIssueStatus
{
    /// <summary>未處理——需要有人接手</summary>
    Open,

    /// <summary>處理中——案件進行中，或問題層級標記為 in_progress／escalated／觀察中未到期</summary>
    Processing,

    /// <summary>已處理——問題層級標記為結案類，或未標記且嚴重度不在「需處理」名單內</summary>
    Resolved
}

/// <summary>
/// 單一（主機, 問題）的處理概況判定（回饋十九輪批次E0）：規則單點化，供依問題視角
/// （<c>RecordListQueryService.BuildIssueGroup</c>）與問題排行卡的處理概況彙總
/// （<c>IssueHandlingRollupQuery</c>）共用——兩處過去各自實作同一套規則（分別在
/// blob 路徑與 SQL 路徑各寫一份），是外部審查點名「三個畫面數字對不起來」那類缺陷的
/// 溫床：改一邊忘了改另一邊，兩個畫面就會對同一筆資料給出不同的處理概況。
///
/// 優先序：案件優先於逐日標記（案件是跨日的協調權威）；沒有案件時看最近一次出現當天的
/// 問題層級標記；都沒有時依嚴重度是否落在「不處理」名單內預設。
/// </summary>
public static class IssueGroupStatusResolver
{
    /// <param name="openCase">該主機該問題目前的進行中案件，沒有則傳 null</param>
    /// <param name="latestHandling">最近一次出現當天的問題層級標記，未標記則傳 null</param>
    /// <param name="latestSeverity">最近一次出現當天的問題嚴重度（未標記時的預設判斷依據）</param>
    /// <param name="unhandledSeverities">「需要處理」的嚴重度集合（來自 SystemSettings）</param>
    /// <param name="today">今天（呼叫端決定基準日，通常是分析錨點——見 §批次C）</param>
    public static HostIssueStatus Resolve(
        IssueCase? openCase, IssueHandling? latestHandling, IssueSeverity latestSeverity,
        IReadOnlySet<IssueSeverity> unhandledSeverities, DateTime today)
    {
        if (openCase != null)
        {
            // 觀察到期：問題仍在發生，計入未處理；否則案件進行中，既非未處理也非已處理
            return IssueHandlingStatuses.IsObservationExpired(openCase.Status, openCase.DueDate, today)
                ? HostIssueStatus.Open
                : HostIssueStatus.Processing;
        }

        if (latestHandling != null && IssueHandlingStatuses.IsClosed(latestHandling.Status)) return HostIssueStatus.Resolved;
        if (latestHandling != null && latestHandling.Status is IssueHandlingStatuses.InProgress or IssueHandlingStatuses.Escalated) return HostIssueStatus.Processing;
        if (latestHandling != null && IssueHandlingStatuses.IsObservationActive(latestHandling.Status, latestHandling.DueDate, today)) return HostIssueStatus.Processing;
        if (latestHandling != null && IssueHandlingStatuses.IsObservationExpired(latestHandling.Status, latestHandling.DueDate, today)) return HostIssueStatus.Open;
        if (!unhandledSeverities.Contains(latestSeverity)) return HostIssueStatus.Resolved;
        return HostIssueStatus.Open;
    }
}
