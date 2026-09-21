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
    private const string DeletedName = "（已刪除）";
    private const string TargetKind = "work_order";
    private const string AutoDispatchNote = "系統依派工策略立即派工";

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
        MailNotificationService mail)
        : this(orders, cases, users, userGroups, hosts, hostGroups, rules, suppressions, query, aggregates, candidates, issueOwners,
            noiseMarks, settings, coordinator, currentUser, audit, displayNames, mail, DefaultMaxOccurrences)
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
        int maxOccurrences)
    {
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
        var loads = _orders.LoadBoard().ToDictionary(l => l.HandlerId);
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
        page = Math.Max(1, page);

        var dto = new GapsDto
        {
            Page = page,
            From = trial.Scope.From,
            To = trial.Scope.To,
            TooLarge = trial.TooLarge
        };
        if (trial.TooLarge) return dto;

        var summaries = Summarize(trial.Gaps)
            .OrderByDescending(s => s.GapHosts)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.EventId)
            .ToList();
        dto.Total = summaries.Count;

        var pageRows = summaries.Skip((page - 1) * GapPageSize).Take(GapPageSize).ToList();
        if (pageRows.Count == 0) return dto;

        var users = _users.GetAll().ToDictionary(u => u.UserId);
        var rules = KnownIssueCatalog.ResolveRules(_rules);
        var groupNames = _hostGroups.GetAll().ToDictionary(g => g.GroupId, g => g.GroupName);

        dto.Rows = pageRows.Select(s => new GapRowDto
        {
            Source = s.Source,
            EventId = s.EventId,
            IssueLabel = LabelOf(s.Source, s.EventId),
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

    // ── 立即派工 ────────────────────────────────────────────────────────────

    public AutoDispatchResultDto RunAutoDispatch(DateTime? from, DateTime? to)
    {
        var trial = RunTrial(from, to);
        if (trial.TooLarge)
            throw DomainException.Validation($"期間內尚未派出的問題出現點超過 {_maxOccurrences} 筆，請縮小期間後再派工。");

        var actor = new WorkOrderActor
        {
            ActorId = _currentUser.UserId > 0 ? _currentUser.UserId : null,
            ActorAccount = _currentUser.Account,
            OccurredAt = DateTime.Now
        };

        // 掛單與建單都依（處理人, 問題）分組；掛進他人範圍單的決策交給同處理人的建單自動併入其進行中單
        var groups = trial.Gaps
            .Where(g => g.Decision.Kind != DispatchDecisionKind.Skip)
            .GroupBy(g => (HandlerId: g.Decision.HandlerId!.Value, SourceKey: g.Issue.Source.ToUpperInvariant(), g.Issue.EventId))
            .OrderBy(g => g.Key.HandlerId)
            .ThenBy(g => g.Key.SourceKey, StringComparer.Ordinal)
            .ThenBy(g => g.Key.EventId)
            .ToList();

        var created = 0;
        var merged = 0;
        var assignedMembers = 0;
        var daySyncPending = 0;
        var hostsByHandler = new Dictionary<long, int>();
        var failedGroups = new List<AutoDispatchFailedGroupDto>();
        var createdOrdersByHandler = new Dictionary<long, List<(long WorkOrderId, string IssueLabel, List<string> HostNames)>>();
        foreach (var group in groups)
        {
            var first = group.First();
            var label = LabelOf(first.Issue.Source, first.Issue.EventId);
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
                Origin = WorkOrderOrigins.AutoDispatch,
                ScopeKind = WorkOrderScopes.Hosts,
                ScopeGroupIds = new List<long>(),
                AutoAttach = false,
                ReassignConflicts = false,
                Note = AutoDispatchNote,
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
                    Hosts = groupHosts, Message = ex.Message
                });
                continue;
            }

            daySyncPending += outcome.DaySync.PendingCases;
            if (outcome.WorkOrderId == 0) continue;

            if (outcome.CreatedOrder) created++;
            else merged++;
            assignedMembers += outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned;

            var skipped = outcome.SkippedConflicts
                .Select(c => (HostNameKey.Of(c.HostName), c.IssueKey))
                .ToHashSet();
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

        var users = _users.GetAll().ToDictionary(u => u.UserId);

        foreach (var (handlerId, orders) in createdOrdersByHandler)
        {
            if (orders.Count == 0) continue;
            if (handlerId == _currentUser.UserId) continue;
            if (!users.TryGetValue(handlerId, out var handler) || string.IsNullOrWhiteSpace(handler.Email)) continue;

            WorkOrderNotice notice;
            if (orders.Count == 1)
            {
                var (workOrderId, issueLabel, hosts) = orders[0];
                notice = new WorkOrderNotice(
                    WorkOrderNoticeKinds.Created, workOrderId,
                    issueLabel, WorkOrderScopes.Hosts,
                    hosts.Count, hosts, AutoDispatchNote, null,
                    handler.Account, handler.Email, _currentUser.Account, null);
            }
            else
            {
                var allHosts = orders.SelectMany(o => o.HostNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var allLabels = string.Join("、", orders.Select(o => o.IssueLabel));
                var orderListText = string.Join("\n", orders.Select(o => $"  - 單號 {o.WorkOrderId}：{o.IssueLabel}"));
                var note = $"{AutoDispatchNote}\n本次共建立 {orders.Count} 張交辦單：\n{orderListText}";

                notice = new WorkOrderNotice(
                    WorkOrderNoticeKinds.Created, orders[0].WorkOrderId,
                    $"共 {orders.Count} 項問題（{allLabels}）", WorkOrderScopes.Hosts,
                    allHosts.Count, allHosts, note, null,
                    handler.Account, handler.Email, _currentUser.Account, null);
            }

            _ = _mail.NotifyWorkOrderAsync(notice);
        }
        var perHandler = hostsByHandler
            .Where(p => p.Value > 0)
            .Select(p => new AutoDispatchHandlerDto { HandlerId = p.Key, Name = NameOf(users, p.Key), Hosts = p.Value })
            .OrderBy(p => p.HandlerId)
            .ToList();
        var unassigned = Summarize(trial.Gaps)
            .SelectMany(s => s.Unassignable)
            .GroupBy(u => u.Reason, StringComparer.Ordinal)
            .Select(g => new GapUnassignableDto { Reason = g.Key, Hosts = g.Sum(u => u.Hosts) })
            .OrderBy(u => u.Reason, StringComparer.Ordinal)
            .ToList();

        var period = $"{trial.Scope.From:yyyy-MM-dd}~{trial.Scope.To:yyyy-MM-dd}";
        var attachedHosts = perHandler.Sum(p => p.Hosts);
        var unassignedHosts = unassigned.Sum(u => u.Hosts);
        _audit.Record(
            action: AuditActions.WorkOrderAutoDispatchRun,
            summary: $"立即派工（{period}）：新建 {created} 張、併入 {merged} 張，掛入 {attachedHosts} 台，無法派 {unassignedHosts} 台，失敗 {failedGroups.Count} 組",
            targetKind: TargetKind,
            targetId: period,
            detail: new
            {
                From = trial.Scope.From, To = trial.Scope.To, CreatedOrders = created, MergedOrders = merged,
                AssignedMembers = assignedMembers,
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
            FailedGroups = failedGroups
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
        var periodTo = (to ?? DateTime.Today.AddDays(-1)).Date;
        var periodFrom = (from ?? periodTo.AddDays(-(DefaultPeriodDays - 1))).Date;
        if (periodFrom > periodTo)
            throw DomainException.Validation("起日不可晚於迄日。");

        var scope = _query.ResolveIssueScope(new RecordSearchRequest { From = periodFrom, To = periodTo });
        // 靜音不排除（不用 scope.Exclusion）：派工決策以紀錄日判定靜音，已在派工脈絡處理
        var issues = _aggregates.Aggregate(IssueExclusion.None, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels)
            .Select(a => (a.Source, a.EventId))
            .ToList();
        // 靜音不排除：理由同上
        var occurrences = _aggregates.LatestOccurrences(
            IssueExclusion.None, issues, scope.From, scope.To, scope.HostIds, scope.VisibleSeverities, scope.DayRiskLevels);

        var openKeys = _cases.GetOpenKeys().ToHashSet();
        var hostsById = _hosts.GetAll().ToDictionary(h => h.HostId);

        var candidates = occurrences
            .Where(o => hostsById.ContainsKey(o.HostId))
            .Select(o => (Host: hostsById[o.HostId], Occurrence: o, Parsed: IssueSignatureKey.TryParseFull(o.IssueKey)))
            .Where(x => x.Parsed != null && !openKeys.Contains((HostNameKey.Of(x.Host.HostName), x.Occurrence.IssueKey)))
            .OrderBy(x => x.Host.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Occurrence.IssueKey, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count > _maxOccurrences) return new Trial(scope, TooLarge: true, new List<GapDecision>());

        var settings = JsonSerializer.Deserialize<SystemSettings>(JsonSerializer.Serialize(_settings.Get()))!;
        settings.AutoDispatchEnabled = true;
        var now = DateTime.Now;
        var ctx = DispatchContext.Build(_candidates.Build(), _issueOwners, _orders, _cases, _noiseMarks, settings, now);
        ctx.PreloadDismissed(candidates.Select(x => x.Host.HostName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        // 抑制：清單與規則各讀一次；每台主機的生效中抑制算一次（純記憶體）
        var allSuppressions = _suppressions.LoadAll();
        var rulesById = KnownIssueCatalog.ResolveRules(_rules)
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
            if (decision.Kind == DispatchDecisionKind.CreateFor)
            {
                ctx.RegisterOrder(new WorkOrder
                {
                    WorkOrderId = --virtualOrderId,
                    SourceName = issue.Source,
                    EventId = issue.EventId,
                    IssueLabel = LabelOf(issue.Source, issue.EventId),
                    HandlerId = decision.HandlerId!.Value,
                    Origin = decision.Origin!,
                    ScopeKind = WorkOrderScopes.Hosts,
                    CreatedAt = now
                });
            }
            ctx.Commit(decision, issue.Source, issue.EventId);

            gaps.Add(new GapDecision(host, issue, occurrence.IssueKey, occurrence.LastSeen, decision));
        }

        return new Trial(scope, TooLarge: false, gaps);
    }

    /// <summary>依問題彙總試跑決策（待派清單的列與立即派工的無法派計數共用）</summary>
    private static List<IssueSummary> Summarize(List<GapDecision> gaps) =>
        gaps
            .GroupBy(g => (SourceKey: g.Issue.Source.ToUpperInvariant(), g.Issue.EventId))
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

    /// <summary>派不出去的原因（無候選的細分或自動派工關閉）；其餘略過屬閘門排除，回 null</summary>
    private static string? UnassignableReasonOf(DispatchDecision decision) => decision.SkipReason switch
    {
        WorkOrderDispatcher.SkipNoCandidate => decision.NoCandidateDetail,
        WorkOrderDispatcher.SkipDisabled => WorkOrderDispatcher.SkipDisabled,
        _ => null
    };

    private static int DistinctHosts(IEnumerable<GapDecision> gaps) => gaps.Select(x => x.Host.HostId).Distinct().Count();

    private static string LabelOf(string source, int eventId) => $"{source} {eventId}";

    private string NameOf(Dictionary<long, WebUser> users, long userId) =>
        users.TryGetValue(userId, out var user) ? _displayNames.WithAccount(user.DisplayName, user.Account) : DeletedName;

    private sealed record GapDecision(WebHost Host, LogIssueSignature Issue, string IssueKey, DateTime LastSeen, DispatchDecision Decision);

    private sealed record Trial(IssueScope Scope, bool TooLarge, List<GapDecision> Gaps);

    private sealed record IssueSummary(
        string Source,
        int EventId,
        int GapHosts,
        Dictionary<string, int> Excluded,
        List<(long HandlerId, int Hosts, bool WillCreate)> Suggested,
        List<GapUnassignableDto> Unassignable,
        List<WebHost> UnseenHosts);
}
