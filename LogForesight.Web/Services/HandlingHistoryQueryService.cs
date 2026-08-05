using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;

namespace LogForesight.Web.Services;

/// <summary>
/// 處理狀態的查詢面：處理歷程、全站待辦（儀表板/報表共用）、處理人員工作頁
/// （docs/WEB-SPEC.md §9.3、§9.4a）。寫入操作見 <see cref="DayHandlingCommandService"/>／
/// <see cref="IssueHandlingCommandService"/>。
///
/// <see cref="GetTodo"/>：全站待辦（儀表板 KPI／報表處理進度共用，docs/archive/HISTORY.md S3）：
/// 未處理與逾期。**母體（高＋中風險日）由本方法內部強制套用**——呼叫端傳整批候選紀錄即可，
/// 不必（也不應該）自己先過濾，否則「待辦只算高＋中風險日」這條規則遲早在某個新呼叫端漏掉。
/// 低風險日全站一律不進待辦（全塞進來待辦永遠爆量，等於沒有待辦）。
/// **沒有 handling 列的風險日也要算未處理**（與問題查詢清單同一套語意），
/// 只數 handling.json 既有列會漏掉所有「從未被動過」的新問題。
/// </summary>
public class HandlingHistoryQueryService
{
    private readonly IRecordHandlingStore _store;
    private readonly IIssueHandlingStore _issueStore;
    private readonly IIssueCaseStore _cases;
    private readonly IHostStore _hosts;
    private readonly IUserStore _users;
    private readonly IVisibilityService _visibility;
    private readonly ISystemSettingsStore _settings;
    private readonly IRecordRepository _repository;
    private readonly HandlingProgressCalculator _progress;

    public HandlingHistoryQueryService(
        IRecordHandlingStore store,
        IIssueHandlingStore issueStore,
        IIssueCaseStore cases,
        IHostStore hosts,
        IUserStore users,
        IVisibilityService visibility,
        ISystemSettingsStore settings,
        IRecordRepository repository,
        HandlingProgressCalculator progress)
    {
        _store = store;
        _issueStore = issueStore;
        _cases = cases;
        _hosts = hosts;
        _users = users;
        _visibility = visibility;
        _settings = settings;
        _repository = repository;
        _progress = progress;
    }

    public List<HandlingLogDto> GetLogs(long hostId, DateTime date)
    {
        var host = RequireVisibleHost(hostId);

        // 案件授與者只看得到自己被交辦問題的歷程（docs/archive/FEEDBACK-10-PLAN.md §7）：
        // 歷程列含 IssueLabel（「Source EventId」）與處理說明，不過濾等於把同一天其他問題的
        // 處理過程一起交出去。**日層級的列（IssueKey 為空）也一併排除**——那是整天的狀態敘事，
        // 不屬於單一問題的交辦範圍
        var allowed = _visibility.GetIssueKeyRestriction(hostId);

        return _store.GetLogs(host.HostName, date)
            .Where(log => allowed == null || (log.IssueKey != null && allowed.Contains(log.IssueKey)))
            .Select(log => new HandlingLogDto
            {
                Action = log.Action,
                ActionText = HandlingTextHelpers.ActionText(log.Action),
                Status = log.Status,
                StatusText = HandlingTextHelpers.StatusText(log.Status),
                // 純顯示字串（歷程列的「處理人：X」），走全站統一的「顯示名稱(帳號)」
                // ——docs/archive/FEEDBACK-10-PLAN.md §6
                HandlerName = log.HandlerId.HasValue ? ResolveHandlerName(log.HandlerId.Value) : null,
                Note = log.Note,
                IssueLabel = log.IssueLabel,
                ActorAccount = log.ActorAccount,
                ActorDisplayName = _users.FindByAccount(log.ActorAccount)?.DisplayName,
                CreatedAt = log.CreatedAt
            })
            .ToList();
    }

    private string ResolveHandlerName(long userId)
    {
        var user = _users.Get(userId);
        return user == null ? "（已刪除）" : NameFormat.WithAccount(user.DisplayName, user.Account);
    }

    /// <summary>報表顯示範圍（§5）：單選過濾——一個母體對應一組明確語意。</summary>
    public static class HandlingScopes
    {
        public const string All = "all";               // 全部（不過濾）
        public const string Unresolved = "unresolved"; // 未結案（排除已結案）
        public const string Open = "open";             // 未處理（再排除處理中）
        public const string Unassigned = "unassigned"; // 未指派

        public static readonly string[] Valid = { All, Unresolved, Open, Unassigned };
        public static string Normalize(string? scope) =>
            scope != null && Valid.Contains(scope) ? scope : All;
    }

    /// <summary>
    /// 報表顯示範圍過濾（§5）：先過濾再聚合，KPI／趨勢／類型分布／排行／占比全部反映同一範圍。
    /// 範圍只對「需注意的」高＋中風險日有意義——scope≠all 時低風險日一律排除（它們不在待辦
    /// 語意內），使用者要看全部就選「全部」。狀態推導與 <see cref="GetTodo"/>／問題查詢清單同一套
    /// （DayHandlingDerivation），不另發明第二份語意；未指派＝日層級無處理人且無案件涵蓋。
    /// </summary>
    public List<DailyAnalysisRecord> FilterByScope(IReadOnlyCollection<DailyAnalysisRecord> records, string scope)
    {
        scope = HandlingScopes.Normalize(scope);
        if (scope == HandlingScopes.All) return records.ToList();

        var actionable = records.Where(r => RiskLevels.IsActionable(r.RiskLevel)).ToList();
        if (actionable.Count == 0) return new List<DailyAnalysisRecord>();

        var lookup = new HostLookup(_hosts.GetAll());
        string NameOf(DailyAnalysisRecord record) => lookup.For(record)?.HostName ?? record.Host;

        var hostNames = actionable.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = actionable.Min(r => r.Date);
        var to = actionable.Max(r => r.Date);
        var handlings = _store.GetMany(hostNames, from, to);
        var issueHandlings = _issueStore.GetMany(hostNames, from, to);
        var openCases = _cases.GetMany(hostNames).Where(c => c.ClosedAt == null && c.HandlerId.HasValue).ToList();
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        return actionable.Where(record =>
        {
            var name = NameOf(record);
            var handling = handlings.FirstOrDefault(h =>
                string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) && h.Date.Date == record.Date.Date);
            var forDay = issueHandlings
                .Where(h => string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) && h.Date.Date == record.Date.Date)
                .ToList();
            var external = HandlingStatuses.ExternalOf(
                DayHandlingDerivation.Derive(record.TopIssues, forDay, handling?.Status, unhandledSeverities).DayStatus);

            return scope switch
            {
                HandlingScopes.Unresolved => external != HandlingStatuses.Resolved,
                HandlingScopes.Open => external == HandlingStatuses.Open,
                HandlingScopes.Unassigned => !HasHandler(record, name, handling, openCases),
                _ => true
            };
        }).ToList();
    }

    /// <summary>是否有處理人：日層級 HandlerId，或當日問題屬某進行中案件（與清單頁 fallback 同語意）。</summary>
    private static bool HasHandler(DailyAnalysisRecord record, string hostName, RecordHandling? handling, List<IssueCase> openCases)
    {
        if (handling?.HandlerId != null) return true;
        if (record.TopIssues.Count == 0) return false;
        var issueKeys = record.TopIssues.Select(IssueSignatureKey.For).ToHashSet(StringComparer.Ordinal);
        return openCases.Any(c =>
            string.Equals(c.HostName, hostName, StringComparison.OrdinalIgnoreCase) && issueKeys.Contains(c.IssueKey));
    }

    public HandlingTodoDto GetTodo(IReadOnlyCollection<DailyAnalysisRecord> records)
    {
        // 待辦母體＝高＋中風險日，全站唯一定義（S3）：呼叫端不必也不應該自己先過濾
        var actionable = records.Where(r => RiskLevels.IsActionable(r.RiskLevel)).ToList();
        if (actionable.Count == 0) return new HandlingTodoDto();

        // 紀錄 → 現行主機名稱：handling 以現行名稱為鍵（與 RecordListQueryService 同一套規則）。
        // 可見範圍不在這裡過濾——傳入的紀錄已經過 RecordRepository 的可見範圍交集
        var lookup = new HostLookup(_hosts.GetAll());
        string NameOf(DailyAnalysisRecord record) => lookup.For(record)?.HostName ?? record.Host;

        var hostNames = actionable.Select(NameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var from = actionable.Min(r => r.Date);
        var to = actionable.Max(r => r.Date);
        var handlings = _store.GetMany(hostNames, from, to);
        var issueHandlings = _issueStore.GetMany(hostNames, from, to);
        var unhandledSeverities = _settings.Get().ParseUnhandledSeverities();

        var todo = new HandlingTodoDto { TotalCount = actionable.Count };
        foreach (var record in actionable)
        {
            var name = NameOf(record);
            var handling = handlings.FirstOrDefault(h =>
                string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                h.Date.Date == record.Date.Date);

            // 日狀態由問題層級推導（方案 B，與問題清單同一套規則）——
            // 全部問題結案的風險日不再算未處理，即使日層級從沒被人動過
            var forDay = issueHandlings
                .Where(h => string.Equals(h.HostName, name, StringComparison.OrdinalIgnoreCase) &&
                            h.Date.Date == record.Date.Date)
                .ToList();
            var progress = DayHandlingDerivation.Derive(record.TopIssues, forDay, handling?.Status, unhandledSeverities);

            // 對外三態（#12）：日層級 fallback 出來的狀態可能是 wont_fix/false_positive/known_noise
            // （使用者把整天標成這些狀態、當天沒有問題層級標記時），改看 ExternalOf 前這三種狀態
            // 誰都不算，會讓 OpenCount+InProgressCount+ResolvedCount 加總小於 TotalCount，
            // 「未完成」（Total－Resolved）因此把已結案的日子誤算成未完成
            switch (HandlingStatuses.ExternalOf(progress.DayStatus))
            {
                case HandlingStatuses.Open: todo.OpenCount++; break;
                case HandlingStatuses.InProgress: todo.InProgressCount++; break;
                default: todo.ResolvedCount++; break;
            }

            // 逾期兩層都看：日層級 DueDate 過期且未結案，或任一問題層級「處理中」過期
            // （與問題查詢清單的 IsOverdue 同一套語意，來源同為 DayHandlingDerivation.HasOverdueIssue）
            var dayOverdue = handling?.DueDate.HasValue == true &&
                             handling.DueDate.Value.Date < DateTime.Today &&
                             progress.IsUnresolved;
            if (dayOverdue || DayHandlingDerivation.HasOverdueIssue(forDay, DateTime.Today))
            {
                todo.OverdueCount++;
            }
        }

        return todo;
    }

    // ── 處理人員工作頁（docs/archive/FEEDBACK-4-PLAN.md §6）────────────────────────────

    /// <summary>
    /// 全登入角色可查看任何人的工作頁，但**資料以檢視者的可見範圍過濾**（不是被看者的）——
    /// user A 看 user B 的頁只看得到 A 授權範圍內主機上的交辦項目，與全站查詢頁一致，
    /// 處理人名字本來就全站可見，此頁未洩漏新資訊。被查看者已停用時仍顯示（歷史事實不因
    /// 停用消失），前端據 Active 後綴「（已停用）」。
    /// </summary>
    public HandlerWorkloadDto GetHandlerWorkload(long userId, bool includeResolvedDays)
    {
        var user = _users.Get(userId) ?? throw DomainException.NotFound("找不到這位使用者。");

        // 可見範圍以**檢視者**為準（§9.4a 既有規則），但要聯集檢視者自己的案件授與
        // （docs/archive/FEEDBACK-10-PLAN.md §7）：被交辦到授權範圍外的主機時，「我的交辦」
        // 若還是看不到那些案件，等於被指派了卻找不到工作在哪
        var visibleHostIds = _visibility.GetVisibleHostIds().ToHashSet();
        foreach (var hostName in _visibility.GetCaseGrants().Keys)
        {
            var granted = _hosts.FindByName(hostName);
            if (granted != null) visibleHostIds.Add(granted.HostId);
        }

        var caseItems = BuildHandlerCaseItems(userId, visibleHostIds);
        var dayItems = BuildHandlerDayItems(userId, visibleHostIds, includeResolvedDays);

        return new HandlerWorkloadDto
        {
            UserId = user.UserId,
            DisplayName = user.DisplayName,
            Account = user.Account,
            Active = user.Active,
            OpenCaseCount = caseItems.Count,
            UnresolvedDayCount = dayItems.Count(d => HandlingStatuses.Unresolved.Contains(d.DerivedStatus)),
            OverdueCount = caseItems.Count(c => c.IsOverdue) + dayItems.Count(d => d.IsOverdue),
            Cases = caseItems,
            Days = dayItems
        };
    }

    private List<HandlerCaseItemDto> BuildHandlerCaseItems(long userId, IReadOnlySet<long> visibleHostIds)
    {
        var items = new List<HandlerCaseItemDto>();

        foreach (var c in _cases.GetOpenByHandler(userId))
        {
            var host = _hosts.FindByName(c.HostName);
            if (host == null || !visibleHostIds.Contains(host.HostId)) continue;

            var isOverdue = (c.Status == IssueHandlingStatuses.InProgress &&
                              c.DueDate.HasValue && c.DueDate.Value.Date < DateTime.Today) ||
                             IssueHandlingStatuses.IsObservationExpired(c.Status, c.DueDate, DateTime.Today);

            // 依問題視角（§7）的分組鍵與「回覆處理狀態」端點的參數，自 IssueKey 反解
            var signature = IssueSignatureKey.TryParseSignature(c.IssueKey);

            items.Add(new HandlerCaseItemDto
            {
                HostId = host.HostId,
                HostName = host.HostName,
                IssueLabel = c.IssueLabel,
                Source = signature?.Source ?? string.Empty,
                EventId = signature?.EventId ?? 0,
                Status = c.Status,
                StatusText = HandlingTextHelpers.IssueStatusText(c.Status),
                DueDate = c.DueDate?.ToString("yyyy-MM-dd"),
                IsOverdue = isOverdue,
                FirstLinkedDate = c.FirstLinkedDate.ToString("yyyy-MM-dd"),
                LastLinkedDate = c.LastLinkedDate.ToString("yyyy-MM-dd")
            });
        }

        return items
            .OrderByDescending(i => i.IsOverdue)
            .ThenByDescending(i => i.LastLinkedDate)
            .ToList();
    }

    /// <summary>
    /// 被指派的風險日：日層級快照指派後恆為 in_progress，必須看推導狀態才知道「現在真正的
    /// 狀態」——沿用詳情頁／清單頁同一套 ComputeProgress。預設只列推導後未結案；
    /// includeResolvedDays 時另外納入近 30 天內已結案的（成就感與回顧用）。
    /// </summary>
    private List<HandlerDayItemDto> BuildHandlerDayItems(long userId, IReadOnlySet<long> visibleHostIds, bool includeResolvedDays)
    {
        var cutoff = DateTime.Today.AddDays(-30);
        var items = new List<HandlerDayItemDto>();

        foreach (var handling in _store.GetByHandler(userId))
        {
            var host = _hosts.FindByName(handling.HostName);
            if (host == null || !visibleHostIds.Contains(host.HostId)) continue;

            var record = _repository.GetOne(host.HostId, handling.Date);
            if (record == null) continue;   // 分析紀錄不存在（理論上不會，防禦性——指派本身要求紀錄存在）

            var progress = _progress.ComputeProgress(host, handling.Date, record);
            var unresolved = progress.IsUnresolved;
            if (!unresolved && (!includeResolvedDays || handling.Date.Date < cutoff)) continue;

            var forDayIssues = _issueStore.GetForDay(host.HostName, handling.Date);
            var isOverdue = (handling.DueDate.HasValue && handling.DueDate.Value.Date < DateTime.Today && unresolved) ||
                            DayHandlingDerivation.HasOverdueIssue(forDayIssues, DateTime.Today);

            items.Add(new HandlerDayItemDto
            {
                HostId = host.HostId,
                HostName = host.HostName,
                Date = handling.Date.ToString("yyyy-MM-dd"),
                RiskLevel = record.RiskLevel,
                DerivedStatus = progress.DayStatus,
                DerivedStatusText = HandlingTextHelpers.StatusText(progress.DayStatus),
                DueDate = handling.DueDate?.ToString("yyyy-MM-dd"),
                IsOverdue = isOverdue
            });
        }

        return items
            .OrderByDescending(i => i.IsOverdue)
            .ThenByDescending(i => i.Date)
            .ToList();
    }

    private WebHost RequireVisibleHost(long hostId)
    {
        _visibility.EnsureVisible(hostId);
        return _hosts.Get(hostId) ?? throw DomainException.NotFound("找不到這台主機。");
    }
}
