namespace LogForesight.Core.Models;

/// <summary>
/// 問題負責人規則（↔ webdata blob，key=issue_owners，回饋十八輪批次F）：以 (Source, EventId)
/// 為鍵、跨主機生效——與「依問題」視角的分組鍵（Web 端 RecordListQueryService 等消費端的
/// GroupBy (Source, EventId)）完全一致。**不用 RuleId**：IssueCase／IssueHandling／
/// lf_top_issues 都沒有 RuleId 欄位，引入第二套鍵只會製造對不上的縫隙。
///
/// 語意比照主機負責人（<see cref="WebHost.OwnerUserIds"/>）但作用在問題層級、優先於主機負責人：
///   1. 自動帶入處理人：問題負責人恰一人時優先於主機負責人（見 Web 端 DayHandlingCommandService）。
///   2. 郵件通知路由：主機日內有問題命中規則時通知問題負責人，取代主機負責人
///      （見 Web 端 MailNotificationService.ResolvePerRecipient）。
///   3. 授權路徑（第四條）：問題負責人自動可見「保留期內出現過其負責問題的主機」
///      （見 Web 端 HostVisibilityResolver.GetIssueOwnedHostIds），並隱含 User 角色。
/// </summary>
public class IssueOwnerRule
{
    /// <summary>比對不分大小寫（見 IssueOwnerStore.Matches），OrdinalIgnoreCase——與
    /// MatchesSignature（IssueHandlingCommandService）等既有 Source 比對慣例一致。</summary>
    public string SourceName { get; set; } = string.Empty;

    public int EventId { get; set; }

    /// <summary>負責人（可多人，與主機負責人對稱）。</summary>
    public List<long> OwnerUserIds { get; set; } = new();

    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedByAccount { get; set; }
}
