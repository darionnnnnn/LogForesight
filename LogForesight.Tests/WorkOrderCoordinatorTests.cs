using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="WorkOrderCoordinator"/>：建單／追加／改派／拆單／取消／代為結案／回覆／結案推導與掃描、
/// 併發重試、整併前無單的舊案件。替身組裝方式同 <see cref="CaseDaySyncTests"/>。
/// </summary>
public class WorkOrderCoordinatorTests
{
    private const string Source = "disk";
    private const int EventId = 153;
    private const string IssueLabel = "disk 153";
    private static readonly string IssueKey = IssueSignatureKey.For("System", Source, EventId, EventLogEntryType.Error);
    private static readonly DateTime D1 = DateTime.Today.AddDays(-3);
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);
    private static readonly DateTime D3 = DateTime.Today.AddDays(-1);
    private static readonly DateTime T0 = new(2026, 9, 17, 10, 0, 0);

    private const long Alice = 1;
    private const long Bob = 2;
    private const long Carol = 3;

    private sealed class CountingCaseStore : FakeIssueCaseStore, IIssueCaseStore
    {
        public int GetOpenCalls { get; set; }

        IssueCase? IIssueCaseStore.GetOpen(string hostName, string issueKey)
        {
            GetOpenCalls++;
            return base.GetOpen(hostName, issueKey);
        }
    }

    private sealed class ThrowingIssueHandlingStore : FakeIssueHandlingStore, IIssueHandlingStore
    {
        public bool Throw { get; set; }

        void IIssueHandlingStore.SaveMany(IEnumerable<IssueHandling> handlings)
        {
            if (Throw) throw new InvalidOperationException("注入：逐日列寫入失敗");
            base.SaveMany(handlings);
        }
    }

    private sealed class World
    {
        public readonly FakeHostStore Hosts = new();
        public readonly FakeAnalysisRecordQuery Records = new();
        public readonly CountingCaseStore Cases = new();
        public readonly ThrowingIssueHandlingStore IssueHandlings = new();
        public readonly CaseDaySyncTests.RecordingHandlingStore HandlingLog = new();
        public readonly FakeIssueOwnerStore IssueProfiles = new();
        public readonly FakeWorkOrderStore Orders;
        public readonly WorkOrderCoordinator Coordinator;

        public World()
        {
            Orders = new FakeWorkOrderStore(Cases);
            var caseCoordinator = new IssueCaseCoordinator(Cases, IssueHandlings, HandlingLog, Records, Hosts, IssueProfiles);
            Coordinator = new WorkOrderCoordinator(Orders, Cases, IssueHandlings, caseCoordinator, HandlingLog, Hosts);
        }

        public void AddHostDays(string host, params DateTime[] dates)
        {
            var existing = Hosts.FindByName(host) ?? Hosts.Upsert(new WebHost { HostName = host });
            foreach (var date in dates)
            {
                Records.Add(new DailyAnalysisRecord
                {
                    Date = date, HostId = existing.HostId, Host = host,
                    TopIssues = new List<LogIssueSignature>
                    {
                        new() { LogName = "System", Source = Source, EventId = EventId, EntryType = EventLogEntryType.Error }
                    }
                });
            }
        }

        public IssueCase AddCase(string caseId, string host, long handler, long? workOrderId, DateTime? closedAt = null)
        {
            if (Hosts.FindByName(host) == null) Hosts.Upsert(new WebHost { HostName = host });
            var c = new IssueCase
            {
                CaseId = caseId, HostName = host, IssueKey = IssueKey, IssueLabel = IssueLabel,
                Status = closedAt == null ? IssueHandlingStatuses.InProgress : IssueHandlingStatuses.Resolved,
                HandlerId = handler, FirstLinkedDate = D2, LastLinkedDate = D2,
                CreatedAt = T0.AddDays(-1), CreatedByAccount = "admin", UpdatedAt = T0.AddDays(-1),
                ClosedAt = closedAt, WorkOrderId = workOrderId
            };
            Cases.Save(c);
            return c;
        }

        public long AddOrder(long handler, string scope = WorkOrderScopes.Hosts, List<long>? groups = null)
        {
            var order = new WorkOrder
            {
                SourceName = Source, EventId = EventId, IssueLabel = IssueLabel, HandlerId = handler,
                ScopeKind = scope, ScopeGroupIds = groups ?? new List<long>(),
                Note = "舊單備註", CreatedByAccount = "admin", CreatedAt = T0.AddDays(-1)
            };
            return Orders.Insert(order);
        }

        public IssueHandling? Row(string host, DateTime date) =>
            IssueHandlings.GetForDay(host, date).SingleOrDefault(h => h.IssueKey == IssueKey);

        public List<string> Events(long id) => Orders.ListEvents(id).Select(e => e.Action).ToList();
    }

    private static WorkOrderActor Actor(DateTime? at = null) => new() { ActorId = 9, ActorAccount = "boss", OccurredAt = at ?? T0 };

    private static WorkOrderMember Member(string host, DateTime? trigger = null) => new()
    {
        HostName = host, IssueKey = IssueKey, IssueLabel = IssueLabel, TriggerDate = trigger ?? D2
    };

    private static WorkOrderCreateRequest Request(long handler, params string[] hosts) => new()
    {
        Source = Source, EventId = EventId, IssueLabel = IssueLabel, HandlerId = handler,
        Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
        Note = "請處理", Members = hosts.Select(h => Member(h)).ToList(), Actor = Actor()
    };

    // ── Create ───────────────────────────────────────────────────────────────

    /// <summary>有效成員為零（全是他人案件且不改派）時不建單，避免留下零成員的進行中空單</summary>
    [Fact]
    public void Create_成員全是他人案件且不改派_不建單不寫事件_略過照實回報()
    {
        var w = new World();
        w.AddCase("b1", "H1", Bob, workOrderId: null);
        w.AddCase("b2", "H2", Carol, workOrderId: null);

        var outcome = w.Coordinator.Create(Request(Alice, "H1", "H2"));

        Assert.Empty(w.Orders.All);
        Assert.Empty(w.Orders.ListEvents(0));
        Assert.Equal(0, outcome.WorkOrderId);
        Assert.False(outcome.CreatedOrder);
        Assert.Equal(0, outcome.NewCases + outcome.LinkedExisting + outcome.Reassigned);
        Assert.Equal(new[] { ("H1", (long?)Bob), ("H2", (long?)Carol) },
            outcome.SkippedConflicts.Select(c => (c.HostName, c.HandlerId)).OrderBy(x => x.HostName));
        Assert.Equal(new CaseDaySubmitResult(Inline: true, Rows: 0, PendingCases: 0), outcome.DaySync);
        Assert.Null(w.Cases.GetOpen("H1", IssueKey)!.WorkOrderId);
    }

    [Fact]
    public void Append_成員全被略過_不寫appended事件且LastAppendedAt不變()
    {
        var w = new World();
        var orderId = w.AddOrder(Alice);
        w.AddCase("b1", "H1", Bob, workOrderId: null);
        var before = w.Orders.Get(orderId)!.LastAppendedAt;
        var eventsBefore = w.Events(orderId);

        var outcome = w.Coordinator.Append(orderId, new List<WorkOrderMember> { Member("H1") }, reassignConflicts: false, Actor(T0.AddHours(1)));

        Assert.Single(outcome.SkippedConflicts);
        Assert.Equal(before, w.Orders.Get(orderId)!.LastAppendedAt);
        Assert.DoesNotContain(WorkOrderEventActions.Appended, w.Events(orderId));
        Assert.Equal(eventsBefore, w.Events(orderId));
    }

    [Fact]
    public void Create_三台主機_新建一台_改連一台_略過他人衝突一台()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var mine = w.AddCase("mine", "H2", Alice, workOrderId: null);
        var others = w.AddCase("others", "H3", Bob, workOrderId: null);

        var outcome = w.Coordinator.Create(Request(Alice, "H1", "H2", "H3"));

        Assert.True(outcome.CreatedOrder);
        Assert.Equal(1, outcome.NewCases);
        Assert.Equal(1, outcome.LinkedExisting);
        Assert.Equal(0, outcome.Reassigned);
        var conflict = Assert.Single(outcome.SkippedConflicts);
        Assert.Equal("H3", conflict.HostName);
        Assert.Equal(Bob, conflict.HandlerId);

        var created = w.Orders.ListEvents(outcome.WorkOrderId).Single();
        Assert.Equal(WorkOrderEventActions.Created, created.Action);
        Assert.Equal(2, created.MemberDelta);

        Assert.True(outcome.DaySync.Inline);
        Assert.Equal(2, outcome.DaySync.Rows);
        var newCase = w.Cases.GetOpen("H1", IssueKey)!;
        Assert.Equal(outcome.WorkOrderId, newCase.WorkOrderId);
        Assert.Equal(Alice, newCase.HandlerId);
        Assert.Equal("請處理", newCase.Note);
        Assert.Equal(T0, newCase.CreatedAt);
        Assert.Equal("boss", newCase.CreatedByAccount);
        Assert.Equal(IssueHandlingStatuses.InProgress, w.Row("H1", D2)!.Status);
        Assert.Contains(w.HandlingLog.Logs, l => l.HostName == "H1" && l.Date == D2 && l.Action == HandlingActions.CaseAssign);

        Assert.Equal(outcome.WorkOrderId, mine.WorkOrderId);
        Assert.Null(others.WorkOrderId);
        Assert.Equal(Bob, others.HandlerId);
        Assert.Equal(T0, w.Orders.Get(outcome.WorkOrderId)!.LastAppendedAt);
    }

    [Fact]
    public void Create_改派衝突_他人案件改派改連_原單歸零moved()
    {
        var w = new World();
        var bobOrder = w.AddOrder(Bob);
        var others = w.AddCase("others", "H3", Bob, bobOrder);

        var req = Request(Alice, "H3");
        req.ReassignConflicts = true;
        var outcome = w.Coordinator.Create(req);

        Assert.Equal(1, outcome.Reassigned);
        Assert.Empty(outcome.SkippedConflicts);
        Assert.Equal(Alice, others.HandlerId);
        Assert.Equal(outcome.WorkOrderId, others.WorkOrderId);
        var log = Assert.Single(w.HandlingLog.Logs);
        Assert.Equal(HandlingActions.CaseReassign, log.Action);
        Assert.Equal(D2, log.Date);
        Assert.Equal("變更案件處理人", log.Note);

        var old = w.Orders.Get(bobOrder)!;
        Assert.Equal(WorkOrderCloseReasons.Moved, old.ClosedReason);
        Assert.Equal(T0, old.ClosedAt);
        Assert.Equal(WorkOrderEventActions.Closed, w.Events(bobOrder).Single());
    }

    [Theory]
    [InlineData(WorkOrderScopes.All, "", WorkOrderScopes.Groups, "1", WorkOrderScopes.All, "")]
    [InlineData(WorkOrderScopes.Groups, "1", WorkOrderScopes.Groups, "2", WorkOrderScopes.Groups, "1,2")]
    [InlineData(WorkOrderScopes.Hosts, "", WorkOrderScopes.Groups, "3", WorkOrderScopes.Groups, "3")]
    public void Create_同處理人同問題已有單_不新建_範圍聯集_記merged_in(
        string existingKind, string existingGroups, string incomingKind, string incomingGroups, string expectKind, string expectGroups)
    {
        static List<long> Parse(string s) => s.Length == 0 ? new List<long>() : s.Split(',').Select(long.Parse).ToList();

        var w = new World();
        w.AddHostDays("H1", D2);
        var existing = w.AddOrder(Alice, existingKind, Parse(existingGroups));

        var req = Request(Alice, "H1");
        req.ScopeKind = incomingKind;
        req.ScopeGroupIds = Parse(incomingGroups);
        req.AutoAttach = true;
        req.Note = "新備註";
        var outcome = w.Coordinator.Create(req);

        Assert.False(outcome.CreatedOrder);
        Assert.Equal(existing, outcome.WorkOrderId);
        Assert.Single(w.Orders.All);
        var order = w.Orders.Get(existing)!;
        Assert.Equal(expectKind, order.ScopeKind);
        Assert.Equal(Parse(expectGroups), order.ScopeGroupIds);
        Assert.True(order.AutoAttach);
        Assert.Equal("舊單備註", order.Note);
        var evt = w.Orders.ListEvents(existing).Single();
        Assert.Equal(WorkOrderEventActions.MergedIn, evt.Action);
        Assert.Equal(1, evt.MemberDelta);
    }

    [Fact]
    public void Create_並發_Insert前被搶先建同鍵單_改走併入不擲例外()
    {
        var w = new World();
        w.AddHostDays("H1", D2);
        long raced = 0;
        w.Orders.BeforeNextInsert = () => raced = w.AddOrder(Alice);

        var outcome = w.Coordinator.Create(Request(Alice, "H1"));

        Assert.NotEqual(0, raced);
        Assert.False(outcome.CreatedOrder);
        Assert.Equal(raced, outcome.WorkOrderId);
        Assert.Single(w.Orders.All);
        Assert.Equal(new[] { WorkOrderEventActions.MergedIn }, w.Events(raced));
    }

    [Fact]
    public void Create_成員寫入失敗_新單自動cancelled並重拋()
    {
        var w = new World();
        w.AddHostDays("H1", D2);
        w.IssueHandlings.Throw = true;

        Assert.Throws<InvalidOperationException>(() => w.Coordinator.Create(Request(Alice, "H1")));

        var order = Assert.Single(w.Orders.All);
        Assert.Equal(WorkOrderCloseReasons.Cancelled, order.ClosedReason);
        var evt = w.Orders.ListEvents(order.WorkOrderId).Single();
        Assert.Equal(WorkOrderEventActions.Cancelled, evt.Action);
        Assert.Equal("建單失敗自動關閉", evt.Note);
    }

    [Fact]
    public void Create_空成員擲ArgumentException()
    {
        var w = new World();
        Assert.Throws<ArgumentException>(() => w.Coordinator.Create(Request(Alice)));
        Assert.Empty(w.Orders.All);
    }

    [Fact]
    public void Create_主機不存在擲InvalidOperationException()
    {
        var w = new World();
        Assert.Throws<InvalidOperationException>(() => w.Coordinator.Create(Request(Alice, "NOPE")));
    }

    [Fact]
    public void Create_整併前無單的同處理人進行中案件_計入LinkedExisting並連到本單()
    {
        var w = new World();
        var legacy = w.AddCase("legacy", "H1", Alice, workOrderId: null);

        var outcome = w.Coordinator.Create(Request(Alice, "H1"));

        Assert.Equal(1, outcome.LinkedExisting);
        Assert.Equal(0, outcome.NewCases);
        Assert.Equal(outcome.WorkOrderId, legacy.WorkOrderId);
        Assert.Empty(w.HandlingLog.Logs);
    }

    [Fact]
    public void Append_已結案單擲InvalidOperationException()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        w.Coordinator.RecomputeClosure(id, T0);

        Assert.Throws<InvalidOperationException>(() =>
            w.Coordinator.Append(id, new List<WorkOrderMember> { Member("H1") }, false, Actor()));
    }

    [Fact]
    public void Append_新增成員_記appended()
    {
        var w = new World();
        w.AddHostDays("H1", D2);
        var id = w.AddOrder(Alice);
        w.AddCase("keep", "H0", Alice, id);

        var outcome = w.Coordinator.Append(id, new List<WorkOrderMember> { Member("H1") }, false, Actor());

        Assert.Equal(1, outcome.NewCases);
        var evt = w.Orders.ListEvents(id).Single();
        Assert.Equal(WorkOrderEventActions.Appended, evt.Action);
        Assert.Equal(1, evt.MemberDelta);
        Assert.Equal(T0, w.Orders.Get(id)!.LastAppendedAt);
    }

    // ── Reassign ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reassign_無目標單_處理人與進行中成員換人_已結案成員不動()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        var a = w.AddCase("a", "H1", Alice, id);
        var b = w.AddCase("b", "H2", Alice, id);
        var done = w.AddCase("done", "H3", Alice, id, closedAt: T0.AddHours(-1));

        var result = w.Coordinator.Reassign(id, Bob, Actor());

        Assert.False(result.MergedIntoExisting);
        Assert.Equal(id, result.TargetWorkOrderId);
        Assert.Equal(2, result.MovedCases);
        Assert.Equal(Alice, result.PreviousHandlerId);
        Assert.Equal(Bob, w.Orders.Get(id)!.HandlerId);
        Assert.Equal(Bob, a.HandlerId);
        Assert.Equal(Bob, b.HandlerId);
        Assert.Equal(Alice, done.HandlerId);
        Assert.Equal(2, w.HandlingLog.Logs.Count(l => l.Action == HandlingActions.CaseReassign));
        Assert.All(w.HandlingLog.Logs.Where(l => l.Action == HandlingActions.CaseReassign), log =>
        {
            Assert.Equal(Alice, log.PreviousHandlerId);
            Assert.Equal(Bob, log.HandlerId);
            Assert.Contains(log.CaseId, new[] { "a", "b" });
        });
        var evt = w.Orders.ListEvents(id).Single();
        Assert.Equal(WorkOrderEventActions.Reassigned, evt.Action);
        Assert.Equal($"{Alice}→{Bob}", evt.Note);
        Assert.Equal(0, evt.MemberDelta);
    }

    [Fact]
    public void Reassign_300成員_不逐案GetOpen()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        for (var i = 0; i < 300; i++) w.AddCase("c" + i, "H" + i, Alice, id);
        w.Cases.GetOpenCalls = 0;

        var result = w.Coordinator.Reassign(id, Bob, Actor());

        Assert.Equal(300, result.MovedCases);
        Assert.Equal(0, w.Cases.GetOpenCalls);
        Assert.Equal(300, w.HandlingLog.Logs.Count(l => l.Action == HandlingActions.CaseReassign));
    }

    [Fact]
    public void Reassign_新處理人已有單_原單moved_目標merged_in_成員全在目標()
    {
        var w = new World();
        var id = w.AddOrder(Alice, WorkOrderScopes.Groups, new List<long> { 5 });
        var target = w.AddOrder(Bob);
        w.AddCase("a", "H1", Alice, id);
        w.AddCase("b", "H2", Alice, id);

        var result = w.Coordinator.Reassign(id, Bob, Actor());

        Assert.True(result.MergedIntoExisting);
        Assert.Equal(target, result.TargetWorkOrderId);
        var old = w.Orders.Get(id)!;
        Assert.Equal(WorkOrderCloseReasons.Moved, old.ClosedReason);
        var outEvt = w.Orders.ListEvents(id).Single();
        Assert.Equal(WorkOrderEventActions.Reassigned, outEvt.Action);
        Assert.Equal(-2, outEvt.MemberDelta);
        Assert.Equal($"移入單號 {target}", outEvt.Note);
        var inEvt = w.Orders.ListEvents(target).Single();
        Assert.Equal(WorkOrderEventActions.MergedIn, inEvt.Action);
        Assert.Equal(2, inEvt.MemberDelta);
        var t = w.Orders.Get(target)!;
        Assert.Equal(WorkOrderScopes.Groups, t.ScopeKind);
        Assert.Equal(T0, t.LastAppendedAt);
        Assert.All(w.Cases.GetByWorkOrder(target, 0, 100), c => Assert.Equal(Bob, c.HandlerId));
        Assert.Equal(2, w.Cases.CountByWorkOrder(target));
        Assert.Equal(0, w.Cases.CountByWorkOrder(id));
    }

    // ── Split ────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_新處理人無單_建單記created與split_in_原單split_out()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        var a = w.AddCase("a", "H1", Alice, id);
        w.AddCase("b", "H2", Alice, id);

        var result = w.Coordinator.Split(id, new[] { "a" }, Carol, Actor());

        Assert.False(result.MergedIntoExisting);
        Assert.NotEqual(id, result.TargetWorkOrderId);
        Assert.Equal(new[] { WorkOrderEventActions.Created, WorkOrderEventActions.SplitIn }, w.Events(result.TargetWorkOrderId));
        Assert.Equal(1, w.Orders.ListEvents(result.TargetWorkOrderId)[1].MemberDelta);
        var outEvt = w.Orders.ListEvents(id).Single();
        Assert.Equal(WorkOrderEventActions.SplitOut, outEvt.Action);
        Assert.Equal(-1, outEvt.MemberDelta);
        var t = w.Orders.Get(result.TargetWorkOrderId)!;
        Assert.Equal(Carol, t.HandlerId);
        Assert.Equal(WorkOrderScopes.Hosts, t.ScopeKind);
        Assert.Equal("舊單備註", t.Note);
        Assert.Equal(Carol, a.HandlerId);
        Assert.Equal(result.TargetWorkOrderId, a.WorkOrderId);
        Assert.Null(w.Orders.Get(id)!.ClosedAt);
    }

    [Fact]
    public void Split_選到已結案成員_整筆不做()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        var a = w.AddCase("a", "H1", Alice, id);
        w.AddCase("done", "H2", Alice, id, closedAt: T0.AddHours(-1));

        Assert.Throws<InvalidOperationException>(() => w.Coordinator.Split(id, new[] { "a", "done" }, Carol, Actor()));

        Assert.Equal(Alice, a.HandlerId);
        Assert.Equal(id, a.WorkOrderId);
        Assert.Single(w.Orders.All);
        Assert.Empty(w.Orders.ListEvents(id));
        Assert.Empty(w.HandlingLog.Logs);
    }

    [Fact]
    public void Split_空清單與同處理人擲例外()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        w.AddCase("a", "H1", Alice, id);

        Assert.Throws<ArgumentException>(() => w.Coordinator.Split(id, Array.Empty<string>(), Carol, Actor()));
        Assert.Throws<InvalidOperationException>(() => w.Coordinator.Split(id, new[] { "a" }, Alice, Actor()));
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_成員取消結案_案件同步日調回open_使用者自標日不動()
    {
        var w = new World();
        w.AddHostDays("H1", D1, D2);
        var outcome = w.Coordinator.Create(Request(Alice, "H1"));
        var id = outcome.WorkOrderId;
        var done = w.AddCase("done", "H9", Alice, id, closedAt: T0.AddHours(-1));
        // 使用者在沒有候選紀錄的一天自己標的列（非本案件擁有）
        w.IssueHandlings.Save(new IssueHandling
        {
            HostName = "H1", Date = D3, IssueKey = IssueKey, Status = IssueHandlingStatuses.InProgress,
            CaseId = null, ActorAccount = "user", UpdatedAt = T0.AddDays(-1)
        });
        w.HandlingLog.Logs.Clear();

        var later = T0.AddHours(1);
        var result = w.Coordinator.Cancel(id, "不需要了", Actor(later));

        Assert.Equal(1, result.ClosedCases);
        Assert.Equal(Alice, result.HandlerId);
        var c = w.Cases.GetByWorkOrder(id, 0, 10).Single(x => x.HostName == "H1");
        Assert.True(c.Cancelled);
        Assert.Equal(later, c.ClosedAt);
        Assert.Equal(IssueHandlingStatuses.Open, c.Status);
        Assert.Null(c.Note);
        Assert.Equal(IssueHandlingStatuses.Open, w.Row("H1", D1)!.Status);
        Assert.Equal(IssueHandlingStatuses.Open, w.Row("H1", D2)!.Status);
        Assert.Equal(2, w.HandlingLog.Logs.Count(l => l.Action == HandlingActions.IssueStatusCleared));
        Assert.Equal(IssueHandlingStatuses.InProgress, w.Row("H1", D3)!.Status);
        Assert.False(done.Cancelled);
        Assert.Equal(T0.AddHours(-1), done.ClosedAt);

        var order = w.Orders.Get(id)!;
        Assert.Equal(WorkOrderCloseReasons.Cancelled, order.ClosedReason);
        var evt = w.Orders.ListEvents(id).Last();
        Assert.Equal(WorkOrderEventActions.Cancelled, evt.Action);
        Assert.Equal("不需要了", evt.Note);
        Assert.Equal(-1, evt.MemberDelta);
    }

    [Fact]
    public void Cancel_原因空白擲ArgumentException()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        Assert.Throws<ArgumentException>(() => w.Coordinator.Cancel(id, "  ", Actor()));
    }

    // ── AdminClose ───────────────────────────────────────────────────────────

    [Fact]
    public void AdminClose_非結案狀態擲ArgumentException_成功時成員結案逐日resolved()
    {
        var w = new World();
        w.AddHostDays("H1", D2);
        var id = w.Coordinator.Create(Request(Alice, "H1")).WorkOrderId;

        Assert.Throws<ArgumentException>(() => w.Coordinator.AdminClose(id, IssueHandlingStatuses.InProgress, "x", Actor()));

        var later = T0.AddHours(1);
        var result = w.Coordinator.AdminClose(id, IssueHandlingStatuses.Resolved, "已換硬碟", Actor(later));

        Assert.Equal(1, result.ClosedCases);
        var c = w.Cases.GetByWorkOrder(id, 0, 10).Single();
        Assert.Equal(IssueHandlingStatuses.Resolved, c.Status);
        Assert.Equal(later, c.ClosedAt);
        Assert.Equal("已換硬碟", c.Note);
        Assert.Equal(IssueHandlingStatuses.Resolved, w.Row("H1", D2)!.Status);
        var order = w.Orders.Get(id)!;
        Assert.Equal(WorkOrderCloseReasons.AdminClosed, order.ClosedReason);
        var evt = w.Orders.ListEvents(id).Last();
        Assert.Equal(WorkOrderEventActions.AdminClosed, evt.Action);
        Assert.Equal($"{IssueHandlingStatuses.Resolved}：已換硬碟", evt.Note);
        Assert.Equal(-1, evt.MemberDelta);
    }

    // ── Reply ────────────────────────────────────────────────────────────────

    private static (World W, long Id, List<IssueCase> Cases) ThreeHostOrder()
    {
        var w = new World();
        foreach (var h in new[] { "H1", "H2", "H3" }) w.AddHostDays(h, D2);
        var id = w.Coordinator.Create(Request(Alice, "H1", "H2", "H3")).WorkOrderId;
        return (w, id, w.Cases.GetByWorkOrder(id, 0, 10));
    }

    [Fact]
    public void Reply_部分結案_單仍進行中_再回覆其餘_單all_closed()
    {
        var (w, id, cases) = ThreeHostOrder();
        var h1 = cases.Single(c => c.HostName == "H1");
        var h2 = cases.Single(c => c.HostName == "H2");
        var h3 = cases.Single(c => c.HostName == "H3");

        var t1 = T0.AddHours(1);
        var first = w.Coordinator.Reply(id, new[] { h1.CaseId, h2.CaseId }, IssueHandlingStatuses.Resolved, "修好了", null, Actor(t1));

        Assert.Equal(2, first.Cases);
        Assert.False(first.WorkOrderClosed);
        Assert.Equal(t1, h1.ClosedAt);
        Assert.Equal(t1, h2.ClosedAt);
        Assert.Null(h3.ClosedAt);
        Assert.Equal(IssueHandlingStatuses.Resolved, w.Row("H1", D2)!.Status);
        Assert.Equal(IssueHandlingStatuses.InProgress, w.Row("H3", D2)!.Status);
        var order = w.Orders.Get(id)!;
        Assert.Equal(t1, order.LastReplyAt);
        Assert.Null(order.ClosedAt);

        var t2 = T0.AddHours(2);
        var second = w.Coordinator.Reply(id, null, IssueHandlingStatuses.Resolved, "也修好了", null, Actor(t2));

        Assert.Equal(1, second.Cases);
        Assert.True(second.WorkOrderClosed);
        order = w.Orders.Get(id)!;
        Assert.Equal(WorkOrderCloseReasons.AllClosed, order.ClosedReason);
        Assert.Equal(WorkOrderEventActions.Closed, w.Events(id).Last());
    }

    [Fact]
    public void Reply_open視為清除_observing保留期限()
    {
        var (w, id, cases) = ThreeHostOrder();
        var h1 = cases.Single(c => c.HostName == "H1");
        var h2 = cases.Single(c => c.HostName == "H2");
        var due = DateTime.Today.AddDays(7);

        w.Coordinator.Reply(id, new[] { h1.CaseId }, IssueHandlingStatuses.Open, "不該留", due, Actor(T0.AddHours(1)));
        w.Coordinator.Reply(id, new[] { h2.CaseId }, IssueHandlingStatuses.Observing, "觀察", due, Actor(T0.AddHours(2)));

        var r1 = w.Row("H1", D2)!;
        Assert.Equal(IssueHandlingStatuses.Open, r1.Status);
        Assert.Null(r1.Note);
        Assert.Null(r1.DueDate);
        Assert.Null(h1.Note);
        Assert.Null(h1.ClosedAt);
        var r2 = w.Row("H2", D2)!;
        Assert.Equal(IssueHandlingStatuses.Observing, r2.Status);
        Assert.Equal(due, r2.DueDate);
        Assert.Equal(due, h2.DueDate);
    }

    [Fact]
    public void Reply_含他單案件_擲例外且零寫入()
    {
        var (w, id, cases) = ThreeHostOrder();
        var otherOrder = w.AddOrder(Bob);
        var foreign = w.AddCase("foreign", "H9", Bob, otherOrder);
        var h1 = cases.Single(c => c.HostName == "H1");
        var logsBefore = w.HandlingLog.Logs.Count;
        var rowBefore = w.Row("H1", D2)!;
        var orderBefore = w.Orders.Get(id)!;

        Assert.Throws<InvalidOperationException>(() =>
            w.Coordinator.Reply(id, new[] { h1.CaseId, foreign.CaseId }, IssueHandlingStatuses.Resolved, "x", null, Actor(T0.AddHours(1))));

        Assert.Equal(IssueHandlingStatuses.InProgress, h1.Status);
        Assert.Null(h1.ClosedAt);
        Assert.Equal(T0, h1.UpdatedAt);
        Assert.Equal(IssueHandlingStatuses.InProgress, foreign.Status);
        Assert.Null(foreign.ClosedAt);
        Assert.Equal(logsBefore, w.HandlingLog.Logs.Count);
        var rowAfter = w.Row("H1", D2)!;
        Assert.Equal(rowBefore.Status, rowAfter.Status);
        Assert.Equal(rowBefore.UpdatedAt, rowAfter.UpdatedAt);
        var orderAfter = w.Orders.Get(id)!;
        Assert.Null(orderAfter.LastReplyAt);
        Assert.Equal(orderBefore.UpdatedAt, orderAfter.UpdatedAt);
    }

    [Fact]
    public void Reply_不支援狀態擲ArgumentException()
    {
        var (w, id, _) = ThreeHostOrder();
        Assert.Throws<ArgumentException>(() => w.Coordinator.Reply(id, null, "bogus", null, null, Actor()));
    }

    // ── RecomputeClosure／SweepClosures ─────────────────────────────────────

    [Fact]
    public void RecomputeClosure_零成員moved_全結案all_closed_有進行中不動_已結案回false()
    {
        var w = new World();
        var empty = w.AddOrder(Alice);
        var allDone = w.AddOrder(Bob);
        w.AddCase("d", "H1", Bob, allDone, closedAt: T0.AddHours(-1));
        var active = w.AddOrder(Carol);
        w.AddCase("a", "H2", Carol, active);

        Assert.True(w.Coordinator.RecomputeClosure(empty, T0));
        Assert.True(w.Coordinator.RecomputeClosure(allDone, T0));
        Assert.False(w.Coordinator.RecomputeClosure(active, T0));

        Assert.Equal(WorkOrderCloseReasons.Moved, w.Orders.Get(empty)!.ClosedReason);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, w.Orders.Get(allDone)!.ClosedReason);
        Assert.Null(w.Orders.Get(active)!.ClosedAt);
        var evt = w.Orders.ListEvents(allDone).Single();
        Assert.Equal(WorkOrderEventActions.Closed, evt.Action);
        Assert.Equal(0, evt.MemberDelta);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, evt.Note);

        Assert.False(w.Coordinator.RecomputeClosure(allDone, T0.AddHours(1)));
        Assert.Single(w.Orders.ListEvents(allDone));
    }

    [Fact]
    public void SweepClosures_外部逐筆標記全結案的兩張單被結案_仍有進行中的不動()
    {
        var w = new World();
        var o1 = w.AddOrder(Alice);
        var c1 = w.AddCase("c1", "H1", Alice, o1);
        var o2 = w.AddOrder(Bob);
        var c2 = w.AddCase("c2", "H2", Bob, o2);
        var o3 = w.AddOrder(Carol);
        w.AddCase("c3", "H3", Carol, o3);

        // 模擬詳情頁逐筆標記：直接改案件 store
        foreach (var c in new[] { c1, c2 })
        {
            c.Status = IssueHandlingStatuses.Resolved;
            c.ClosedAt = T0;
            w.Cases.Save(c);
        }

        Assert.Equal(2, w.Coordinator.SweepClosures(200, T0.AddHours(1)));
        Assert.Equal(WorkOrderCloseReasons.AllClosed, w.Orders.Get(o1)!.ClosedReason);
        Assert.Equal(WorkOrderCloseReasons.AllClosed, w.Orders.Get(o2)!.ClosedReason);
        Assert.Null(w.Orders.Get(o3)!.ClosedAt);
        Assert.Equal(0, w.Coordinator.SweepClosures(200, T0.AddHours(2)));
    }

    // ── 併發 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 併發_Save衝突一次_重讀重做成功()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        w.AddCase("a", "H1", Alice, id);
        w.Orders.FailNextSaves = 1;

        var result = w.Coordinator.Reassign(id, Bob, Actor());

        Assert.Equal(Bob, w.Orders.Get(id)!.HandlerId);
        Assert.Equal(1, result.MovedCases);
        Assert.Equal(0, w.Orders.FailNextSaves);
    }

    [Fact]
    public void 併發_Save連續衝突兩次_例外往上拋()
    {
        var w = new World();
        var id = w.AddOrder(Alice);
        w.AddCase("a", "H1", Alice, id);
        w.Orders.FailNextSaves = 2;

        Assert.Throws<DbUpdateConcurrencyException>(() => w.Coordinator.Reassign(id, Bob, Actor()));
        Assert.Equal(Alice, w.Orders.Get(id)!.HandlerId);
    }

    // ── Reply 事件（回饋第 47 輪 C-2a）──────────────────────────────────────────────

    [Fact]
    public void Reply_寫replied事件_內容含狀態說明台數_排在結案事件之前()
    {
        var (w, id, cases) = ThreeHostOrder();
        var h1 = cases.Single(c => c.HostName == "H1");

        var t1 = T0.AddHours(1);
        w.Coordinator.Reply(id, new[] { h1.CaseId }, IssueHandlingStatuses.InProgress, "換硬碟中", null, Actor(t1));
        var t2 = T0.AddHours(2);
        w.Coordinator.Reply(id, null, IssueHandlingStatuses.Resolved, "  ", null, Actor(t2));

        var replied = w.Orders.ListEvents(id).Where(e => e.Action == WorkOrderEventActions.Replied).ToList();
        Assert.Equal(2, replied.Count);
        Assert.Equal("in_progress：換硬碟中（1 台）", replied[0].Note);
        Assert.Equal(9, replied[0].ActorId);
        Assert.Equal("boss", replied[0].ActorAccount);
        Assert.Equal(0, replied[0].MemberDelta);
        Assert.Equal(t1, replied[0].CreatedAt);
        Assert.Equal("resolved（3 台）", replied[1].Note);
        Assert.Equal(new[] { WorkOrderEventActions.Replied, WorkOrderEventActions.Closed }, w.Events(id).TakeLast(2));
    }

    // ── TouchReply（詳情頁逐筆標記推進回覆時間）───────────────────────────

    [Fact]
    public void TouchReply_只改LastReplyAt_不寫事件_不推導結案()
    {
        var (w, id, cases) = ThreeHostOrder();
        foreach (var c in cases)
        {
            c.ClosedAt = T0;
            w.Cases.Save(c);
        }
        var eventsBefore = w.Events(id);
        var t1 = T0.AddHours(3);

        w.Coordinator.TouchReply(id, t1);

        var order = w.Orders.Get(id)!;
        Assert.Equal(t1, order.LastReplyAt);
        Assert.Null(order.ClosedAt);
        Assert.Equal(eventsBefore, w.Events(id));
    }

    [Fact]
    public void TouchReply_已結案或不存在的單_不動不擲()
    {
        var (w, id, _) = ThreeHostOrder();
        w.Coordinator.Cancel(id, "不做了", Actor(T0.AddHours(1)));
        var before = w.Orders.Get(id)!;
        var eventsBefore = w.Events(id);
        var savesBefore = w.Orders.SaveCalls;

        w.Coordinator.TouchReply(id, T0.AddHours(3));
        w.Coordinator.TouchReply(9999, T0.AddHours(3));

        var after = w.Orders.Get(id)!;
        Assert.Null(after.LastReplyAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(eventsBefore, w.Events(id));
        Assert.Equal(savesBefore, w.Orders.SaveCalls);
    }
}
