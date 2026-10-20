namespace LogForesight.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

public class WorkOrderOutboxTests : IDisposable
{
    private readonly EfSqliteFixture _fx = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeSmtpMailSender _sender = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _groupAccess = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeHandlingStore _handlings = new();
    private readonly ScheduleFreshnessService _freshness;
    private readonly WorkOrderOutboxStore _outbox;
    private readonly MailNotificationService _mail;

    public WorkOrderOutboxTests()
    {
        _freshness = new ScheduleFreshnessService(
            new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        _outbox = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey));
        _mail = new MailNotificationService(
            _settingsStore, _sender, _hosts, _users, _userGroups, _groupAccess,
            _records, _handlings, new MailNotifyStateStore(_fx.Blob("mail_notify_state")),
            _freshness, _outbox, new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore(),
            new FakeIssueOwnerStore());

        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.MailNotifyWorkOrders = true;
        });
    }

    public void Dispose()
    {
        _fx.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CaseA_建單入列一筆created_RecipientUserId與ActorAccount正確()
    {
        _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Created, workOrderId: 101, recipientUserId: 2, actorUserId: 1, actorAccount: "admin");

        var items = _outbox.Get().Items.Values.ToList();
        var item = Assert.Single(items);
        Assert.Equal(WorkOrderOutboxKinds.Created, item.Kind);
        Assert.Equal(101, item.WorkOrderId);
        Assert.Equal(2, item.RecipientUserId);
        Assert.Equal("admin", item.ActorAccount);
    }

    [Fact]
    public void CaseB_收件人等於操作者不入列()
    {
        _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Created, workOrderId: 102, recipientUserId: 1, actorUserId: 1, actorAccount: "admin");

        Assert.Empty(_outbox.Get().Items);
    }

    [Fact]
    public void CaseC_MailNotifyWorkOrders關閉時不入列()
    {
        _settingsStore.Update(s => s.MailNotifyWorkOrders = false);

        _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Created, workOrderId: 103, recipientUserId: 2, actorUserId: 1, actorAccount: "admin");

        Assert.Empty(_outbox.Get().Items);
    }

    [Fact]
    public void CaseD_EnqueueNightlyDispatch_一位處理人兩行_兩筆分別created與appended()
    {
        var summary = new NightlyDispatchSummary
        {
            PerHandler = new Dictionary<long, IReadOnlyList<NightlyDispatchOrderLine>>
            {
                [10] = new List<NightlyDispatchOrderLine>
                {
                    new(WorkOrderId: 201, CreatedThisRun: true, AddedMembers: 1, IssueLabel: "Disk Full") { RecurrenceMembers = 0 },
                    new(WorkOrderId: 202, CreatedThisRun: false, AddedMembers: 3, IssueLabel: "High CPU") { RecurrenceMembers = 1 }
                }
            }
        };

        _mail.EnqueueNightlyDispatch(summary);

        var items = _outbox.Get().Items.Values.OrderBy(x => x.WorkOrderId).ToList();
        Assert.Equal(2, items.Count);

        var item1 = items[0];
        Assert.Equal(201, item1.WorkOrderId);
        Assert.Equal(WorkOrderOutboxKinds.Created, item1.Kind);
        Assert.Equal(10, item1.RecipientUserId);
        Assert.Equal(1, item1.MemberDelta);
        Assert.Equal("system", item1.ActorAccount);

        var item2 = items[1];
        Assert.Equal(202, item2.WorkOrderId);
        Assert.Equal(WorkOrderOutboxKinds.Appended, item2.Kind);
        Assert.Equal(10, item2.RecipientUserId);
        Assert.Equal(3, item2.MemberDelta);
        Assert.Equal(1, item2.RecurrenceMembers);
        Assert.Equal("system", item2.ActorAccount);

        // 入列時間是本地時間：寄送時刻（08:00／12:00）以本地時刻判定，混用 UTC 會落到錯的時段
        Assert.All(items, item =>
            Assert.InRange(item.EnqueuedAt, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1)));
    }

    [Fact]
    public void CaseE_超過MaxItems_回傳1且最舊那筆被移除()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _outbox.Update(box =>
        {
            for (int i = 0; i < WorkOrderOutboxStore.MaxItems; i++)
            {
                var time = baseTime.AddSeconds(i);
                var key = $"created|{i}|1|{time.Ticks}";
                box.Items[key] = new WorkOrderOutboxItem
                {
                    Kind = WorkOrderOutboxKinds.Created,
                    WorkOrderId = i,
                    RecipientUserId = 1,
                    EnqueuedAt = time,
                    ActorAccount = "system"
                };
            }
        });

        var newItem = new WorkOrderOutboxItem
        {
            Kind = WorkOrderOutboxKinds.Created,
            WorkOrderId = 999999,
            RecipientUserId = 1,
            EnqueuedAt = baseTime.AddSeconds(WorkOrderOutboxStore.MaxItems + 10),
            ActorAccount = "system"
        };

        var dropped = _outbox.Enqueue(newItem);
        Assert.Equal(1, dropped);

        var currentItems = _outbox.Get().Items;
        Assert.Equal(WorkOrderOutboxStore.MaxItems, currentItems.Count);
        Assert.Contains(currentItems.Values, x => x.WorkOrderId == 999999);
        Assert.DoesNotContain(currentItems.Values, x => x.WorkOrderId == 0);
    }

    [Fact]
    public void CaseF_重建store物件後項目仍在_持久化()
    {
        _mail.EnqueueWorkOrderNotice(WorkOrderOutboxKinds.Created, workOrderId: 301, recipientUserId: 5, actorUserId: 1, actorAccount: "admin");

        var reloadedStore = new WorkOrderOutboxStore(_fx.Blob(WorkOrderOutboxStore.BlobKey));
        var items = reloadedStore.Get().Items.Values.ToList();
        var item = Assert.Single(items);
        Assert.Equal(301, item.WorkOrderId);
        Assert.Equal(5, item.RecipientUserId);
        Assert.Equal(WorkOrderOutboxKinds.Created, item.Kind);
    }

    [Fact]
    public void CaseG_WorkOrderCommandService改派_入列created與transferred各一筆()
    {
        var recordStore = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var issueHandlingStore = new FakeIssueHandlingStore();
        var caseStore = new FakeIssueCaseStore();
        var orderStore = new FakeWorkOrderStore(caseStore);
        var visibility = new AllVisible(_hosts);
        var severity = new FakeSystemSettingsService();
        var repository = new RecordRepository(recordStore, _hosts, visibility, severity);
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, issueHandlingStore, caseStore, _settingsStore);
        var displayNames = new UserDisplayNameService(_settingsStore);

        var query = new RecordListQueryService(
            repository, _hosts, _users, _handlings, issueHandlingStore, caseStore, _settingsStore, severity,
            visibility, aggregates, statusResolver, displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        var caseCoordinator = new IssueCaseCoordinator(caseStore, issueHandlingStore, _handlings, recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(orderStore, caseStore, issueHandlingStore, caseCoordinator, _handlings, _hosts);

        var handlerRole = _userGroups.Upsert(new UserGroup { GroupName = "工程師", Role = UserRole.User, Active = true });
        var alice = _users.Upsert(new WebUser { Account = "alice", DisplayName = "愛麗絲", Active = true, GroupIds = new List<long> { handlerRole.GroupId } });
        var bob = _users.Upsert(new WebUser { Account = "bob", DisplayName = "鮑伯", Active = true, GroupIds = new List<long> { handlerRole.GroupId } });

        var host = _hosts.Upsert(new WebHost { HostName = "HOST-G" });
        recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            RiskLevel = "高",
            Headline = host.HostName,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other }
            }
        });

        var cmdService = new WorkOrderCommandService(
            query, aggregates, coordinator, orderStore, caseStore, new FakeNoiseMarkStore(), _hosts, _users, visibility,
            new UserCapabilityResolver(_userGroups, _hosts), FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            new RecordingAuditService(), displayNames, _mail);

        var created = cmdService.Create(new CreateWorkOrderRequest
        {
            Source = "disk",
            EventId = 153,
            AssignMode = "single",
            HandlerId = alice.UserId,
            ScopeKind = WorkOrderScopes.Hosts,
            HostIds = new List<long> { host.HostId }
        });
        var orderId = Assert.Single(created.Orders).WorkOrderId;

        _outbox.Update(b => b.Items.Clear());

        cmdService.Reassign(orderId, new ReassignWorkOrderRequest { HandlerId = bob.UserId });

        var items = _outbox.Get().Items.Values.ToList();
        Assert.Equal(2, items.Count);

        var createdItem = Assert.Single(items, i => i.Kind == WorkOrderOutboxKinds.Created);
        Assert.Equal(bob.UserId, createdItem.RecipientUserId);

        var transferredItem = Assert.Single(items, i => i.Kind == WorkOrderOutboxKinds.Transferred);
        Assert.Equal(alice.UserId, transferredItem.RecipientUserId);
    }

    [Fact]
    public void CaseH_WorkOrderBoardService立即派工_每張單一筆created且outbox不含Hosts字串()
    {
        var recordStore = new EfAnalysisRecordStore(_fx.NewContext, "test");
        var caseStore = new FakeIssueCaseStore();
        var orderStore = new FakeWorkOrderStore(caseStore);
        var issueHandlingStore = new FakeIssueHandlingStore();
        var visibility = new AllVisible(_hosts);
        var severity = new FakeSystemSettingsService();
        var repository = new RecordRepository(recordStore, _hosts, visibility, severity);
        var aggregates = new EfIssueAggregateQuery(_fx.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, issueHandlingStore, caseStore, _settingsStore);
        var displayNames = new UserDisplayNameService(_settingsStore);

        var query = new RecordListQueryService(
            repository, _hosts, _users, _handlings, issueHandlingStore, caseStore, _settingsStore, severity,
            visibility, aggregates, statusResolver, displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        var caseCoordinator = new IssueCaseCoordinator(caseStore, issueHandlingStore, _handlings, recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(orderStore, caseStore, issueHandlingStore, caseCoordinator, _handlings, _hosts);

        var hostGroups = new FakeHostGroupStore();
        var ruleStore = new FakeRuleStore();
        var suppressionStore = new FakeSuppressionStore();
        var candidates = new FakeCandidateSource();
        var issueOwners = new FakeIssueOwnerStore();
        var noiseMarks = new FakeNoiseMarkStore();

        var board = new WorkOrderBoardService(
            orderStore, caseStore, _users, _userGroups, _hosts, hostGroups, ruleStore, suppressionStore,
            query, aggregates, candidates, issueOwners, noiseMarks, _settingsStore, coordinator,
            FakeCurrentUser.WithCapabilities(Capability.Maintain), new RecordingAuditService(), displayNames, _mail);

        var pool = _userGroups.Upsert(new UserGroup { GroupName = "派工池", Role = UserRole.User, Active = true, DispatchPool = true });
        var handler = _users.Upsert(new WebUser { Account = "engineer1", DisplayName = "工程師", Email = "eng1@test.local", Active = true, GroupIds = new List<long> { pool.GroupId } });

        var host = _hosts.Upsert(new WebHost { HostName = "MAIL-HOST" });
        recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId,
            Host = host.HostName,
            Date = DateTime.Today.AddDays(-1),
            RiskLevel = "高",
            Headline = host.HostName,
            TopIssues = new List<LogIssueSignature>
            {
                new() { LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other },
                new() { LogName = "System", Source = "ntfs", EventId = 55, EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other }
            }
        });

        candidates.Candidates.Add(new DispatchCandidate
        {
            UserId = handler.UserId,
            Account = handler.Account,
            InPool = true,
            Paused = false,
            VisibleHostIds = new HashSet<long> { host.HostId }
        });

        _outbox.Update(box => box.Items.Clear());

        var result = board.RunAutoDispatch(null, null);
        Assert.True(result.CreatedOrders > 0);

        var items = _outbox.Get().Items.Values.ToList();
        Assert.Equal(result.CreatedOrders, items.Count);

        foreach (var item in items)
        {
            Assert.Equal(WorkOrderOutboxKinds.Created, item.Kind);
            Assert.Equal(handler.UserId, item.RecipientUserId);

            // 確保沒有任何欄位含字串 "Hosts"
            Assert.DoesNotContain("Hosts", item.Kind, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Hosts", item.ActorAccount, StringComparison.OrdinalIgnoreCase);
            if (item.Reason != null)
            {
                Assert.DoesNotContain("Hosts", item.Reason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private sealed class AllVisible : IVisibilityService
    {
        private readonly FakeHostStore _hosts;
        public AllVisible(FakeHostStore hosts) => _hosts = hosts;
        public IReadOnlySet<long> GetVisibleHostIds() => _hosts.GetAll().Select(h => h.HostId).ToHashSet();
        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => GetVisibleHostIds();
        public List<WebHost> GetVisibleHosts() => _hosts.GetAll();
        public void EnsureVisible(long hostId) { }
        public IReadOnlyList<string> GetCaseGrantHostNames() => Array.Empty<string>();
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    }

    private sealed class FakeCandidateSource : IDispatchCandidateSource
    {
        public List<DispatchCandidate> Candidates { get; } = new();
        public DispatchCandidatePool Build() => new()
        {
            ByUserId = Candidates.ToDictionary(c => c.UserId),
            PoolMemberCount = Candidates.Count(c => c.InPool),
            ActivePoolMemberCount = Candidates.Count(c => c.InPool && !c.Paused)
        };
    }
}
