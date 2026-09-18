using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>
/// 問題處理狀態驗證的唯一一份：詳情頁單筆／批次標記、依問題「回覆處理狀態」、交辦單回覆共用。
/// 規則改動只改這裡——各入口各寫一份會讓同一個狀態在不同頁面擋法不同。
/// </summary>
public static class IssueStatusValidation
{
    public static void Validate(string status, DateTime? dueDate, bool clearing, string? note)
    {
        if (!clearing && !IssueHandlingStatuses.IsValid(status))
            throw DomainException.Validation($"未知的問題處理狀態「{status}」。");

        if (status == IssueHandlingStatuses.InProgress && dueDate.HasValue && dueDate.Value.Date < DateTime.Today)
            throw DomainException.Validation("預計完成日不可早於今天。");

        // 無法處理必填原因（回饋十八輪批次G）：admin 收到上報通知要看得出「為什麼處理不了」
        // 才決定得了結案或改派——前端已標必填，這裡是防繞過的實際防線（同 wont_fix 的前端
        // 必填慣例，但上報多了「別人要據此做決定」的分量，值得後端也擋）
        if (status == IssueHandlingStatuses.Escalated && string.IsNullOrWhiteSpace(note))
            throw DomainException.Validation("標記為無法處理時必須填寫原因——管理者要據此決定結案或重新指派。");

        // 觀察中一定要有觀察至日期（docs/archive/FEEDBACK-8-PLAN.md #4）——沒有終點的「觀察」沒有意義，
        // 前端固定送「今天 + N 天」（1~90 天），這裡防禦性驗證同一個範圍，不只信前端
        if (status == IssueHandlingStatuses.Observing)
        {
            if (!dueDate.HasValue)
                throw DomainException.Validation("標記為觀察中時必須指定觀察至日期。");
            if (dueDate.Value.Date < DateTime.Today)
                throw DomainException.Validation("觀察至日期不可早於今天。");
            if (dueDate.Value.Date > DateTime.Today.AddDays(90))
                throw DomainException.Validation("觀察至日期不可超過 90 天。");
        }
    }
}
