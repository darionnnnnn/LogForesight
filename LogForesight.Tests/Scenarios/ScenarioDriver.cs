using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LogForesight.Core.Time;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Extensions.DependencyInjection;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 逐日情境驅動器。每天固定三步：當天事件 → 分析與夜間派工 → 當天人為動作。
///
/// 分析與派工走正式流程：<see cref="NetiqPipelineService"/> 搭配假的 Sentinel 查詢端
/// （規則、趨勢、關聯、主機日風險、案件同步、夜間派工都是真的），夜間派工用真實的 <see cref="DispatchContext"/>，
/// 候選人來自站台的 <see cref="IDispatchCandidateSource"/>（與正式排程同一份規則）；
/// 人為動作走 HTTP API，各帳號以自己的登入身分呼叫。站台與分析共用同一個 SQLite 檔。
///
/// 時間：<c>NetiqPipelineService</c> 的 <c>runToday</c> 可注入，所以「第 N 天」對應的資料日是
/// 「執行當天為錨點、第 1 天＝錨點往前 TotalDays 天」，最後一天的資料日是昨天；輸出一律用「第 N 天」。
/// 交辦、靜音、觀察中的「現在」「今天」（建單時間、稽核時間、靜音天數起算、回覆時間、到期判定等）取自站台 DI 的
/// <see cref="TimeProvider"/>，情境站台以 <see cref="ScenarioClock"/> 取代它；每一天開始前驅動器把時鐘撥到
/// 該天的「執行當天」（資料日的隔天）09:00，所以夜間派工與人為動作寫下的時間都落在第 N 天。
/// 範圍外的流程（分析、PRTG、排程）仍讀真實時鐘，驅動器不輸出那些時間。
/// </summary>
public static class ScenarioDriver
{
    public static ScenarioResult Run(Scenario scenario) => RunAsync(scenario).GetAwaiter().GetResult();

    public static async Task<ScenarioResult> RunAsync(Scenario scenario)
    {
        Validate(scenario);
        CryptoHelper.ResetForTests();
        using var site = new ScenarioSite();
        var seeded = await SeedAsync(site, scenario);

        var totalDays = scenario.TotalDays;
        var anchor = DateTime.Today;
        DateTime DataDateOf(int day) => anchor.AddDays(-(totalDays - day + 1));
        var dateToDay = Enumerable.Range(1, totalDays).ToDictionary(DataDateOf, d => d);

        var tracker = new Tracker(site);
        tracker.Baseline();

        var days = new List<DayResult>();
        for (var day = 1; day <= totalDays; day++)
        {
            scenario.Plans.TryGetValue(day, out var plan);
            var dataDate = DataDateOf(day);
            // 假時鐘撥到第 N 天的執行當天（資料日隔天）09:00：夜間派工與當天的人為動作共用同一個「現在」
            site.Clock.SetLocal(dataDate.AddDays(1).AddHours(9));
            tracker.BeginDay();
            foreach (var change in plan?.SettingChanges ?? new List<Action<SystemSettings>>())
                site.Get<ISystemSettingsStore>().Update(change);

            // 1. 當天事件 → 2. 分析與夜間派工
            var night = await RunNightAsync(site, scenario, seeded, plan, day, dataDate);
            var outboxAfterAnalysis = tracker.NewOutbox("分析");
            var auditAfterAnalysis = tracker.NewAudit("分析");

            // 3. 當天人為動作
            var actionResults = new List<ActionResultView>();
            var context = new ScenarioActionContext(site, day, DataDateOf, seeded.HostIds, seeded.HostGroupIds, seeded.UserSpecs);
            foreach (var action in plan?.ActionList ?? new List<ScenarioAction>())
            {
                var result = await action.RunAsync(context);
                actionResults.Add(new ActionResultView(action.Actor, action.Describe(), result.StatusCode, result.ErrorCode, result.Message, result.Body));
            }

            foreach (var inspect in plan?.InspectList ?? new List<Action<ScenarioSite, ScenarioActionContext>>())
                inspect(site, context);

            // 當天寄出到期的交辦摘要（驗證管理者摘要信的內文；寄送時刻取當天 13:00，涵蓋 08:00／12:00 兩個時刻）
            if (plan?.SendDigest == true)
                await site.Get<MailNotificationService>().CheckAndSendWorkOrderDigestAsync(site.Clock.LocalNow().Date.AddHours(13));

            // 上報信等即時通知是 fire-and-forget；替身寄送是同步完成的，留一小段讓背景續行收尾
            if (actionResults.Count > 0) await Task.Delay(30);

            // 4. 當天結束時的讀取探針（只走 HTTP）
            var probes = new List<ProbeView>();
            foreach (var probe in plan?.ProbeList ?? new List<ScenarioProbe>())
                probes.Add(await probe.RunAsync(context));

            var outbox = outboxAfterAnalysis.Concat(tracker.NewOutbox("動作")).ToList();
            var audit = auditAfterAnalysis.Concat(tracker.NewAudit("動作")).ToList();

            days.Add(new DayResult
            {
                Day = day,
                HostDaysAnalyzed = night.HostDaysAnalyzed,
                HostDaysFailed = night.HostDaysFailed,
                AnalysisWarnings = night.Warnings,
                Hosts = night.Hosts,
                Issues = night.Issues,
                Alerts = night.Alerts,
                Decisions = night.Decisions,
                Dispatch = night.Dispatch,
                Orders = tracker.OrderSnapshot(),
                OutboxAdded = outbox,
                AuditAdded = audit,
                Actions = actionResults,
                Probes = probes,
                MailsSent = tracker.NewMail()
            });
        }

        var result0 = new ScenarioResult(scenario.Name, days, dateToDay);
        result0.SetVerdict(ScenarioVerdict.Evaluate(scenario, result0));
        return result0;
    }

    // ── 驗證 ──────────────────────────────────────────────────────────────

    private static void Validate(Scenario s)
    {
        if (s.Hosts.Count == 0) throw new ArgumentException("情境至少要有一台主機");
        Unique(s.Hosts.Select(h => h.Name), "主機名稱");
        Unique(s.Users.Select(u => u.Account), "使用者帳號");
        var users = s.Users.Select(u => u.Account).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = s.Hosts.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in s.Hosts.SelectMany(h => h.Owners))
            if (!users.Contains(owner)) throw new ArgumentException($"主機負責人「{owner}」不在情境的使用者清單內");
        foreach (var plan in s.Plans.Values)
        {
            foreach (var (host, _) in plan.EventSets)
                if (!hosts.Contains(host)) throw new ArgumentException($"第 {plan.Day} 天的事件指向不存在的主機「{host}」");
            foreach (var action in plan.ActionList)
                if (!users.Contains(action.Actor)) throw new ArgumentException($"第 {plan.Day} 天的動作帳號「{action.Actor}」不在情境的使用者清單內");
            foreach (var probe in plan.ProbeList)
                if (!users.Contains(probe.Actor)) throw new ArgumentException($"第 {plan.Day} 天的探針帳號「{probe.Actor}」不在情境的使用者清單內");
            var dupProbe = plan.ProbeList.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
            if (dupProbe != null) throw new ArgumentException($"第 {plan.Day} 天的探針名稱重複：{dupProbe.Key}");
        }
    }

    private static void Unique(IEnumerable<string> values, string what)
    {
        var dup = values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"{what}重複：{dup.Key}");
    }

    // ── 種資料（全部經站台自己的 store）────────────────────────────────────

    private sealed record Seeded(
        IReadOnlyDictionary<string, long> HostIds, IReadOnlyDictionary<string, long> HostGroupIds,
        IReadOnlyDictionary<string, UserSpec> UserSpecs, IReadOnlyDictionary<string, string> HostIps);

    private static async Task<Seeded> SeedAsync(ScenarioSite site, Scenario scenario)
    {
        var settingsStore = site.Get<ISystemSettingsStore>();
        settingsStore.Update(s =>
        {
            s.AutoDispatchEnabled = scenario.Settings.AutoDispatchEnabled;
            if (scenario.Settings.WorkOrderMailQueue)
            {
                s.MailEnabled = true;
                s.MailNotifyWorkOrders = true;
            }
            scenario.Settings.Customize?.Invoke(s);
        });

        var sentinels = site.Get<ISentinelStore>();
        var sentinelIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in scenario.Hosts.Select(h => h.Sentinel).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var saved = sentinels.Upsert(new Sentinel { Name = name, BaseUrl = "https://sentinel.scenario.test", Username = "u", PasswordEnc = "p", Active = true });
            sentinelIds[name] = saved.SentinelId;
        }

        var hostGroupStore = site.Get<IHostGroupStore>();
        var hostGroupIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var groupNames = scenario.Hosts.SelectMany(h => h.Groups)
            .Concat(scenario.Users.SelectMany(u => u.AuthorizedHostGroups))
            .Concat(scenario.ExtraHostGroups)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in groupNames)
            hostGroupIds[name] = hostGroupStore.Upsert(new HostGroup { GroupName = name }).GroupId;

        var userGroups = site.Get<IUserGroupStore>();
        var users = site.Get<IUserStore>();
        var access = site.Get<IGroupAccessStore>();
        var userIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in scenario.Users)
        {
            var group = userGroups.Upsert(new UserGroup
            {
                GroupName = "ug-" + spec.Account,
                Role = spec.Role,
                Active = true,
                DispatchPool = spec.InDispatchPool
            });
            var user = users.Upsert(new WebUser
            {
                Account = spec.Account,
                DisplayName = spec.Account,
                Email = spec.HasEmail ? $"{spec.Account}@scenario.test" : null,
                Active = spec.Active,
                GroupIds = new List<long> { group.GroupId },
                DispatchPaused = spec.DispatchPaused
            });
            userIds[spec.Account] = user.UserId;
            access.SetForUserGroup(group.GroupId, spec.AuthorizedHostGroups.Select(g => hostGroupIds[g]).ToArray());
        }

        var hostStore = site.Get<IHostStore>();
        var hostIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var hostIps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < scenario.Hosts.Count; i++)
        {
            var spec = scenario.Hosts[i];
            var ip = spec.Ip ?? $"10.76.0.{i + 1}";
            var saved = hostStore.Upsert(new WebHost
            {
                HostName = spec.Name,
                IpAddress = ip,
                Os = spec.IsLinux ? WebHost.OsLinux : WebHost.OsWindows,
                SentinelId = sentinelIds[spec.Sentinel],
                NetiqServer = spec.Sentinel,
                Source = "netiq",
                Active = true,
                GroupIds = spec.Groups.Select(g => hostGroupIds[g]).ToList(),
                OwnerUserIds = spec.Owners.Select(o => userIds[o]).ToList()
            });
            hostIds[spec.Name] = saved.HostId;
            hostIps[spec.Name] = ip;
        }

        foreach (var spec in scenario.Users.Where(u => u.Active))
            await site.LoginAsync(spec.Account);

        return new Seeded(hostIds, hostGroupIds, scenario.Users.ToDictionary(u => u.Account, StringComparer.OrdinalIgnoreCase), hostIps);
    }

    // ── 一晚的分析與夜間派工 ────────────────────────────────────────────────

    private sealed record NightResult(
        int HostDaysAnalyzed, int HostDaysFailed, IReadOnlyList<string> Warnings, IReadOnlyList<HostDayView> Hosts,
        IReadOnlyList<IssueView> Issues, IReadOnlyList<AlertView> Alerts, IReadOnlyList<DispatchDecisionView> Decisions,
        DispatchSummaryView Dispatch);

    private static async Task<NightResult> RunNightAsync(
        ScenarioSite site, Scenario scenario, Seeded seeded, DayPlan? plan, int day, DateTime dataDate)
    {
        var runToday = dataDate.AddDays(1);
        var backend = site.Get<StorageBackend>();
        var hostStore = site.Get<IHostStore>();
        var sentinelStore = site.Get<ISentinelStore>();
        var issueOwners = site.Get<IIssueOwnerStore>();
        var systemSettings = site.Get<ISystemSettingsStore>().Get();

        // 假的 Sentinel 查詢端：每個 Sentinel 一個替身，依請求的資料日／主機 IP／平台回當天的合成事件
        var clients = new Dictionary<string, FakeSentinelSearchClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in scenario.Hosts.Select(h => h.Sentinel).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var client = new FakeSentinelSearchClient();
            client.Responder = request => Respond(scenario, seeded, plan, name, dataDate, request);
            clients[name] = client;
        }

        var consoleLines = new List<string>();
        var console = new RecordingRunConsole(consoleLines);

        // BatchRunRecorder 必須在涵蓋整趟執行的 async 方法本體內建構（AsyncLocal 作用域），所以放在這裡而不是輔助方法
        using var runRecorder = new BatchRunRecorder(site.Get<BatchRunStore>(), "scenario-host", Array.Empty<string>());
        var ai = new FakeAiService();
        var reportService = new RiskReportService(ai, new FakeReportSink());
        var suppressionStore = new MuteAwareSuppressionStore(site.Get<ISuppressionStore>(), issueOwners);
        var caseCoordinator = new IssueCaseCoordinator(
            backend.IssueCaseStore(), backend.IssueHandlingStore(), backend.RecordHandlingStore(),
            backend.RecordStore(), hostStore, issueOwners);

        // 夜間派工：與 AnalysisOrchestrator 同一份組裝——候選人快照來自站台的 IDispatchCandidateSource，
        // 派工脈絡是真的 DispatchContext（不是 NightlyDispatchFakes 的空殼）
        var candidatePool = site.Get<IDispatchCandidateSource>().Build();
        var workOrderStore = backend.WorkOrderStore();
        var issueCaseStore = backend.IssueCaseStore();
        var dispatchContext = DispatchContext.Build(
            candidatePool, issueOwners, workOrderStore, issueCaseStore, site.Get<INoiseMarkStore>(), systemSettings, site.Clock.LocalNow());
        var coordinator = new WorkOrderCoordinator(
            workOrderStore, issueCaseStore, backend.IssueHandlingStore(), caseCoordinator, backend.RecordHandlingStore(), hostStore);
        var nightlyDispatch = new NightlyDispatch(coordinator, dispatchContext, hostStore);

        var netiqOptions = new NetiqOptions { BackfillDays = 1, MaxParallelServers = 1, MaxParallelQueriesPerServer = 1 };
        var pipeline = new NetiqPipelineService(
            backend, netiqOptions, sentinelStore, hostStore, new EventLogService(), ai, suppressionStore, reportService,
            runRecorder, caseCoordinator, nightlyDispatch, console,
            riskyEventStore: backend.RiskyEventStore(), rawEventRetentionDays: systemSettings.RawEventRetentionDays,
            useAi: false, progress: null, clientFactory: sentinel => clients[sentinel.Name],
            workflow: site.Get<HostDayWorkflowService>(), prtgEnabled: false, runToday: runToday);

        var result = await pipeline.RunAsync(HostListSelection.FromStore(hostStore, sentinelStore), trendWindowDays: 14);

        var summary = nightlyDispatch.FlushRun(site.Clock.LocalNow());
        // 與 SchedulerHostedService 同一步：夜間派工的結果入通知佇列（寄送由背景服務負責，這裡不寄）
        if (summary.PerHandler.Count > 0) site.Get<MailNotificationService>().EnqueueNightlyDispatch(summary);
        // 與 SchedulerHostedService 同一步：夜間派工結束後的管理者提醒（上報逾期、派不出去、上報沒有收件人）
        site.Get<AdminReminderService>().RunAfterNightly(summary);

        foreach (var client in clients.Values) await client.DisposeAsync();

        var warnings = consoleLines.Select(l => l.TrimStart()).Where(l => l.StartsWith('⚠') || l.StartsWith('✗')).ToList();
        var collected = Collect(site, seeded, scenario, dataDate);
        var userNames = site.Get<IUserStore>().GetAll().ToDictionary(u => u.UserId, u => u.Account);
        var perHandler = summary.PerHandler
            .OrderBy(p => userNames.GetValueOrDefault(p.Key, p.Key.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
            .SelectMany(p => p.Value.Select(l =>
                $"{userNames.GetValueOrDefault(p.Key, p.Key.ToString(CultureInfo.InvariantCulture))}：單#{l.WorkOrderId} " +
                $"{(l.CreatedThisRun ? "新建" : "續掛")} +{l.AddedMembers} 台（{l.IssueLabel}）" +
                (l.RecurrenceMembers > 0 ? $"，復發 {l.RecurrenceMembers} 台" : "")))
            .ToList();

        return new NightResult(
            result.HostDaysAnalyzed, result.HostsFailed, warnings, collected.Hosts, collected.Issues, collected.Alerts,
            collected.Decisions,
            new DispatchSummaryView(summary.CreatedOrders, summary.AttachedMembers,
                new SortedDictionary<string, int>(summary.SkipCounts.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal),
                perHandler));
    }

    private static SentinelSearchResult Respond(
        Scenario scenario, Seeded seeded, DayPlan? plan, string sentinelName, DateTime dataDate, SentinelSearchRequest request)
    {
        var events = new List<SentinelEvent>();
        // 請求的查詢日只有「本晚的資料日」有合成事件；其他日（回補範圍之外的探測）回空
        if (plan != null && request.Start.ToLocalTime().Date == dataDate)
        {
            var isLinuxQuery = request.Filter.Contains($"{SentinelFieldMap.LinuxProgram}:", StringComparison.Ordinal);
            foreach (var (hostName, hostEvents) in plan.EventSets)
            {
                var host = scenario.Hosts.First(h => string.Equals(h.Name, hostName, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(host.Sentinel, sentinelName, StringComparison.OrdinalIgnoreCase)) continue;
                if (host.IsLinux != isLinuxQuery) continue;
                var ip = seeded.HostIps[host.Name];
                if (!request.Filter.Contains(ip, StringComparison.Ordinal)) continue;
                events.AddRange(hostEvents.Select(e => ToSentinelEvent(host, ip, e, dataDate)));
            }
        }

        return new SentinelSearchResult { Events = events, Found = events.Count, State = SentinelJobState.Completed };
    }

    private static SentinelEvent ToSentinelEvent(HostSpec host, string ip, ScenarioEvent e, DateTime dataDate)
    {
        var local = DateTime.SpecifyKind(dataDate.Date + e.At, DateTimeKind.Local);
        var utc = new DateTimeOffset(local).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var fields = new Dictionary<string, string>
        {
            [SentinelFieldMap.HostIp] = ip,
            [SentinelFieldMap.HostName] = host.Name,
            [SentinelFieldMap.Timestamp] = utc,
            [SentinelFieldMap.Message] = e.Message
        };

        if (host.IsLinux)
        {
            fields[SentinelFieldMap.LinuxProgram] = e.LinuxProgram ?? e.Source;
            fields[SentinelFieldMap.Severity] = e.Level switch
            {
                ScenarioEventLevel.Error => "3",
                ScenarioEventLevel.Warning => "2",
                _ => "1"
            };
        }
        else
        {
            fields[SentinelFieldMap.EventId] = e.EventId.ToString(CultureInfo.InvariantCulture);
            fields[SentinelFieldMap.Source] = e.Source;
            fields[SentinelFieldMap.LogName] = e.LogName;
            fields[SentinelFieldMap.Severity] = e.Level switch
            {
                ScenarioEventLevel.Error => "4",
                ScenarioEventLevel.Warning => "2",
                ScenarioEventLevel.FailureAudit => "4",
                ScenarioEventLevel.SuccessAudit => "0",
                _ => "1"
            };
            if (e.Level == ScenarioEventLevel.FailureAudit) fields[SentinelFieldMap.XdasOutcome] = "1";
            if (e.Level == ScenarioEventLevel.SuccessAudit) fields[SentinelFieldMap.XdasOutcome] = "0";
        }

        return new SentinelEvent(fields);
    }

    // ── 讀回當晚結果 ──────────────────────────────────────────────────────

    private sealed record Collected(
        IReadOnlyList<HostDayView> Hosts, IReadOnlyList<IssueView> Issues, IReadOnlyList<AlertView> Alerts,
        IReadOnlyList<DispatchDecisionView> Decisions);

    private static Collected Collect(ScenarioSite site, Seeded seeded, Scenario scenario, DateTime dataDate)
    {
        var backend = site.Get<StorageBackend>();
        var workflow = site.Get<HostDayWorkflowService>();
        var cases = backend.IssueCaseStore();

        var hosts = new List<HostDayView>();
        var issues = new List<IssueView>();
        var alerts = new List<AlertView>();
        var decisions = new List<DispatchDecisionView>();

        foreach (var spec in scenario.Hosts)
        {
            var hostId = seeded.HostIds[spec.Name];
            var record = backend.RecordStore(new HostKey { HostId = hostId, HostName = spec.Name })
                .ReadRecent(dataDate, 1).FirstOrDefault(r => r.Date.Date == dataDate);
            if (record == null)
            {
                hosts.Add(new HostDayView(spec.Name, false, "", "", 0, 0, false));
                continue;
            }

            hosts.Add(new HostDayView(spec.Name, true, record.RiskLevel, record.RiskBasis ?? "", record.ErrorCount,
                record.WarningCount, record.DataIncomplete));

            foreach (var issue in record.TopIssues)
                issues.Add(new IssueView(spec.Name, issue.SourceEventLabel, issue.Severity.ToString(), issue.Count,
                    issue.ElevatesDayRisk, issue.Suppressed, issue.Trend.ToString()));

            alerts.AddRange(record.TrendAlerts.Select(a => new AlertView(spec.Name, "趨勢", a, false)));
            alerts.AddRange(record.SuppressedTrendAlerts.Select(a => new AlertView(spec.Name, "趨勢", a, true)));
            alerts.AddRange(record.CorrelationAlerts.Select(a => new AlertView(spec.Name, "關聯", a, false)));
            alerts.AddRange(record.SuppressedCorrelationAlerts.Select(a => new AlertView(spec.Name, "關聯", a, true)));

            var state = workflow.Get(hostId, dataDate);
            foreach (var issue in record.TopIssues)
            {
                var key = IssueSignatureKey.For(issue);
                var intent = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
                string outcome;
                if (state == null) outcome = "untracked";
                else if (state.CaseFailedIntents.Contains(intent)) outcome = "failed";
                else if (state.CaseSkippedIntents.Contains(intent)) outcome = "skipped";
                else if (state.CaseDeliveredIntents.Contains(intent)) outcome = "delivered";
                else if (state.CaseIntents.Contains(intent)) outcome = "deferred";
                else outcome = "untracked";

                string? reason = null;
                if (state != null && state.CaseDispositionReasons.TryGetValue(intent, out var r)) reason = r;
                long? orderId = outcome == "delivered" ? cases.GetOpen(spec.Name, key)?.WorkOrderId : null;
                decisions.Add(new DispatchDecisionView(spec.Name, issue.SourceEventLabel, outcome, reason, orderId));
            }
        }

        return new Collected(hosts, issues, alerts, decisions);
    }

    // ── 逐日差異追蹤（通知佇列、稽核、交辦單）─────────────────────────────────

    private sealed class Tracker
    {
        private readonly ScenarioSite _site;
        private readonly HashSet<string> _seenOutbox = new(StringComparer.Ordinal);
        private long _lastAudit;
        private HashSet<long> _knownOrders = new();
        private HashSet<long> _ordersAtDayStart = new();

        public Tracker(ScenarioSite site) => _site = site;

        public void Baseline()
        {
            foreach (var key in _site.Get<WorkOrderOutboxStore>().Get().Items.Keys) _seenOutbox.Add(key);
            _lastAudit = ReadAudit().Select(a => a.AuditId).DefaultIfEmpty(0).Max();
            _knownOrders = OrderRef.AllOrders(_site.Get<IWorkOrderStore>()).Select(o => o.WorkOrderId).ToHashSet();
        }

        public void BeginDay() => _ordersAtDayStart = _knownOrders.ToHashSet();

        private int _mailSeen;

        public List<SentMailRecord> NewMail()
        {
            var all = _site.SentMail;
            var fresh = all.Skip(_mailSeen).ToList();
            _mailSeen = all.Count;
            return fresh;
        }

        public List<OutboxItemView> NewOutbox(string phase)
        {
            var users = _site.Get<IUserStore>().GetAll().ToDictionary(u => u.UserId, u => u.Account);
            var fresh = _site.Get<WorkOrderOutboxStore>().Get().Items.Where(p => !_seenOutbox.Contains(p.Key)).ToList();
            foreach (var p in fresh) _seenOutbox.Add(p.Key);
            return fresh.Select(p => p.Value)
                .Select(i => new OutboxItemView(phase, i.Kind, users.GetValueOrDefault(i.RecipientUserId, $"user#{i.RecipientUserId}"),
                    i.WorkOrderId, i.MemberDelta, i.ActorAccount, i.Reason, i.Status, i.Until, i.BlocksRedispatch,
                    i.Detail, i.ReminderEscalated, i.ReminderUnassignable, i.ReminderNoRecipient))
                .OrderBy(i => i.WorkOrderId).ThenBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Recipient, StringComparer.Ordinal)
                .ToList();
        }

        public List<AuditItemView> NewAudit(string phase)
        {
            var fresh = ReadAudit().Where(a => a.AuditId > _lastAudit).OrderBy(a => a.AuditId).ToList();
            if (fresh.Count > 0) _lastAudit = fresh.Max(a => a.AuditId);
            return fresh.Select(a => new AuditItemView(phase, a.Account, a.Action, a.Result.ToString(), a.Summary)).ToList();
        }

        private List<AuditEntry> ReadAudit()
        {
            var store = _site.Get<AuditLogStore>();
            var all = new List<AuditEntry>();
            for (var page = 1; ; page++)
            {
                var batch = store.Query(new AuditQuery { Ascending = true, Page = page, PageSize = 200 });
                all.AddRange(batch.Items);
                if (batch.Items.Count < 200) break;
            }
            return all;
        }

        public List<OrderView> OrderSnapshot()
        {
            var users = _site.Get<IUserStore>().GetAll().ToDictionary(u => u.UserId, u => u.Account);
            string UserName(long id) => users.GetValueOrDefault(id, $"user#{id}");
            var cases = _site.Get<IIssueCaseStore>();
            var orders = OrderRef.AllOrders(_site.Get<IWorkOrderStore>());
            var views = new List<OrderView>();
            foreach (var order in orders)
            {
                var members = cases.GetByWorkOrder(order.WorkOrderId, 0, 500)
                    .Select(c => new OrderMemberView(c.HostName, c.IssueLabel, c.Status, c.HandlerId is { } h ? UserName(h) : "(未指派)"))
                    .OrderBy(m => m.Host, StringComparer.Ordinal).ThenBy(m => m.Issue, StringComparer.Ordinal)
                    .ToList();
                var state = order.ClosedAt == null ? "進行中" : $"已結案({order.ClosedReason})";
                views.Add(new OrderView(order.WorkOrderId, order.IssueLabel, UserName(order.HandlerId), order.Origin, state,
                    !_ordersAtDayStart.Contains(order.WorkOrderId), members));
            }
            _knownOrders = orders.Select(o => o.WorkOrderId).ToHashSet();
            return views;
        }
    }
}
