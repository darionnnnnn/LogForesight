using LogForesight.Web.Auth;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;

namespace LogForesight.Tests;

/// <summary>
/// 把 <see cref="ScaleDataSet"/> 接上**真正的**產品 Service（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P0）。
///
/// 這裡刻意**不用任何 store 假實作**：壓測要量的正是整份 blob 讀改寫、可見範圍展開、
/// 全量載入聚合這三件事的成本，換成 in-memory 假 store 就把要量的東西全部量掉了。
/// 唯二的替身是 <see cref="ICurrentUser"/>（沒有 HTTP 情境）與
/// <c>ISystemSettingsService</c> 的嚴重度可見性（與規模無關，且真實作需要更多 Web 相依）。
/// </summary>
internal sealed class ScaleServices
{
    public IRecordRepository Repository { get; }
    public IVisibilityService Visibility { get; }
    public DashboardService Dashboard { get; }
    public ReportService Report { get; }
    public RecordListQueryService RecordList { get; }
    public IssueHandlingCommandService IssueCommands { get; }
    public IssueCaseCoordinator CaseCoordinator { get; }
    public IHostStore Hosts { get; }
    public IIssueHandlingStore IssueHandlings { get; }
    public IIssueCaseStore Cases { get; }
    public WorkOrderCoordinator WorkOrders { get; }
    public IWorkOrderStore WorkOrderStore { get; }
    public WorkOrderCommandService WorkOrderCommands { get; }
    public WorkOrderBoardService WorkOrderBoard { get; }

    private readonly MailNotificationService _mail;

    public WorkOrderReplyService ReplyServiceAs(long userId) =>
        new(WorkOrders, WorkOrderStore, Cases, FakeCurrentUser.ForUser(userId, Capability.Handle), new RecordingAuditService(), _mail);

    /// <param name="viewAll">true＝以 ViewAll（admin）身分，全部主機可見；false＝受限部門使用者</param>
    public ScaleServices(ScaleDataSet data, bool viewAll = true)
    {
        var backend = data.Backend;

        Hosts = new HostStore(backend.Blob("hosts"));
        var users = new UserStore(backend.Blob("users"));
        var userGroups = new UserGroupStore(backend.Blob("user_groups"));
        var hostGroups = new HostGroupStore(backend.Blob("host_groups"));
        var access = new GroupAccessStore(backend.Blob("group_access"));
        IssueHandlings = backend.IssueHandlingStore();
        Cases = backend.IssueCaseStore();
        var recordHandling = backend.RecordHandlingStore();
        var noiseMarks = new NoiseMarkStore(backend.Blob("noise_marks"));
        var settingsStore = new SystemSettingsStore(backend.Blob("system_settings"));
        var audit = new AuditLogStore(backend.LogStore("audit"));
        var recordStore = backend.RecordStore();

        var currentUser = viewAll
            ? FakeCurrentUser.ForUser(1, Capability.ViewAll, Capability.Assign, Capability.Handle, Capability.ViewAudit)
            : FakeCurrentUser.ForUser(2, Capability.Handle);

        Visibility = new VisibilityService(currentUser, users, userGroups, access, Hosts, Cases, settingsStore);
        var settingsService = new FakeSystemSettingsService();
        Repository = new RecordRepository(recordStore, Hosts, Visibility, settingsService);

        var progress = new HandlingProgressCalculator(IssueHandlings, recordHandling, Cases, settingsStore, new FixedIssueExclusionSource(IssueExclusion.None));
        var displayNames = new UserDisplayNameService(settingsStore);
        var handlingHistory = new HandlingHistoryQueryService(
            recordHandling, IssueHandlings, Cases, Hosts, users, Visibility, settingsStore, Repository, progress, new FakeIssueAggregateQuery(), displayNames, new FixedIssueExclusionSource(IssueExclusion.None));

        var permissionChanges = new PermissionChangeService(
            backend.PermissionChanges(),
            Hosts, Visibility, currentUser, new RecordingAuditService(), users, new NullReportReader(), new FakeSystemSettingsStore());

        var aggregates = backend.IssueAggregateQuery(Hosts);
        var issueRanking = new IssueRankingBuilder(aggregates, Hosts, new FixedIssueExclusionSource(IssueExclusion.None));
        var statusResolver = new OccurrenceStatusResolver(Hosts, IssueHandlings, Cases, settingsStore);
        var issueTodo = new IssueTodoQuery(aggregates, statusResolver, new FixedIssueExclusionSource(IssueExclusion.None));

        Dashboard = new DashboardService(
            Visibility, audit, currentUser, handlingHistory, permissionChanges, hostGroups, issueRanking,
            settingsStore, aggregates, issueTodo, settingsService, new SummaryCache(new DataVersionStamp()),
            backend.PrtgStore(), new FixedIssueExclusionSource(IssueExclusion.None));

        Report = new ReportService(Repository, Hosts, Visibility, handlingHistory, issueRanking, settingsStore, aggregates, settingsService, new SummaryCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        WorkOrderStore = backend.WorkOrderStore();
        RecordList = new RecordListQueryService(
            Repository, Hosts, users, recordHandling, IssueHandlings, Cases, settingsStore,
            settingsService, Visibility, aggregates, statusResolver, displayNames,
            new NextUnhandledSequenceCache(new DataVersionStamp()), new FixedIssueExclusionSource(IssueExclusion.None));

        var issueOwners = new IssueOwnerStore(backend.Blob("issue_owners"));
        CaseCoordinator = new IssueCaseCoordinator(Cases, IssueHandlings, recordHandling, recordStore, Hosts, issueOwners);

        WorkOrders = new WorkOrderCoordinator(WorkOrderStore, Cases, IssueHandlings, CaseCoordinator, recordHandling, Hosts);
        var issueOwnerAdmin = new IssueOwnerAdminService(issueOwners, aggregates, users, new RecordingAuditService(), currentUser, displayNames,
            WorkOrderStore, WorkOrders);

        var capabilities = new UserCapabilityResolver(userGroups, Hosts, issueOwners);
        var auditService = new RecordingAuditService();

        IssueCommands = new IssueHandlingCommandService(
            recordHandling, IssueHandlings, Cases, CaseCoordinator,
            WorkOrders,
            noiseMarks, Repository,
            Hosts, users, Visibility, currentUser, auditService, progress,
            capabilities, issueOwnerAdmin, displayNames);

        var rules = new KnownIssueRuleStore(backend.Blob("rules"));
        var suppressions = new SuppressionStore(backend.Blob("suppressions"));
        var mailSender = new FakeSmtpMailSender();
        var mailState = new MailNotifyStateStore(backend.Blob("mail_notify_state"));
        _mail = new MailNotificationService(
            settingsStore,
            mailSender,
            Hosts,
            users,
            userGroups,
            access,
            recordStore,
            recordHandling,
            mailState,
            issueOwners,
            aggregates);

        WorkOrderCommands = new WorkOrderCommandService(
            RecordList,
            aggregates,
            WorkOrders,
            WorkOrderStore,
            Cases,
            noiseMarks,
            Hosts,
            users,
            Visibility,
            capabilities,
            currentUser,
            auditService,
            displayNames,
            rules,
            _mail);

        var candidates = new DispatchCandidateSource(
            users, userGroups, Hosts, access, issueOwners, aggregates, settingsStore);

        WorkOrderBoard = new WorkOrderBoardService(
            WorkOrderStore,
            Cases,
            users,
            userGroups,
            Hosts,
            hostGroups,
            rules,
            suppressions,
            RecordList,
            aggregates,
            candidates,
            issueOwners,
            noiseMarks,
            settingsStore,
            WorkOrders,
            currentUser,
            auditService,
            displayNames);
    }
}
