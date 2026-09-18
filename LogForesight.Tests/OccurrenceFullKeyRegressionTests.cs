using System.Diagnostics;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 出現點查詢（<see cref="EfIssueAggregateQuery.LatestOccurrences"/>）回傳完整簽章鍵（含 EventKey 第五段）的
/// 端到端迴歸。組裝比照 WorkOrderCommandServiceTests／WorkOrderBoardServiceTests：真正的
/// <see cref="EfAnalysisRecordStore"/>＋<see cref="EfIssueAggregateQuery"/>＋<see cref="RecordListQueryService"/>，
/// 處理狀態、案件、交辦單 store 用記憶體替身，協調器用真的。
///
/// 修正前出現點的鍵是四段，PRTG／Linux 命中規則的問題與處理列、案件（五段鍵）永遠比對不到：
/// 已處理的問題被算成未處理、交辦單建出四段鍵的案件、同主機多顆 sensor 被併成一個成員。
/// </summary>
public class OccurrenceFullKeyRegressionTests : IDisposable
{
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    private readonly EfSqliteFixture _fixture = new();
    private readonly EfAnalysisRecordStore _recordStore;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeHandlingStore _handlingStore = new();
    private readonly FakeIssueHandlingStore _issueHandlingStore = new();
    private readonly FakeIssueCaseStore _caseStore = new();
    private readonly FakeSystemSettingsStore _settingsStore = new();
    private readonly FakeNoiseMarkStore _noiseMarks = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();
    private readonly FakeWorkOrderStore _orderStore;
    private readonly FakeRuleStore _ruleStore = new();
    private readonly FakeSuppressionStore _suppressionStore = new();
    private readonly RecordingAuditService _audit = new();
    private readonly CandidateSource _candidates = new();
    private readonly RecordListQueryService _query;
    private readonly EfIssueAggregateQuery _aggregates;
    private readonly IssueCaseCoordinator _caseCoordinator;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly UserDisplayNameService _displayNames;
    private readonly UserGroup _handlerRole;
    private readonly FakeSmtpMailSender _mailSender = new();
    private readonly MailNotificationService _mail;

    public OccurrenceFullKeyRegressionTests()
    {
        _recordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        _orderStore = new FakeWorkOrderStore(_caseStore);
        var visibility = new AllVisible(_hosts);
        var severity = new FakeSystemSettingsService();
        var repository = new RecordRepository(_recordStore, _hosts, visibility, severity);
        _aggregates = new EfIssueAggregateQuery(_fixture.NewContext, _hosts);
        var statusResolver = new OccurrenceStatusResolver(_hosts, _issueHandlingStore, _caseStore, _settingsStore);
        _displayNames = new UserDisplayNameService(_settingsStore);

        _query = new RecordListQueryService(
            repository, _hosts, _users, _handlingStore, _issueHandlingStore, _caseStore, _settingsStore, severity,
            visibility, _aggregates, statusResolver, _displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        _caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orderStore, _caseStore, _issueHandlingStore, _caseCoordinator, _handlingStore, _hosts);

        _handlerRole = _userGroups.Upsert(new UserGroup { GroupName = "處理人", Role = UserRole.User, Active = true });
        _settingsStore.Update(s => s.AutoDispatchEnabled = false);

        _mail = new MailNotificationService(
            _settingsStore, _mailSender, _hosts, _users, _userGroups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlingStore, new MailNotifyStateStore(_fixture.Blob("mail_notify_state")), _issueOwners);
    }

    public void Dispose() => _fixture.Dispose();

    private WorkOrderCommandService CommandService() => new(
        _query, _aggregates, _coordinator, _orderStore, _caseStore, _noiseMarks, _hosts, _users, new AllVisible(_hosts),
        new UserCapabilityResolver(_userGroups, _hosts), FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
        _audit, _displayNames, _ruleStore, _mail);

    private WorkOrderBoardService BoardService() => new(
        _orderStore, _caseStore, _users, _userGroups, _hosts, _hostGroups, _ruleStore, _suppressionStore, _query, _aggregates,
        _candidates, _issueOwners, _noiseMarks, _settingsStore, _coordinator,
        FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, _displayNames, WorkOrderBoardService.DefaultMaxOccurrences);

    // ── 測試資料 ─────────────────────────────────────────────────────────────

    private static LogIssueSignature PrtgDown(string sensorKey) => new()
    {
        LogName = "PRTG", Source = "PRTG:down", EventId = 0, EntryType = EventLogEntryType.Error,
        EventKey = sensorKey, Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Hardware
    };

    private static LogIssueSignature Sshd() => new()
    {
        LogName = "Linux", Source = "sshd", EventId = 0, EntryType = EventLogEntryType.Warning,
        EventKey = "builtin-linux-ssh-bruteforce", Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Security
    };

    private WebHost AddHost(string name) => _hosts.Upsert(new WebHost { HostName = name });

    private void AddDay(WebHost host, DateTime date, params LogIssueSignature[] issues) =>
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = date, RiskLevel = "高",
            Headline = host.HostName, TopIssues = issues.ToList()
        });

    private WebUser AddHandler(string account) => _users.Upsert(new WebUser
    {
        Account = account, DisplayName = account.ToUpperInvariant(), Active = true,
        GroupIds = new List<long> { _handlerRole.GroupId }
    });

    private void MarkResolved(WebHost host, DateTime date, LogIssueSignature issue) =>
        _issueHandlingStore.Save(new IssueHandling
        {
            HostName = host.HostName, Date = date, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.Resolved, UpdatedAt = DateTime.Now
        });

    private IssueGroupDto IssueRow(string source) =>
        Assert.Single(_query.SearchByIssue(new RecordSearchRequest()).Items, i => i.Source == source && i.EventId == 0);

    private static CreateWorkOrderRequest PrtgOrder(long handlerId) => new()
    {
        Source = "PRTG:down", EventId = 0, AssignMode = "single", HandlerId = handlerId, ScopeKind = WorkOrderScopes.Hosts
    };

    // ── 既有缺陷：依問題視角處理概況 ─────────────────────────────────────────

    /// <summary>
    /// 同一台主機兩顆 sensor：較晚出現的已處理、較早出現的未處理——這台主機還沒處理完，
    /// 依問題視角必須算未處理（取「最近出現那筆」會把未處理的那顆蓋掉，算成已處理）。
    /// </summary>
    [Fact]
    public void 依問題視角_同主機兩顆sensor一顆未處理_整台計為未處理()
    {
        var host = AddHost("HOST-A");
        var older = PrtgDown("prtg:down:1001");
        var newer = PrtgDown("prtg:down:1002");
        AddDay(host, Yesterday.AddDays(-1), older);
        AddDay(host, Yesterday, newer);
        MarkResolved(host, Yesterday, newer);

        var row = IssueRow("PRTG:down");

        Assert.Equal(1, row.UnhandledCount);
        Assert.Equal(0, row.ResolvedCount);
    }

    /// <summary>修正前出現點鍵是四段、處理列是五段，已處理的 PRTG 問題被算成未處理</summary>
    [Fact]
    public void 依問題視角_PRTG問題已標resolved_計為已處理()
    {
        var host = AddHost("HOST-A");
        var sensor = PrtgDown("prtg:down:1001");
        AddDay(host, Yesterday, sensor);
        MarkResolved(host, Yesterday, sensor);

        var row = IssueRow("PRTG:down");

        Assert.Equal(1, row.ResolvedCount);
        Assert.Equal(0, row.UnhandledCount);
    }

    [Fact]
    public void 依問題視角_Linux命中規則的問題已標resolved_計為已處理()
    {
        var host = AddHost("HOST-L");
        AddDay(host, Yesterday, Sshd());
        MarkResolved(host, Yesterday, Sshd());

        var row = IssueRow("sshd");

        Assert.Equal(1, row.ResolvedCount);
        Assert.Equal(0, row.UnhandledCount);
    }

    // ── 交辦單端到端 ─────────────────────────────────────────────────────────

    private (WebHost Host, LogIssueSignature S1, LogIssueSignature S2, WebUser Handler) SeedPrtgThreeDays()
    {
        var host = AddHost("HOST-P");
        var s1 = PrtgDown("prtg:down:1001");
        var s2 = PrtgDown("prtg:down:1002");
        for (var i = 2; i >= 0; i--) AddDay(host, Yesterday.AddDays(-i), s1, s2);
        return (host, s1, s2, AddHandler("h"));
    }

    [Fact]
    public void 建單_PRTG一台兩顆sensor三天_兩件案件鍵為完整鍵且逐日列寫到三天()
    {
        var (host, s1, s2, handler) = SeedPrtgThreeDays();

        var result = CommandService().Create(PrtgOrder(handler.UserId));

        var order = Assert.Single(result.Orders);
        var members = _caseStore.GetByWorkOrder(order.WorkOrderId, 0, int.MaxValue);
        var expected = new[] { IssueSignatureKey.For(s1), IssueSignatureKey.For(s2) }.OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(expected, members.Select(c => c.IssueKey).OrderBy(k => k, StringComparer.Ordinal));

        var days = _issueHandlingStore.GetMany(new[] { host.HostName }, Yesterday.AddDays(-2), Yesterday);
        foreach (var key in expected)
        {
            Assert.Equal(
                new[] { Yesterday.AddDays(-2), Yesterday.AddDays(-1), Yesterday },
                days.Where(d => d.IssueKey == key).Select(d => d.Date.Date).OrderBy(d => d));
        }
    }

    [Fact]
    public void 夜間掛接_已建單的PRTG問題掛入既有案件_不交給派工()
    {
        var (host, s1, s2, handler) = SeedPrtgThreeDays();
        CommandService().Create(PrtgOrder(handler.UserId));

        var attach = _caseCoordinator.AttachNewDay(host.HostName, DateTime.Today, new[] { s1, s2 }, DateTime.Now);

        Assert.Empty(attach.Unassigned);
        var today = _issueHandlingStore.GetForDay(host.HostName, DateTime.Today);
        Assert.Equal(2, today.Count(h => h.CaseId != null));
    }

    [Fact]
    public void 待派_已建單的PRTG問題不再出現在缺口()
    {
        var (_, _, _, handler) = SeedPrtgThreeDays();
        CommandService().Create(PrtgOrder(handler.UserId));

        var dto = BoardService().GetGaps(null, null, 1);

        Assert.DoesNotContain(dto.Rows, r => r.Source == "PRTG:down");
    }

    /// <summary>修正前出現點鍵是四段，五段鍵的主機層 Signature 抑制在試跑對不上</summary>
    [Fact]
    public void 待派_主機層Signature抑制某顆sensor_只排除該成員_同主機另一顆不受影響()
    {
        var pool = _userGroups.Upsert(new UserGroup { GroupName = "派工池", Role = UserRole.User, Active = true, DispatchPool = true });
        var h = _users.Upsert(new WebUser { Account = "h", DisplayName = "H", Active = true, GroupIds = new List<long> { pool.GroupId } });
        var host = AddHost("HOST-S");
        var s1 = PrtgDown("prtg:down:2001");
        var s2 = PrtgDown("prtg:down:2002");
        AddDay(host, Yesterday, s1, s2);
        _candidates.Candidates.Add(new DispatchCandidate
        {
            UserId = h.UserId, Account = h.Account, InPool = true, Paused = false,
            VisibleHostIds = new HashSet<long> { host.HostId }
        });
        _suppressionStore.SaveAll(new List<RuleSuppression>
        {
            new()
            {
                TargetType = SuppressionTargetTypes.Signature, SignatureKey = IssueSignatureKey.For(s1),
                Scope = SuppressionScopes.Host, Host = host.HostName, Reason = "測試"
            }
        });

        var row = Assert.Single(BoardService().GetGaps(null, null, 1).Rows, r => r.Source == "PRTG:down");

        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipSuppressed]);
        var suggestion = Assert.Single(row.Suggested);
        Assert.Equal(h.UserId, suggestion.HandlerId);

        var dispatched = BoardService().RunAutoDispatch(null, null);
        Assert.Equal(1, dispatched.AssignedMembers);
        Assert.Null(_caseStore.GetOpen(host.HostName, IssueSignatureKey.For(s1)));
        Assert.NotNull(_caseStore.GetOpen(host.HostName, IssueSignatureKey.For(s2)));
    }

    // ── 替身 ─────────────────────────────────────────────────────────────────

    private sealed class CandidateSource : IDispatchCandidateSource
    {
        public List<DispatchCandidate> Candidates { get; } = new();

        public DispatchCandidatePool Build() => new()
        {
            ByUserId = Candidates.ToDictionary(c => c.UserId),
            PoolMemberCount = Candidates.Count(c => c.InPool),
            ActivePoolMemberCount = Candidates.Count(c => c.InPool && !c.Paused)
        };
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
}
