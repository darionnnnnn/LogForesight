using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 問題負責與靜音負責人自動派工（夜間掛接接上交辦單派工）：
/// 掛接 ①②③ 不動；其餘問題由 <see cref="NightlyDispatch"/> 依派工決策建交辦單＋案件＋當日一列。
/// 負責人規則改為「每位負責人此問題一張單、多位負責人選負載最輕者」，並受派工閘門（含不再打擾）約束。
/// </summary>
public class IssueCaseCoordinatorAutoAssignTests
{
    private const string Host = "SRV-A";
    private const string IssueLabel = "disk 153";
    private static readonly string IssueKey = IssueSignatureKey.For("System", "disk", 153, System.Diagnostics.EventLogEntryType.Error);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _issueProfiles = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly List<DispatchCandidate> _candidates = new();

    public IssueCaseCoordinatorAutoAssignTests()
    {
        _hosts.Upsert(new WebHost { HostName = Host });
        _orders = new FakeWorkOrderStore(_cases);
    }

    private IssueCaseCoordinator Create() => new(_cases, _issueHandlings, _handlingLog, _records, _hosts, _issueProfiles);

    /// <summary>負責人不要求在派工池，但必須是具處理能力的使用者（在候選快照裡）</summary>
    private void Users(params long[] userIds)
    {
        foreach (var id in userIds)
            _candidates.Add(new DispatchCandidate { UserId = id, Account = "u" + id, InPool = false });
    }

    private NightlyDispatch Dispatch(IssueCaseCoordinator coordinator)
    {
        var pool = new DispatchCandidatePool { ByUserId = _candidates.ToDictionary(c => c.UserId) };
        var ctx = DispatchContext.Build(pool, _issueProfiles, _orders, _cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now);
        var workOrders = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, coordinator, _handlingLog, _hosts);
        return new NightlyDispatch(workOrders, ctx, _hosts);
    }

    private static List<LogIssueSignature> CreateDiskIssues() => new()
    {
        new()
        {
            LogName = "System", Source = "disk", EventId = 153, EntryType = System.Diagnostics.EventLogEntryType.Error,
            Severity = IssueSeverity.High
        }
    };

    private void Owners(params long[] userIds) =>
        _issueProfiles.Upsert(new IssueProfile { SourceName = "disk", EventId = 153, OwnerUserIds = userIds.ToList() });

    [Fact]
    public void 派工_問題負責與靜音兩位負責人_建一張owner_rule單給負載最輕者並掛當日一列()
    {
        Users(101, 102);
        Owners(101, 102);
        // 101 身上已有別的問題的進行中單與一件成員 → 102 較輕
        var otherId = _orders.Insert(new WorkOrder { SourceName = "Other", EventId = 1, HandlerId = 101, CreatedAt = DateTime.Today });
        _cases.Save(new IssueCase
        {
            CaseId = "load-1", HostName = "SRV-Z", IssueKey = "x", WorkOrderId = otherId, HandlerId = 101,
            Status = IssueHandlingStatuses.InProgress
        });

        var today = DateTime.Today;
        var coordinator = Create();
        var dispatch = Dispatch(coordinator);
        HostDayPostProcessor.AttachCase(coordinator, dispatch, Host, today, CreateDiskIssues());

        var order = Assert.Single(_orders.All, o => o.SourceName == "disk");
        Assert.Equal(WorkOrderOrigins.OwnerRule, order.Origin);
        Assert.Equal(102, order.HandlerId);
        Assert.Equal(WorkOrderScopes.Hosts, order.ScopeKind);
        Assert.False(order.AutoAttach);

        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(102, openCase.HandlerId);
        Assert.Equal(order.WorkOrderId, openCase.WorkOrderId);
        Assert.Equal(IssueHandlingStatuses.InProgress, openCase.Status);
        Assert.Equal(today, openCase.FirstLinkedDate);
        Assert.Equal(today, openCase.LastLinkedDate);

        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(IssueHandlingStatuses.InProgress, dayHandling.Status);
        Assert.Equal(openCase.CaseId, dayHandling.CaseId);
        Assert.Null(dayHandling.ActorId);

        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.OwnerAutoAssign, log.Action);
        Assert.Equal("系統依問題檔案自動派送", log.Note);
        Assert.Null(log.ActorId);
    }

    [Fact]
    public void 派工_已有進行中案件時走既有案件掛接_不建單()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        var coordinator = Create();
        coordinator.BuildCase(Host, IssueKey, IssueLabel, yesterday, handlerId: 99, note: "既有案件處理中",
            dueDate: null, actorId: 1, actorAccount: "admin", occurredAt: DateTime.Now);
        Users(101);
        Owners(101);

        var today = DateTime.Today;
        HostDayPostProcessor.AttachCase(coordinator, Dispatch(coordinator), Host, today, CreateDiskIssues());

        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(99, openCase.HandlerId);
        Assert.Empty(_orders.All);

        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(openCase.CaseId, dayHandling.CaseId);

        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.CaseAttach, log.Action);
    }

    [Fact]
    public void 派工_同時有AutoApply機房結論與負責人時_機房結論優先不建單()
    {
        Users(101);
        _issueProfiles.Upsert(new IssueProfile
        {
            SourceName = "disk",
            EventId = 153,
            ConclusionStatus = IssueHandlingStatuses.KnownNoise,
            ConclusionNote = "機房已知雜訊",
            AutoApply = true,
            OwnerUserIds = new List<long> { 101 }
        });

        var today = DateTime.Today;
        var coordinator = Create();
        HostDayPostProcessor.AttachCase(coordinator, Dispatch(coordinator), Host, today, CreateDiskIssues());

        Assert.Null(_cases.GetOpen(Host, IssueKey));
        Assert.Empty(_orders.All);

        var dayHandling = _issueHandlings.GetForDay(Host, today).Single();
        Assert.Equal(IssueHandlingStatuses.KnownNoise, dayHandling.Status);
        Assert.Equal("〔機房結論〕機房已知雜訊", dayHandling.Note);
        Assert.Null(dayHandling.CaseId);

        var log = _handlingLog.GetLogs(Host, today).Single(l => l.IssueKey == IssueKey);
        Assert.Equal(HandlingActions.FleetApply, log.Action);
    }

    [Fact]
    public void 派工_同一天重跑不產生第二件案件第二筆列或第二張單()
    {
        Users(101);
        Owners(101);

        var today = DateTime.Today;
        var coordinator = Create();
        var dispatch = Dispatch(coordinator);

        var first = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);
        dispatch.DispatchDay(Host, today, first.Unassigned, DateTime.Now);
        var second = coordinator.AttachNewDay(Host, today, CreateDiskIssues(), DateTime.Now);
        dispatch.DispatchDay(Host, today, second.Unassigned, DateTime.Now);

        Assert.Single(first.Unassigned);
        Assert.Empty(second.Unassigned);
        Assert.Equal(0, second.AttachedCount);

        // 另一趟（新脈絡）重跑同一天也一樣
        var rerun = Dispatch(coordinator);
        HostDayPostProcessor.AttachCase(coordinator, rerun, Host, today, CreateDiskIssues());

        Assert.Single(_cases.GetMany(new[] { Host }));
        Assert.Single(_issueHandlings.GetForDay(Host, today));
        Assert.Single(_handlingLog.GetLogs(Host, today));
        Assert.Single(_orders.All);
    }

    [Fact]
    public void 接線測試_透過HostDayPostProcessorAttachCase走得到派工()
    {
        Users(101);
        Owners(101);

        var today = DateTime.Today;
        var coordinator = Create();
        var dispatch = Dispatch(coordinator);

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Host, today, CreateDiskIssues());

        var openCase = _cases.GetOpen(Host, IssueKey);
        Assert.NotNull(openCase);
        Assert.Equal(101, openCase.HandlerId);
        Assert.NotNull(openCase.WorkOrderId);
        Assert.Equal(_issueHandlings.GetForDay(Host, today).Single().CaseId, openCase.CaseId);

        var summary = dispatch.FlushRun(DateTime.Now);
        Assert.Equal(1, summary.CreatedOrders);
        Assert.Equal(1, summary.AttachedMembers);
    }

    [Theory]
    [InlineData(IssueHandlingStatuses.WontFix, false)]
    [InlineData(IssueHandlingStatuses.FalsePositive, false)]
    [InlineData(IssueHandlingStatuses.KnownNoise, false)]
    [InlineData(IssueHandlingStatuses.Resolved, true)]
    public void 派工_最近一件案件已結案時_不處理類結案不再派_已解決則重新派(string closedStatus, bool expectDispatch)
    {
        Users(101);
        Owners(101);
        _cases.Save(new IssueCase
        {
            CaseId = "closed-1", HostName = Host, IssueKey = IssueKey, IssueLabel = IssueLabel,
            Status = closedStatus, HandlerId = 101, ClosedAt = DateTime.Now.AddDays(-1),
            FirstLinkedDate = DateTime.Today.AddDays(-3), LastLinkedDate = DateTime.Today.AddDays(-2),
            CreatedAt = DateTime.Now.AddDays(-3), UpdatedAt = DateTime.Now.AddDays(-1)
        });

        var coordinator = Create();
        var dispatch = Dispatch(coordinator);
        HostDayPostProcessor.AttachCase(coordinator, dispatch, Host, DateTime.Today, CreateDiskIssues());
        var summary = dispatch.FlushRun(DateTime.Now);

        var open = _cases.GetOpen(Host, IssueKey);
        if (expectDispatch)
        {
            Assert.NotNull(open);
            Assert.NotEqual("closed-1", open.CaseId);
            Assert.Single(_orders.All);
            Assert.False(summary.SkipCounts.ContainsKey(WorkOrderDispatcher.SkipDismissed));
        }
        else
        {
            Assert.Null(open);
            Assert.Empty(_orders.All);
            Assert.Equal(1, summary.SkipCounts[WorkOrderDispatcher.SkipDismissed]);
        }
    }
}
