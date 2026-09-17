using System.Diagnostics;
using System.Reflection;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 交辦單命令 API（<see cref="WorkOrderCommandService"/>）。組裝比照 RecordQueryServiceSearchTests：
/// 真正的 <see cref="EfAnalysisRecordStore"/>＋<see cref="EfIssueAggregateQuery"/>＋<see cref="RecordListQueryService"/>，
/// 所以「同口徑」測到的是依問題視角實際會跑的那條 SQL 聚合路徑；交辦單與案件 store 用記憶體替身。
/// </summary>
public class WorkOrderCommandServiceTests : IDisposable
{
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeNoiseMarkStore _noiseMarks = new();
    private readonly FakeWorkOrderStore _orderStore;
    private readonly ScopedVisibility _visibility;
    private readonly RecordingAuditService _audit = new();
    private readonly RecordListQueryService _query;
    private readonly WorkOrderCommandService _service;
    private readonly UserGroup _handlerRole;

    public WorkOrderCommandServiceTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _orderStore = new FakeWorkOrderStore(_caseStore);
        _visibility = new ScopedVisibility(_hosts);
        var severity = new FakeSystemSettingsService();
        var repository = new RecordRepository(_recordStore, _hosts, _visibility, severity);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        var displayNames = new UserDisplayNameService(_settingsStore);

        _query = new RecordListQueryService(
            repository, _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore, severity,
            _visibility, aggregates, statusResolver, displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(_orderStore, _caseStore, _issueHandlingStore, caseCoordinator, _handlingStore, _hosts);

        _handlerRole = _userGroups.Upsert(new UserGroup { GroupName = "處理人", Role = UserRole.User, Active = true });

        _service = new WorkOrderCommandService(
            _query, aggregates, coordinator, _orderStore, _caseStore, _noiseMarks, _hosts, _users, _visibility,
            new UserCapabilityResolver(_userGroups, _hosts), FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            _audit, displayNames);
    }

    public void Dispose() => _fixture.Dispose();

    // ── 測試資料 ─────────────────────────────────────────────────────────────

    private static LogIssueSignature Disk(string logName = "System", EventLogEntryType entryType = EventLogEntryType.Error) => new()
    {
        LogName = logName, Source = "disk", EventId = 153, EntryType = entryType,
        Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other
    };

    private static LogIssueSignature Ntfs() => new()
    {
        LogName = "System", Source = "ntfs", EventId = 55, EntryType = EventLogEntryType.Error,
        Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Other
    };

    private WebHost AddHost(string name, params LogIssueSignature[] issues)
    {
        var host = TestData.AddHost(_hosts, name);
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = "高",
            Headline = name, TopIssues = issues.Length == 0 ? new List<LogIssueSignature> { Disk() } : issues.ToList()
        });
        return host;
    }

    private WebUser AddUser(string account, string displayName, bool active = true, bool paused = false, bool canHandle = true, long? groupId = null)
    {
        var groups = new List<long>();
        if (canHandle) groups.Add(_handlerRole.GroupId);
        if (groupId.HasValue) groups.Add(groupId.Value);
        return _users.Upsert(new WebUser
        {
            Account = account, DisplayName = displayName, Active = active, DispatchPaused = paused, GroupIds = groups
        });
    }

    private UserGroup AddTeam() => _userGroups.Upsert(new UserGroup { GroupName = "值班組", Role = UserRole.User, Active = true });

    private static CreateWorkOrderRequest Single(long handlerId) => new()
    {
        Source = "disk", EventId = 153, AssignMode = "single", HandlerId = handlerId, ScopeKind = WorkOrderScopes.Hosts
    };

    private static CreateWorkOrderRequest Group(long groupId, string splitMode) => new()
    {
        Source = "disk", EventId = 153, AssignMode = "group", GroupId = groupId, SplitMode = splitMode, ScopeKind = WorkOrderScopes.Hosts
    };

    private IssueCase? OpenCase(string hostName, LogIssueSignature? issue = null) =>
        _caseStore.GetOpen(hostName, IssueSignatureKey.For(issue ?? Disk()));

    private List<IssueCase> MembersOf(long workOrderId) => _caseStore.GetByWorkOrder(workOrderId, 0, int.MaxValue);

    private int TotalCases() => _caseStore.GetMany(_hosts.GetAll().Select(h => h.HostName)).Count;

    private static Dictionary<long, int> AllocationOf(WorkOrderPreviewDto preview) =>
        preview.Allocation.ToDictionary(a => a.HandlerId, a => a.HostCount);

    // ── 同口徑／預覽＝落盤 ───────────────────────────────────────────────────

    [Fact]
    public void 同口徑_依問題視角該問題的主機數等於預覽受影響主機數()
    {
        AddHost("HOST-A");
        AddHost("HOST-B", Disk(), Disk(entryType: EventLogEntryType.Warning));
        AddHost("HOST-C");
        AddHost("HOST-D", Ntfs());
        var handler = AddUser("DOMAIN\\h", "處理人");

        var row = Assert.Single(_query.SearchByIssue(new RecordSearchRequest()).Items, i => i.Source == "disk" && i.EventId == 153);
        var preview = _service.Preview(Single(handler.UserId));

        Assert.Equal(3, row.HostCount);
        Assert.Equal(row.HostCount, preview.AffectedHosts);
        Assert.Equal(4, preview.AffectedMembers);
        Assert.Equal(row.DayCount, preview.EstimatedHostDays);
    }

    /// <summary>
    /// 預覽與落盤共用計畫：同一個請求，預覽的每人台數＝落盤後每張單的成員數（每台一個簽章），
    /// 預覽標為略過／雜訊排除的主機落盤後沒有新處理人的案件。
    /// </summary>
    [Fact]
    public void 預覽等於落盤_每人台數等於單成員數_略過與雜訊主機沒有被寫入()
    {
        foreach (var name in new[] { "HOST-A", "HOST-B", "HOST-C", "HOST-D", "HOST-E", "HOST-F" }) AddHost(name);
        var team = AddTeam();
        var first = AddUser("DOMAIN\\a", "甲", groupId: team.GroupId);
        var second = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        var other = AddUser("DOMAIN\\z", "丙");

        var host = _hosts.GetAll().Single(h => h.HostName == "HOST-B");
        var existing = Single(other.UserId);
        existing.HostIds = new List<long> { host.HostId };
        _service.Create(existing);
        _audit.Entries.Clear();
        _noiseMarks.Save(new NoiseMark { HostName = "HOST-D", IssueKey = IssueSignatureKey.For(Disk()) });
        var otherOrderId = OpenCase("HOST-B")!.WorkOrderId;

        var request = Group(team.GroupId, "roundRobin");
        var preview = _service.Preview(request);
        var result = _service.Create(request);

        // 母體 6 台：B 由丙處理中（略過、不參與分攤）、D 雜訊排除 → A、C、E、F 輪流：甲 A、E；乙 C、F
        Assert.Equal(new Dictionary<long, int> { [first.UserId] = 2, [second.UserId] = 2 }, AllocationOf(preview));
        Assert.Equal(2, result.Orders.Count);
        foreach (var order in result.Orders)
            Assert.Equal(AllocationOf(preview)[order.HandlerId], MembersOf(order.WorkOrderId).Count);

        var skippedInPreview = preview.Hosts.Where(h => h.AllocatedHandlerId == null).Select(h => h.HostName).ToList();
        Assert.Equal(new[] { "HOST-B", "HOST-D" }, skippedInPreview);
        Assert.Equal(other.UserId, OpenCase("HOST-B")!.HandlerId);
        Assert.Equal(otherOrderId, OpenCase("HOST-B")!.WorkOrderId);
        Assert.Null(OpenCase("HOST-D"));
        Assert.Equal("HOST-B", Assert.Single(result.Skipped).HostName);
        Assert.Equal(1, result.NoiseExcludedHosts);
    }

    [Fact]
    public void 一台兩個簽章_兩件案件同一人()
    {
        AddHost("HOST-A", Disk(), Disk(logName: "Application"));
        var handler = AddUser("DOMAIN\\h", "處理人");

        var preview = _service.Preview(Single(handler.UserId));
        var result = _service.Create(Single(handler.UserId));

        Assert.Equal(1, preview.AffectedHosts);
        Assert.Equal(2, preview.AffectedMembers);
        var order = Assert.Single(result.Orders);
        Assert.Equal(2, order.NewCases);
        Assert.All(MembersOf(order.WorkOrderId), c => Assert.Equal(handler.UserId, c.HandlerId));
        Assert.Equal(2, MembersOf(order.WorkOrderId).Select(c => c.IssueKey).Distinct().Count());
    }

    [Fact]
    public void 手動排除整台生效()
    {
        var a = AddHost("HOST-A");
        AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人");

        var request = Single(handler.UserId);
        request.ExcludeHostIds = new List<long> { a.HostId };
        var preview = _service.Preview(request);
        _service.Create(request);

        Assert.Equal(1, preview.ManuallyExcludedHosts);
        Assert.Equal(1, preview.AffectedHosts);
        Assert.True(preview.Hosts.Single(h => h.HostName == "HOST-A").ManuallyExcluded);
        Assert.Null(OpenCase("HOST-A"));
        Assert.NotNull(OpenCase("HOST-B"));
    }

    [Fact]
    public void 雜訊記憶_整台全部成員被扣才算一台()
    {
        AddHost("HOST-A", Disk(), Disk(logName: "Application"));
        AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人");
        _noiseMarks.Save(new NoiseMark { HostName = "host-a", IssueKey = IssueSignatureKey.For(Disk()) });
        _noiseMarks.Save(new NoiseMark { HostName = "HOST-B", IssueKey = IssueSignatureKey.For(Disk()) });

        var preview = _service.Preview(Single(handler.UserId));
        var result = _service.Create(Single(handler.UserId));

        Assert.Equal(1, preview.NoiseExcludedHosts);
        Assert.Equal(1, preview.AffectedHosts);
        Assert.Equal(1, preview.AffectedMembers);
        Assert.Equal(1, result.NoiseExcludedHosts);
        Assert.Null(OpenCase("HOST-A", Disk()));
        Assert.NotNull(OpenCase("HOST-A", Disk(logName: "Application")));
        Assert.Null(OpenCase("HOST-B"));
    }

    // ── 群組分攤 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 群組輪流_三台兩人依帳號序分二與一()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");
        AddHost("HOST-C");
        var team = AddTeam();
        // 建立順序刻意與帳號序相反：分派依帳號，不依 id
        var later = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        var earlier = AddUser("DOMAIN\\a", "甲", groupId: team.GroupId);

        var result = _service.Create(Group(team.GroupId, "roundRobin"));

        Assert.Equal(earlier.UserId, OpenCase("HOST-A")!.HandlerId);
        Assert.Equal(later.UserId, OpenCase("HOST-B")!.HandlerId);
        Assert.Equal(earlier.UserId, OpenCase("HOST-C")!.HandlerId);
        Assert.Equal(2, result.Orders.Single(o => o.HandlerId == earlier.UserId).NewCases);
        Assert.Equal(1, result.Orders.Single(o => o.HandlerId == later.UserId).NewCases);
    }

    /// <summary>給處理人一張別的問題的進行中單，底下 n 件進行中案件（LoadBoard.ActiveMembers＝n）</summary>
    private void GiveLoad(WebUser user, int n)
    {
        var order = new WorkOrder
        {
            SourceName = "ntfs", EventId = 55, IssueLabel = "ntfs 55", HandlerId = user.UserId,
            Origin = WorkOrderOrigins.Manual, CreatedAt = DateTime.Now, CreatedByAccount = "x"
        };
        _orderStore.Insert(order);
        for (var i = 0; i < n; i++)
        {
            _caseStore.Save(new IssueCase
            {
                CaseId = Guid.NewGuid().ToString("n"), HostName = $"LOAD-{user.UserId}-{i}", IssueKey = IssueSignatureKey.For(Ntfs()),
                Status = IssueHandlingStatuses.InProgress, HandlerId = user.UserId, WorkOrderId = order.WorkOrderId
            });
        }
    }

    [Fact]
    public void 群組依負載_甲負載5乙0_三台全給乙()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");
        AddHost("HOST-C");
        var team = AddTeam();
        var first = AddUser("DOMAIN\\a", "甲", groupId: team.GroupId);
        var second = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        GiveLoad(first, 5);

        // 每台給「負載＋本計畫已分到成員數」最小者，同分依帳號：
        //   HOST-A：甲 5+0、乙 0+0 → 乙（乙=1）
        //   HOST-B：甲 5+0、乙 0+1 → 乙（乙=2）
        //   HOST-C：甲 5+0、乙 0+2 → 乙（乙=3）
        var preview = _service.Preview(Group(team.GroupId, "byLoad"));

        Assert.Equal(new Dictionary<long, int> { [second.UserId] = 3 }, AllocationOf(preview));
    }

    [Fact]
    public void 群組依負載_同分依帳號序()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");
        AddHost("HOST-C");
        var team = AddTeam();
        var second = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        var first = AddUser("DOMAIN\\a", "甲", groupId: team.GroupId);
        GiveLoad(first, 1);

        //   HOST-A：甲 1+0、乙 0+0 → 乙（乙=1）
        //   HOST-B：甲 1+0、乙 0+1 同分 → 帳號序甲（甲=1）
        //   HOST-C：甲 1+1、乙 0+1 → 乙（乙=2）
        _service.Create(Group(team.GroupId, "byLoad"));

        Assert.Equal(second.UserId, OpenCase("HOST-A")!.HandlerId);
        Assert.Equal(first.UserId, OpenCase("HOST-B")!.HandlerId);
        Assert.Equal(second.UserId, OpenCase("HOST-C")!.HandlerId);
    }

    [Fact]
    public void 群組_暫停接單者不分派且計數()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");
        var team = AddTeam();
        var active = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        AddUser("DOMAIN\\a", "甲", paused: true, groupId: team.GroupId);

        var preview = _service.Preview(Group(team.GroupId, "roundRobin"));

        Assert.Equal(1, preview.PausedMembersExcluded);
        Assert.Equal(new Dictionary<long, int> { [active.UserId] = 2 }, AllocationOf(preview));
    }

    [Fact]
    public void 群組_零候選_驗證錯誤且零寫入()
    {
        AddHost("HOST-A");
        var team = AddTeam();
        AddUser("DOMAIN\\a", "甲", paused: true, groupId: team.GroupId);
        AddUser("DOMAIN\\b", "乙", active: false, groupId: team.GroupId);

        var ex = Assert.Throws<DomainException>(() => _service.Create(Group(team.GroupId, "roundRobin")));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("沒有可分派的成員", ex.Message);
        AssertNoWrites();
    }

    // ── 衝突／併入 ───────────────────────────────────────────────────────────

    [Fact]
    public void 衝突_不改派_略過且不參與分攤()
    {
        var a = AddHost("HOST-A");
        AddHost("HOST-B");
        AddHost("HOST-C");
        var team = AddTeam();
        var first = AddUser("DOMAIN\\a", "甲", groupId: team.GroupId);
        var second = AddUser("DOMAIN\\b", "乙", groupId: team.GroupId);
        var other = AddUser("DOMAIN\\z", "丙");
        var existing = Single(other.UserId);
        existing.HostIds = new List<long> { a.HostId };
        _service.Create(existing);

        var preview = _service.Preview(Group(team.GroupId, "roundRobin"));
        var result = _service.Create(Group(team.GroupId, "roundRobin"));

        // HOST-A 不參與分攤：輪流從 HOST-B 開始（甲 B、乙 C），而不是甲 A、乙 B、甲 C
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(other.UserId, conflict.HandlerId);
        Assert.Equal(1, conflict.HostCount);
        Assert.Equal(new Dictionary<long, int> { [first.UserId] = 1, [second.UserId] = 1 }, AllocationOf(preview));
        Assert.Equal(first.UserId, OpenCase("HOST-B")!.HandlerId);
        Assert.Equal(second.UserId, OpenCase("HOST-C")!.HandlerId);
        Assert.Equal(other.UserId, OpenCase("HOST-A")!.HandlerId);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("HOST-A", skipped.HostName);
        Assert.Equal("丙(DOMAIN\\z)", skipped.ExistingHandlerName);
    }

    /// <summary>單人指派同樣先在計畫裡判定略過：預覽台數不含略過主機，才會等於落盤後單的成員數</summary>
    [Fact]
    public void 衝突_單人不改派_預覽台數不含略過主機且等於落盤成員數()
    {
        var a = AddHost("HOST-A");
        AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人");
        var other = AddUser("DOMAIN\\z", "丙");
        var existing = Single(other.UserId);
        existing.HostIds = new List<long> { a.HostId };
        _service.Create(existing);

        var preview = _service.Preview(Single(handler.UserId));
        var result = _service.Create(Single(handler.UserId));

        Assert.Equal(new Dictionary<long, int> { [handler.UserId] = 1 }, AllocationOf(preview));
        Assert.Null(preview.Hosts.Single(h => h.HostName == "HOST-A").AllocatedHandlerId);
        Assert.Equal("丙(DOMAIN\\z)", preview.Hosts.Single(h => h.HostName == "HOST-A").ExistingHandlerName);
        var order = Assert.Single(result.Orders, o => o.HandlerId == handler.UserId);
        Assert.Equal(AllocationOf(preview)[handler.UserId], MembersOf(order.WorkOrderId).Count);
        Assert.Equal("HOST-A", Assert.Single(result.Skipped).HostName);
        Assert.Equal(other.UserId, OpenCase("HOST-A")!.HandlerId);
    }

    [Fact]
    public void 衝突_要求改派_改派給新處理人()
    {
        var a = AddHost("HOST-A");
        var handler = AddUser("DOMAIN\\h", "處理人");
        var other = AddUser("DOMAIN\\z", "丙");
        _service.Create(Single(other.UserId));

        var request = Single(handler.UserId);
        request.ReassignConflicts = true;
        var preview = _service.Preview(request);
        var result = _service.Create(request);

        Assert.Equal(new Dictionary<long, int> { [handler.UserId] = 1 }, AllocationOf(preview));
        Assert.Equal(1, Assert.Single(result.Orders).Reassigned);
        Assert.Empty(result.Skipped);
        Assert.Equal(handler.UserId, OpenCase(a.HostName)!.HandlerId);
    }

    [Fact]
    public void 併入_處理人已有同問題進行中單_預覽帶單號且落盤後單數不增()
    {
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人");
        var first = Single(handler.UserId);
        first.HostIds = new List<long> { a.HostId };
        var existingId = Assert.Single(_service.Create(first).Orders).WorkOrderId;

        var second = Single(handler.UserId);
        second.HostIds = new List<long> { b.HostId };
        var preview = _service.Preview(second);
        var result = _service.Create(second);

        Assert.Equal(existingId, Assert.Single(preview.Allocation).MergeIntoWorkOrderId);
        var order = Assert.Single(result.Orders);
        Assert.False(order.CreatedOrder);
        Assert.Equal(existingId, order.WorkOrderId);
        Assert.Single(_orderStore.All);
        Assert.Equal(2, MembersOf(existingId).Count);
    }

    [Fact]
    public void 提示_處理人看不到主機與沒有處理能力()
    {
        var a = AddHost("HOST-A");
        AddHost("HOST-B");
        var handler = AddUser("DOMAIN\\h", "處理人", canHandle: false);
        _visibility.HiddenFor[handler.UserId] = new HashSet<long> { a.HostId };

        var result = _service.Create(Single(handler.UserId));

        var noAccess = Assert.Single(result.AssigneeNoAccess);
        Assert.Equal("HOST-A", noAccess.HostName);
        Assert.Equal(1, result.AssigneeNoAccessTotal);
        var cannot = Assert.Single(result.AssigneeCannotHandle);
        Assert.Equal(2, cannot.HostCount);
        Assert.Single(result.Orders);
    }

    // ── 驗證錯誤：皆 DomainException 且零寫入 ────────────────────────────────

    private void AssertNoWrites()
    {
        Assert.Empty(_orderStore.All);
        Assert.Equal(0, TotalCases());
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 驗證_群組範圍缺主機群組()
    {
        AddHost("HOST-A");
        var handler = AddUser("DOMAIN\\h", "處理人");
        var request = Single(handler.UserId);
        request.ScopeKind = WorkOrderScopes.Groups;

        var ex = Assert.Throws<DomainException>(() => _service.Create(request));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        AssertNoWrites();
    }

    [Fact]
    public void 驗證_未知處理人為NotFound()
    {
        AddHost("HOST-A");

        var ex = Assert.Throws<DomainException>(() => _service.Create(Single(12345)));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
        AssertNoWrites();
    }

    [Fact]
    public void 驗證_停用處理人()
    {
        AddHost("HOST-A");
        var handler = AddUser("DOMAIN\\h", "處理人", active: false);

        var ex = Assert.Throws<DomainException>(() => _service.Create(Single(handler.UserId)));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        AssertNoWrites();
    }

    [Fact]
    public void 驗證_零主機()
    {
        AddHost("HOST-A", Ntfs());
        var handler = AddUser("DOMAIN\\h", "處理人");

        var ex = Assert.Throws<DomainException>(() => _service.Create(Single(handler.UserId)));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("找不到任何符合條件", ex.Message);
        AssertNoWrites();
    }

    [Fact]
    public void 驗證_代為結案非結案狀態_零寫入()
    {
        var (orderId, _) = CreateOneOrder();
        _audit.Entries.Clear();

        var ex = Assert.Throws<DomainException>(() =>
            _service.AdminClose(orderId, new AdminCloseWorkOrderRequest { Status = IssueHandlingStatuses.InProgress, Reason = "r" }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Null(_orderStore.Get(orderId)!.ClosedAt);
        Assert.All(MembersOf(orderId), c => Assert.Null(c.ClosedAt));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 驗證_取消缺原因_零寫入()
    {
        var (orderId, _) = CreateOneOrder();
        _audit.Entries.Clear();

        var ex = Assert.Throws<DomainException>(() => _service.Cancel(orderId, new CancelWorkOrderRequest { Reason = "  " }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Null(_orderStore.Get(orderId)!.ClosedAt);
        Assert.All(MembersOf(orderId), c => Assert.Null(c.ClosedAt));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 單不存在一律NotFound()
    {
        var handler = AddUser("DOMAIN\\h", "處理人");

        Assert.Equal(ApiErrorCodes.NotFound, Assert.Throws<DomainException>(() => _service.Cancel(999, new CancelWorkOrderRequest { Reason = "r" })).Code);
        Assert.Equal(ApiErrorCodes.NotFound, Assert.Throws<DomainException>(() => _service.Reassign(999, new ReassignWorkOrderRequest { HandlerId = handler.UserId })).Code);
        Assert.Equal(ApiErrorCodes.NotFound, Assert.Throws<DomainException>(() => _service.Append(999, new AppendWorkOrderRequest { HostIds = new List<long> { 1 } })).Code);
    }

    // ── 單一單的操作 ─────────────────────────────────────────────────────────

    private (long OrderId, WebUser Handler) CreateOneOrder(params string[] hostNames)
    {
        var names = hostNames.Length == 0 ? new[] { "HOST-A" } : hostNames;
        foreach (var name in names) AddHost(name);
        var handler = AddUser("DOMAIN\\h", "處理人");
        var result = _service.Create(Single(handler.UserId));
        return (Assert.Single(result.Orders).WorkOrderId, handler);
    }

    private AuditEntry SingleAudit(string action) => Assert.Single(_audit.Entries, e => e.Action == action);

    [Fact]
    public void 建單_稽核帶問題鍵與摘要()
    {
        CreateOneOrder("HOST-A", "HOST-B");

        var entry = SingleAudit(AuditActions.WorkOrderCreate);
        Assert.Equal("work_order", entry.TargetKind);
        Assert.Equal("disk/153", entry.TargetId);
        Assert.Equal("交辦『disk 153』給 處理人(DOMAIN\\h)：新建 1 張、併入 0 張，加入 2 台（略過 0 台、雜訊排除 0 台）", entry.Summary);
    }

    [Fact]
    public void 追加_不可見主機被忽略並回報數量()
    {
        var (orderId, _) = CreateOneOrder("HOST-A");
        var b = AddHost("HOST-B");
        var c = AddHost("HOST-C");
        _visibility.Hidden.Add(c.HostId);

        var result = _service.Append(orderId, new AppendWorkOrderRequest { HostIds = new List<long> { b.HostId, c.HostId } });

        Assert.Equal(1, result.NewCases);
        Assert.Equal(1, result.IgnoredHosts);
        Assert.Equal(orderId, OpenCase("HOST-B")!.WorkOrderId);
        Assert.Null(OpenCase("HOST-C"));
        Assert.Equal(orderId.ToString(), SingleAudit(AuditActions.WorkOrderAppend).TargetId);
    }

    [Fact]
    public void 改派_回傳原處理人()
    {
        var (orderId, handler) = CreateOneOrder("HOST-A");
        var next = AddUser("DOMAIN\\n", "新處理人");

        var result = _service.Reassign(orderId, new ReassignWorkOrderRequest { HandlerId = next.UserId });

        Assert.Equal(handler.UserId, result.PreviousHandlerId);
        Assert.False(result.MergedIntoExisting);
        Assert.Equal(orderId, result.TargetWorkOrderId);
        Assert.Equal(next.UserId, OpenCase("HOST-A")!.HandlerId);
        Assert.Equal(orderId.ToString(), SingleAudit(AuditActions.WorkOrderReassign).TargetId);
    }

    [Fact]
    public void 拆單_選中案件移到新處理人的單()
    {
        var (orderId, handler) = CreateOneOrder("HOST-A", "HOST-B");
        var next = AddUser("DOMAIN\\n", "新處理人");
        var caseB = OpenCase("HOST-B")!;

        var result = _service.Split(orderId, new SplitWorkOrderRequest { CaseIds = new List<string> { caseB.CaseId }, HandlerId = next.UserId });

        Assert.Equal(1, result.MovedCases);
        Assert.Equal(handler.UserId, result.PreviousHandlerId);
        Assert.NotEqual(orderId, result.TargetWorkOrderId);
        Assert.Equal(result.TargetWorkOrderId, OpenCase("HOST-B")!.WorkOrderId);
        Assert.Equal(orderId, OpenCase("HOST-A")!.WorkOrderId);
        Assert.Equal(orderId.ToString(), SingleAudit(AuditActions.WorkOrderSplit).TargetId);
    }

    [Fact]
    public void 取消_成員結案且單關閉()
    {
        var (orderId, _) = CreateOneOrder("HOST-A", "HOST-B");

        var result = _service.Cancel(orderId, new CancelWorkOrderRequest { Reason = "誤派" });

        Assert.Equal(2, result.ClosedCases);
        Assert.Equal(WorkOrderCloseReasons.Cancelled, _orderStore.Get(orderId)!.ClosedReason);
        Assert.Null(OpenCase("HOST-A"));
        Assert.Equal(orderId.ToString(), SingleAudit(AuditActions.WorkOrderCancel).TargetId);
    }

    [Fact]
    public void 代為結案_成員標成結案狀態()
    {
        var (orderId, _) = CreateOneOrder("HOST-A");

        var result = _service.AdminClose(orderId, new AdminCloseWorkOrderRequest { Status = IssueHandlingStatuses.Resolved, Reason = "已換硬碟" });

        Assert.Equal(1, result.ClosedCases);
        Assert.Equal(WorkOrderCloseReasons.AdminClosed, _orderStore.Get(orderId)!.ClosedReason);
        Assert.All(MembersOf(orderId), c => Assert.Equal(IssueHandlingStatuses.Resolved, c.Status));
        Assert.Equal(orderId.ToString(), SingleAudit(AuditActions.WorkOrderAdminClose).TargetId);
    }

    // ── 權限標註 ─────────────────────────────────────────────────────────────

    private static List<Capability[]> PermissionsOf(Type controller) =>
        controller.GetCustomAttributes<PermissionAttribute>(inherit: false)
            .Select(a => (Capability[])a.Arguments![0]!)
            .ToList();

    [Fact]
    public void 權限_代為結案要Assign且Handle_其餘命令要Assign()
    {
        var adminClose = PermissionsOf(typeof(WorkOrderAdminCloseController));
        Assert.Equal(2, adminClose.Count);
        Assert.Contains(adminClose, caps => caps.SequenceEqual(new[] { Capability.Assign }));
        Assert.Contains(adminClose, caps => caps.SequenceEqual(new[] { Capability.Handle }));

        var commands = Assert.Single(PermissionsOf(typeof(WorkOrdersController)));
        Assert.Equal(new[] { Capability.Assign }, commands);
    }

    /// <summary>可隱藏特定主機的可見範圍替身：目前使用者（Hidden）與指定處理人（HiddenFor）分開設定</summary>
    private sealed class ScopedVisibility : IVisibilityService
    {
        private readonly FakeHostStore _hosts;

        public ScopedVisibility(FakeHostStore hosts) => _hosts = hosts;

        public HashSet<long> Hidden { get; } = new();

        public Dictionary<long, HashSet<long>> HiddenFor { get; } = new();

        public IReadOnlySet<long> GetVisibleHostIds() =>
            _hosts.GetAll().Select(h => h.HostId).Where(id => !Hidden.Contains(id)).ToHashSet();

        public IReadOnlySet<long> GetVisibleHostIdsFor(long userId) =>
            _hosts.GetAll().Select(h => h.HostId)
                .Where(id => !(HiddenFor.TryGetValue(userId, out var hidden) && hidden.Contains(id)))
                .ToHashSet();

        public IReadOnlySet<long> GetOwnedHostIdsFor(long userId) => new HashSet<long>();
        public IReadOnlySet<long> GetGroupVisibleHostIdsFor(long userId) => GetVisibleHostIdsFor(userId);
        public List<WebHost> GetVisibleHosts() => _hosts.GetAll().Where(h => !Hidden.Contains(h.HostId)).ToList();
        public void EnsureVisible(long hostId) { }
        public IReadOnlyDictionary<string, IReadOnlySet<string>> GetCaseGrants() => new Dictionary<string, IReadOnlySet<string>>();
        public bool IsCaseGrantOnly(long hostId) => false;
        public IReadOnlySet<string>? GetIssueKeyRestriction(long hostId) => null;
    }
}
