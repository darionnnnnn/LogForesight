using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="NightlyDispatch"/>：派工決策寫成交辦單成員、一趟收尾的 appended 事件、鎖的不變式、
/// 撞唯一索引採用既有單，以及掛接 ②③ 不受派工閘門影響。
/// </summary>
public class NightlyDispatchTests
{
    private const string Source = "Disk";
    private const int EventId = 153;

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly SystemSettings _settings = new();
    private readonly List<DispatchCandidate> _candidates = new();
    private readonly IssueCaseCoordinator _caseCoordinator;
    private FakeWorkOrderStore _orders;

    public NightlyDispatchTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, _owners);
    }

    // ── 組裝 ────────────────────────────────────────────────────────────

    private WebHost AddHost(string name, params long[] groupIds) =>
        _hosts.Upsert(new WebHost { HostName = name, GroupIds = groupIds.ToList() });

    private static LogIssueSignature Issue(bool suppressed = false) => new()
    {
        LogName = "System", Source = Source, EventId = EventId, EntryType = EventLogEntryType.Error,
        Severity = IssueSeverity.High, Suppressed = suppressed
    };

    private static string IssueKey => IssueSignatureKey.For(Issue());

    private (NightlyDispatch Dispatch, DispatchContext Ctx) Create()
    {
        var pool = new DispatchCandidatePool
        {
            ByUserId = _candidates.ToDictionary(c => c.UserId),
            PoolMemberCount = _candidates.Count(c => c.InPool),
            ActivePoolMemberCount = _candidates.Count(c => c.InPool && !c.Paused)
        };
        var ctx = DispatchContext.Build(pool, _owners, _orders, _cases, new FakeNoiseMarkStore(), _settings, DateTime.Now);
        var coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, _caseCoordinator, _handlingLog, _hosts);
        return (new NightlyDispatch(coordinator, ctx, _hosts), ctx);
    }

    private void Owner(long userId)
    {
        _candidates.Add(new DispatchCandidate { UserId = userId, Account = "owner" + userId, InPool = false });
        _owners.Upsert(new IssueProfile { SourceName = Source, EventId = EventId, OwnerUserIds = new List<long> { userId } });
    }

    private void Attach(NightlyDispatch dispatch, string host, DateTime date, params LogIssueSignature[] issues) =>
        HostDayPostProcessor.AttachCase(_caseCoordinator, dispatch, host, date, issues.ToList());

    // ── 行為 ────────────────────────────────────────────────────────────

    [Fact]
    public void 同一趟兩台主機同一負責人問題_一張單兩件案件_收尾一筆appended且不重複()
    {
        AddHost("SRV-01");
        AddHost("SRV-02");
        Owner(7);
        var (dispatch, _) = Create();
        var today = DateTime.Today;

        Attach(dispatch, "SRV-01", today, Issue());
        Attach(dispatch, "SRV-02", today, Issue());

        var order = Assert.Single(_orders.All);
        Assert.Equal(7, order.HandlerId);
        Assert.Equal(2, _cases.GetByWorkOrder(order.WorkOrderId, 0, 100).Count);

        var flushedAt = DateTime.Now;
        var summary = dispatch.FlushRun(flushedAt);
        Assert.Equal(1, summary.CreatedOrders);
        Assert.Equal(2, summary.AttachedMembers);
        var line = Assert.Single(summary.PerHandler[7]);
        Assert.Equal((order.WorkOrderId, true, 2), (line.WorkOrderId, line.CreatedThisRun, line.AddedMembers));

        var events = _orders.ListEvents(order.WorkOrderId);
        Assert.Equal(new[] { WorkOrderEventActions.Created, WorkOrderEventActions.Appended }, events.Select(e => e.Action));
        var created = events[0];
        Assert.Equal(0, created.MemberDelta);
        var appended = events[1];
        Assert.Equal(2, appended.MemberDelta);
        Assert.Null(appended.ActorId);
        Assert.Equal(AuditActions.SystemAccount, appended.ActorAccount);
        Assert.Equal("夜間派工", appended.Note);
        Assert.Equal(flushedAt, _orders.Get(order.WorkOrderId)!.LastAppendedAt);

        var second = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(2, _orders.ListEvents(order.WorkOrderId).Count);
        Assert.Equal(0, second.CreatedOrders);
        Assert.Equal(0, second.AttachedMembers);
        Assert.Empty(second.PerHandler);
    }

    [Fact]
    public void 自動派工關閉且無負責人_不建單_disabled計數()
    {
        AddHost("SRV-01");
        var (dispatch, _) = Create();

        Attach(dispatch, "SRV-01", DateTime.Today, Issue());

        Assert.Empty(_orders.All);
        Assert.Empty(_cases.GetMany(new[] { "SRV-01" }));
        Assert.Equal(1, dispatch.FlushRun(DateTime.Now).SkipCounts[WorkOrderDispatcher.SkipDisabled]);
    }

    [Fact]
    public void 自動派工開啟_候選一人看得到主機_建auto_dispatch單與歷程()
    {
        var host = AddHost("SRV-01");
        _settings.AutoDispatchEnabled = true;
        _candidates.Add(new DispatchCandidate { UserId = 3, Account = "c", InPool = true, VisibleHostIds = new HashSet<long> { host.HostId } });
        var (dispatch, _) = Create();
        var today = DateTime.Today;

        Attach(dispatch, "SRV-01", today, Issue());

        var order = Assert.Single(_orders.All);
        Assert.Equal(WorkOrderOrigins.AutoDispatch, order.Origin);
        Assert.Equal(3, order.HandlerId);
        Assert.Equal("系統自動派工", order.Note);
        Assert.Equal(AuditActions.SystemAccount, order.CreatedByAccount);
        var log = _handlingLog.GetLogs("SRV-01", today).Single();
        Assert.Equal(HandlingActions.AutoDispatch, log.Action);
        Assert.Equal(3, _cases.GetOpen("SRV-01", IssueKey)!.HandlerId);
    }

    [Fact]
    public void 人工群組續掛單_主機群組含5_掛入該單不建新單()
    {
        AddHost("SRV-01", 5);
        var manualId = _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = EventId, HandlerId = 11, Origin = WorkOrderOrigins.Manual,
            ScopeKind = WorkOrderScopes.Groups, ScopeGroupIds = new List<long> { 5 }, AutoAttach = true,
            CreatedAt = DateTime.Today.AddDays(-3)
        });
        var (dispatch, _) = Create();
        var today = DateTime.Today;

        Attach(dispatch, "SRV-01", today, Issue());

        Assert.Single(_orders.All);
        var openCase = _cases.GetOpen("SRV-01", IssueKey)!;
        Assert.Equal(manualId, openCase.WorkOrderId);
        Assert.Equal(11, openCase.HandlerId);
        Assert.Equal("系統依交辦單續掛", openCase.Note);
        Assert.Equal(HandlingActions.WorkOrderAttach, _handlingLog.GetLogs("SRV-01", today).Single().Action);

        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(0, summary.CreatedOrders);
        Assert.False(Assert.Single(summary.PerHandler[11]).CreatedThisRun);
    }

    [Fact]
    public void 抑制中問題不派_但有進行中案件仍由掛接掛入()
    {
        AddHost("SRV-01");
        AddHost("SRV-02");
        Owner(7);
        _caseCoordinator.BuildCase("SRV-02", IssueKey, "Disk 153", DateTime.Today.AddDays(-1), handlerId: 99,
            note: null, dueDate: null, actorId: 1, actorAccount: "admin", occurredAt: DateTime.Now);
        var (dispatch, _) = Create();
        var today = DateTime.Today;

        Attach(dispatch, "SRV-01", today, Issue(suppressed: true));
        Attach(dispatch, "SRV-02", today, Issue(suppressed: true));

        Assert.Empty(_orders.All);
        Assert.Null(_cases.GetOpen("SRV-01", IssueKey));
        Assert.Empty(_issueHandlings.GetForDay("SRV-01", today));
        Assert.Equal(HandlingActions.CaseAttach, _handlingLog.GetLogs("SRV-02", today).Single().Action);
        Assert.Equal(1, dispatch.FlushRun(DateTime.Now).SkipCounts[WorkOrderDispatcher.SkipSuppressed]);
    }

    /// <summary>靜音區間由問題負責與靜音經 DispatchContext.Build 填入：紀錄日在區間內→⓪ 略過，不建單；區間外照常派</summary>
    [Fact]
    public void 靜音中問題_Build由問題負責與靜音填入區間_夜間派工略過()
    {
        AddHost("SRV-01");
        _candidates.Add(new DispatchCandidate { UserId = 7, Account = "owner7", InPool = false });
        var today = DateTime.Today;
        _owners.Upsert(new IssueProfile
        {
            SourceName = Source, EventId = EventId, OwnerUserIds = new List<long> { 7 },
            Mutes = new List<MuteInterval> { new() { From = today.AddDays(-1), To = today, Reason = "維護中", ByAccount = "admin" } }
        });
        var (dispatch, ctx) = Create();

        Assert.True(ctx.IsMuted(Source, EventId, today));
        Attach(dispatch, "SRV-01", today, Issue());

        Assert.Empty(_orders.All);
        Assert.Equal(1, dispatch.FlushRun(DateTime.Now).SkipCounts[WorkOrderDispatcher.SkipMuted]);
    }

    [Fact]
    public void 鎖的不變式_建單事件更新都在Gate內()
    {
        AddHost("SRV-01");
        Owner(7);
        var guarded = new GateAssertingWorkOrderStore(_cases);
        _orders = guarded;
        var (dispatch, ctx) = Create();
        guarded.Gate = ctx.Gate;

        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);
        dispatch.FlushRun(DateTime.Now);

        Assert.Equal(1, guarded.Inserts);
        Assert.Equal(2, guarded.Events);
        Assert.True(guarded.Saves >= 1);
    }

    [Fact]
    public void 建單撞唯一索引_採用既有單_不寫created_成員掛進該單()
    {
        AddHost("SRV-01");
        Owner(7);
        var (dispatch, _) = Create();
        long existingId = 0;
        _orders.BeforeNextInsert = () => existingId = _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = EventId, HandlerId = 7, Origin = WorkOrderOrigins.Manual,
            ScopeKind = WorkOrderScopes.Hosts, CreatedAt = DateTime.Today
        });

        dispatch.DispatchDay("SRV-01", DateTime.Today, new[] { Issue() }, DateTime.Now);

        Assert.Single(_orders.All);
        Assert.Equal(existingId, _cases.GetOpen("SRV-01", IssueKey)!.WorkOrderId);
        Assert.DoesNotContain(_orders.ListEvents(existingId), e => e.Action == WorkOrderEventActions.Created);
        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(0, summary.CreatedOrders);
        Assert.Equal(1, summary.AttachedMembers);
    }

    [Fact]
    public void FlushRun_彙總列帶問題名稱_新建單與掛入既有單都有()
    {
        AddHost("SRV-01", 5);
        var existingOrder = new WorkOrder
        {
            SourceName = Source, EventId = EventId, IssueLabel = "Disk 153 既有單", HandlerId = 11,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Groups,
            ScopeGroupIds = new List<long> { 5 }, AutoAttach = true,
            CreatedAt = DateTime.Today.AddDays(-3)
        };
        var existingId = _orders.Insert(existingOrder);

        const string newSource = "Service";
        const int newEventId = 7031;
        _candidates.Add(new DispatchCandidate { UserId = 7, Account = "owner7", InPool = false });
        _owners.Upsert(new IssueProfile { SourceName = newSource, EventId = newEventId, OwnerUserIds = new List<long> { 7 } });

        var (dispatch, _) = Create();
        var today = DateTime.Today;

        var existingIssue = Issue();
        var newIssue = new LogIssueSignature
        {
            LogName = "System", Source = newSource, EventId = newEventId,
            EntryType = EventLogEntryType.Error, Severity = IssueSeverity.High
        };

        Attach(dispatch, "SRV-01", today, existingIssue, newIssue);

        var summary = dispatch.FlushRun(DateTime.Now);

        var existingLine = Assert.Single(summary.PerHandler[11]);
        Assert.False(existingLine.CreatedThisRun);
        Assert.Equal(existingOrder.IssueLabel, existingLine.IssueLabel);

        var createdLine = Assert.Single(summary.PerHandler[7]);
        Assert.True(createdLine.CreatedThisRun);
        var createdOrder = _orders.All.Single(o => o.WorkOrderId != existingId);
        Assert.Equal(createdOrder.IssueLabel, createdLine.IssueLabel);
    }

    [Fact]
    public void 趟末摘要_略過原因以中文分項且不列未開啟()
    {
        var text = NightlyDispatch.DescribeSummary(new NightlyDispatchSummary
        {
            CreatedOrders = 2, AttachedMembers = 7,
            SkipCounts = new Dictionary<string, int>
            {
                [WorkOrderDispatcher.SkipNoCandidate] = 3, [WorkOrderDispatcher.SkipMuted] = 1,
                [WorkOrderDispatcher.SkipSuppressed] = 4, [WorkOrderDispatcher.SkipNoise] = 5,
                [WorkOrderDispatcher.SkipSeverity] = 6, [WorkOrderDispatcher.SkipDismissed] = 7,
                [WorkOrderDispatcher.SkipDisabled] = 99
            }
        });

        Assert.Equal("自動派工：建 2 單／掛入 7 台／無候選人 3 台／靜音略過 1／閘門略過 22（抑制 4、已知雜訊 5、嚴重度 6、不再打擾 7）", text);
    }

    [Fact]
    public void 趟末摘要_派工資料讀取失敗時明講()
    {
        var text = NightlyDispatch.DescribeSummary(new NightlyDispatchSummary
        {
            SkipCounts = new Dictionary<string, int> { [WorkOrderDispatcher.SkipUnavailable] = 12 }
        });

        Assert.EndsWith("；派工資料讀取失敗，本趟未派工", text);
    }

    [Fact]
    public void 夜間彙總_復發台數計入單行()
    {
        var host1 = AddHost("SRV-01");
        var host2 = AddHost("SRV-02");
        _settings.AutoDispatchEnabled = true;
        _candidates.Add(new DispatchCandidate { UserId = 1, Account = "user1", InPool = true, VisibleHostIds = new HashSet<long> { host1.HostId, host2.HostId } });
        _candidates.Add(new DispatchCandidate { UserId = 2, Account = "user2", InPool = true, VisibleHostIds = new HashSet<long> { host1.HostId, host2.HostId } });

        _cases.Save(new IssueCase
        {
            CaseId = "res1", HostName = "SRV-01", IssueKey = IssueKey, HandlerId = 1,
            Status = IssueHandlingStatuses.Resolved, ClosedAt = DateTime.Today.AddDays(-5)
        });

        var (dispatch, _) = Create();
        var today = DateTime.Today;

        Attach(dispatch, "SRV-01", today, Issue());
        Attach(dispatch, "SRV-02", today, Issue());

        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(1, summary.CreatedOrders);
        Assert.Equal(2, summary.AttachedMembers);
        var line = Assert.Single(summary.PerHandler[1]);
        Assert.Equal(2, line.AddedMembers);
        Assert.Equal(1, line.RecurrenceMembers);
    }

    /// <summary>寫入交辦單時斷言呼叫端持有派工脈絡的鎖</summary>
    private sealed class GateAssertingWorkOrderStore : FakeWorkOrderStore
    {
        public GateAssertingWorkOrderStore(FakeIssueCaseStore cases) : base(cases) { }

        public object? Gate { get; set; }
        public int Inserts { get; private set; }
        public int Saves { get; private set; }
        public int Events { get; private set; }

        public override long Insert(WorkOrder order)
        {
            Assert.True(Monitor.IsEntered(Gate!));
            Inserts++;
            return base.Insert(order);
        }

        public override void Save(WorkOrder order)
        {
            Assert.True(Monitor.IsEntered(Gate!));
            Saves++;
            base.Save(order);
        }

        public override void AppendEvent(WorkOrderEvent evt)
        {
            Assert.True(Monitor.IsEntered(Gate!));
            Events++;
            base.AppendEvent(evt);
        }
    }
}
