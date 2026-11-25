using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 F（F11-1）：管理員待辦新增類別（昨晚取數結果、可派未派、處理人已停用或暫停仍有單、未分組主機）。
/// 每類的計數定義、全 0、Manager／Dev 不帶、站內提醒與摘要信同一彙整、信件去重與間隔、快取。
/// </summary>
public sealed class AdminReminderTodoTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 20);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeTimeProvider _clock;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly MailNotifyStateStore _state;
    private readonly WorkOrderOutboxStore _outbox;
    private readonly SchedulerRunState _runState = new();
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly MailNotificationService _mail;
    private readonly long _hostGroupId = 5;
    private int _seq;

    public AdminReminderTodoTests()
    {
        _clock = LocalClock(Today.AddHours(23));
        _cases.Clock = _clock;
        _orders = new FakeWorkOrderStore(_cases, _clock);
        _settings.Update(s => { s.MailEnabled = true; s.MailNotifyWorkOrders = true; s.EscalationReminderDays = 3; });
        var admin = _groups.Upsert(new UserGroup { GroupName = "管理者", Role = UserRole.Admin, Active = true });
        _users.Upsert(new WebUser { Account = "boss", DisplayName = "boss", Email = "boss@example.com", Active = true, GroupIds = new List<long> { admin.GroupId } });
        _state = new MailNotifyStateStore(_fx.Blob("mail_notify_state"));
        _outbox = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey));
        _mail = new MailNotificationService(
            _settings, new FakeSmtpMailSender(), _hosts, _users, _groups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), new FakeHandlingStore(), _state,
            new ScheduleFreshnessService(
                new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(_fx.Blob("schedule_options"))),
            _outbox, _orders, _cases, new FakeRuleStore(), new FakeIssueOwnerStore());
    }

    public void Dispose() => _fx.Dispose();

    private AdminReminderService Service(SummaryCache? cache = null) => new(
        _settings, _orders, _cases, _hosts, _users, _groups, _state, _mail, new FakeRuleStore(), clock: _clock,
        runState: _runState, cache: cache, displayNames: new UserDisplayNameService(_settings));

    private static FakeTimeProvider LocalClock(DateTime localNow)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        return clock;
    }

    private void SetNow(DateTime local) => _clock.SetUtcNow(LocalClock(local).GetUtcNow());

    private WebHost Grouped(string name) => _hosts.Upsert(new WebHost { HostName = name, GroupIds = new List<long> { _hostGroupId } });

    /// <summary>給處理人一張進行中單（底下 <paramref name="members"/> 件進行中案件）</summary>
    private long OrderOf(long handlerId, int members = 1)
    {
        var id = _orders.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = 1000 + ++_seq, IssueLabel = "disk 153", HandlerId = handlerId, Origin = WorkOrderOrigins.Manual,
            ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin", CreatedAt = Today.AddDays(-2)
        });
        for (var i = 0; i < members; i++)
        {
            var host = Grouped($"H-{handlerId}-{++_seq}");
            _cases.Save(new IssueCase
            {
                CaseId = $"case-{_seq:D4}", HostName = host.HostName, IssueKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Error),
                IssueLabel = "disk 153", Status = IssueHandlingStatuses.InProgress, HandlerId = handlerId, WorkOrderId = id,
                CreatedAt = Today.AddDays(-2), UpdatedAt = Today.AddDays(-2), FirstLinkedDate = Today.AddDays(-2), LastLinkedDate = Today.AddDays(-2)
            });
        }
        return id;
    }

    private void FailLastRun(DateTime endedAt, bool success = false) =>
        _runState.EndRun(new RunOutcome(success, success ? null : "boom", "schedule", endedAt));

    // ── 昨晚取數結果 ─────────────────────────────────────────────────────────

    [Fact]
    public void 取數結果_最近一趟失敗算1_成功或還沒跑過是0()
    {
        Assert.Equal(0, Service().Compute(true).FetchFailedCount);   // 站台啟動後沒跑過

        FailLastRun(Today.AddHours(2));
        var failed = Service().Compute(true);
        Assert.Equal(1, failed.FetchFailedCount);
        Assert.Equal(Today.AddHours(2), failed.FetchFailed!.EndedAt);
        Assert.Equal("schedule", failed.FetchFailed.Trigger);
        Assert.True(failed.Any);

        FailLastRun(Today.AddHours(3), success: true);              // 之後成功：回到 0
        var ok = Service().Compute(true);
        Assert.Equal(0, ok.FetchFailedCount);
        Assert.Null(ok.FetchFailed);
    }

    // ── 處理人已停用或暫停仍有單 ─────────────────────────────────────────────

    [Fact]
    public void 停用或暫停的處理人名下仍有單才算_單位是人_明細帶單數與台數()
    {
        var disabled = _users.Upsert(new WebUser { Account = "gone", DisplayName = "離職者", Active = false });
        var paused = _users.Upsert(new WebUser { Account = "rest", DisplayName = "休假者", Active = true, DispatchPaused = true });
        var normal = _users.Upsert(new WebUser { Account = "busy", DisplayName = "正常者", Active = true });
        var pausedNoOrder = _users.Upsert(new WebUser { Account = "idle", DisplayName = "暫停無單", Active = true, DispatchPaused = true });
        OrderOf(disabled.UserId, members: 2);
        OrderOf(disabled.UserId, members: 1);   // 同一人兩張單：仍算 1 人
        OrderOf(paused.UserId, members: 3);
        OrderOf(normal.UserId, members: 4);
        OrderOf(424242, members: 1);             // 查無帳號（已刪除）：不在這一類

        var dto = Service().Compute(true);

        Assert.Equal(2, dto.InactiveHandlerCount);
        var byId = dto.InactiveHandlers.ToDictionary(h => h.UserId);
        Assert.Equal((true, 2, 3), (byId[disabled.UserId].Disabled, byId[disabled.UserId].ActiveWorkOrders, byId[disabled.UserId].ActiveMembers));
        Assert.Equal((false, 1, 3), (byId[paused.UserId].Disabled, byId[paused.UserId].ActiveWorkOrders, byId[paused.UserId].ActiveMembers));
        Assert.False(byId.ContainsKey(424242));
        Assert.DoesNotContain(dto.InactiveHandlers, h => h.UserId == normal.UserId || h.UserId == pausedNoOrder.UserId);
    }

    /// <summary>
    /// 待辦的「處理人已停用或暫停仍有單」人數＝點連結（handlerInactive=1）後清單裡不重複的處理人數。
    /// 三種原因各一人：帳號停用、失去 Handle、暫停接單；正常處理人兩邊都不算。
    /// </summary>
    [Fact]
    public void 處理人無法接單的待辦人數_等於連結篩選後清單的不重複處理人數()
    {
        var userRole = _groups.Upsert(new UserGroup { GroupName = "處理人", Role = UserRole.User, Active = true });
        var managerRole = _groups.Upsert(new UserGroup { GroupName = "主管", Role = UserRole.Manager, Active = true });
        var capabilities = new UserCapabilityResolver(_groups, _hosts);
        var disabled = _users.Upsert(new WebUser { Account = "gone", Active = false, GroupIds = new List<long> { userRole.GroupId } });
        var noHandle = _users.Upsert(new WebUser { Account = "mgr", Active = true, GroupIds = new List<long> { managerRole.GroupId } });
        var paused = _users.Upsert(new WebUser { Account = "rest", Active = true, DispatchPaused = true, GroupIds = new List<long> { userRole.GroupId } });
        var normal = _users.Upsert(new WebUser { Account = "busy", Active = true, GroupIds = new List<long> { userRole.GroupId } });
        foreach (var u in new[] { disabled, noHandle, paused, normal }) { OrderOf(u.UserId, 1); OrderOf(u.UserId, 1); }

        var reminder = new AdminReminderService(
            _settings, _orders, _cases, _hosts, _users, _groups, _state, _mail, new FakeRuleStore(), clock: _clock,
            runState: _runState, displayNames: new UserDisplayNameService(_settings), capabilities: capabilities)
            .Compute(true);
        var listed = new WorkOrderQueryService(
            _orders, _cases, _users, _hosts, new FakeHostGroupStore(), new FakeRuleStore(), new AlwaysVisibleService(_hosts),
            FakeCurrentUser.ForUser(1, Capability.ViewAll, Capability.Assign), new UserDisplayNameService(_settings),
            new FixedIssueExclusionSource(IssueExclusion.None), _groups, new FakeAnalysisRecordQuery(), _clock, capabilities)
            .List(new WorkOrderListRequest { Status = "active", HandlerInactive = true, PageSize = 100 });

        var handlers = listed.Items.Select(i => i.HandlerId).Distinct().ToHashSet();
        Assert.Equal(new[] { disabled.UserId, noHandle.UserId, paused.UserId }.ToHashSet(), handlers);
        Assert.Equal(handlers.Count, reminder.InactiveHandlerCount);
        Assert.DoesNotContain(normal.UserId, handlers);
    }

    private sealed class SwitchableExclusionSource : IIssueExclusionSource
    {
        public IssueExclusion Current_ { get; set; } = IssueExclusion.None;
        public IssueExclusion Current() => Current_;
    }

    // ── 快取鍵 ───────────────────────────────────────────────────────────────

    [Fact]
    public void 提醒彙整快取_靜音版本或日期改變時鍵不同_不沿用舊結果()
    {
        var stamp = new DataVersionStamp();
        var cache = new SummaryCache(stamp);
        var exclusions = new SwitchableExclusionSource { Current_ = IssueExclusion.None };
        var service = new AdminReminderService(
            _settings, _orders, _cases, _hosts, _users, _groups, _state, _mail, new FakeRuleStore(), exclusions, _clock,
            _runState, cache, new UserDisplayNameService(_settings));

        FailLastRun(Today.AddHours(2));
        var first = service.Compute(true);
        Assert.Equal(1, first.FetchFailedCount);
        FailLastRun(Today.AddHours(3), success: true);

        Assert.Equal(1, service.Compute(true).FetchFailedCount);        // 同鍵：命中快取（證明快取有效）
        exclusions.Current_ = IssueExclusion.From(new[]
        {
            new IssueProfile { SourceName = "disk", EventId = 1, Mutes = new List<MuteInterval> { new() { From = Today, To = Today.AddDays(3) } } }
        }, Today);
        Assert.Equal(0, service.Compute(true).FetchFailedCount);        // 靜音版本變了：重算

        FailLastRun(Today.AddHours(4));
        Assert.Equal(0, service.Compute(true).FetchFailedCount);        // 又命中舊鍵（還沒跨日）
        SetNow(Today.AddDays(1).AddHours(1));
        Assert.Equal(1, service.Compute(true).FetchFailedCount);        // 跨日：鍵不同，重算
    }

    // ── 未分組主機 ───────────────────────────────────────────────────────────

    [Fact]
    public void 未分組主機_只算啟用中且沒有任何群組的_與主機頁未分組同一個判定()
    {
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED-1" });
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED-2" });
        Grouped("GROUPED");
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED-DISABLED", Active = false });

        var dto = Service().Compute(true);

        Assert.Equal(2, dto.UngroupedHostCount);
        Assert.Equal(NetiqHostList.Ungrouped(_hosts.GetAll()).Count(), dto.UngroupedHostCount);
    }

    // ── 可派未派 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 可派未派_取自待派試跑的可派問題數_超過上限或沒提供時不可信()
    {
        var service = Service();

        var withPending = service.Compute(true, new PendingDispatchCounts(5, false));
        Assert.Equal((5, true), (withPending.DispatchableCount, withPending.DispatchableKnown));
        Assert.True(withPending.Any);

        var tooLarge = service.Compute(true, new PendingDispatchCounts(5, true));
        Assert.Equal((0, false), (tooLarge.DispatchableCount, tooLarge.DispatchableKnown));

        var none = service.Compute(true);
        Assert.Equal((0, false), (none.DispatchableCount, none.DispatchableKnown));

        var zero = service.Compute(true, new PendingDispatchCounts(0, false));
        Assert.Equal((0, true), (zero.DispatchableCount, zero.DispatchableKnown));
    }

    // ── 派不出去：畫面路徑用即時試跑、夜間路徑用快照 ─────────────────────────

    private void SeedUnassignableSnapshot() => _state.Update(s =>
    {
        s.LastUnassignable = new List<UnassignableIssue>
        {
            new() { IssueLabel = "disk 153", Source = "disk", EventId = 153, Reason = "no_visibility", Hosts = 2, ObservedAt = Today }
        };
        s.LastUnassignableAt = Today;
    });

    [Fact]
    public void 派不出去_畫面路徑帶即時計數就用即時問題數_就地授權後歸零_沒帶即時計數仍取夜間快照()
    {
        SeedUnassignableSnapshot();
        var service = Service();

        // 畫面路徑：待派試跑第二段已經是 0（管理者剛就地授權），不再沿用昨晚的快照
        var live = service.Compute(true, new PendingDispatchCounts(0, false, 0));
        Assert.Equal(0, live.UnassignableCount);
        Assert.Empty(live.Unassignable);
        Assert.False(live.Any);

        // 即時有 3 個問題派不出去：數字以即時為準
        Assert.Equal(3, service.Compute(true, new PendingDispatchCounts(0, false, 3)).UnassignableCount);

        // 沒帶即時計數（摘要信等夜間路徑）：同一個 Compute，維持快照
        var nightly = service.Compute();
        Assert.Equal(1, nightly.UnassignableCount);
        Assert.Single(nightly.Unassignable);
        Assert.Equal(1, service.Compute(true, new PendingDispatchCounts(0, false)).UnassignableCount);

        // 試跑超過上限時即時數字不可信，退回快照
        Assert.Equal(1, service.Compute(true, new PendingDispatchCounts(0, true, 0)).UnassignableCount);
    }

    // ── 全 0 與 Manager／Dev ─────────────────────────────────────────────────

    [Fact]
    public void 全部為0時Any為false_具Assign的結果TodoIncluded為true()
    {
        Grouped("H1");

        var dto = Service().Compute(true, new PendingDispatchCounts(0, false));

        Assert.False(dto.Any);
        Assert.True(dto.TodoIncluded);
        Assert.Equal(0, dto.FetchFailedCount + dto.DispatchableCount + dto.InactiveHandlerCount + dto.UngroupedHostCount);
    }

    [Fact]
    public void Manager與Dev不帶待辦類別_四類歸零_既有類別不受影響()
    {
        FailLastRun(Today.AddHours(2));
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED" });
        var paused = _users.Upsert(new WebUser { Account = "rest", Active = true, DispatchPaused = true });
        OrderOf(paused.UserId);

        var dto = Service().Compute(includeTodo: false, new PendingDispatchCounts(9, false));

        Assert.False(dto.TodoIncluded);
        Assert.Equal(0, dto.FetchFailedCount + dto.DispatchableCount + dto.InactiveHandlerCount + dto.UngroupedHostCount);
        Assert.Null(dto.FetchFailed);
        Assert.Empty(dto.InactiveHandlers);
        Assert.False(dto.Any);
    }

    // ── 摘要信：同一彙整、去重與間隔 ────────────────────────────────────────

    private List<WorkOrderOutboxItem> Reminders() =>
        _outbox.Get().Items.Values.Where(i => i.Kind == WorkOrderOutboxKinds.AdminReminder).OrderBy(i => i.EnqueuedAt).ToList();

    [Fact]
    public void 摘要信帶新類別_計數明細與站內提醒同一個彙整_並出現在信件內文()
    {
        FailLastRun(Today.AddHours(2));
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED" });
        var paused = _users.Upsert(new WebUser { Account = "rest", DisplayName = "休假者", Active = true, DispatchPaused = true });
        OrderOf(paused.UserId, members: 2);
        var service = Service();

        service.RunAfterNightly(null);

        var item = Assert.Single(Reminders());
        var categories = item.ReminderItems!.Select(i => i.Category).ToHashSet();
        Assert.Contains(ReminderDetailItem.CategoryFetchFailed, categories);
        Assert.Contains(ReminderDetailItem.CategoryInactiveHandler, categories);
        Assert.Contains(ReminderDetailItem.CategoryUngroupedHosts, categories);
        var dto = service.Compute(true);
        Assert.Equal((1, 1, 1), (dto.FetchFailedCount, dto.InactiveHandlerCount, dto.UngroupedHostCount));

        var (_, body) = WorkOrderDigestBuilder.Build(new[] { (item, (WorkOrderDigestOrder?)null) });
        Assert.Contains("最近一趟取數失敗 1 趟", body);
        Assert.Contains("名下仍有單 1 位", body);
        Assert.Contains("未分組主機 1 台", body);
        Assert.Contains("【處理人已停用或暫停接單、名下仍有單】共 1 位", body);
        Assert.Contains("休假者(rest)（暫停接單）名下還有 1 張單、2 台，請改派", body);
    }

    [Fact]
    public void 新類別沿用提醒間隔_同天不重複_間隔不足不寄_滿間隔再寄一次()
    {
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED" });
        var service = Service();
        service.RunAfterNightly(null);
        Assert.Single(Reminders());
        Assert.Equal(Today.ToString("yyyy-MM-dd"), _state.Get().AdminReminderLastSent[AdminReminderService.UngroupedKey]);

        service.RunAfterNightly(null);                       // 同一天
        Assert.Single(Reminders());

        SetNow(Today.AddDays(2).AddHours(23));               // 間隔 2 天 < 3
        service.RunAfterNightly(null);
        Assert.Single(Reminders());

        SetNow(Today.AddDays(3).AddHours(23));               // 間隔 3 天 >= 3
        service.RunAfterNightly(null);
        Assert.Equal(2, Reminders().Count);
    }

    [Fact]
    public void 新類別消失後去重鍵清掉_間隔設為0不寄信但站內仍有數字()
    {
        var ungrouped = _hosts.Upsert(new WebHost { HostName = "UNGROUPED" });
        var service = Service();
        service.RunAfterNightly(null);
        Assert.True(_state.Get().AdminReminderLastSent.ContainsKey(AdminReminderService.UngroupedKey));

        ungrouped.GroupIds = new List<long> { _hostGroupId };
        _hosts.Upsert(ungrouped);
        service.RunAfterNightly(null);
        Assert.False(_state.Get().AdminReminderLastSent.ContainsKey(AdminReminderService.UngroupedKey));

        _hosts.Upsert(new WebHost { HostName = "UNGROUPED-2" });
        _settings.Update(s => s.EscalationReminderDays = 0);
        SetNow(Today.AddDays(10).AddHours(23));
        service.RunAfterNightly(null);
        Assert.Single(Reminders());                           // 沒有再多寄
        Assert.Equal(1, service.Compute(true).UngroupedHostCount);
    }

    // ── 快取（與使用者無關，鍵含資料版本戳）──────────────────────────────────

    [Fact]
    public void 彙整走快取_版本戳推進後重算_includeTodo與pending在快取外疊上不污染快取()
    {
        var stamp = new DataVersionStamp();
        var cache = new SummaryCache(stamp);
        var service = Service(cache);
        _hosts.Upsert(new WebHost { HostName = "UNGROUPED" });
        Assert.Equal(1, service.Compute(true).UngroupedHostCount);

        _hosts.Upsert(new WebHost { HostName = "UNGROUPED-2" });
        Assert.Equal(1, service.Compute(true).UngroupedHostCount);      // 命中快取，不重算

        stamp.Bump();
        Assert.Equal(2, service.Compute(true).UngroupedHostCount);      // 資料版本推進：重算

        // 同一份快取：具 Assign 疊上 pending 後，Manager 取到的仍是歸零的複本
        var assign = service.Compute(true, new PendingDispatchCounts(7, false));
        var manager = service.Compute(false);
        Assert.Equal(7, assign.DispatchableCount);
        Assert.Equal(0, manager.DispatchableCount);
        Assert.Equal(0, manager.UngroupedHostCount);
        Assert.Equal(2, service.Compute(true).UngroupedHostCount);
        Assert.Equal(0, service.Compute(true).DispatchableCount);
    }

    // ── AdminTodoService：能力與可派問題數來源 ───────────────────────────────

    [Fact]
    public void AdminTodoService_不具Assign不跑待派試跑_具Assign只跑一次並快取()
    {
        var calls = 0;
        PendingDispatchGapsProvider gaps = (from, to, page) =>
        {
            calls++;
            return new GapsDto { Dispatchable = new GapSectionDto { Issues = 4, Hosts = 9 } };
        };
        var cache = new SummaryCache(new DataVersionStamp());
        var reminders = Service(cache);
        var visibility = new FixedVisibilityService(new long[] { 1, 2 });
        var exclusions = new FixedIssueExclusionSource(IssueExclusion.None);

        PendingDispatchCountsService Counts(IVisibilityService v) => PendingCountsTestFactory.FromGaps(gaps, cache, v, exclusions);
        var plain = new AdminTodoService(reminders, FakeCurrentUser.WithCapabilities(Capability.ViewAll), Counts(visibility));
        var dto = plain.GetForCurrentUser();
        Assert.False(dto.TodoIncluded);
        Assert.Equal(0, calls);

        var assigner = new AdminTodoService(reminders, FakeCurrentUser.WithCapabilities(Capability.Assign), Counts(visibility));
        Assert.Equal(4, assigner.GetForCurrentUser().DispatchableCount);
        Assert.Equal(4, assigner.GetForCurrentUser().DispatchableCount);
        Assert.Equal(1, calls);

        // 不同可見範圍不共用待派數字（鍵含可見主機集合）
        var other = new AdminTodoService(reminders, FakeCurrentUser.WithCapabilities(Capability.Assign),
            Counts(new FixedVisibilityService(new long[] { 3 })));
        other.GetForCurrentUser();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void AdminTodoService_待派數字快取鍵含日期_跨日重跑試跑()
    {
        var calls = 0;
        PendingDispatchGapsProvider gaps = (from, to, page) =>
        {
            calls++;
            return new GapsDto { Dispatchable = new GapSectionDto { Issues = calls, Hosts = 1 } };
        };
        var cache = new SummaryCache(new DataVersionStamp());
        var visibility = new FixedVisibilityService(new long[] { 1 });
        var service = (DateTime day) => new AdminTodoService(Service(cache), FakeCurrentUser.WithCapabilities(Capability.Assign),
            PendingCountsTestFactory.FromGaps(gaps, cache, visibility,
                new FixedIssueExclusionSource(IssueExclusion.From(Array.Empty<IssueProfile>(), day))));

        Assert.Equal(1, service(Today).GetForCurrentUser().DispatchableCount);
        Assert.Equal(1, service(Today).GetForCurrentUser().DispatchableCount);   // 同一天：命中
        Assert.Equal(2, service(Today.AddDays(1)).GetForCurrentUser().DispatchableCount);   // 跨日：重跑
    }
}
