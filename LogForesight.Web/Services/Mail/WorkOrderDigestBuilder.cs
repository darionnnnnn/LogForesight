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

    /// <param name="rows">待寄項目與寄送當下的單內容</param>
    /// <param name="siteBaseUrl">設定的站台網址；沒填（null／空白）時信件不含任何連結</param>
    /// <param name="recipientUserId">收件人的使用者編號，用來組「我的交辦」連結</param>
    internal static (string SubjectSummary, string Body) Build(IReadOnlyList<(WorkOrderOutboxItem Item, WorkOrderDigestOrder? Order)> rows,
        string? siteBaseUrl = null, long recipientUserId = 0)
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
        AppendReminders(body, reminders, siteBaseUrl);
        if (rows.Count > reminders.Count)
        {
            // 要處理人動作的種類（新交辦、新增成員、退回、靜音恢復）才請他去看；其餘（代為結案、取消、移交、靜音暫停）只是知會
            var needsAction = created.Count + appended.Count + returned.Count + muteResumed.Count > 0;
            if (needsAction)
            {
                if (created.Count > 0)
                    body.AppendLine("新交辦請回覆處理狀態：開啟單後依實際情形標示處理中、已處理或無法處理（無法處理請寫明原因）。");
                body.AppendLine($"請至站台的「{MailLinks.MyWorkOrdersName}」檢視上列單號。");
            }
            else
            {
                body.AppendLine("這些單不需要你再回覆。");
            }
            if (mutePaused.Count > 0)
                body.AppendLine($"暫停中的單可至站台的「{MailLinks.MyWorkOrdersName}」，勾選「只看暫停的單」查看。");
            // 取消通知也連到「我的交辦」：取消後主機已調回未處理，不連到已收回授與的單或主機
            AppendLink(body, MailLinks.Build(siteBaseUrl, MailLinks.MyWorkOrdersPath(recipientUserId)));
        }
        return (subjectSummary, body.ToString());
    }

    /// <summary>連結為 null（沒設站台網址）時什麼都不寫，信件與未設定時完全相同</summary>
    private static void AppendLink(StringBuilder body, string? link)
    {
        if (link != null) body.AppendLine($"連結：{link}");
    }

    /// <summary>
    /// 管理者提醒：同一位收件人同一時段可能有多筆（同一天摘要時刻前跑了不只一趟夜間派工）。每趟入列的是「本趟到期」的增量，
    /// 且入列當下就記為已提醒，所以只取最後一筆會讓前一趟那組永遠不出現在信裡；這裡把所有筆合併成一段（每人仍是一封）：
    /// 三項計數加總，明細依段落標題併段、同一行去重。
    /// </summary>
    private static void AppendReminders(StringBuilder body, List<(WorkOrderOutboxItem Item, WorkOrderDigestOrder? Order)> reminders,
        string? siteBaseUrl)
    {
        if (reminders.Count == 0) return;
        body.AppendLine("【管理者提醒】");
        // 有結構化明細的以（類別, 成員鍵）去重；只有文字的舊項目退回逐行合併
        var structured = reminders.Where(r => r.Item.ReminderItems is { Count: > 0 }).Select(r => r.Item).ToList();
        var legacy = reminders.Where(r => r.Item.ReminderItems is not { Count: > 0 }).Select(r => r.Item).ToList();
        var merged = MergeReminderItems(structured);
        int CountOf(string category) => merged.FirstOrDefault(m => m.Category == category)?.Count ?? 0;
        var escalatedCount = CountOf(ReminderDetailItem.CategoryEscalated) + legacy.Sum(i => i.ReminderEscalated);
        var unassignableCount = CountOf(ReminderDetailItem.CategoryUnassignable) + legacy.Sum(i => i.ReminderUnassignable);
        var noRecipientCount = CountOf(ReminderDetailItem.CategoryNoRecipient) + legacy.Sum(i => i.ReminderNoRecipient);
        body.AppendLine($"  上報逾期未處理 {escalatedCount} 個成員、本趟派不出去 {unassignableCount} 項、上報時沒有收件人 {noRecipientCount} 張單。");
        foreach (var section in merged)
        {
            body.AppendLine($"  {section.Heading}共 {section.Count} {section.Unit}");
            foreach (var line in section.Lines) body.AppendLine("    " + line);
            if (section.Count > section.Lines.Count) body.AppendLine($"    …等共 {section.Count} {section.Unit}");
        }
        var detail = MergeReminderDetails(legacy.Select(i => i.Detail));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            foreach (var line in detail.Split('\n'))
                body.AppendLine("  " + line.TrimEnd('\r'));
        }
        body.AppendLine($"  請至站台的「{MailLinks.WorkOrdersName}」檢視：上報中篩選、待派頁籤與對應的單。");
        if (MailLinks.Build(siteBaseUrl, MailLinks.WorkOrdersPath) is { } link) body.AppendLine($"  連結：{link}");
        body.AppendLine();
    }

    internal sealed record MergedReminderSection(string Category, string Heading, string Unit, int Count, List<string> Lines);

    /// <summary>
    /// 結構化明細合併：依（類別, 成員鍵）去重，同鍵保留最新入列的文字；每類計數＝去重後的項目數，
    /// 任一筆的該類總數更大（明細被截斷）時取最大總數。每類最多列 <see cref="AdminReminderService.MailLineLimit"/> 行。
    /// </summary>
    internal static List<MergedReminderSection> MergeReminderItems(IEnumerable<WorkOrderOutboxItem> items)
    {
        var order = new[] { ReminderDetailItem.CategoryEscalated, ReminderDetailItem.CategoryUnassignable, ReminderDetailItem.CategoryNoRecipient };
        var byKey = new Dictionary<(string, string), (ReminderDetailItem Item, int Seq)>();
        var maxTotal = new Dictionary<string, int>();
        var heading = new Dictionary<string, (string Heading, string Unit)>();
        var seq = 0;
        foreach (var outbox in items.OrderBy(i => i.EnqueuedAt))
        {
            foreach (var item in outbox.ReminderItems!)
            {
                var key = (item.Category, item.Key);
                byKey[key] = (item, byKey.TryGetValue(key, out var prior) ? prior.Seq : seq++);
                maxTotal[item.Category] = Math.Max(maxTotal.GetValueOrDefault(item.Category), item.Total);
                heading[item.Category] = (item.Heading, item.Unit);
            }
        }

        var result = new List<MergedReminderSection>();
        foreach (var category in order.Concat(byKey.Keys.Select(k => k.Item1).Except(order).Distinct()))
        {
            var rows = byKey.Where(kv => kv.Key.Item1 == category).Select(kv => kv.Value).OrderBy(v => v.Seq).ToList();
            if (rows.Count == 0) continue;
            var count = Math.Max(rows.Count, maxTotal.GetValueOrDefault(category));
            result.Add(new MergedReminderSection(category, heading[category].Heading, heading[category].Unit, count,
                rows.Take(AdminReminderService.MailLineLimit).Select(r => r.Item.Text).ToList()));
        }
        return result;
    }

    /// <summary>
    /// 合併多筆提醒的明細文字（只有文字的舊項目用；格式見 AdminReminderService.FormatMailDetail：「【標題】共 N 單位」後接縮排明細行，可能有「…等 N」截斷行）。
    /// 只有一筆時原文回傳；多筆時同標題併成一段，明細行去重，標題計數＝去重後的行數（段內有「…等」截斷行時行數不等於筆數，改用各段計數加總）。
    /// 去重只擋「完全相同的行」（同一趟派不出去清單在不同趟重複的情形）。
    /// </summary>
    internal static string? MergeReminderDetails(IEnumerable<string?> details)
    {
        var list = details.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (list.Count == 0) return null;
        if (list.Count == 1) return list[0];

        var sections = new List<(string Title, string Unit, int Count, List<string> Lines)>();
        foreach (var detail in list)
        {
            (string Title, string Unit, int Count, List<string> Lines)? current = null;
            foreach (var raw in detail!.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^(【[^】]*】)共 (\d+) (.+)$");
                if (m.Success)
                {
                    var title = m.Groups[1].Value;
                    var idx = sections.FindIndex(x => x.Title == title);
                    if (idx < 0)
                    {
                        sections.Add((title, m.Groups[3].Value, 0, new List<string>()));
                        idx = sections.Count - 1;
                    }
                    var sec = sections[idx];
                    sections[idx] = (sec.Title, sec.Unit, sec.Count + int.Parse(m.Groups[2].Value), sec.Lines);
                    current = sections[idx];
                    continue;
                }
                if (current == null)
                {
                    // 沒有標題的雜行：獨立成一段，原樣保留
                    sections.Add(("", "", 0, new List<string> { line }));
                    continue;
                }
                if (!current.Value.Lines.Contains(line)) current.Value.Lines.Add(line);
            }
        }

        var sb = new StringBuilder();
        foreach (var (title, unit, count, lines) in sections)
        {
            // 沒有截斷行時，去重後的行數就是實際筆數（同一行在不同趟重複出現不重複計）；有截斷行就只能用加總
            var truncated = lines.Any(l => l.TrimStart().StartsWith("…等", StringComparison.Ordinal));
            if (title.Length > 0) sb.AppendLine($"{title}共 {(truncated ? count : lines.Count)} {unit}");
            foreach (var l in lines) sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
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
                    body.AppendLine("    這些主機已改回處理中，請重新處理並回覆。");
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
                    body.AppendLine($"    由 {item.ActorAccount} 取消，其中的主機已調回未處理。問題若仍出現，夜間派工可能重新派工。");
                    AppendReason(body, item);
                    break;
                default:
                    AppendOrderDetail(body, item, order);
                    break;
            }
        }
        body.AppendLine();
    }

    internal const string NoActiveHostsText = "成員已結案、移出或主機已停用";

    private static void AppendOrderDetail(StringBuilder body, WorkOrderOutboxItem item, WorkOrderDigestOrder order)
    {
        // 台數只算進行中且未停用的主機：寄出前成員已全數結案、移出或主機已停用時，不寫「0 台」也不列主機行
        if (order.HostCount == 0)
        {
            body.AppendLine($"  單號 {order.WorkOrderId}：{order.IssueLabel}（{NoActiveHostsText}）");
            if (!string.IsNullOrWhiteSpace(order.Note)) body.AppendLine($"    交辦說明：{order.Note}");
            return;
        }

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
