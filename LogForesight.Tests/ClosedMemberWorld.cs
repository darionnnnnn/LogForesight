using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 A-1「建案前排除已結案與不再打擾的成員」測試共用的組裝（組合進各測試類別，不用繼承：fake 是 internal）：
/// 真正的 <see cref="EfAnalysisRecordStore"/>＋<see cref="EfIssueAggregateQuery"/>＋<see cref="RecordListQueryService"/>，
/// 交辦單與案件 store 用記憶體替身，協調器用真的——候選日走 lf_top_issues，與正式路徑同一條。
/// </summary>
internal sealed class ClosedMemberWorld : IDisposable
{
    public static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);

    private readonly EfSqliteFixture _fixture = new();
    public readonly EfAnalysisRecordStore RecordStore;
    public readonly FakeHostStore Hosts = new();
    public readonly FakeUserStore Users = new();
    public readonly FakeUserGroupStore UserGroups = new();
    public readonly FakeHandlingStore HandlingStore = new();
    public readonly FakeIssueHandlingStore IssueHandlingStore = new();
    public readonly FakeIssueCaseStore CaseStore = new();
    public readonly FakeSystemSettingsStore SettingsStore = new();
    public readonly FakeNoiseMarkStore NoiseMarks = new();
    public readonly FakeWorkOrderStore OrderStore;
    public readonly RecordingAuditService Audit = new();
    public readonly WorkOrderCoordinator Coordinator;
    public readonly RecordListQueryService Query;
    public readonly WorkOrderCommandService Service;
    private readonly UserGroup _handlerRole;

    public ClosedMemberWorld()
    {
        RecordStore = new EfAnalysisRecordStore(_fixture.NewContext, "test");
        OrderStore = new FakeWorkOrderStore(CaseStore);
        var visibility = new AllVisible(Hosts);
        var severity = new FakeSystemSettingsService();
        var repository = new RecordRepository(RecordStore, Hosts, visibility, severity);
        var aggregates = new EfIssueAggregateQuery(_fixture.NewContext, Hosts);
        var statusResolver = new OccurrenceStatusResolver(Hosts, IssueHandlingStore, CaseStore, SettingsStore);
        var displayNames = new UserDisplayNameService(SettingsStore);
        var query = Query = new RecordListQueryService(
            repository, Hosts, Users, HandlingStore, IssueHandlingStore, CaseStore, SettingsStore, severity,
            visibility, aggregates, statusResolver, displayNames, new NextUnhandledSequenceCache(new DataVersionStamp()),
            new FixedIssueExclusionSource(IssueExclusion.None));

        var caseCoordinator = new IssueCaseCoordinator(CaseStore, IssueHandlingStore, HandlingStore, RecordStore, Hosts, new FakeIssueOwnerStore());
        Coordinator = new WorkOrderCoordinator(OrderStore, CaseStore, IssueHandlingStore, caseCoordinator, HandlingStore, Hosts);

        _handlerRole = UserGroups.Upsert(new UserGroup { GroupName = "處理人", Role = UserRole.User, Active = true });

        var mail = new MailNotificationService(
            SettingsStore, new FakeSmtpMailSender(), Hosts, Users, UserGroups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), HandlingStore, new MailNotifyStateStore(_fixture.Blob("mail_notify_state")),
            new ScheduleFreshnessService(new BatchRunStore(_fixture.LogStore("batch_runs"), _fixture.LogStore("batch_run_logs")), new ScheduleOptionsStore(_fixture.Blob("schedule_options"))),
            new WorkOrderOutboxStore(_fixture.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore(),
            new FakeIssueOwnerStore());

        Service = new WorkOrderCommandService(
            query, aggregates, Coordinator, OrderStore, CaseStore, NoiseMarks, Hosts, Users, visibility,
            new UserCapabilityResolver(UserGroups, Hosts), FakeCurrentUser.WithCapabilities(Capability.Assign, Capability.Handle),
            Audit, displayNames, mail);
    }

    public void Dispose() => _fixture.Dispose();

    public static LogIssueSignature Disk(IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error,
        Severity = severity, Count = 1, Category = IssueCategory.Other
    };

    public static string KeyOf(LogIssueSignature issue) => IssueSignatureKey.For(issue);

    public WebHost AddHost(string name, LogIssueSignature? issue = null)
    {
        var host = TestData.AddHost(Hosts, name);
        RecordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = "高",
            Headline = name, TopIssues = new List<LogIssueSignature> { issue ?? Disk() }
        });
        return host;
    }

    public WebUser AddHandler(string account = "DOMAIN\\h") =>
        Users.Upsert(new WebUser
        {
            Account = account, DisplayName = "處理人", Active = true, GroupIds = new List<long> { _handlerRole.GroupId }
        });

    /// <summary>最近出現日（昨天）已被處理人回覆為結案類</summary>
    public void CloseDay(string hostName, string status = IssueHandlingStatuses.Resolved, string actor = "DOMAIN\\closer") =>
        IssueHandlingStore.Save(new IssueHandling
        {
            HostName = hostName, Date = Yesterday, IssueKey = KeyOf(Disk()), Status = status,
            ActorAccount = actor, UpdatedAt = DateTime.Now.AddHours(-2)
        });

    /// <summary>處理人判定不處理：案件已結案（不處理類）、逐日列也是結案類——與正式資料同形</summary>
    public IssueCase Dismiss(string hostName)
    {
        CloseDay(hostName, IssueHandlingStatuses.WontFix);
        var blocked = new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("n"), HostName = hostName, IssueKey = KeyOf(Disk()),
            Status = IssueHandlingStatuses.WontFix, HandlerId = 77, CreatedAt = DateTime.Today.AddDays(-9),
            ClosedAt = DateTime.Today.AddDays(-8), FirstLinkedDate = Yesterday, LastLinkedDate = Yesterday
        };
        CaseStore.Save(blocked);
        return blocked;
    }

    public static CreateWorkOrderRequest Single(long handlerId) => new()
    {
        Source = "disk", EventId = 153, AssignMode = "single", HandlerId = handlerId, ScopeKind = WorkOrderScopes.All
    };

    public IssueCase? OpenCase(string hostName) => CaseStore.GetOpen(hostName, KeyOf(Disk()));

    public int TotalCases() => CaseStore.GetMany(Hosts.GetAll().Select(h => h.HostName)).Count;

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
