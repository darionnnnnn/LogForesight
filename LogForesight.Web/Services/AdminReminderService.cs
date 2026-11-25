using System.Globalization;
using System.Text;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Time;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 管理者提醒（第 76-2 輪批次 C-2）：夜間派工結束後彙整四類，交辦總覽與管理者的交辦摘要信共用同一個彙整函式
/// <see cref="Compute"/>——不寫第二份計算。
/// <list type="number">
/// <item>上報（無法處理）超過 N 天（<see cref="SystemSettings.EscalationReminderDays"/>）仍沒人處理的成員，依案件上報時間（EscalatedAt，舊資料退回最後更新日）起算；</item>
/// <item>最近一趟夜間派工派不出去的問題（<see cref="MailNotifyState.LastUnassignable"/>，每趟合併）；</item>
/// <item>上報時沒有任何收件人的單（<see cref="MailNotificationService"/> 上報當下記下）；</item>
/// <item>反覆結案又復發的進行中成員：復發次數（<see cref="IssueRecurrence"/>）達 <see cref="RecurrenceReminderThreshold"/>，
/// 同一成員的去重沿用上面的「每 N 天至多提醒一次」機制（鍵 <see cref="RecurrenceKey"/>）。</item>
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

    /// <summary>「反覆結案又復發」的門檻（暫定）：進行中成員的復發次數 N 達到這個值就列入</summary>
    internal const int RecurrenceReminderThreshold = 3;

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
    private readonly IKnownIssueRuleStore _rules;
    private readonly TimeProvider _clock;
    private readonly SchedulerRunState? _runState;
    private readonly SummaryCache? _cache;
    private readonly IUserDisplayNameService? _displayNames;

    /// <summary>處理能力解析（與交辦單清單的處理人篩選共用 <see cref="HandlerInactivity"/>）；null＝不看處理能力</summary>
    private readonly UserCapabilityResolver? _capabilities;

    /// <param name="runState">最近一趟取數結果的來源（昨晚取數結果類別）；null＝該類別恆為 0</param>
    /// <param name="cache">
    /// 跨請求快取（<see cref="SummaryCache"/>，繼承 <see cref="BoundedTtlCache{T}"/>）：<see cref="Compute"/> 的彙整每次開儀表板或交辦總覽都會跑，
    /// 含全站進行中成員掃描，以快取取得；null＝每次重算（測試）。夜間入列一律重算、不走快取。
    /// </param>
    public AdminReminderService(
        ISystemSettingsStore settings, IWorkOrderStore orders, IIssueCaseStore cases, IHostStore hosts,
        IUserStore users, IUserGroupStore userGroups, MailNotifyStateStore state, MailNotificationService mail,
        IKnownIssueRuleStore rules, IIssueExclusionSource? exclusions = null, TimeProvider? clock = null,
        SchedulerRunState? runState = null, SummaryCache? cache = null, IUserDisplayNameService? displayNames = null,
        UserCapabilityResolver? capabilities = null)
    {
        _capabilities = capabilities;
        _runState = runState;
        _cache = cache;
        _displayNames = displayNames;
        _settings = settings;
        _orders = orders;
        _cases = cases;
        _hosts = hosts;
        _users = users;
        _userGroups = userGroups;
        _state = state;
        _mail = mail;
        _rules = rules;
        _exclusions = exclusions;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 管理者提醒與待辦的彙整（唯一的計算點；站內提醒區、儀表板「需要你處理」列與夜間摘要信共用）。純讀取、不改任何狀態。
    /// 逾期天數＝今天 − 案件最後更新日，大於 N 才算「超過 N 天」；N＝0（不提醒）時總覽仍以出廠預設天數判定逾期。
    /// <para>
    /// <paramref name="includeTodo"/>：呼叫者具 Assign 時為 true，才帶出待辦新增的四類（昨晚取數結果、可派未派、處理人已停用或暫停仍有單、未分組主機）；
    /// Manager、Dev 傳 false，那四類為 0、<see cref="AdminReminderDto.TodoIncluded"/> 為 false。
    /// <paramref name="pending"/>：待派試跑的可派問題數（呼叫端有使用者範圍才有；null＝可派未派不計）。
    /// </para>
    /// <para>
    /// 彙整本身與使用者無關（全站口徑，不依可見範圍），所以快取鍵不含主機集合（以空集合組鍵）；
    /// 使用者相關的只有 includeTodo 與 pending，在快取之外以複本疊上。快取鍵含資料版本戳並保留短 TTL。
    /// </para>
    /// </summary>
    public AdminReminderDto Compute(bool includeTodo = false, PendingDispatchCounts? pending = null)
    {
        var result = _cache != null
            ? _cache.GetOrAdd(SummaryCache.KeyOf("admin-reminders", CacheParameters(), Array.Empty<long>()), ComputeResult)
            : ComputeResult();
        return result.Dto.ForCaller(includeTodo, pending);
    }

    /// <summary>
    /// 快取鍵的參數段：靜音版本（<see cref="IssueExclusion.CacheToken"/>）與今天的日期。三類提醒都排除目前靜音中的問題，
    /// 靜音一變、鍵就換；逾期天數以今天計，跨日也不能沿用前一天的結果。資料版本戳與短 TTL 仍由 <see cref="SummaryCache"/> 負責。
    /// </summary>
    private string CacheParameters()
    {
        var exclusion = _exclusions?.Current() ?? IssueExclusion.None;
        return $"global|{exclusion.CacheToken}|{_clock.LocalToday():yyyyMMdd}";
    }

    /// <summary>
    /// <see cref="Compute"/> 的完整結果：對外回應（<see cref="AdminReminderResult.Dto"/>）加上入列與組信用的內部彙整欄位。
    /// 目前靜音中的問題不列入三類（靜音期間不該提醒管理者處理它；派不出去清單是上一趟夜間的快照，靜音後才設的問題也要排除）。
    /// </summary>
    internal AdminReminderResult ComputeResult()
    {
        var settings = _settings.Get();
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var today = _clock.LocalToday();
        var state = _state.Get();
        var days = settings.EscalationReminderDays;
        var threshold = days > 0 ? days : SystemSettings.DefaultEscalationReminderDays;

        var dto = new AdminReminderDto { ReminderDays = days, ThresholdDays = threshold, UnassignableAt = state.LastUnassignableAt };
        var exclusion = _exclusions?.Current() ?? IssueExclusion.None;

        var excluded = WorkOrderQueries.DisabledHostKeys(_hosts.GetAll());
        var escalated = new List<EscalatedEntry>();
        var newToday = new List<EscalatedEntry>();
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
                        var escalatedDay = (c.EscalatedAt ?? c.UpdatedAt).Date;
                        // 今天新上報：與「超過 N 天」分開計數與列出（逾期類只認 elapsed > threshold，今天上報的 elapsed 為 0 不會重複）
                        if (escalatedDay == today)
                            newToday.Add(new EscalatedEntry(c.CaseId, order.WorkOrderId, order.DisplayLabel(rules), c.HostName, 0, false));
                        var elapsed = (int)(today - escalatedDay).TotalDays;
                        if (elapsed <= threshold) continue;
                        overdueOrderIds.Add(order.WorkOrderId);
                        escalated.Add(new EscalatedEntry(c.CaseId, order.WorkOrderId, order.DisplayLabel(rules), c.HostName, elapsed,
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
            noRecipient.Add(new NoRecipientEntry(order.WorkOrderId, order.DisplayLabel(rules),
                days > 0 && IsDue(state, NoRecipientKey(order.WorkOrderId), today, days)));
        }

        var recurrence = ScanRecurrence(exclusion, excluded, state, today, days, rules);

        escalated = escalated.OrderByDescending(e => e.Days).ThenBy(e => e.WorkOrderId).ThenBy(e => e.HostName, StringComparer.Ordinal).ToList();
        var unassignable = state.LastUnassignable
            .Where(u => !IsMuted(exclusion, u))
            .Select(u => new AdminReminderUnassignableDto { IssueLabel = IssueNames.Display(u.Source, u.EventId, u.IssueLabel, rules), Reason = u.Reason, Hosts = u.Hosts })
            .ToList();

        newToday = newToday.OrderBy(e => e.WorkOrderId).ThenBy(e => e.HostName, StringComparer.Ordinal).ToList();
        dto.NewEscalatedCount = newToday.Count;
        dto.NewEscalated = newToday.Take(OverviewItemLimit)
            .Select(e => new AdminReminderEscalatedDto { WorkOrderId = e.WorkOrderId, IssueLabel = e.IssueLabel, HostName = e.HostName, Days = 0 })
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
        dto.RecurrenceCount = recurrence.Count;
        dto.Recurrence = recurrence.Take(OverviewItemLimit)
            .Select(e => new AdminReminderRecurrenceDto
                { WorkOrderId = e.WorkOrderId, IssueLabel = e.IssueLabel, HostName = e.HostName, RecurrenceCount = e.Count })
            .ToList();
        var todo = new List<TodoEntry>();
        BuildTodoCategories(dto, todo, state, today, days, excluded);
        return new AdminReminderResult(dto, escalated, noRecipient, recurrence)
        {
            TodoAll = todo,
            UnassignableDueForMail = days > 0 && unassignable.Count > 0
                && state.AdminReminderLastSent.GetValueOrDefault(UnassignableStateKey) != DateKey(today)
        };
    }

    /// <summary>
    /// 待辦新增的三類（與使用者無關的部分；可派未派在 <see cref="AdminReminderDto.ForCaller"/> 疊上）。同時產生信件用的 <see cref="TodoEntry"/>，
    /// 去重鍵沿用 <see cref="MailNotifyState.AdminReminderLastSent"/> 與提醒間隔（<see cref="IsDue"/>）。
    /// </summary>
    private void BuildTodoCategories(AdminReminderDto dto, List<TodoEntry> todo, MailNotifyState state, DateTime today, int days,
        IReadOnlyCollection<string> excludedHostKeys)
    {
        // ① 最近一趟取數失敗：與健康頁「最近一趟分析執行失敗」同一個來源
        if (_runState?.Snapshot().LastOutcome is { Success: false } failed)
        {
            dto.FetchFailedCount = 1;
            dto.FetchFailed = new AdminReminderFetchDto { EndedAt = failed.EndedAt, Trigger = failed.Trigger };
            var key = FetchKey(failed.EndedAt);
            todo.Add(new TodoEntry(ReminderDetailItem.CategoryFetchFailed, key, "【最近一趟取數失敗】", "趟", 1,
                $"{failed.EndedAt:MM-dd HH:mm} 結束的取數沒有成功，請到「執行紀錄」查看原因",
                days > 0 && IsDue(state, key, today, days)));
        }

        // ② 處理人已停用或暫停接單、名下仍有進行中單
        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var inactive = new List<AdminReminderInactiveHandlerDto>();
        var reasons = new Dictionary<long, string>();
        foreach (var load in _orders.LoadBoard(excludedHostKeys).Where(l => l.ActiveWorkOrders > 0).OrderBy(l => l.HandlerId))
        {
            // 使用者已刪除（查無帳號）的單不在這一類：判定只看帳號的停用與暫停旗標
            if (!users.TryGetValue(load.HandlerId, out var user)) continue;
            if (!HandlerInactivity.IsInactive(user, _capabilities)) continue;
            var disabled = !user.Active;
            reasons[load.HandlerId] = user.DispatchPaused ? "暫停接單" : "已無處理權限";
            inactive.Add(new AdminReminderInactiveHandlerDto
            {
                UserId = load.HandlerId,
                Name = _displayNames != null ? NameFormat.UserLabel(_displayNames, user) : user.Account,
                Disabled = disabled,
                ActiveWorkOrders = load.ActiveWorkOrders,
                ActiveMembers = load.ActiveMembers
            });
        }
        dto.InactiveHandlerCount = inactive.Count;
        dto.InactiveHandlers = inactive.Take(OverviewItemLimit).ToList();
        foreach (var h in inactive)
        {
            var key = $"handler:{h.UserId}";
            todo.Add(new TodoEntry(ReminderDetailItem.CategoryInactiveHandler, key, "【處理人已停用或暫停接單、名下仍有單】", "位", inactive.Count,
                $"{h.Name}（{(h.Disabled ? "已停用" : reasons.GetValueOrDefault(h.UserId, "暫停接單"))}）名下還有 {h.ActiveWorkOrders} 張單、{h.ActiveMembers} 台，請改派",
                days > 0 && IsDue(state, key, today, days)));
        }

        // ③ 未分組主機（一般使用者看不到）
        var ungrouped = NetiqHostList.Ungrouped(_hosts.GetAll()).Count();
        dto.UngroupedHostCount = ungrouped;
        if (ungrouped > 0)
            todo.Add(new TodoEntry(ReminderDetailItem.CategoryUngroupedHosts, UngroupedKey, "【未分組主機】", "台", ungrouped,
                $"有 {ungrouped} 台主機沒有主機群組，一般使用者看不到，請補上群組",
                days > 0 && IsDue(state, UngroupedKey, today, days)));
    }

    /// <summary>
    /// 反覆結案又復發：全站進行中成員（精簡欄位）一次取回、前案候選一次取回，以 <see cref="IssueRecurrence.Evaluate"/> 算復發次數，
    /// 達 <see cref="RecurrenceReminderThreshold"/> 的列入。靜音中的問題與停用主機上的成員不列（同上報類）；成員之後結案為不處理類
    /// 就不再是進行中成員，自然不列。前案只取結案日不早於「最早一件的建立日往前一個回看窗」者（更早的不影響次數）。
    /// 排序：復發次數多的在前，再依單號、主機。
    /// </summary>
    private List<RecurrenceEntry> ScanRecurrence(IssueExclusion exclusion, IReadOnlyCollection<string> excludedHostKeys,
        MailNotifyState state, DateTime today, int days, IReadOnlyList<KnownIssueRule> rules)
    {
        var disabled = excludedHostKeys.ToHashSet(StringComparer.Ordinal);
        var members = _cases.GetActiveMemberSummaries(null)
            .Where(c => c.WorkOrderId != null && !disabled.Contains(Core.Persistence.Sql.HostNameKey.Of(c.HostName))
                        && !(c.SourceName != null && c.EventId is int eventId && exclusion.IsCurrentlyMuted(c.SourceName, eventId)))
            .ToList();
        if (members.Count == 0) return new List<RecurrenceEntry>();

        var hostNames = members.Select(c => c.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var issues = members.Where(c => c.SourceName != null && c.EventId != null)
            .Select(c => (c.SourceName!, c.EventId!.Value)).Distinct().ToList();
        var since = members.Min(c => c.CreatedAt).Date.AddDays(-IssueRecurrence.WindowDays);
        var info = IssueRecurrence.Evaluate(members, _cases.GetClosedHistory(hostNames, issues, since));

        return members
            .Where(c => info[c.CaseId].Count >= RecurrenceReminderThreshold)
            .Select(c => new RecurrenceEntry(c.CaseId, c.WorkOrderId!.Value, IssueNames.Display(c.SourceName, c.EventId ?? 0, c.IssueLabel, rules), c.HostName, info[c.CaseId].Count,
                days > 0 && IsDue(state, RecurrenceKey(c.CaseId), today, days)))
            .OrderByDescending(e => e.Count).ThenBy(e => e.WorkOrderId).ThenBy(e => e.HostName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 派不出去清單只存標籤「Source EventId」（派工端組字串的格式）：拆出事件編號與來源，走 <see cref="IssueExclusion"/> 同一條靜音判定。
    /// 標籤不是這個格式時視為不靜音。
    /// </summary>
    private static bool IsMuted(IssueExclusion exclusion, UnassignableIssue u)
    {
        // 有來源與事件編號（新資料）直接判定；舊資料只有標籤，標籤的最後一段是事件編號（「來源 編號」或「來源 EventId 編號」）
        if (!string.IsNullOrWhiteSpace(u.Source)) return exclusion.IsCurrentlyMuted(u.Source, u.EventId);
        var label = u.IssueLabel;
        var cut = label.LastIndexOf(' ');
        if (cut <= 0 || !int.TryParse(label[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId)) return false;
        var source = label[..cut];
        const string marker = " EventId";
        if (source.EndsWith(marker, StringComparison.Ordinal)) source = source[..^marker.Length];
        return exclusion.IsCurrentlyMuted(source, eventId);
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
            var mailTotal = result.MailEscalatedCount + mailUnassignable + result.MailNoRecipientCount + result.MailRecurrenceCount
                + result.MailTodoCount;

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
                             FormatMailDetail(result, mailUnassignable > 0), BuildMailItems(result, mailUnassignable > 0),
                             result.MailRecurrenceCount, result.MailTodoCount);

            _state.Update(s =>
            {
                var todayKey = DateKey(today);
                if (queued)
                {
                    foreach (var key in result.MailEscalatedKeys.Concat(result.MailNoRecipientKeys).Concat(result.MailRecurrenceKeys).Concat(result.MailTodoKeys))
                        s.AdminReminderLastSent[key] = todayKey;
                    if (mailUnassignable > 0) s.AdminReminderLastSent[UnassignableStateKey] = todayKey;
                }

                // 不再逾期（已處理、已結案）的鍵清掉；本趟提醒的鍵一定在目前逾期集合內
                var live = result.EscalatedAll.Select(e => e.CaseId)
                    .Concat(result.NoRecipientAll.Select(e => NoRecipientKey(e.WorkOrderId)))
                    .Concat(result.RecurrenceAll.Select(e => RecurrenceKey(e.CaseId)))
                    .Concat(result.TodoAll.Select(e => e.Key))
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
                IssueLabel = e.IssueLabel, Source = e.Source, EventId = e.EventId, Reason = e.Reason, Hosts = e.Hosts, ObservedAt = e.ObservedAt ?? lastAt
            };
        foreach (var o in observed)
        {
            var key = (o.IssueLabel, o.Reason);
            var hosts = merged.TryGetValue(key, out var prior) && prior.ObservedAt?.Date == now.Date
                ? Math.Max(prior.Hosts, o.Hosts)
                : o.Hosts;
            merged[key] = new UnassignableIssue { IssueLabel = o.IssueLabel, Source = o.Source, EventId = o.EventId, Reason = o.Reason, Hosts = hosts, ObservedAt = now };
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

    /// <summary>「反覆結案又復發」的提醒去重鍵：加前綴避免與同一案件的上報鍵（案件編號）互相覆蓋</summary>
    internal static string RecurrenceKey(string caseId) => $"recurrence:{caseId}";

    /// <summary>「最近一趟取數失敗」的去重鍵：以結束時間區分趟（同一趟只提醒一次間隔、下一趟失敗是新的鍵）</summary>
    internal static string FetchKey(DateTime endedAt) => $"fetch:{endedAt.Ticks}";

    internal const string UngroupedKey = "ungrouped";

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
        var recurrence = result.RecurrenceAll.Where(e => e.DueForMail).ToList();
        items.AddRange(recurrence.Take(MailLineLimit).Select(r => new ReminderDetailItem
        {
            Category = ReminderDetailItem.CategoryRecurrence, Key = RecurrenceKey(r.CaseId), Total = recurrence.Count,
            Text = $"單號 {r.WorkOrderId}：{r.IssueLabel}（{r.HostName}），已處理後又復發 {r.Count} 次",
            Heading = "【反覆結案又復發】", Unit = "個成員"
        }));
        foreach (var group in result.TodoAll.Where(e => e.DueForMail).GroupBy(e => e.Category))
            items.AddRange(group.Take(MailLineLimit).Select(e => new ReminderDetailItem
            {
                Category = e.Category, Key = e.Key, Total = e.Total, Text = e.Text, Heading = e.Heading, Unit = e.Unit
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
        var recurrence = result.RecurrenceAll.Where(e => e.DueForMail).ToList();
        if (recurrence.Count > 0)
        {
            sb.AppendLine($"【反覆結案又復發】共 {recurrence.Count} 個成員");
            foreach (var r in recurrence.Take(MailLineLimit))
                sb.AppendLine($"  單號 {r.WorkOrderId}：{r.IssueLabel}（{r.HostName}），已處理後又復發 {r.Count} 次");
            if (recurrence.Count > MailLineLimit) sb.AppendLine($"  …等 {recurrence.Count} 個成員");
        }
        foreach (var group in result.TodoAll.Where(e => e.DueForMail).GroupBy(e => e.Category))
        {
            var first = group.First();
            sb.AppendLine($"{first.Heading}共 {first.Total} {first.Unit}");
            foreach (var e in group.Take(MailLineLimit)) sb.AppendLine($"  {e.Text}");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>上報逾期的成員（內部彙整：含案件編號與「本趟要不要提醒」，不外送）</summary>
internal sealed record EscalatedEntry(string CaseId, long WorkOrderId, string IssueLabel, string HostName, int Days, bool DueForMail);

/// <summary>上報時沒有收件人的單（內部彙整：含「本趟要不要提醒」，不外送）</summary>
internal sealed record NoRecipientEntry(long WorkOrderId, string IssueLabel, bool DueForMail);

/// <summary>反覆結案又復發的成員（內部彙整：含案件編號、復發次數與「本趟要不要提醒」，不外送）</summary>
internal sealed record RecurrenceEntry(string CaseId, long WorkOrderId, string IssueLabel, string HostName, int Count, bool DueForMail);

/// <summary>待辦新增類別的一筆信件項目（內部彙整，不外送）：Total 是該類在彙整當下的總數（寫「…等共 N」用）</summary>
internal sealed record TodoEntry(string Category, string Key, string Heading, string Unit, int Total, string Text, bool DueForMail);

/// <summary>
/// <see cref="AdminReminderService.ComputeResult"/> 的結果：對外回應 <see cref="Dto"/> 加上入列與組信用的內部彙整。
/// 對外 DTO 只留回應欄位，內部欄位不靠序列化屬性遮蔽。
/// </summary>
internal sealed class AdminReminderResult
{
    public AdminReminderResult(AdminReminderDto dto, List<EscalatedEntry> escalatedAll, List<NoRecipientEntry> noRecipientAll,
        List<RecurrenceEntry>? recurrenceAll = null)
    {
        Dto = dto;
        EscalatedAll = escalatedAll;
        NoRecipientAll = noRecipientAll;
        RecurrenceAll = recurrenceAll ?? new List<RecurrenceEntry>();
    }

    public AdminReminderDto Dto { get; }
    public List<EscalatedEntry> EscalatedAll { get; }
    public List<NoRecipientEntry> NoRecipientAll { get; }
    public List<RecurrenceEntry> RecurrenceAll { get; }

    /// <summary>派不出去清單這趟要不要提醒（間隔天數已開、清單非空、今天還沒提醒過）</summary>
    public bool UnassignableDueForMail { get; init; }

    /// <summary>待辦新增類別的信件項目（昨晚取數結果、處理人已停用或暫停仍有單、未分組主機；可派未派沒有使用者範圍，不進信）</summary>
    public List<TodoEntry> TodoAll { get; init; } = new();

    public int MailTodoCount => TodoAll.Count(e => e.DueForMail);
    public List<string> MailTodoKeys => TodoAll.Where(e => e.DueForMail).Select(e => e.Key).ToList();
    public int MailEscalatedCount => EscalatedAll.Count(e => e.DueForMail);
    public int MailNoRecipientCount => NoRecipientAll.Count(e => e.DueForMail);
    public int MailRecurrenceCount => RecurrenceAll.Count(e => e.DueForMail);
    public List<string> MailRecurrenceKeys => RecurrenceAll.Where(e => e.DueForMail).Select(e => AdminReminderService.RecurrenceKey(e.CaseId)).ToList();
    public List<string> MailEscalatedKeys => EscalatedAll.Where(e => e.DueForMail).Select(e => e.CaseId).ToList();
    public List<string> MailNoRecipientKeys => NoRecipientAll.Where(e => e.DueForMail).Select(e => AdminReminderService.NoRecipientKey(e.WorkOrderId)).ToList();
}
