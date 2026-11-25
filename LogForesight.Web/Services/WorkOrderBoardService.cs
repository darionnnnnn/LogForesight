using LogForesight.Core.Models;
using System.Text.Json;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Web.Services;

/// <summary>
/// 交辦單長期維運的三個端點：負載看板、待派清單（派工試跑）、立即派工。
///
/// **待派清單與立即派工共用同一個試跑私有方法（<see cref="RunTrial"/>）**：清單上的建議處理人與
/// 「為什麼派不出去」，就是按下立即派工時會發生的事——兩邊只呼叫它一次，立即派工送給協調器的成員
/// 就是試跑決策裡「會派出去」的那一份，不另跑一段決策。
///
/// 試跑只在設定的**複本**上開啟自動派工（回答「按立即派工會怎樣」，與夜間開關無關），存檔的設定不動。
///
/// **抑制在試跑內判定**：每台主機取生效中抑制（<see cref="SuppressionFilter.ActiveForHost"/>），
/// Signature 型比對簽章鍵，Rule 型抑制以規則的來源／程式／代碼樣式判定（<see cref="KnownIssueCatalog.RuleMayHit"/>），
/// 比分析當下的訊息層條件寬，可能多擋、不會少擋——立即派工寧可少派也不派出假工作。
///
/// 查詢次數不隨缺口數或主機數線性增長：聚合兩次、進行中鍵一次、案件預載一次、候選人快照一次、負載一次
/// （另加主機、使用者、主機群組、規則、抑制清單各一次）。
/// </summary>
public class WorkOrderBoardService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>出現點上限（暫定）：超過就不做試跑，請使用者縮小期間</summary>
    internal const int DefaultMaxOccurrences = 20000;

    private const int GapPageSize = 50;
    private const int DefaultPeriodDays = 7;
    private const int UnseenHostGroupLimit = 10;
    private const string TargetKind = "work_order";

    private readonly IWorkOrderStore _orders;
    private readonly IIssueCaseStore _cases;
    private readonly IUserStore _users;
    private readonly IUserGroupStore _userGroups;
    private readonly IHostStore _hosts;
    private readonly IHostGroupStore _hostGroups;
    private readonly IKnownIssueRuleStore _rules;
    private readonly ISuppressionStore _suppressions;
    private readonly RecordListQueryService _query;
    private readonly IIssueAggregateQuery _aggregates;
    private readonly IDispatchCandidateSource _candidates;
    private readonly IIssueOwnerStore _issueOwners;
    private readonly INoiseMarkStore _noiseMarks;
    private readonly ISystemSettingsStore _settings;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditService _audit;
    private readonly IUserDisplayNameService _displayNames;
    private readonly MailNotificationService _mail;
    private readonly int _maxOccurrences;

    private readonly TimeProvider _clock;

    public WorkOrderBoardService(
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        IUserStore users,
        IUserGroupStore userGroups,
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IKnownIssueRuleStore rules,
        ISuppressionStore suppressions,
        RecordListQueryService query,
        IIssueAggregateQuery aggregates,
        IDispatchCandidateSource candidates,
        IIssueOwnerStore issueOwners,
        INoiseMarkStore noiseMarks,
        ISystemSettingsStore settings,
        WorkOrderCoordinator coordinator,
        ICurrentUser currentUser,
        IAuditService audit,
        IUserDisplayNameService displayNames,
        MailNotificationService mail,
        TimeProvider? clock = null)
        : this(orders, cases, users, userGroups, hosts, hostGroups, rules, suppressions, query, aggregates, candidates, issueOwners,
            noiseMarks, settings, coordinator, currentUser, audit, displayNames, mail, DefaultMaxOccurrences, clock)
    {
    }

    /// <summary>測試用：縮小出現點上限（DI 只看公開建構子）</summary>
    internal WorkOrderBoardService(
        IWorkOrderStore orders,
        IIssueCaseStore cases,
        IUserStore users,
        IUserGroupStore userGroups,
        IHostStore hosts,
        IHostGroupStore hostGroups,
        IKnownIssueRuleStore rules,
        ISuppressionStore suppressions,
        RecordListQueryService query,
        IIssueAggregateQuery aggregates,
        IDispatchCandidateSource candidates,
        IIssueOwnerStore issueOwners,
        INoiseMarkStore noiseMarks,
        ISystemSettingsStore settings,
        WorkOrderCoordinator coordinator,
        ICurrentUser currentUser,
        IAuditService audit,
        IUserDisplayNameService displayNames,
        MailNotificationService mail,
        int maxOccurrences,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _orders = orders;
        _cases = cases;
        _users = users;
        _userGroups = userGroups;
        _hosts = hosts;
        _hostGroups = hostGroups;
        _rules = rules;
        _suppressions = suppressions;
        _query = query;
        _aggregates = aggregates;
        _candidates = candidates;
        _issueOwners = issueOwners;
        _noiseMarks = noiseMarks;
        _settings = settings;
        _coordinator = coordinator;
        _currentUser = currentUser;
        _audit = audit;
        _displayNames = displayNames;
        _mail = mail;
        _maxOccurrences = maxOccurrences;
    }

    // ── 負載看板 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 列＝LoadBoard 回傳的處理人 ∪ 屬於任一啟用中派工池群組的啟用使用者；
    /// <paramref name="userGroupId"/> 有值時只留該使用者群組成員。查詢：負載、使用者、群組各一次。
    /// </summary>
    public LoadBoardDto GetLoadBoard(long? userGroupId)
    {
        var loads = _orders.LoadBoard(WorkOrderQueries.DisabledHostKeys(_hosts.GetAll())).ToDictionary(l => l.HandlerId);
        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var poolGroupIds = _userGroups.GetAll()
            .Where(g => g.Active && g.DispatchPool)
            .Select(g => g.GroupId)
            .ToHashSet();

        var ids = loads.Keys
            .Concat(users.Values.Where(u => u.Active && u.GroupIds.Any(poolGroupIds.Contains)).Select(u => u.UserId))
            .Distinct();
        if (userGroupId is long groupId)
            ids = ids.Where(id => users.TryGetValue(id, out var u) && u.GroupIds.Contains(groupId));

        var rows = ids
            .Select(id =>
            {
                users.TryGetValue(id, out var user);
                var load = loads.GetValueOrDefault(id);
                return new
                {
                    Account = user == null ? string.Empty : user.Account,
                    Row = new LoadBoardRowDto
                    {
                        UserId = id,
                        Name = NameOf(users, id),
                        Active = user != null && user.Active,
                        Paused = user != null && user.DispatchPaused,
                        InPool = user != null && user.GroupIds.Any(poolGroupIds.Contains),
                        ActiveWorkOrders = load == null ? 0 : load.ActiveWorkOrders,
                        ActiveMembers = load == null ? 0 : load.ActiveMembers,
                        UnrepliedWorkOrders = load == null ? 0 : load.UnrepliedWorkOrders,
                        OverdueMembers = load == null ? 0 : load.OverdueMembers,
                        ClosedLast7Days = load == null ? 0 : load.ClosedLast7Days,
                        OldestActiveCreatedAt = load == null ? null : load.OldestActiveCreatedAt
                    }
                };
            })
            .OrderByDescending(x => x.Row.ActiveMembers)
            .ThenBy(x => x.Account, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.UserId)
            .Select(x => x.Row)
            .ToList();

        return new LoadBoardDto { Rows = rows };
    }

    // ── 待派清單 ────────────────────────────────────────────────────────────

    public GapsDto GetGaps(DateTime? from, DateTime? to, int page)
    {
        var trial = RunTrial(from, to);
        (page, _) = Paging.Normalize(page, GapPageSize, GapPageSize);

        var dto = new GapsDto
        {
            Page = page,
            From = trial.Scope.From,
            To = trial.Scope.To,
            TooLarge = trial.TooLarge,
            PendingAtLeast = trial.TooLarge ? trial.PendingAtLeast : 0,
            MaxOccurrences = _maxOccurrences
        };
        if (trial.TooLarge) return dto;

        var summaries = Summarize(trial.Gaps)
            .OrderByDescending(s => s.GapHosts)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.EventId)
            .ToList();
        dto.Total = summaries.Count;
        dto.DispatchableIssueHosts = summaries.Sum(s => s.Suggested.Sum(x => x.Hosts));
        foreach (var gap in trial.Gaps.Where(g => g.Decision.Kind != DispatchDecisionKind.Skip))
            dto.DispatchableHostNames.Add(gap.Host.HostName);
        dto.UnassignableTotal = summaries.Count(s => s.Unassignable.Count > 0);
        FillSections(dto, summaries);

        var pageRows = summaries.Skip((page - 1) * GapPageSize).Take(GapPageSize).ToList();
        if (pageRows.Count == 0) return dto;

        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var groupNames = _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);

        dto.Rows = pageRows.Select(s => new GapRowDto
        {
            Source = s.Source,
            EventId = s.EventId,
            IssueLabel = IssueNames.ForRules(rules, s.Source, s.EventId),
            PlainExplanation = KnownIssueCatalog.PlainExplanationFor(rules, s.Source, s.EventId),
            GapHosts = s.GapHosts,
            Excluded = s.Excluded,
            Suggested = s.Suggested
                .Select(x => new GapSuggestionDto
                {
                    HandlerId = x.HandlerId, Name = NameOf(users, x.HandlerId), Hosts = x.Hosts, WillCreate = x.WillCreate
                })
                .ToList(),
            Unassignable = s.Unassignable,
            UnseenHostGroups = s.UnseenHosts
                .SelectMany(h => h.GroupIds)
                .Where(groupNames.ContainsKey)
                .Select(gid => groupNames[gid])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(UnseenHostGroupLimit)
                .ToList()
        }).ToList();

        return dto;
    }

    /// <summary>
    /// 待派三段與派不出去的分組（不分頁，全部問題）：
    /// 可以派＝有建議處理人的（問題, 主機）；派不出去＝無候選人（no_pool／all_paused／no_visibility）；不會派＝閘門排除（嚴重度未達、不再打擾、抑制、已知雜訊、靜音）。
    /// 一個問題在不同主機上可能同時落在多段，各段各算一次；單位見 <see cref="GapSectionDto"/>。
    /// </summary>
    private void FillSections(GapsDto dto, List<IssueSummary> summaries)
    {
        dto.Dispatchable = new GapSectionDto
        {
            Issues = summaries.Count(s => s.Suggested.Count > 0),
            Hosts = summaries.Sum(s => s.Suggested.Sum(x => x.Hosts))
        };
        dto.UnassignableSection = new GapSectionDto
        {
            Issues = summaries.Count(s => s.Unassignable.Count > 0),
            Hosts = summaries.Sum(s => s.Unassignable.Sum(x => x.Hosts))
        };
        dto.NotDispatched = new GapSectionDto
        {
            Issues = summaries.Count(s => s.Excluded.Count > 0),
            Hosts = summaries.Sum(s => s.Excluded.Values.Sum())
        };
        dto.UnassignableReasons = summaries
            .SelectMany(s => s.Unassignable.Select(u => (Summary: s, Unassignable: u)))
            .GroupBy(x => x.Unassignable.Reason, StringComparer.Ordinal)
            .Select(g => new GapUnassignableReasonDto
            {
                Reason = g.Key,
                Issues = g.Count(),
                Hosts = g.Sum(x => x.Unassignable.Hosts)
            })
            .OrderBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

        // 「沒有人看得到」的主機群組：每個群組只出現一次（不隨問題重複），附受影響的主機數與問題數
        var groupNames = _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);
        var unseen = new Dictionary<long, (HashSet<long> HostIds, int Issues)>();
        foreach (var summary in summaries)
        {
            var groupsOfIssue = new HashSet<long>();
            foreach (var host in summary.UnseenHosts)
            {
                foreach (var gid in host.GroupIds.Where(groupNames.ContainsKey))
                {
                    if (!unseen.TryGetValue(gid, out var entry)) unseen[gid] = entry = (new HashSet<long>(), 0);
                    entry.HostIds.Add(host.HostId);
                    if (groupsOfIssue.Add(gid)) unseen[gid] = (entry.HostIds, entry.Issues + 1);
                }
            }
        }
        dto.UnseenHostGroups = unseen
            .Select(p => new GapUnseenHostGroupDto { GroupId = p.Key, GroupName = groupNames[p.Key], Hosts = p.Value.HostIds.Count, Issues = p.Value.Issues })
            .OrderByDescending(g => g.Hosts)
            .ThenBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupId)
            .ToList();
        dto.PoolGroups = _userGroups.GetAll()
            .Where(g => g.Active && g.DispatchPool)
            .OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupId)
            .Select(g => new GapPoolGroupDto { UserGroupId = g.GroupId, GroupName = g.GroupName })
            .ToList();
        dto.CanGrantAccess = _currentUser.Has(Capability.Maintain);
    }

    // ── 立即派工 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 決策的建單來源：⑤ 問題負責人規則與 ⑥ 自動派工各自保留；掛單決策（Origin 為空）以步驟為準，
    /// 步驟是 ④ 續掛等其他值時沿用自動派工。
    /// </summary>
    private static string OriginOf(DispatchDecision d) =>
        d.Origin ?? (d.Step == WorkOrderOrigins.OwnerRule ? WorkOrderOrigins.OwnerRule : WorkOrderOrigins.AutoDispatch);

    /// <summary>零個新成員時的訊息（立即派工與單題派工共用；兩人幾乎同時派工時，後到的人會看到這個）</summary>
    internal const string NothingToDispatchMessage = "沒有需要派的項目（可能剛由其他人派出）";

    /// <summary>
    /// 問題篩選（單題派工）：來源與事件編號要一起給或都不給；都不給＝全頁。篩選在**全頁試跑之後**套用：
    /// 決策（負載平衡、同人併單）一律以整頁為準，單題派工的結果因此等於全頁試跑中該問題的那幾列，預覽也等於結果。
    /// </summary>
    private static (string SourceKey, int EventId)? IssueFilterOf(string? source, int? eventId)
    {
        if (string.IsNullOrWhiteSpace(source) && eventId == null) return null;
        if (string.IsNullOrWhiteSpace(source) || eventId == null)
            throw DomainException.Validation("單題派工需要同時指定問題的來源與事件編號。");
        return (WorkOrderIssueKey.SourceKeyOf(source), eventId.Value);
    }

    private static List<GapDecision> ApplyIssueFilter(List<GapDecision> gaps, (string SourceKey, int EventId)? filter) =>
        filter is not { } f
            ? gaps
            : gaps.Where(g => WorkOrderIssueKey.SourceKeyOf(g.Issue.Source) == f.SourceKey && g.Issue.EventId == f.EventId).ToList();

    /// <summary>
    /// 要建單或掛單的決策，依（處理人, 問題）分組：掛單與建單都依這組鍵；掛進他人範圍單的決策交給同處理人的建單自動併入其進行中單。
    /// 立即派工與預覽共用同一份分組，預覽的張數與台數才會等於實際結果。
    /// </summary>
    private static List<IGrouping<(long HandlerId, string SourceKey, int EventId, string Origin), GapDecision>> DispatchGroups(IEnumerable<GapDecision> gaps) =>
        gaps
            .Where(g => g.Decision.Kind != DispatchDecisionKind.Skip)
            .GroupBy(g => (HandlerId: g.Decision.HandlerId!.Value, SourceKey: WorkOrderIssueKey.SourceKeyOf(g.Issue.Source), g.Issue.EventId,
                Origin: OriginOf(g.Decision)))
            .OrderBy(g => g.Key.HandlerId)
            .ThenBy(g => g.Key.SourceKey, StringComparer.Ordinal)
            .ThenBy(g => g.Key.EventId)
            .ThenBy(g => g.Key.Origin, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 立即派工的預覽（不落盤）：與 <see cref="RunAutoDispatch"/> 共用同一個試跑與同一份分組，回「將建立／併入 N 張單、每人 M 台」。
    /// 單位：<see cref="AutoDispatchPreviewDto.CreateOrders"/>＝分組中會建新單的組數（試跑內有建單決策）；
    /// <see cref="AutoDispatchPreviewDto.MergeOrders"/>＝只掛進既有單的組數；每人台數＝該人各組相異主機數加總（與結果的 PerHandler 同口徑）。
    /// 沒有任何可派的決策時 <see cref="AutoDispatchPreviewDto.NothingToDispatch"/> 為 true。預覽不預測協調器的衝突略過，
    /// 實際結果只會少、不會多。
    /// </summary>
    public AutoDispatchPreviewDto PreviewAutoDispatch(DateTime? from, DateTime? to, string? source = null, int? eventId = null)
    {
        var filter = IssueFilterOf(source, eventId);
        var trial = RunTrial(from, to);
        EnsureNotTooLarge(trial);
        var gaps = ApplyIssueFilter(trial.Gaps, filter);
        var groups = DispatchGroups(gaps);

        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var hostsByHandler = new Dictionary<long, int>();
        foreach (var group in groups)
            hostsByHandler[group.Key.HandlerId] = hostsByHandler.GetValueOrDefault(group.Key.HandlerId)
                + group.Select(g => g.Host.HostId).Distinct().Count();

        var dto = new AutoDispatchPreviewDto
        {
            CreateOrders = groups.Count(g => g.Any(x => x.Decision.Kind == DispatchDecisionKind.CreateFor)),
            MergeOrders = groups.Count(g => g.All(x => x.Decision.Kind != DispatchDecisionKind.CreateFor)),
            PerHandler = hostsByHandler
                .Select(p => new AutoDispatchHandlerDto { HandlerId = p.Key, Name = NameOf(users, p.Key), Hosts = p.Value })
                .OrderBy(p => p.HandlerId)
                .ToList(),
            Unassigned = UnassignedOf(gaps)
        };
        dto.Hosts = dto.PerHandler.Sum(p => p.Hosts);
        dto.NothingToDispatch = groups.Count == 0;
        if (dto.NothingToDispatch) dto.Message = NothingToDispatchMessage;
        return dto;
    }

    private void EnsureNotTooLarge(Trial trial)
    {
        if (trial.TooLarge)
            throw DomainException.Validation($"期間內尚未派出的問題出現點至少 {trial.PendingAtLeast:N0} 筆，超過上限 {_maxOccurrences:N0} 筆，請縮小期間後再派工。");
    }

    private static List<GapUnassignableDto> UnassignedOf(List<GapDecision> gaps) =>
        Summarize(gaps)
            .SelectMany(s => s.Unassignable)
            .GroupBy(u => u.Reason, StringComparer.Ordinal)
            .Select(g => new GapUnassignableDto { Reason = g.Key, Hosts = g.Sum(u => u.Hosts) })
            .OrderBy(u => u.Reason, StringComparer.Ordinal)
            .ToList();

    /// <summary>立即派工；<paramref name="source"/>／<paramref name="eventId"/> 都給＝只派這個問題（見 <see cref="IssueFilterOf"/>），都不給＝全頁。</summary>
    public AutoDispatchResultDto RunAutoDispatch(DateTime? from, DateTime? to, string? source = null, int? eventId = null)
    {
        var filter = IssueFilterOf(source, eventId);
        var trial = RunTrial(from, to);
        EnsureNotTooLarge(trial);
        var gaps = ApplyIssueFilter(trial.Gaps, filter);

        var actor = _currentUser.ToWorkOrderActor(_clock);

        var groups = DispatchGroups(gaps);

        var nameRules = KnownIssueCatalog.ResolveRules(_rules);
        var created = 0;
        var merged = 0;
        var assignedMembers = 0;
        var daySyncPending = 0;
        var hostsByHandler = new Dictionary<long, int>();
        var failedGroups = new List<AutoDispatchFailedGroupDto>();
        var createdOrdersByHandler = new Dictionary<long, List<(long WorkOrderId, string IssueLabel, List<string> HostNames)>>();
        var releasedTally = new DismissalReleaseTally();
        foreach (var group in groups)
        {
            var first = group.First();
            var label = IssueNames.ForRules(nameRules, first.Issue.Source, first.Issue.EventId);
            var members = group
                .Select(g => new WorkOrderMember
                {
                    HostName = g.Host.HostName, IssueKey = g.IssueKey, IssueLabel = label, TriggerDate = g.LastSeen
                })
                .ToList();

            WorkOrderMemberOutcome outcome;
            try
            {
                outcome = _coordinator.Create(new WorkOrderCreateRequest
            {
                Source = first.Issue.Source,
                EventId = first.Issue.EventId,
                IssueLabel = label,
                HandlerId = group.Key.HandlerId,
                Origin = group.Key.Origin,
                ScopeKind = WorkOrderScopes.Hosts,
                ScopeGroupIds = new List<long>(),
                AutoAttach = false,
                ReassignConflicts = false,
                Note = WorkOrderCoordinator.NightlyNoteOf(group.Key.Origin),
                Members = members,
                Actor = actor
            });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 部分失敗：記下這一組、繼續其餘組；全部跑完後一定寫稽核
                var groupHosts = members.Select(m => HostNameKey.Of(m.HostName)).Distinct().Count();
                Log.Warn(ex, "立即派工：處理人 {HandlerId} 的問題 {Label}（{Hosts} 台）建單失敗", group.Key.HandlerId, label, groupHosts);
                failedGroups.Add(new AutoDispatchFailedGroupDto
                {
                    HandlerId = group.Key.HandlerId, Source = first.Issue.Source, EventId = first.Issue.EventId,
                    Hosts = groupHosts, Message = UserFacingMessage.For(ex, "建立交辦單時發生未預期的錯誤，詳細原因見記錄檔。")
                });
                continue;
            }

            daySyncPending += outcome.DaySync.PendingCases;
            if (outcome.WorkOrderId == 0) continue;

            var newMembers = outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned;
            // 「併入」只算真的併入了新成員的單：兩人同時派工時後到的人併進去零個成員，不算併入（否則回「併入 5 張、共 0 台」）
            if (outcome.CreatedOrder) created++;
            else if (newMembers > 0) merged++;
            assignedMembers += newMembers;
            // 因「不再打擾已滿 30 天」放行而寫入的成員：與夜間派工共用同一份累計，趟末各單合併一筆系統事件
            var skippedKeys = outcome.SkippedConflicts
                .Select(c => (HostNameKey.Of(c.HostName), c.IssueKey))
                .ToHashSet(HostIssueSignatureKeyComparer.Instance);
            releasedTally.Add(outcome.WorkOrderId,
                group.Count(g => g.Released && !skippedKeys.Contains((HostNameKey.Of(g.Host.HostName), g.IssueKey))));

            var skipped = outcome.SkippedConflicts
                .Select(c => (HostNameKey.Of(c.HostName), c.IssueKey))
                .ToHashSet(HostIssueSignatureKeyComparer.Instance);
            var distinctGroupHosts = members
                .Where(m => !skipped.Contains((HostNameKey.Of(m.HostName), m.IssueKey)))
                .Select(m => m.HostName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var hosts = distinctGroupHosts.Count;
            hostsByHandler[group.Key.HandlerId] = hostsByHandler.GetValueOrDefault(group.Key.HandlerId) + hosts;

            if (outcome.CreatedOrder && outcome.WorkOrderId > 0)
            {
                if (distinctGroupHosts.Count == 0)
                {
                    distinctGroupHosts = members.Select(m => m.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                if (!createdOrdersByHandler.TryGetValue(group.Key.HandlerId, out var list))
                {
                    list = new List<(long WorkOrderId, string IssueLabel, List<string> HostNames)>();
                    createdOrdersByHandler[group.Key.HandlerId] = list;
                }
                list.Add((outcome.WorkOrderId, label, distinctGroupHosts));
            }
        }

        releasedTally.Flush(_coordinator, _clock.LocalNow());

        var users = _users.GetAll().ToDictionary(u => u.UserId);

        // 一次入列：立即派工一趟可能建數百張單，逐筆入列會把整份 outbox blob 重寫數百次
        _mail.EnqueueWorkOrderNotices(
            createdOrdersByHandler
                .SelectMany(p => p.Value.Select(o => new WorkOrderNoticeRequest(WorkOrderOutboxKinds.Created, o.WorkOrderId, p.Key)))
                .ToList(),
            _currentUser.UserId, _currentUser.Account);
        var perHandler = hostsByHandler
            .Where(p => p.Value > 0)
            .Select(p => new AutoDispatchHandlerDto { HandlerId = p.Key, Name = NameOf(users, p.Key), Hosts = p.Value })
            .OrderBy(p => p.HandlerId)
            .ToList();
        var unassigned = UnassignedOf(gaps);

        var period = $"{trial.Scope.From:yyyy-MM-dd}~{trial.Scope.To:yyyy-MM-dd}";
        var attachedHosts = perHandler.Sum(p => p.Hosts);
        var unassignedHosts = unassigned.Sum(u => u.Hosts);
        _audit.Record(
            action: AuditActions.WorkOrderAutoDispatchRun,
            summary: $"{(filter == null ? "立即派工" : "單題派工")}（{period}）：新建 {created} 張、併入 {merged} 張，掛入 {attachedHosts} 台，無法派 {unassignedHosts} 台，失敗 {failedGroups.Count} 組",
            targetKind: TargetKind,
            targetId: period,
            detail: new
            {
                From = trial.Scope.From, To = trial.Scope.To, CreatedOrders = created, MergedOrders = merged,
                AssignedMembers = assignedMembers,
                IssueFilter = filter == null ? null : new { Source = source, EventId = eventId },
                FailedGroups = failedGroups.Select(g => new { g.HandlerId, g.Source, g.EventId, g.Hosts, g.Message }).ToList(),
                PerHandler = perHandler.Select(p => new { p.HandlerId, p.Hosts }).ToList(),
                Unassigned = unassigned.Select(u => new { u.Reason, u.Hosts }).ToList()
            });

        return new AutoDispatchResultDto
        {
            CreatedOrders = created,
            MergedOrders = merged,
            AssignedMembers = assignedMembers,
            PerHandler = perHandler,
            Unassigned = unassigned,
            DaySyncPendingCases = daySyncPending,
            FailedGroups = failedGroups,
            // 沒有任何新成員、也沒有失敗的組：多半是別人剛派完。明確標出，畫面不要顯示「已建立 0 張」
            NothingToDispatch = assignedMembers == 0 && failedGroups.Count == 0,
            Message = assignedMembers == 0 && failedGroups.Count == 0 ? NothingToDispatchMessage : null
        };
    }

    // ── 試跑（待派清單與立即派工共用的唯一一份）──────────────────────────────

    /// <summary>
    /// 1. 範圍 → 期間內全部問題 → 出現點 → 排除已有進行中案件 → 超過上限回 TooLarge（上限算尚未派出的出現點，已派出的不佔額度）。
    /// 2. 扣掉已有進行中案件的（主機, 簽章）＝缺口。
    /// 3. 候選人快照＋設定複本（開啟自動派工）建派工脈絡，一次預載全部缺口主機的「不再打擾」。
    /// 4. 缺口依（主機名不分大小寫、簽章）排序逐一決策；建單決策以負數單號登記虛擬單，後續同人同問題才會掛進去。
    /// </summary>
    private Trial RunTrial(DateTime? from, DateTime? to)
    {
        var periodTo = (to ?? _clock.LocalToday().AddDays(-1)).Date;
        var periodFrom = (from ?? periodTo.AddDays(-(DefaultPeriodDays - 1))).Date;
        if (periodFrom > periodTo)
            throw DomainException.Validation("起日不可晚於迄日。");

        var scope = _query.ResolveIssueScope(new RecordSearchRequest { From = periodFrom, To = periodTo });
        // 靜音不排除（不用 scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        var aggregates = _aggregates.Aggregate(IssueExclusion.None, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels);
        var issues = aggregates.Select(a => (a.Source, a.EventId)).ToList();

        var openKeys = _cases.GetOpenKeys().Select(x => (x.HostNameKey, x.IssueKey))
            .ToHashSet(HostIssueSignatureKeyComparer.Instance);

        // 載入逐出現點的原始列之前先用聚合的主機數估下界：每個（主機, 問題）至少一個出現點，每個進行中案件最多扣掉一個，
        // 下界已超過上限就一定超過——不必把整期出現點讀進記憶體（3000 台時是數十萬列）才得到 TooLarge。下界沒超過時照原本精算。
        var lowerBound = aggregates.Sum(a => (long)a.HostCount) - openKeys.Count;
        if (lowerBound > _maxOccurrences) return new Trial(scope, TooLarge: true, new List<GapDecision>(), lowerBound);

        // 靜音不排除：理由同上
        var occurrences = _aggregates.LatestOccurrences(
            IssueExclusion.None, issues, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels);
        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);

        var candidates = occurrences
            .Where(o => hostsById.ContainsKey(o.HostId))
            .Select(o => (Host: hostsById[o.HostId], Occurrence: o, Parsed: IssueSignatureKey.TryParseFull(o.IssueKey)))
            .Where(x => x.Parsed != null && !openKeys.Contains((HostNameKey.Of(x.Host.HostName), x.Occurrence.IssueKey)))
            .OrderBy(x => x.Host.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Occurrence.IssueKey, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count > _maxOccurrences) return new Trial(scope, TooLarge: true, new List<GapDecision>(), candidates.Count);

        // 已結案的（主機, 簽章）：處理概況只有一份判定（OccurrenceStatusResolver）；Resolved 狀態含「沒標記的預設已處理」，
        // 所以一定要看逐日處理列真的是結案類，才算被人結案
        var latestClosed = _query.ResolveOccurrenceStatuses(candidates.Select(x => x.Occurrence).ToList(), scope.From, scope.To)
            .Where(r => r is { OpenCase: null, Handling: not null } && IssueHandlingStatuses.IsClosed(r.Handling.Status))
            .Select(r => (r.Host.HostName, r.Occurrence.IssueKey))
            .ToList();
        // 最近出現日已結案還不夠：期間內任一天未結案，建案會寫那一天，仍是待派的工作。
        // 所以再問協調器「沒有可寫入逐日列」（與建案略過同一份判定），批次一次，兩個條件都成立才算已結案
        var noWritableDays = latestClosed.Count == 0
            ? new HashSet<(string HostName, string IssueKey)>()
            : _coordinator.KeysWithoutWritableDays(latestClosed);
        var closedKeys = latestClosed
            .Where(k => noWritableDays.Contains(k))
            .Select(k => (HostNameKey.Of(k.HostName), k.IssueKey))
            .ToHashSet(HostIssueSignatureKeyComparer.Instance);

        var settings = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(_settings.Get()))!;
        settings.AutoDispatchEnabled = true;
        var now = _clock.LocalNow();
        var ctx = DispatchContext.Build(_candidates.Build(), _issueOwners, _orders, _cases, _noiseMarks, settings, now,
            DismissalRelease.LookupFrom(KnownIssueCatalog.ResolveRules(_rules)),
            WorkOrderQueries.DisabledHostKeys(_hosts.GetAll()));
        ctx.PreloadDismissed(candidates.Select(x => x.Host.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        // 抑制：清單與規則各讀一次；每台主機的生效中抑制算一次（純記憶體）
        var allSuppressions = _suppressions.LoadAll();
        var nameRules = KnownIssueCatalog.ResolveRules(_rules);
        var rulesById = nameRules
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var activeByHost = new Dictionary<long, (HashSet<string> SignatureKeys, List<KnownIssueRule> Rules)>();

        var gaps = new List<GapDecision>(candidates.Count);
        var virtualOrderId = 0L;
        foreach (var (host, occurrence, parsed) in candidates)
        {
            var p = parsed!.Value;
            var issue = new LogIssueSignature
            {
                LogName = p.LogName,
                Source = p.Source,
                EventId = p.EventId,
                EntryType = p.EntryType,
                EventKey = p.EventKey,
                Severity = LegacySeverityRank.ToSeverity(occurrence.SeverityRank),
                Suppressed = false
            };
            if (!activeByHost.TryGetValue(host.HostId, out var active))
            {
                var forHost = SuppressionFilter.ActiveForHost(allSuppressions, host.HostName, host.GroupIds, now);
                active = (SuppressionFilter.ToSignatureKeySet(forHost),
                    SuppressionFilter.ToRuleIdSet(forHost).Where(rulesById.ContainsKey).Select(id => rulesById[id]).ToList());
                activeByHost[host.HostId] = active;
            }
            issue.Suppressed = active.SignatureKeys.Contains(occurrence.IssueKey)
                               || active.Rules.Any(r => KnownIssueCatalog.RuleMayHit(r, issue.Source, issue.EventId));

            var decision = WorkOrderDispatcher.Decide(ctx, host, issue, occurrence.LastSeen.Date);
            // 最近一次出現當天已被處理人結案：不是待派的工作，整筆不進試跑結果（不列在待派頁、不計入儀表板待派卡、
            // 不登記虛擬單、不計負載）。閘門已略過的（例如不再打擾）維持原因，讓待派頁照常標出被擋的主機。
            // 在這裡而不在 WorkOrderDispatcher 判定：結案狀態讀的是 OccurrenceStatusResolver 的處理概況，夜間派工路徑不受影響。
            if (decision.Kind != DispatchDecisionKind.Skip && closedKeys.Contains((HostNameKey.Of(host.HostName), occurrence.IssueKey)))
                continue;
            if (decision.Kind == DispatchDecisionKind.CreateFor)
            {
                ctx.RegisterOrder(new WorkOrder
                {
                    WorkOrderId = --virtualOrderId,
                    SourceName = issue.Source,
                    EventId = issue.EventId,
                    IssueLabel = IssueNames.ForRules(nameRules, issue.Source, issue.EventId),
                    HandlerId = decision.HandlerId!.Value,
                    Origin = decision.Origin!,
                    ScopeKind = WorkOrderScopes.Hosts,
                    CreatedAt = now
                });
            }
            // 本來被不再打擾擋住、因期滿而放行：必須在 Commit 之前判（案件快取尚未被本趟新單影響）
            var released = decision.Kind != DispatchDecisionKind.Skip && ctx.IsReleased(host.HostName, occurrence.IssueKey);
            ctx.Commit(decision, issue.Source, issue.EventId);

            gaps.Add(new GapDecision(host, issue, occurrence.IssueKey, occurrence.LastSeen, decision, released));
        }

        return new Trial(scope, TooLarge: false, gaps, candidates.Count);
    }

    /// <summary>依問題彙總試跑決策（待派清單的列與立即派工的無法派計數共用）</summary>
    private static List<IssueSummary> Summarize(List<GapDecision> gaps) =>
        gaps
            .GroupBy(g => (SourceKey: WorkOrderIssueKey.SourceKeyOf(g.Issue.Source), g.Issue.EventId))
            .Select(g =>
            {
                var list = g.ToList();
                var skips = list.Where(x => x.Decision.Kind == DispatchDecisionKind.Skip).ToList();

                var excluded = skips
                    .Where(x => UnassignableReasonOf(x.Decision) == null)
                    .GroupBy(x => x.Decision.SkipReason!, StringComparer.Ordinal)
                    .ToDictionary(r => r.Key, r => DistinctHosts(r));

                var unassignable = skips
                    .Where(x => UnassignableReasonOf(x.Decision) != null)
                    .GroupBy(x => UnassignableReasonOf(x.Decision)!, StringComparer.Ordinal)
                    .Select(r => new GapUnassignableDto { Reason = r.Key, Hosts = DistinctHosts(r) })
                    .OrderBy(r => r.Reason, StringComparer.Ordinal)
                    .ToList();

                var suggested = list
                    .Where(x => x.Decision.Kind != DispatchDecisionKind.Skip)
                    .GroupBy(x => x.Decision.HandlerId!.Value)
                    .Select(h => (HandlerId: h.Key, Hosts: DistinctHosts(h),
                        WillCreate: h.Any(x => x.Decision.Kind == DispatchDecisionKind.CreateFor)))
                    .OrderByDescending(h => h.Hosts)
                    .ThenBy(h => h.HandlerId)
                    .ToList();

                var unseenHosts = skips
                    .Where(x => x.Decision.NoCandidateDetail == WorkOrderDispatcher.NoCandidateNoVisibility)
                    .Select(x => x.Host)
                    .DistinctBy(h => h.HostId)
                    .ToList();

                return new IssueSummary(list[0].Issue.Source, g.Key.EventId, DistinctHosts(list),
                    excluded, suggested, unassignable, unseenHosts);
            })
            .ToList();

    /// <summary>派不出去的原因（無候選的細分）；其餘略過屬閘門排除，回 null</summary>
    private static string? UnassignableReasonOf(DispatchDecision decision) => decision.SkipReason switch
    {
        WorkOrderDispatcher.SkipNoCandidate => decision.NoCandidateDetail,
        _ => null
    };

    private static int DistinctHosts(IEnumerable<GapDecision> gaps) => gaps.Select(x => x.Host.HostId).Distinct().Count();

    private string NameOf(Dictionary<long, WebUser> users, long userId) =>
        NameFormat.UserLabel(_displayNames, users.GetValueOrDefault(userId));

    private sealed record GapDecision(WebHost Host, LogIssueSignature Issue, string IssueKey, DateTime LastSeen, DispatchDecision Decision, bool Released = false);

    /// <summary><paramref name="PendingAtLeast"/>：尚未派出的出現點數；以下界判定超限時是下界（實際至少這麼多）</summary>
    private sealed record Trial(IssueScope Scope, bool TooLarge, List<GapDecision> Gaps, long PendingAtLeast);

    private sealed record IssueSummary(
        string Source,
        int EventId,
        int GapHosts,
        Dictionary<string, int> Excluded,
        List<(long HandlerId, int Hosts, bool WillCreate)> Suggested,
        List<GapUnassignableDto> Unassignable,
        List<WebHost> UnseenHosts);
}
