using System.Globalization;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Time;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 管理者提醒（第 76-2 輪批次 C-2）：夜間派工結束後彙整三類，交辦總覽與管理者的交辦摘要信共用同一個彙整函式
/// <see cref="Compute"/>——不寫第二份計算。
/// <list type="number">
/// <item>上報（無法處理）超過 N 天（<see cref="SystemSettings.EscalationReminderDays"/>）仍沒人處理的成員，依案件上報時間（EscalatedAt，舊資料退回最後更新日）起算；</item>
/// <item>最近一趟夜間派工派不出去的問題（<see cref="MailNotifyState.LastUnassignable"/>，每趟合併）；</item>
/// <item>上報時沒有任何收件人的單（<see cref="MailNotificationService"/> 上報當下記下）。</item>
/// </list>
/// 信件走同一套每人摘要（通知佇列新種類 <see cref="WorkOrderOutboxKinds.AdminReminder"/>），收件人＝上報信的收件人規則
/// （admin 群組啟用中成員、有 email）。同一個成員每 N 天至多提醒一次，上次提醒日記在 <see cref="MailNotifyState.AdminReminderLastSent"/>；
/// N＝0 不寄提醒信。總覽顯示的數字不受郵件開關與 N 的間隔影響（郵件關閉時這是唯一管道）。
/// 通知永遠不能弄掛夜間流程：對外方法自行 try/catch 到底。
/// </summary>
public sealed class AdminReminderService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>總覽回應每類最多列幾筆明細（計數永遠是全部）</summary>
    internal const int OverviewItemLimit = 50;

    /// <summary>信件明細每類最多列幾行</summary>
    internal const int MailLineLimit = 20;

    /// <summary>派不出去的項目連續幾個日曆日沒再被觀察到就移除</summary>
    internal const int UnassignableUnobservedDays = 1;

    private const string UnassignableStateKey = "unassignable";
    private const int OrderPageSize = 100;
    private const int MemberPageSize = 200;

    private readonly ISystemSettingsStore _settings;
    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly MailNotifyStateStore _state;
    private readonly MailNotificationService _mail;
    private readonly IIssueExclusionSource? _exclusions;
    private readonly TimeProvider _clock;

    public AdminReminderService(
        ISystemSettingsStore settings, IWorkOrderStore orders, IIssueCaseStore cases, IHostStore hosts,
        IUserStore users, IUserGroupStore userGroups, MailNotifyStateStore state, MailNotificationService mail,
        IIssueExclusionSource? exclusions = null, TimeProvider? clock = null)
    {
        _settings = settings;
        _orders = orders;
        _cases = cases;
        _hosts = hosts;
        _users = users;
        _userGroups = userGroups;
        _state = state;
        _mail = mail;
        _exclusions = exclusions;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 彙整三類（唯一的計算點；交辦總覽端點與夜間入列共用）。純讀取、不改任何狀態。
    /// 逾期天數＝今天 − 案件最後更新日，大於 N 才算「超過 N 天」；N＝0（不提醒）時總覽仍以出廠預設天數判定逾期。
    /// </summary>
    public AdminReminderDto Compute() => ComputeResult().Dto;

    /// <summary>
    /// <see cref="Compute"/> 的完整結果：對外回應（<see cref="AdminReminderResult.Dto"/>）加上入列與組信用的內部彙整欄位。
    /// 目前靜音中的問題不列入三類（靜音期間不該提醒管理者處理它；派不出去清單是上一趟夜間的快照，靜音後才設的問題也要排除）。
    /// </summary>
    internal AdminReminderResult ComputeResult()
    {
        var settings = _settings.Get();
        var today = _clock.LocalToday();
        var state = _state.Get();
        var days = settings.EscalationReminderDays;
        var threshold = days > 0 ? days : SystemSettings.DefaultEscalationReminderDays;

        var dto = new AdminReminderDto { ReminderDays = days, ThresholdDays = threshold, UnassignableAt = state.LastUnassignableAt };
        var exclusion = _exclusions?.Current() ?? IssueExclusion.None;

        var excluded = WorkOrderQueries.DisabledHostKeys(_hosts.GetAll());
        var escalated = new List<EscalatedEntry>();
        var overdueOrderIds = new HashSet<long>();
        for (var page = 1; ; page++)
        {
            var orders = _orders.QueryOrders(new WorkOrderQuery
            {
                Status = WorkOrderQueries.StatusEscalated, Sort = WorkOrderQueries.SortCreatedDesc,
                Page = page, PageSize = OrderPageSize, ExcludedHostKeys = excluded
            });
            foreach (var order in orders.Items)
            {
                for (var mPage = 1; ; mPage++)
                {
                    var members = _cases.QueryMembers(new WorkOrderMemberQuery
                    {
                        WorkOrderId = order.WorkOrderId, Status = WorkOrderQueries.StatusEscalated,
                        HostNameKeys = null, Page = mPage, PageSize = MemberPageSize
                    });
                    foreach (var c in members.Items)
                    {
                        if (c.ClosedAt != null || excluded.Contains(Core.Persistence.Sql.HostNameKey.Of(c.HostName))) continue;
                        if (c.SourceName != null && c.EventId is int caseEvent && exclusion.IsCurrentlyMuted(c.SourceName, caseEvent)) continue;
                        // 上報起算日：EscalatedAt（轉進 escalated 的時間，夜間掛接不動它）；升級前的舊資料為 null，退回最後更新日
                        var elapsed = (int)(today - (c.EscalatedAt ?? c.UpdatedAt).Date).TotalDays;
                        if (elapsed <= threshold) continue;
                        overdueOrderIds.Add(order.WorkOrderId);
                        escalated.Add(new EscalatedEntry(c.CaseId, order.WorkOrderId, order.IssueLabel, c.HostName, elapsed,
                            days > 0 && IsDue(state, c.CaseId, today, days)));
                    }
                    if (members.Items.Count < MemberPageSize || mPage * MemberPageSize >= members.Total) break;
                }
            }
            if (orders.Items.Count < OrderPageSize || page * OrderPageSize >= orders.Total) break;
        }

        var noRecipient = new List<NoRecipientEntry>();
        foreach (var entry in state.NoRecipientEscalations.OrderBy(e => e.At).ThenBy(e => e.WorkOrderId))
        {
            var order = _orders.Get(entry.WorkOrderId);
            if (order == null || order.ClosedAt != null) continue;
            if (order.SourceName != null && order.EventId is int orderEvent && exclusion.IsCurrentlyMuted(order.SourceName, orderEvent)) continue;
            var open = _cases.QueryMembers(new WorkOrderMemberQuery
            {
                WorkOrderId = order.WorkOrderId, Status = WorkOrderQueries.StatusEscalated, Page = 1, PageSize = 1
            });
            if (open.Total == 0) continue;
            noRecipient.Add(new NoRecipientEntry(order.WorkOrderId, order.IssueLabel,
                days > 0 && IsDue(state, NoRecipientKey(order.WorkOrderId), today, days)));
        }

        escalated = escalated.OrderByDescending(e => e.Days).ThenBy(e => e.WorkOrderId).ThenBy(e => e.HostName, StringComparer.Ordinal).ToList();
        var unassignable = state.LastUnassignable
            .Where(u => !IsLabelMuted(exclusion, u.IssueLabel))
            .Select(u => new AdminReminderUnassignableDto { IssueLabel = u.IssueLabel, Reason = u.Reason, Hosts = u.Hosts })
            .ToList();

        dto.EscalatedCount = escalated.Count;
        dto.UnassignableCount = unassignable.Count;
        dto.NoRecipientCount = noRecipient.Count;
        dto.Escalated = escalated.Take(OverviewItemLimit)
            .Select(e => new AdminReminderEscalatedDto { WorkOrderId = e.WorkOrderId, IssueLabel = e.IssueLabel, HostName = e.HostName, Days = e.Days })
            .ToList();
        dto.Unassignable = unassignable.Take(OverviewItemLimit).ToList();
        dto.NoRecipient = noRecipient.Take(OverviewItemLimit)
            .Select(e => new AdminReminderNoRecipientDto { WorkOrderId = e.WorkOrderId, IssueLabel = e.IssueLabel })
            .ToList();
        return new AdminReminderResult(dto, escalated, noRecipient)
        {
            UnassignableDueForMail = days > 0 && unassignable.Count > 0
                && state.AdminReminderLastSent.GetValueOrDefault(UnassignableStateKey) != DateKey(today)
        };
    }

    /// <summary>
    /// 派不出去清單只存標籤「Source EventId」（派工端組字串的格式）：拆出事件編號與來源，走 <see cref="IssueExclusion"/> 同一條靜音判定。
    /// 標籤不是這個格式時視為不靜音。
    /// </summary>
    private static bool IsLabelMuted(IssueExclusion exclusion, string label)
    {
        var cut = label.LastIndexOf(' ');
        return cut > 0 && int.TryParse(label[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId)
            && exclusion.IsCurrentlyMuted(label[..cut], eventId);
    }

    /// <summary>
    /// 夜間派工結束後呼叫（排程與情境驅動器共用同一個入口）：先把本趟派不出去的問題合併進清單，再彙整、入列給管理者。
    /// 合併以（標籤, 原因）為鍵：本趟觀察到的寫入或更新，沒觀察到的保留；任何範圍的趟（全量、只跑部分主機、續跑、複查）都適用，
    /// 連續兩個日曆日的執行都沒再觀察到的項目才移除（<see cref="UnassignableUnobservedDays"/>）。
    /// <paramref name="summary"/> 為 null（這趟沒有派工結果）時不動派不出去的清單，其餘照常。
    /// </summary>
    public void RunAfterNightly(NightlyDispatchSummary? summary)
    {
        try
        {
            var today = _clock.LocalToday();
            if (summary != null)
            {
                var now = _clock.LocalNow();
                _state.Update(s =>
                {
                    s.LastUnassignable = MergeUnassignable(s.LastUnassignable, s.LastUnassignableAt, summary.Unassignable, now);
                    s.LastUnassignableAt = now;
                });
            }

            var settings = _settings.Get();
            var result = ComputeResult();
            var dto = result.Dto;
            var mailUnassignable = result.UnassignableDueForMail ? dto.UnassignableCount : 0;
            var mailTotal = result.MailEscalatedCount + mailUnassignable + result.MailNoRecipientCount;

            var recipients = new List<long>();
            if (settings.EscalationReminderDays > 0 && settings.MailEnabled && settings.MailNotifyWorkOrders && mailTotal > 0)
            {
                recipients = AdminMembersResolver.GetAdminMembers(_userGroups, _users)
                    .Where(u => MailNotificationService.EligibleEmailOrWarn(u, "管理者提醒") != null)
                    .Select(u => u.UserId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
            }

            var queued = recipients.Count > 0 &&
                         _mail.EnqueueAdminReminders(recipients, result.MailEscalatedCount, mailUnassignable, result.MailNoRecipientCount,
                             FormatMailDetail(result, mailUnassignable > 0), BuildMailItems(result, mailUnassignable > 0));

            _state.Update(s =>
            {
                var todayKey = DateKey(today);
                if (queued)
                {
                    foreach (var key in result.MailEscalatedKeys.Concat(result.MailNoRecipientKeys)) s.AdminReminderLastSent[key] = todayKey;
                    if (mailUnassignable > 0) s.AdminReminderLastSent[UnassignableStateKey] = todayKey;
                }

                // 不再逾期（已處理、已結案）的鍵清掉；本趟提醒的鍵一定在目前逾期集合內
                var live = result.EscalatedAll.Select(e => e.CaseId)
                    .Concat(result.NoRecipientAll.Select(e => NoRecipientKey(e.WorkOrderId)))
                    .Append(UnassignableStateKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var stale in s.AdminReminderLastSent.Keys.Where(k => !live.Contains(k)).ToList())
                    s.AdminReminderLastSent.Remove(stale);

                // 已不在彙整結果內的「沒有收件人」記錄一併清掉（單結案或上報已處理）
                var noRecipientLive = result.NoRecipientAll.Select(e => e.WorkOrderId).ToHashSet();
                s.NoRecipientEscalations.RemoveAll(e => !noRecipientLive.Contains(e.WorkOrderId));
            });
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[Mail] 管理者提醒彙整失敗（不影響夜間派工本身）");
        }
    }

    /// <summary>
    /// 合併派不出去清單：本趟觀察到的以（標籤, 原因）為鍵寫入或更新（同一個日曆日已有觀察時台數取較大者，部分趟不把台數蓋小），
    /// 沒觀察到的保留；觀察日早於（今天 − <see cref="UnassignableUnobservedDays"/>）的移除。舊資料的 ObservedAt 為 null，退回 <paramref name="lastAt"/>。
    /// </summary>
    internal static List<UnassignableIssue> MergeUnassignable(
        IEnumerable<UnassignableIssue> existing, DateTime? lastAt, IEnumerable<NightlyUnassignableIssue> observed, DateTime now)
    {
        var merged = new Dictionary<(string, string), UnassignableIssue>();
        foreach (var e in existing)
            merged[(e.IssueLabel, e.Reason)] = new UnassignableIssue
            {
                IssueLabel = e.IssueLabel, Reason = e.Reason, Hosts = e.Hosts, ObservedAt = e.ObservedAt ?? lastAt
            };
        foreach (var o in observed)
        {
            var key = (o.IssueLabel, o.Reason);
            var hosts = merged.TryGetValue(key, out var prior) && prior.ObservedAt?.Date == now.Date
                ? Math.Max(prior.Hosts, o.Hosts)
                : o.Hosts;
            merged[key] = new UnassignableIssue { IssueLabel = o.IssueLabel, Reason = o.Reason, Hosts = hosts, ObservedAt = now };
        }
        var cutoff = now.Date.AddDays(-UnassignableUnobservedDays);
        // 沒有任何時間可依據（ObservedAt 與 lastAt 都是 null）的項目保留，下一趟寫入時才有觀察時間
        return merged.Values.Where(u => u.ObservedAt == null || u.ObservedAt.Value.Date >= cutoff).ToList();
    }

    private static bool IsDue(MailNotifyState state, string key, DateTime today, int days)
    {
        if (!state.AdminReminderLastSent.TryGetValue(key, out var raw)) return true;
        if (!DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)) return true;
        return (today - last.Date).TotalDays >= days;
    }

    internal static string NoRecipientKey(long workOrderId) => $"wo:{workOrderId}";

    private static string DateKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>派不出去的原因白話（與待派頁的原因同義）</summary>
    internal static string ReasonText(string reason) => reason switch
    {
        WorkOrderDispatcher.NoCandidateNoPool => "派工池沒有人",
        WorkOrderDispatcher.NoCandidateAllPaused => "派工池的人全部暫停接單",
        WorkOrderDispatcher.NoCandidateNoVisibility => "池內的人都看不到這些主機",
        _ => reason
    };

    /// <summary>信件內文：只列「這次要提醒」的明細（逾期成員與沒有收件人的單依間隔過濾，派不出去的問題整份列）</summary>
    internal static List<ReminderDetailItem> BuildMailItems(AdminReminderResult result, bool includeUnassignable)
    {
        var dto = result.Dto;
        var items = new List<ReminderDetailItem>();
        var escalated = result.EscalatedAll.Where(e => e.DueForMail).ToList();
        items.AddRange(escalated.Take(MailLineLimit).Select(e => new ReminderDetailItem
        {
            Category = ReminderDetailItem.CategoryEscalated, Key = e.CaseId, Total = escalated.Count,
            Text = $"單號 {e.WorkOrderId}：{e.IssueLabel}（{e.HostName}），已上報 {e.Days} 天",
            Heading = $"【上報後逾 {dto.ReminderDays} 天沒人處理】", Unit = "個成員"
        }));
        if (includeUnassignable && dto.UnassignableCount > 0)
            items.AddRange(dto.Unassignable.Take(MailLineLimit).Select(u => new ReminderDetailItem
            {
                Category = ReminderDetailItem.CategoryUnassignable, Key = $"{u.IssueLabel}|{u.Reason}", Total = dto.UnassignableCount,
                Text = $"{u.IssueLabel}：{ReasonText(u.Reason)}（{u.Hosts} 台）",
                Heading = "【本趟派不出去的問題】", Unit = "項"
            }));
        var noRecipient = result.NoRecipientAll.Where(e => e.DueForMail).ToList();
        items.AddRange(noRecipient.Take(MailLineLimit).Select(n => new ReminderDetailItem
        {
            Category = ReminderDetailItem.CategoryNoRecipient, Key = NoRecipientKey(n.WorkOrderId), Total = noRecipient.Count,
            Text = $"單號 {n.WorkOrderId}：{n.IssueLabel}（上報信寄不出去，請確認管理者的 email）",
            Heading = "【上報時沒有任何收件人的單】", Unit = "張"
        }));
        return items;
    }

    internal static string FormatMailDetail(AdminReminderResult result, bool includeUnassignable)
    {
        var dto = result.Dto;
        var sb = new StringBuilder();
        var escalated = result.EscalatedAll.Where(e => e.DueForMail).ToList();
        if (escalated.Count > 0)
        {
            sb.AppendLine($"【上報後逾 {dto.ReminderDays} 天沒人處理】共 {escalated.Count} 個成員");
            foreach (var e in escalated.Take(MailLineLimit))
                sb.AppendLine($"  單號 {e.WorkOrderId}：{e.IssueLabel}（{e.HostName}），已上報 {e.Days} 天");
            if (escalated.Count > MailLineLimit) sb.AppendLine($"  …等 {escalated.Count} 個成員");
        }
        if (includeUnassignable && dto.UnassignableCount > 0)
        {
            sb.AppendLine($"【本趟派不出去的問題】共 {dto.UnassignableCount} 項");
            foreach (var u in dto.Unassignable.Take(MailLineLimit))
                sb.AppendLine($"  {u.IssueLabel}：{ReasonText(u.Reason)}（{u.Hosts} 台）");
            if (dto.UnassignableCount > MailLineLimit) sb.AppendLine($"  …等 {dto.UnassignableCount} 項");
        }
        var noRecipient = result.NoRecipientAll.Where(e => e.DueForMail).ToList();
        if (noRecipient.Count > 0)
        {
            sb.AppendLine($"【上報時沒有任何收件人的單】共 {noRecipient.Count} 張");
            foreach (var n in noRecipient.Take(MailLineLimit))
                sb.AppendLine($"  單號 {n.WorkOrderId}：{n.IssueLabel}（上報信寄不出去，請確認管理者的 email）");
            if (noRecipient.Count > MailLineLimit) sb.AppendLine($"  …等 {noRecipient.Count} 張");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>上報逾期的成員（內部彙整：含案件編號與「本趟要不要提醒」，不外送）</summary>
internal sealed record EscalatedEntry(string CaseId, long WorkOrderId, string IssueLabel, string HostName, int Days, bool DueForMail);

/// <summary>上報時沒有收件人的單（內部彙整：含「本趟要不要提醒」，不外送）</summary>
internal sealed record NoRecipientEntry(long WorkOrderId, string IssueLabel, bool DueForMail);

/// <summary>
/// <see cref="AdminReminderService.ComputeResult"/> 的結果：對外回應 <see cref="Dto"/> 加上入列與組信用的內部彙整。
/// 對外 DTO 只留回應欄位，內部欄位不靠序列化屬性遮蔽。
/// </summary>
internal sealed class AdminReminderResult
{
    public AdminReminderResult(AdminReminderDto dto, List<EscalatedEntry> escalatedAll, List<NoRecipientEntry> noRecipientAll)
    {
        Dto = dto;
        EscalatedAll = escalatedAll;
        NoRecipientAll = noRecipientAll;
    }

    public AdminReminderDto Dto { get; }
    public List<EscalatedEntry> EscalatedAll { get; }
    public List<NoRecipientEntry> NoRecipientAll { get; }

    /// <summary>派不出去清單這趟要不要提醒（間隔天數已開、清單非空、今天還沒提醒過）</summary>
    public bool UnassignableDueForMail { get; init; }

    public int MailEscalatedCount => EscalatedAll.Count(e => e.DueForMail);
    public int MailNoRecipientCount => NoRecipientAll.Count(e => e.DueForMail);
    public List<string> MailEscalatedKeys => EscalatedAll.Where(e => e.DueForMail).Select(e => e.CaseId).ToList();
    public List<string> MailNoRecipientKeys => NoRecipientAll.Where(e => e.DueForMail).Select(e => AdminReminderService.NoRecipientKey(e.WorkOrderId)).ToList();
}
