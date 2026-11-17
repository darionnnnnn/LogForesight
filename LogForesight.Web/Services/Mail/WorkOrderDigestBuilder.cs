using System.Text;
using LogForesight.Core.Models;

namespace LogForesight.Web.Services.Mail;

/// <summary>單張單在寄送當下的資料（呼叫端從 store 讀好再傳入；單已刪除時傳 null）。</summary>
internal sealed record WorkOrderDigestOrder(long WorkOrderId, string IssueLabel, string? PlainExplanation, int HostCount,
    IReadOnlyList<string> HostNames, string? Note, DateTime? DueDate);

/// <summary>
/// 交辦摘要信的組信（純函數）：把同一位處理人的待寄項目組成一封信，分「新交辦／新增成員／移交／取消／退回／代為結案／靜音暫停／靜音恢復」八段，空段不輸出。
/// 單的內容（問題、主機、期限）由呼叫端在寄送當下讀好傳入，這裡不碰任何儲存。
/// </summary>
internal static class WorkOrderDigestBuilder
{
    /// <summary>主機名單最多列幾台，超過寫「…等 N 台」。</summary>
    private const int HostListLimit = 20;

    internal static (string SubjectSummary, string Body) Build(IReadOnlyList<(WorkOrderOutboxItem Item, WorkOrderDigestOrder? Order)> rows)
    {
        var created = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.Created).OrderBy(r => r.Item.WorkOrderId).ToList();
        var appended = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.Appended).OrderBy(r => r.Item.WorkOrderId).ToList();
        var transferred = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.Transferred).OrderBy(r => r.Item.WorkOrderId).ToList();
        var cancelled = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.Cancelled).OrderBy(r => r.Item.WorkOrderId).ToList();

        var returned = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.Returned).OrderBy(r => r.Item.WorkOrderId).ToList();

        var adminClosed = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.AdminClosed).OrderBy(r => r.Item.WorkOrderId).ToList();
        var mutePaused = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.MutePaused).OrderBy(r => r.Item.WorkOrderId).ToList();
        var muteResumed = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.MuteResumed).OrderBy(r => r.Item.WorkOrderId).ToList();

        var reminders = rows.Where(r => r.Item.Kind == WorkOrderOutboxKinds.AdminReminder).OrderBy(r => r.Item.EnqueuedAt).ToList();

        var subjectSummary = $"新交辦 {created.Count} 張、新增成員 {appended.Count} 張、移交 {transferred.Count}、取消 {cancelled.Count}、退回 {returned.Count}";
        // 新種類有才列，沒有時標題與既有寫法相同
        if (adminClosed.Count > 0) subjectSummary += $"、代為結案 {adminClosed.Count}";
        if (mutePaused.Count > 0) subjectSummary += $"、靜音暫停 {mutePaused.Count}";
        if (muteResumed.Count > 0) subjectSummary += $"、靜音恢復 {muteResumed.Count}";
        if (reminders.Count > 0) subjectSummary = rows.Count == reminders.Count ? "管理者提醒" : subjectSummary + "、管理者提醒";

        var body = new StringBuilder();
        AppendSection(body, "【新交辦】", created);
        AppendSection(body, "【新增成員】", appended);
        AppendSection(body, "【移交】", transferred);
        AppendSection(body, "【取消】", cancelled);
        AppendSection(body, "【退回】", returned);
        AppendSection(body, "【代為結案】", adminClosed);
        AppendSection(body, "【靜音暫停】", mutePaused);
        AppendSection(body, "【靜音恢復】", muteResumed);
        AppendReminders(body, reminders);
        if (rows.Count > reminders.Count) body.AppendLine("請至站台的「我的交辦」檢視上列單號。");
        return (subjectSummary, body.ToString());
    }

    /// <summary>管理者提醒：同一位收件人同一時段有多筆時只列最新一筆（內容是當時的整份彙整，後一筆涵蓋前一筆）</summary>
    private static void AppendReminders(StringBuilder body, List<(WorkOrderOutboxItem Item, WorkOrderDigestOrder? Order)> reminders)
    {
        if (reminders.Count == 0) return;
        var latest = reminders[^1].Item;
        body.AppendLine("【管理者提醒】");
        body.AppendLine($"  上報逾期未處理 {latest.ReminderEscalated} 個成員、本趟派不出去 {latest.ReminderUnassignable} 項、上報時沒有收件人 {latest.ReminderNoRecipient} 張單。");
        if (!string.IsNullOrWhiteSpace(latest.Detail))
        {
            foreach (var line in latest.Detail.Split('\n'))
                body.AppendLine("  " + line.TrimEnd('\r'));
        }
        body.AppendLine("  請至站台的「交辦總覽」檢視：上報中篩選、待派頁籤與對應的單。");
        body.AppendLine();
    }

    private static void AppendSection(StringBuilder body,
        string title, List<(WorkOrderOutboxItem Item, WorkOrderDigestOrder? Order)> rows)
    {
        if (rows.Count == 0) return;
        body.AppendLine(title);
        foreach (var (item, order) in rows)
        {
            if (order == null)
            {
                body.AppendLine($"  單號 {item.WorkOrderId}（已刪除）");
                continue;
            }

            switch (item.Kind)
            {
                case WorkOrderOutboxKinds.Transferred:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}");
                    body.AppendLine($"    由 {item.ActorAccount} 移交給其他處理人，你不需再處理這張單中移出的主機。");
                    AppendReason(body, item);
                    break;
                case WorkOrderOutboxKinds.Returned:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}，由 {item.ActorAccount} 退回：{item.Reason}");
                    break;
                case WorkOrderOutboxKinds.AdminClosed:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}");
                    body.AppendLine($"    由 {item.ActorAccount} 代為結案，結案狀態：{StatusText(item.Status)}。");
                    AppendReason(body, item);
                    body.AppendLine($"    {RedispatchText(item)}");
                    break;
                case WorkOrderOutboxKinds.MutePaused:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}");
                    body.AppendLine($"    由 {item.ActorAccount} 設定靜音{UntilText(item)}，這張單暫停處理，到期自動恢復。");
                    AppendReason(body, item);
                    break;
                case WorkOrderOutboxKinds.MuteResumed:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}");
                    body.AppendLine($"    由 {item.ActorAccount} 提前解除靜音，這張單已恢復，請繼續處理。");
                    break;
                case WorkOrderOutboxKinds.Cancelled:
                    body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}");
                    body.AppendLine($"    由 {item.ActorAccount} 取消，其中的主機已調回未處理。");
                    AppendReason(body, item);
                    break;
                default:
                    AppendOrderDetail(body, item, order);
                    break;
            }
        }
        body.AppendLine();
    }

    private static void AppendOrderDetail(StringBuilder body, WorkOrderOutboxItem item, WorkOrderDigestOrder order)
    {
        body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}（{order.HostCount} 台）");

        if (item.Kind == WorkOrderOutboxKinds.Appended)
        {
            var line = $"    新增 {item.MemberDelta} 台";
            if (item.RecurrenceMembers > 0) line += $"，其中 {item.RecurrenceMembers} 台是復發：你最近 30 天內修好過的主機又出現同一個問題";
            body.AppendLine(line);
        }
        else if (item.RecurrenceMembers > 0)
        {
            body.AppendLine($"    其中 {item.RecurrenceMembers} 台是復發：你最近 30 天內修好過的主機又出現同一個問題");
        }

        if (order.HostNames.Count > 0)
        {
            var hostList = string.Join("、", order.HostNames.Take(HostListLimit));
            // 以總台數判定：呼叫端可能只傳部分主機名，名單比總數少就要註明總數
            body.AppendLine(order.HostCount > Math.Min(order.HostNames.Count, HostListLimit)
                ? $"    主機：{hostList}…等 {order.HostCount} 台"
                : $"    主機：{hostList}");
        }
        if (!string.IsNullOrWhiteSpace(order.PlainExplanation)) body.AppendLine($"    說明：{order.PlainExplanation}");
        if (!string.IsNullOrWhiteSpace(order.Note)) body.AppendLine($"    交辦說明：{order.Note}");
        if (order.DueDate.HasValue) body.AppendLine($"    期限：{order.DueDate.Value:yyyy-MM-dd}");
    }

    private static string StatusText(string? status) => status switch
    {
        IssueHandlingStatuses.Resolved => "已處理",
        IssueHandlingStatuses.WontFix => "不處理",
        IssueHandlingStatuses.FalsePositive => "誤報",
        IssueHandlingStatuses.KnownNoise => "已知雜訊",
        _ => status ?? "已結案"
    };

    private static string UntilText(WorkOrderOutboxItem item) =>
        item.Until.HasValue ? $"至 {item.Until.Value:yyyy-MM-dd}" : "";

    /// <summary>問題再出現會不會重新派工：勾選「之後不再自動派工」＝不會；靜音造成的結案＝靜音期間不會、迄日後會；其餘會。</summary>
    private static string RedispatchText(WorkOrderOutboxItem item)
    {
        if (item.BlocksRedispatch) return "問題再出現時：不會重新派工（已勾選之後不再自動派工）。";
        if (item.Until.HasValue)
            return $"問題再出現時：靜音期間（至 {item.Until.Value:yyyy-MM-dd}）不會派工，靜音結束後再出現會重新派工。";
        return "問題再出現時：會重新派工。";
    }

    private static void AppendReason(StringBuilder body, WorkOrderOutboxItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Reason)) body.AppendLine($"    原因：{item.Reason}");
    }
}
