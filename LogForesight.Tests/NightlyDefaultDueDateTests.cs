using LogForesight.Core.Analysis;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 P-1（W1、Q19）：夜間派工新建的單、案件與逐日列依問題嚴重度帶預設期限（高 3 天、中 7 天、低不帶）。
/// 只影響修改上線後新建的；既有案件不回填。天數是程式常數，測試以字面值斷言——把天數對調或改常數就會紅。
/// </summary>
public class NightlyDefaultDueDateTests
{
    private const string Source = "Disk";
    private const int EventId = 153;
    private static readonly DateTime Day = new(2026, 11, 20);
    private static readonly DateTime Occurred = new(2026, 11, 20, 2, 30, 0);

    private readonly FakeHostStore _hosts = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly SystemSettings _settings = new() { AutoDispatchEnabled = true };
    private readonly List<DispatchCandidate> _candidates = new();
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly FakeWorkOrderStore _orders;

    /// <summary>夜間掛接寫下的時間取自這個假時鐘（站台時鐘），不是系統時間</summary>
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(Occurred, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));

    public NightlyDefaultDueDateTests()
    {
        _clock.SetLocalTimeZone(TimeZoneInfo.Local);
        _orders = new FakeWorkOrderStore(_cases);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, _owners);
    }

    private static LogIssueSignature Issue(IssueSeverity severity, int eventId = EventId) => new()
    {
        LogName = "System", Source = Source, EventId = eventId, EntryType = LogEntryType.Error, Severity = severity
    };

    private WebHost AddHost(string name)
    {
        var host = _hosts.Upsert(new WebHost { HostName = name });
        return host;
    }

    private (NightlyDispatch Dispatch, WorkOrderCoordinator Coordinator) Create()
    {
        var pool = new DispatchCandidatePool
        {
            ByUserId = _candidates.ToDictionary(c => c.UserId),
            PoolMemberCount = _candidates.Count(c => c.InPool),
            ActivePoolMemberCount = _candidates.Count(c => c.InPool && !c.Paused)
        };
        var ctx = DispatchContext.Build(pool, _owners, _orders, _cases, new FakeNoiseMarkStore(), _settings, Occurred, TestRules.None);
        var coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, _caseCoordinator, _handlingLog, _hosts);
        return (new NightlyDispatch(coordinator, ctx, _hosts), coordinator);
    }

    private void AllowAll(params string[] hostNames)
    {
        _candidates.Add(new DispatchCandidate
        {
            UserId = 1, Account = "h1", InPool = true,
            VisibleHostIds = hostNames.Select(n => _hosts.FindByName(n)!.HostId).ToHashSet()
        });
    }

    private void Dispatch(NightlyDispatch dispatch, string host, params LogIssueSignature[] issues)
    {
        foreach (var issue in issues)
            HostDayPostProcessor.AttachCase(_caseCoordinator, dispatch, host, Day, new List<LogIssueSignature> { issue }, clock: _clock);
        dispatch.FlushRun(Occurred);
    }

    [Theory]
    [InlineData(IssueSeverity.High, 3)]
    [InlineData(IssueSeverity.Medium, 7)]
    public void 夜間新建單_依嚴重度帶預設期限_單案件逐日列一致(IssueSeverity severity, int days)
    {
        AddHost("SRV-01");
        AllowAll("SRV-01");
        var (dispatch, _) = Create();

        Dispatch(dispatch, "SRV-01", Issue(severity));

        var order = _orders.All.Single();
        var expected = Day.AddDays(days);
        Assert.Equal(expected, order.DueDate);
        var member = _cases.GetOpenForHost("SRV-01").Single();
        Assert.Equal(expected, member.DueDate);
        Assert.Equal(expected, _issueHandlings.GetForDay("SRV-01", Day).Single().DueDate);
    }

    /// <summary>低嚴重度正常不會被夜間派工建單（嚴重度閘門），這裡直接呼叫建單入口驗「低＝不帶期限」</summary>
    [Fact]
    public void 夜間建單入口_低嚴重度不帶期限()
    {
        AddHost("SRV-01");
        var (_, coordinator) = Create();
        var decision = new DispatchDecision { Kind = DispatchDecisionKind.CreateFor, HandlerId = 1, Origin = WorkOrderOrigins.AutoDispatch };

        var (order, created) = coordinator.EnsureNightlyOrder(decision, Issue(IssueSeverity.Low), Occurred);

        Assert.True(created);
        Assert.Null(order.DueDate);
        Assert.Null(_orders.Get(order.WorkOrderId)!.DueDate);
    }

    [Fact]
    public void 預設期限的逾期邊界_高嚴重度建立後第3天不逾期_第4天逾期()
    {
        AddHost("SRV-01");
        AllowAll("SRV-01");
        var (dispatch, _) = Create();
        Dispatch(dispatch, "SRV-01", Issue(IssueSeverity.High));
        var member = _cases.GetOpenForHost("SRV-01").Single();

        Assert.False(WorkOrderQueries.IsOverdue(member, Day.AddDays(3)));
        Assert.True(WorkOrderQueries.IsOverdue(member, Day.AddDays(4)));
    }

    [Fact]
    public void 預設期限的逾期邊界_中嚴重度建立後第7天不逾期_第8天逾期()
    {
        AddHost("SRV-01");
        AllowAll("SRV-01");
        var (dispatch, _) = Create();
        Dispatch(dispatch, "SRV-01", Issue(IssueSeverity.Medium));
        var member = _cases.GetOpenForHost("SRV-01").Single();

        Assert.False(WorkOrderQueries.IsOverdue(member, Day.AddDays(7)));
        Assert.True(WorkOrderQueries.IsOverdue(member, Day.AddDays(8)));
    }

    [Fact]
    public void 續掛進有期限的新單_新成員沿用單的期限()
    {
        AddHost("SRV-01");
        AddHost("SRV-02");
        AllowAll("SRV-01", "SRV-02");
        var (dispatch, _) = Create();

        Dispatch(dispatch, "SRV-01", Issue(IssueSeverity.High));
        Dispatch(dispatch, "SRV-02", Issue(IssueSeverity.High));

        var order = _orders.All.Single();
        Assert.Equal(Day.AddDays(3), order.DueDate);
        Assert.All(new[] { "SRV-01", "SRV-02" }, h => Assert.Equal(order.DueDate, _cases.GetOpenForHost(h).Single().DueDate));
    }

    [Fact]
    public void 續掛進無期限舊單_新成員依嚴重度帶預設_舊成員維持沒有期限()
    {
        AddHost("SRV-01");
        AddHost("SRV-02");
        AllowAll("SRV-01", "SRV-02");
        // 上線前建的舊單與舊成員：沒有期限
        var oldOrderId = _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = EventId, IssueLabel = $"{Source} {EventId}", HandlerId = 1,
            Origin = WorkOrderOrigins.AutoDispatch, ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
            CreatedByAccount = "system", CreatedAt = Day.AddDays(-2), DueDate = null
        });
        var oldKey = IssueSignatureKey.For(Issue(IssueSeverity.High));
        _cases.Save(new IssueCase
        {
            CaseId = "old", HostName = "SRV-01", IssueKey = oldKey, IssueLabel = $"{Source} {EventId}",
            Status = IssueHandlingStatuses.InProgress, HandlerId = 1, WorkOrderId = oldOrderId,
            FirstLinkedDate = Day.AddDays(-2), LastLinkedDate = Day.AddDays(-2),
            CreatedAt = Day.AddDays(-2), UpdatedAt = Day.AddDays(-2), DueDate = null
        });
        var (dispatch, _) = Create();

        Dispatch(dispatch, "SRV-02", Issue(IssueSeverity.High));

        Assert.Null(_orders.Get(oldOrderId)!.DueDate);
        Assert.Null(_cases.Get("old")!.DueDate);
        var fresh = _cases.GetOpenForHost("SRV-02").Single();
        Assert.Equal(oldOrderId, fresh.WorkOrderId);
        Assert.Equal(Day.AddDays(3), fresh.DueDate);
        Assert.Equal(Day.AddDays(3), _issueHandlings.GetForDay("SRV-02", Day).Single().DueDate);
        // Q19：舊成員不回填，也不會因此出現在逾期
        Assert.False(WorkOrderQueries.IsOverdue(_cases.Get("old")!, Day.AddDays(60)));
    }

    /// <summary>
    /// Q19 升級測試：上線前已存在的無期限進行中單與案件（直接寫入 store）。夜間派工續掛進其中一張後，
    /// 舊單、舊案件仍沒有期限；過了很久之後的清單查詢也不把它們算成逾期、三天內到期，處理優先序的逾期層只有新成員那一件。
    /// </summary>
    [Fact]
    public void 升級_上線前無期限的進行中單與案件_夜間續掛與清單查詢後仍無期限_不進逾期與三天內到期()
    {
        AddHost("SRV-01");
        AddHost("SRV-02");
        AddHost("SRV-03");
        AllowAll("SRV-01", "SRV-02", "SRV-03");
        var handler = new FakeUserStore().Upsert(new WebUser { Account = "h1", DisplayName = "處理人" });
        Assert.Equal(1, handler.UserId);

        long LegacyOrder(int eventId) => _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = eventId, IssueLabel = $"{Source} {eventId}", HandlerId = 1,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, ScopeGroupIds = new List<long>(),
            CreatedByAccount = "admin", CreatedAt = Day.AddDays(-30), DueDate = null
        });
        void LegacyCase(string caseId, string host, long orderId, int eventId) => _cases.Save(new IssueCase
        {
            CaseId = caseId, HostName = host, IssueKey = IssueSignatureKey.For(Issue(IssueSeverity.High, eventId)),
            IssueLabel = $"{Source} {eventId}", Status = IssueHandlingStatuses.InProgress, HandlerId = 1, WorkOrderId = orderId,
            FirstLinkedDate = Day.AddDays(-30), LastLinkedDate = Day.AddDays(-30),
            CreatedAt = Day.AddDays(-30), UpdatedAt = Day.AddDays(-30), DueDate = null
        });
        var continued = LegacyOrder(EventId);          // 夜間會續掛進來
        var untouched = LegacyOrder(EventId + 1);      // 夜間不會碰
        LegacyCase("old-1", "SRV-01", continued, EventId);
        LegacyCase("old-3", "SRV-03", untouched, EventId + 1);

        var (dispatch, _) = Create();
        Dispatch(dispatch, "SRV-02", Issue(IssueSeverity.High));

        // 夜間掛接之後：舊單、舊案件仍沒有期限；只有新成員依嚴重度帶了預設
        Assert.Null(_orders.Get(continued)!.DueDate);
        Assert.Null(_orders.Get(untouched)!.DueDate);
        Assert.Null(_cases.Get("old-1")!.DueDate);
        Assert.Null(_cases.Get("old-3")!.DueDate);
        var fresh = _cases.GetOpenForHost("SRV-02").Single();
        Assert.Equal(continued, fresh.WorkOrderId);
        Assert.Equal(Day.AddDays(3), fresh.DueDate);

        // 兩個月後查詢：舊成員仍不逾期、不是三天內到期；逾期只剩新成員那一件
        _clock.Advance(TimeSpan.FromDays(60));
        Assert.False(WorkOrderQueries.IsOverdue(_cases.Get("old-1")!, Day.AddDays(60)));
        Assert.False(WorkOrderQueries.IsOverdue(_cases.Get("old-3")!, Day.AddDays(60)));
        var admin = FakeCurrentUser.ForUser(9001, new[] { Capability.Assign, Capability.ViewAll });
        var users = new FakeUserStore();
        users.Upsert(new WebUser { Account = "h1", DisplayName = "處理人" });
        var service = new WorkOrderQueryService(
            _orders, _cases, users, _hosts, new FakeHostGroupStore(), new FakeRuleStore(),
            new ScopedVisibility(_hosts.GetAll().Select(h => h.HostId).ToArray()),
            admin, new UserDisplayNameService(new FakeSystemSettingsStore()), new FixedIssueExclusionSource(IssueExclusion.None),
            new FakeUserGroupStore(), _records, _clock, null, _issueHandlings, new FakeIssueAggregateQuery());

        var dueSoon = service.List(new WorkOrderListRequest { Flag = "dueSoon", Status = "all" });
        Assert.Empty(dueSoon.Items);

        var summary = service.HandlerSummary(1);
        Assert.Equal(1, summary.OverdueMembers);       // 只有夜間新帶期限、現在已過期的 SRV-02；兩個舊成員不算

        // 處理優先序：逾期層只有續掛的那張（新成員逾期）；沒碰過的舊單沒有逾期成員，排在它後面
        var priority = service.List(new WorkOrderListRequest { Sort = "priority", Status = "all" }).Items.Select(i => i.WorkOrderId).ToList();
        Assert.Equal(new[] { continued, untouched }, priority);
        var untouchedRow = service.List(new WorkOrderListRequest { Sort = "priority", Status = "all" }).Items.Single(i => i.WorkOrderId == untouched);
        Assert.Null(untouchedRow.DueDate);
    }

    [Fact]
    public void 預設期限常數_高3天_中7天_低與Critical的對應()
    {
        Assert.Equal(3, DefaultDueDays.High);
        Assert.Equal(7, DefaultDueDays.Medium);
        Assert.Equal(Day.AddDays(3), DefaultDueDays.DueDateFor(IssueSeverity.High, Day));
        Assert.Equal(Day.AddDays(7), DefaultDueDays.DueDateFor(IssueSeverity.Medium, Day));
        Assert.Null(DefaultDueDays.DueDateFor(IssueSeverity.Low, Day));
        // 舊資料的 Critical 等同 High
        Assert.Equal(Day.AddDays(3), DefaultDueDays.DueDateFor(IssueSeverity.Critical, Day));
        // 只取日期、不帶時間
        Assert.Equal(Day.AddDays(3), DefaultDueDays.DueDateFor(IssueSeverity.High, Day.AddHours(23)));
    }
}
