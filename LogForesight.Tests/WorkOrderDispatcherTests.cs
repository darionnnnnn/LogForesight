using System.Diagnostics;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="WorkOrderDispatcher.Decide"/> 的決策順序、續掛範圍、選人規則與 <see cref="DispatchContext"/>
/// 本趟狀態。候選人池直接手組（Web 端規則另由 DispatchCandidateSourceTests 驗）。
/// </summary>
public class WorkOrderDispatcherTests
{
    private const string Source = "Disk";
    private const int EventId = 153;

    private readonly FakeIssueOwnerStore _owners = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeNoiseMarkStore _noise = new();
    private readonly SystemSettings _settings = new() { AutoDispatchEnabled = true };
    private readonly List<DispatchCandidate> _candidates = new();
    private int _loadSeq;
    private int _caseSeq;

    public WorkOrderDispatcherTests()
    {
        _orders = new FakeWorkOrderStore(_cases);
    }

    // ── 組裝 ────────────────────────────────────────────────────────────

    private static WebHost Host(long id = 1, string name = "SRV-01", params long[] groupIds) =>
        new() { HostId = id, HostName = name, GroupIds = groupIds.ToList() };

    private static LogIssueSignature Issue(IssueSeverity severity = IssueSeverity.High, bool suppressed = false) => new()
    {
        LogName = "System", Source = Source, EventId = EventId, EntryType = EventLogEntryType.Error,
        Severity = severity, Suppressed = suppressed
    };

    private static string IssueKey => IssueSignatureKey.For(Issue());

    private void Candidate(long userId, string account, bool inPool = true, bool paused = false, params long[] visibleHostIds) =>
        _candidates.Add(new DispatchCandidate
        {
            UserId = userId, Account = account, InPool = inPool, Paused = paused,
            VisibleHostIds = visibleHostIds.ToHashSet()
        });

    private DispatchCandidatePool Pool() => new()
    {
        ByUserId = _candidates.ToDictionary(c => c.UserId),
        PoolMemberCount = _candidates.Count(c => c.InPool),
        ActivePoolMemberCount = _candidates.Count(c => c.InPool && !c.Paused)
    };

    private DispatchContext Build(DateTime? now = null) =>
        DispatchContext.Build(Pool(), _owners, _orders, _cases, _noise, _settings, now ?? DateTime.Today);

    private void Owners(params long[] userIds) =>
        _owners.Upsert(new IssueProfile { SourceName = Source, EventId = EventId, OwnerUserIds = userIds.ToList() });

    /// <summary>以其他問題的進行中單與成員案件墊出負載（ActiveMembers, ActiveWorkOrders）</summary>
    private void Load(long handlerId, int members, int workOrders)
    {
        for (var i = 0; i < workOrders; i++)
        {
            var id = _orders.Insert(new WorkOrder
            {
                SourceName = "Other" + (++_loadSeq), EventId = 1, HandlerId = handlerId,
                ScopeKind = WorkOrderScopes.Hosts, CreatedAt = DateTime.Today
            });
            if (i != 0) continue;
            for (var m = 0; m < members; m++)
            {
                _cases.Save(new IssueCase
                {
                    CaseId = "load" + (++_caseSeq), HostName = "H" + _caseSeq, IssueKey = "x", WorkOrderId = id,
                    Status = IssueHandlingStatuses.InProgress
                });
            }
        }
    }

    private long Order(long handlerId, string scope = WorkOrderScopes.All, bool autoAttach = true,
        DateTime? createdAt = null, params long[] scopeGroupIds) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = Source, EventId = EventId, HandlerId = handlerId, ScopeKind = scope,
            ScopeGroupIds = scopeGroupIds.ToList(), AutoAttach = autoAttach, CreatedAt = createdAt ?? DateTime.Today
        });

    private void Resolved(string hostName, long handlerId, DateTime closedAt) =>
        _cases.Save(new IssueCase
        {
            CaseId = "res" + (++_caseSeq), HostName = hostName, IssueKey = IssueKey, HandlerId = handlerId,
            Status = IssueHandlingStatuses.Resolved, ClosedAt = closedAt
        });

    private static void AssertSkip(DispatchDecision d, string reason, string? detail = null)
    {
        Assert.Equal(DispatchDecisionKind.Skip, d.Kind);
        Assert.Equal(reason, d.SkipReason);
        Assert.Equal(detail, d.NoCandidateDetail);
    }

    private static void AssertCreate(DispatchDecision d, long handlerId, string origin)
    {
        Assert.Equal(DispatchDecisionKind.CreateFor, d.Kind);
        Assert.Equal(handlerId, d.HandlerId);
        Assert.Equal(origin, d.Origin);
    }

    private static void AssertAttach(DispatchDecision d, long workOrderId, long handlerId)
    {
        Assert.Equal(DispatchDecisionKind.AttachTo, d.Kind);
        Assert.Equal(workOrderId, d.WorkOrderId);
        Assert.Equal(handlerId, d.HandlerId);
    }

    // ── 各步驟只命中此步 ────────────────────────────────────────────────

    [Fact]
    public void 靜音_紀錄日落在區間含首尾()
    {
        Candidate(1, "a", visibleHostIds: 1);
        var ctx = Build();
        var from = new DateTime(2026, 9, 1);
        var to = new DateTime(2026, 9, 3);
        ctx.MuteIntervals = new Dictionary<(string SourceUpper, int EventId), IReadOnlyList<(DateTime From, DateTime To)>>
        {
            [IssueProfile.KeyOf("disk", EventId)] = new List<(DateTime, DateTime)> { (from, to) }
        };

        AssertSkip(WorkOrderDispatcher.Decide(ctx, Host(), Issue(), from), WorkOrderDispatcher.SkipMuted);
        AssertSkip(WorkOrderDispatcher.Decide(ctx, Host(), Issue(), to), WorkOrderDispatcher.SkipMuted);
        AssertCreate(WorkOrderDispatcher.Decide(ctx, Host(), Issue(), to.AddDays(1)), 1, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 本段靜音區間恆為空()
    {
        Assert.Empty(Build().MuteIntervals);
    }

    [Fact]
    public void 閘門1_已抑制略過()
    {
        Candidate(1, "a", visibleHostIds: 1);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(suppressed: true), DateTime.Today),
            WorkOrderDispatcher.SkipSuppressed);
    }

    [Fact]
    public void 閘門2_已知雜訊略過_主機名不分大小寫()
    {
        Candidate(1, "a", visibleHostIds: new long[] { 1, 2 });
        _noise.Save(new NoiseMark { HostName = "srv-01", IssueKey = IssueKey });
        var ctx = Build();

        AssertSkip(WorkOrderDispatcher.Decide(ctx, Host(), Issue(), DateTime.Today), WorkOrderDispatcher.SkipNoise);
        AssertCreate(WorkOrderDispatcher.Decide(ctx, Host(2, "SRV-02"), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 閘門3_嚴重度不在未處理清單略過()
    {
        Candidate(1, "a", visibleHostIds: 1);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(IssueSeverity.Low), DateTime.Today),
            WorkOrderDispatcher.SkipSeverity);
    }

    [Fact]
    public void 續掛_範圍All命中()
    {
        var id = Order(9);
        AssertAttach(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), id, 9);
    }

    [Fact]
    public void 負責人_無進行中單時建單()
    {
        Candidate(7, "owner", inPool: false);
        Owners(7);
        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 7, WorkOrderOrigins.OwnerRule);
    }

    [Fact]
    public void 自動派工_無候選_no_pool()
    {
        Candidate(1, "a", inPool: false);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today),
            WorkOrderDispatcher.SkipNoCandidate, WorkOrderDispatcher.NoCandidateNoPool);
    }

    [Fact]
    public void 自動派工_無候選_all_paused()
    {
        Candidate(1, "a", paused: true);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today),
            WorkOrderDispatcher.SkipNoCandidate, WorkOrderDispatcher.NoCandidateAllPaused);
    }

    [Fact]
    public void 自動派工_無候選_no_visibility()
    {
        Candidate(1, "a", visibleHostIds: 99);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today),
            WorkOrderDispatcher.SkipNoCandidate, WorkOrderDispatcher.NoCandidateNoVisibility);
    }

    [Fact]
    public void 自動派工_總開關關閉略過()
    {
        _settings.AutoDispatchEnabled = false;
        Candidate(1, "a", visibleHostIds: 1);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), WorkOrderDispatcher.SkipDisabled);
    }

    // ── 順序 ────────────────────────────────────────────────────────────

    [Fact]
    public void 順序_可續掛單優先於負責人()
    {
        Candidate(7, "owner", inPool: false);
        Owners(7);
        var id = Order(9);

        AssertAttach(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), id, 9);
    }

    [Fact]
    public void 順序_負責人全暫停落到自動派工()
    {
        Candidate(7, "owner", inPool: false, paused: true);
        Candidate(1, "a", visibleHostIds: 1);
        Owners(7);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 順序_負責人不在候選池名單落到自動派工()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Owners(42);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    // ── ④ 續掛範圍 ─────────────────────────────────────────────────────

    [Fact]
    public void 續掛_Groups範圍與主機群組有交集才命中()
    {
        _settings.AutoDispatchEnabled = false;
        var id = Order(9, WorkOrderScopes.Groups, scopeGroupIds: 5);
        var ctx = Build();

        AssertAttach(WorkOrderDispatcher.Decide(ctx, Host(1, "SRV-01", 5, 6), Issue(), DateTime.Today), id, 9);
        AssertSkip(WorkOrderDispatcher.Decide(ctx, Host(2, "SRV-02", 7), Issue(), DateTime.Today), WorkOrderDispatcher.SkipDisabled);
    }

    [Fact]
    public void 續掛_Hosts範圍永不續掛()
    {
        _settings.AutoDispatchEnabled = false;
        Order(9, WorkOrderScopes.Hosts);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), WorkOrderDispatcher.SkipDisabled);
    }

    [Fact]
    public void 續掛_AutoAttach關閉不續掛()
    {
        _settings.AutoDispatchEnabled = false;
        Order(9, autoAttach: false);
        AssertSkip(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), WorkOrderDispatcher.SkipDisabled);
    }

    [Fact]
    public void 續掛_兩張可續掛取建立較晚者()
    {
        var older = Order(8, createdAt: DateTime.Today.AddDays(-2));
        var newer = Order(9, createdAt: DateTime.Today.AddDays(-1));
        Order(10, createdAt: DateTime.Today.AddDays(-3));

        AssertAttach(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), newer, 9);
        Assert.NotEqual(older, newer);
    }

    [Fact]
    public void 續掛_建立時間相同取單號大者()
    {
        var at = DateTime.Today.AddDays(-1);
        Order(8, createdAt: at);
        var larger = Order(9, createdAt: at);

        AssertAttach(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), larger, 9);
    }

    // ── ⑤ 負責人選人 ───────────────────────────────────────────────────

    [Fact]
    public void 負責人_成員數優先於單數()
    {
        Candidate(1, "a", inPool: false);
        Candidate(2, "b", inPool: false);
        Owners(1, 2);
        Load(1, members: 3, workOrders: 1);
        Load(2, members: 1, workOrders: 5);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 2, WorkOrderOrigins.OwnerRule);
    }

    [Fact]
    public void 負責人_本趟已選中者沿用()
    {
        Candidate(1, "a", inPool: false);
        Candidate(2, "b", inPool: false);
        Owners(1, 2);
        Load(1, members: 3, workOrders: 1);
        Load(2, members: 1, workOrders: 5);
        var ctx = Build();

        ctx.Commit(new DispatchDecision { Kind = DispatchDecisionKind.CreateFor, HandlerId = 1, Origin = WorkOrderOrigins.OwnerRule },
            Source, EventId);

        AssertCreate(WorkOrderDispatcher.Decide(ctx, Host(2, "SRV-02"), Issue(), DateTime.Today), 1, WorkOrderOrigins.OwnerRule);
    }

    [Fact]
    public void 負責人_已有此問題進行中單則掛進去()
    {
        Candidate(1, "a", inPool: false);
        Owners(1);
        var id = Order(1, WorkOrderScopes.Hosts, autoAttach: false);

        AssertAttach(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), id, 1);
    }

    [Fact]
    public void 負責人_不要求在派工池也不檢查可見主機()
    {
        Candidate(1, "a", inPool: false, paused: false);
        Candidate(2, "pool", visibleHostIds: 1);
        Owners(1);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.OwnerRule);
    }

    // ── ⑥ 自動派工選人 ─────────────────────────────────────────────────

    [Fact]
    public void 自動派工_負載相同時帳號不分大小寫升冪()
    {
        Candidate(1, "Carol", visibleHostIds: 1);
        Candidate(2, "dave", visibleHostIds: 1);
        Candidate(3, "bob", visibleHostIds: 1);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 3, WorkOrderOrigins.AutoDispatch);
    }

    /// <summary>突變參考：決勝順序「單數」與「帳號」對調時這條轉紅</summary>
    [Fact]
    public void 自動派工_成員數相同時單數優先於帳號()
    {
        Candidate(1, "alice", visibleHostIds: 1);
        Candidate(2, "zed", visibleHostIds: 1);
        Load(1, members: 1, workOrders: 2);
        Load(2, members: 1, workOrders: 1);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 2, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 自動派工_只在看得到主機的池成員中選()
    {
        Candidate(1, "a", visibleHostIds: 2);
        Candidate(2, "b", inPool: false, visibleHostIds: 1);
        Candidate(3, "c", visibleHostIds: 1);
        Load(3, members: 9, workOrders: 9);

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 3, WorkOrderOrigins.AutoDispatch);
    }

    // ── 延續性 ─────────────────────────────────────────────────────────

    [Fact]
    public void 延續性_30天內resolved處理人仍是候選_即使負載較重()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", visibleHostIds: 1);
        Load(2, members: 5, workOrders: 5);
        Resolved("srv-01", 2, DateTime.Today.AddDays(-10));
        Resolved("SRV-01", 1, DateTime.Today.AddDays(-20));

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 2, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 延續性_超過30天不適用()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", visibleHostIds: 1);
        Load(2, members: 5, workOrders: 5);
        Resolved("SRV-01", 2, DateTime.Today.AddDays(-31));

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 延續性_處理人已暫停不適用()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", paused: true);
        Resolved("SRV-01", 2, DateTime.Today.AddDays(-1));

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void 延續性_只看同一台主機()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", visibleHostIds: 1);
        Load(2, members: 5, workOrders: 5);
        Resolved("SRV-99", 2, DateTime.Today.AddDays(-1));

        AssertCreate(WorkOrderDispatcher.Decide(Build(), Host(), Issue(), DateTime.Today), 1, WorkOrderOrigins.AutoDispatch);
    }

    // ── Commit／RegisterOrder ──────────────────────────────────────────

    [Fact]
    public void Commit_同問題兩台主機都看得到時第二台沿用本趟已選中者()
    {
        Candidate(1, "a", visibleHostIds: new long[] { 1, 2 });
        Candidate(2, "b", visibleHostIds: new long[] { 1, 2 });
        var ctx = Build();

        var first = WorkOrderDispatcher.Decide(ctx, Host(1, "SRV-01"), Issue(), DateTime.Today);
        AssertCreate(first, 1, WorkOrderOrigins.AutoDispatch);
        ctx.Commit(first, Source, EventId);
        ctx.RegisterOrder(new WorkOrder { WorkOrderId = -1, SourceName = Source, EventId = EventId, HandlerId = 1 });

        // A 的負載已加上 (1,1)，單看負載會選 B；本趟已選中規則讓第二台仍選 A 並掛進虛擬單
        AssertAttach(WorkOrderDispatcher.Decide(ctx, Host(2, "SRV-02"), Issue(), DateTime.Today), -1, 1);
        Assert.Single(ctx.RegisteredOrders);
    }

    [Fact]
    public void Commit_兩台主機各自只有一人看得到時各自選對人()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", visibleHostIds: 2);
        var ctx = Build();

        var first = WorkOrderDispatcher.Decide(ctx, Host(1, "SRV-01"), Issue(), DateTime.Today);
        AssertCreate(first, 1, WorkOrderOrigins.AutoDispatch);
        ctx.Commit(first, Source, EventId);
        ctx.RegisterOrder(new WorkOrder { WorkOrderId = -1, SourceName = Source, EventId = EventId, HandlerId = 1 });

        AssertCreate(WorkOrderDispatcher.Decide(ctx, Host(2, "SRV-02"), Issue(), DateTime.Today), 2, WorkOrderOrigins.AutoDispatch);
    }

    [Fact]
    public void Commit_略過依原因計數()
    {
        Candidate(1, "a", visibleHostIds: 1);
        var ctx = Build();

        var skip = WorkOrderDispatcher.Decide(ctx, Host(), Issue(suppressed: true), DateTime.Today);
        ctx.Commit(skip, Source, EventId);
        ctx.Commit(skip, Source, EventId);

        Assert.Equal(2, ctx.SkipCounts[WorkOrderDispatcher.SkipSuppressed]);
    }

    [Fact]
    public void Decide不改脈絡_呼叫兩次結果相同()
    {
        Candidate(1, "a", visibleHostIds: 1);
        Candidate(2, "b", visibleHostIds: 1);
        var ctx = Build();

        var first = WorkOrderDispatcher.Decide(ctx, Host(), Issue(), DateTime.Today);
        var second = WorkOrderDispatcher.Decide(ctx, Host(), Issue(), DateTime.Today);

        Assert.Equal((first.Kind, first.HandlerId, first.WorkOrderId, first.Origin, first.SkipReason),
            (second.Kind, second.HandlerId, second.WorkOrderId, second.Origin, second.SkipReason));
        Assert.Equal((0, 0), ctx.LoadOf(1));
        Assert.Empty(ctx.ChosenFor(Source, EventId));
        Assert.Empty(ctx.SkipCounts);
    }
}
