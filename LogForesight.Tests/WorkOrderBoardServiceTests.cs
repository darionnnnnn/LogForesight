using System.Diagnostics;
using System.Reflection;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Repositories;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 負載看板／待派清單（派工試跑）／立即派工（<see cref="WorkOrderBoardService"/>）。組裝比照
/// WorkOrderCommandServiceTests：真正的 <see cref="EfAnalysisRecordStore"/>＋<see cref="EfIssueAggregateQuery"/>＋
/// <see cref="RecordListQueryService"/>，交辦單與案件 store 用記憶體替身、協調器用真的。
/// 候選人池以手組快照替身帶入（Web 端規則另由 DispatchCandidateSourceTests 驗）。
/// </summary>
public class WorkOrderBoardServiceTests : IDisposable
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
    private readonly FakeCandidateSource _candidates = new();
    private readonly FakeRuleStore _ruleStore = new();
    private readonly FakeSuppressionStore _suppressionStore = new();
    private readonly RecordingAuditService _audit = new();
    private readonly RecordListQueryService _query;
    private readonly EfIssueAggregateQuery _aggregates;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly UserDisplayNameService _displayNames;
    private readonly MailNotificationService _mail;
    private readonly FakeSmtpMailSender _sender = new();

    public WorkOrderBoardServiceTests()
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

        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orderStore, _caseStore, _issueHandlingStore, caseCoordinator, _handlingStore, _hosts);

        _mail = new MailNotificationService(
            _settingsStore, _sender, _hosts, _users, _userGroups, new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlingStore, new MailNotifyStateStore(_fixture.Blob("mail_notify_state")),
            new ScheduleFreshnessService(new BatchRunStore(_fixture.LogStore("batch_runs"), _fixture.LogStore("batch_run_logs")), new ScheduleOptionsStore(_fixture.Blob("schedule_options"))),
            new FakeIssueOwnerStore());

        _settingsStore.Update(s => s.AutoDispatchEnabled = false);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void 一次派工三張單給同一人只寄一封通知且列出每張單()
    {
        var third = Disk();
        third.Source = "third";
        var host = AddHost("MAIL-HOST", null, Disk(), Ntfs(), third);
        var (handler, _) = PoolSeeing(host);
        handler.Email = "handler@example.test";
        _users.Upsert(handler);
        _settingsStore.Update(s =>
        {
            s.MailEnabled = true;
            s.MailNotifyWorkOrders = true;
            s.SmtpServer = "smtp.example.test";
            s.MailFrom = "system@example.test";
        });
        var result = Service().RunAutoDispatch(null, null);
        Assert.Equal(3, result.CreatedOrders);
        var mail = Assert.Single(_sender.Sent).Message;
        Assert.Contains("handler@example.test", mail.To);
        foreach (var order in _orderStore.All)
            Assert.Contains($"單號 {order.WorkOrderId}：", mail.Body);
    }

    private WorkOrderBoardService Service(int maxOccurrences = WorkOrderBoardService.DefaultMaxOccurrences) => new(
        _orderStore, _caseStore, _users, _userGroups, _hosts, _hostGroups, _ruleStore, _suppressionStore, _query, _aggregates,
        _candidates, _issueOwners, _noiseMarks, _settingsStore, _coordinator,
        FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, _displayNames, _mail, maxOccurrences);

    // ── 測試資料 ─────────────────────────────────────────────────────────────

    private static LogIssueSignature Disk(IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = "disk", EventId = 153, EntryType = EventLogEntryType.Error,
        Severity = severity, Count = 1, Category = IssueCategory.Other
    };

    private static LogIssueSignature Ntfs(IssueSeverity severity = IssueSeverity.High) => new()
    {
        LogName = "System", Source = "ntfs", EventId = 55, EntryType = EventLogEntryType.Error,
        Severity = severity, Count = 1, Category = IssueCategory.Other
    };

    private static string KeyOf(LogIssueSignature s) => IssueSignatureKey.For(s);

    private WebHost AddHost(string name, long[]? hostGroupIds = null, params LogIssueSignature[] issues)
    {
        var host = _hosts.Upsert(new WebHost { HostName = name, GroupIds = (hostGroupIds ?? Array.Empty<long>()).ToList() });
        _recordStore.Append(new DailyAnalysisRecord
        {
            HostId = host.HostId, Host = host.HostName, Date = Yesterday, RiskLevel = "高",
            Headline = name, TopIssues = issues.Length == 0 ? new List<LogIssueSignature> { Disk() } : issues.ToList()
        });
        return host;
    }

    private WebUser AddUser(string account, bool active = true, bool paused = false, params long[] groupIds) =>
        _users.Upsert(new WebUser
        {
            Account = account, DisplayName = account.ToUpperInvariant(), Active = active, DispatchPaused = paused,
            GroupIds = groupIds.ToList()
        });

    private UserGroup AddPoolGroup(string name = "派工池") =>
        _userGroups.Upsert(new UserGroup { GroupName = name, Role = UserRole.User, Active = true, DispatchPool = true });

    /// <summary>候選人（啟用中具處理能力者）；visibleHostIds 只對池內未暫停者有意義</summary>
    private void Candidate(WebUser user, bool inPool, params long[] visibleHostIds) =>
        _candidates.Candidates.Add(new DispatchCandidate
        {
            UserId = user.UserId, Account = user.Account, InPool = inPool, Paused = user.DispatchPaused,
            VisibleHostIds = visibleHostIds.ToHashSet()
        });

    /// <summary>給處理人一張別的問題的進行中單，底下 n 件進行中案件</summary>
    private long GiveLoad(WebUser user, int n, string source = "other", DateTime? due = null)
    {
        var order = new WorkOrder
        {
            SourceName = source, EventId = 1, IssueLabel = $"{source} 1", HandlerId = user.UserId,
            Origin = WorkOrderOrigins.Manual, CreatedAt = DateTime.Today.AddDays(-2), CreatedByAccount = "x"
        };
        _orderStore.Insert(order);
        for (var i = 0; i < n; i++)
        {
            _caseStore.Save(new IssueCase
            {
                CaseId = Guid.NewGuid().ToString("n"), HostName = $"LOAD-{user.UserId}-{source}-{i}",
                IssueKey = $"System|{source}|1|1", Status = IssueHandlingStatuses.InProgress, HandlerId = user.UserId,
                WorkOrderId = order.WorkOrderId, DueDate = due
            });
        }
        return order.WorkOrderId;
    }

    private void ClosedOrder(WebUser user, DateTime closedAt, string source) =>
        _orderStore.Insert(new WorkOrder
        {
            SourceName = source, EventId = 9, IssueLabel = $"{source} 9", HandlerId = user.UserId,
            Origin = WorkOrderOrigins.Manual, CreatedAt = closedAt.AddDays(-1), CreatedByAccount = "x", ClosedAt = closedAt,
            ClosedReason = WorkOrderCloseReasons.AllClosed
        });

    private void OpenCase(string hostName, LogIssueSignature issue, long handlerId) =>
        _caseStore.Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("n"), HostName = hostName, IssueKey = KeyOf(issue),
            Status = IssueHandlingStatuses.InProgress, HandlerId = handlerId, CreatedAt = DateTime.Today.AddDays(-3)
        });

    private void DismissedCase(string hostName, LogIssueSignature issue) =>
        _caseStore.Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("n"), HostName = hostName, IssueKey = KeyOf(issue),
            Status = IssueHandlingStatuses.WontFix, HandlerId = 77, CreatedAt = DateTime.Today.AddDays(-9),
            ClosedAt = DateTime.Today.AddDays(-8)
        });

    private static GapRowDto RowOf(GapsDto dto, string source) => Assert.Single(dto.Rows, r => r.Source == source);

    // ── 負載看板 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 看板_兩位有單處理人加一位池成員無單_三列_欄位與排序()
    {
        var pool = AddPoolGroup();
        var other = _userGroups.Upsert(new UserGroup { GroupName = "其他", Role = UserRole.User, Active = true });
        var alice = AddUser("alice", groupIds: other.GroupId);
        var bob = AddUser("bob", paused: true, groupIds: pool.GroupId);
        var carol = AddUser("carol", groupIds: pool.GroupId);
        AddUser("dave", groupIds: other.GroupId);                       // 不在池、無單→不列
        AddUser("erin", active: false, groupIds: pool.GroupId);          // 停用的池成員→不列

        GiveLoad(alice, 3, "a1", due: DateTime.Today.AddDays(-2));        // 3 件逾期
        GiveLoad(alice, 1, "a2");
        GiveLoad(bob, 1, "b1");
        ClosedOrder(bob, DateTime.Today.AddDays(-3), "b2");
        ClosedOrder(bob, DateTime.Today.AddDays(-8), "b3");               // 8 天前結案不算

        var rows = Service().GetLoadBoard(null).Rows;

        Assert.Equal(new[] { alice.UserId, bob.UserId, carol.UserId }, rows.Select(r => r.UserId));
        var a = rows[0];
        Assert.Equal(4, a.ActiveMembers);
        Assert.Equal(2, a.ActiveWorkOrders);
        Assert.Equal(3, a.OverdueMembers);
        Assert.False(a.InPool);
        Assert.Equal("ALICE(alice)", a.Name);
        var b = rows[1];
        Assert.True(b.Paused);
        Assert.True(b.InPool);
        Assert.Equal(1, b.ClosedLast7Days);
        Assert.Equal(0, b.OverdueMembers);
        var c = rows[2];
        Assert.True(c.InPool);
        Assert.True(c.Active);
        Assert.Equal(0, c.ActiveMembers);
        Assert.Null(c.OldestActiveCreatedAt);

        var filtered = Service().GetLoadBoard(pool.GroupId).Rows;
        Assert.Equal(new[] { bob.UserId, carol.UserId }, filtered.Select(r => r.UserId));
    }

    [Fact]
    public void 看板_同負載依帳號排序_已刪除處理人顯示已刪除()
    {
        var pool = AddPoolGroup();
        AddUser("Zed", groupIds: pool.GroupId);
        AddUser("amy", groupIds: pool.GroupId);
        var ghost = new WebUser { UserId = 4242, Account = "ghost" };
        GiveLoad(ghost, 1, "g1");

        var rows = Service().GetLoadBoard(null).Rows;

        Assert.Equal("（已刪除）", rows[0].Name);
        Assert.Equal(new[] { "AMY(amy)", "ZED(Zed)" }, rows.Skip(1).Select(r => r.Name));
    }

    // ── 待派清單 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 待派_有進行中案件的主機不列_三種閘門排除分開計數()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var open = AddHost("HOST-OPEN");
        var noise = AddHost("HOST-NOISE");
        var dismissed = AddHost("HOST-DISMISSED");
        var free = AddHost("HOST-FREE");
        var low = AddHost("HOST-LOW", null, Disk(IssueSeverity.Low));
        Candidate(h, inPool: true, open.HostId, noise.HostId, dismissed.HostId, free.HostId, low.HostId);

        OpenCase("host-open", Disk(), 99);
        _noiseMarks.Save(new NoiseMark { HostName = "HOST-NOISE", IssueKey = KeyOf(Disk()) });
        DismissedCase("HOST-DISMISSED", Disk());

        var dto = Service().GetGaps(null, null, 1);

        Assert.False(dto.TooLarge);
        Assert.Equal(Yesterday.AddDays(-6), dto.From);
        Assert.Equal(Yesterday, dto.To);
        var row = RowOf(dto, "disk");
        Assert.Equal(4, row.GapHosts);
        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipNoise]);
        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipSeverity]);
        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipDismissed]);
        var suggestion = Assert.Single(row.Suggested);
        Assert.Equal((h.UserId, 1, true), (suggestion.HandlerId, suggestion.Hosts, suggestion.WillCreate));
        Assert.Empty(row.Unassignable);
        Assert.Equal("disk 153", row.IssueLabel);
    }

    /// <summary>
    /// 問題今天剛設靜音（區間＝今天～今天＋6），出現日是昨天、不在區間內：
    /// 與單一列判定同口徑，仍視為靜音中——不建議派給任何人，缺口主機計入靜音排除。
    /// </summary>
    [Fact]
    public void 待派_目前靜音中的問題不列入待派()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var host = AddHost("HOST-A");
        Candidate(h, inPool: true, host.HostId);
        _issueOwners.Upsert(new IssueProfile
        {
            SourceName = "disk", EventId = 153,
            Mutes = new List<MuteInterval> { new() { From = DateTime.Today, To = DateTime.Today.AddDays(6), Reason = "維護中" } }
        });

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        Assert.Empty(row.Suggested);
        Assert.Empty(row.Unassignable);
        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipMuted]);
    }

    [Fact]
    public void 待派_池內兩人負載不同_建議給輕者且同問題多台聚在同一人()
    {
        var pool = AddPoolGroup();
        var heavy = AddUser("heavy", groupIds: pool.GroupId);
        var light = AddUser("light", groupIds: pool.GroupId);
        var hosts = new[] { AddHost("HOST-A"), AddHost("HOST-B"), AddHost("HOST-C") }.Select(x => x.HostId).ToArray();
        Candidate(heavy, inPool: true, hosts);
        Candidate(light, inPool: true, hosts);
        GiveLoad(heavy, 2);
        GiveLoad(light, 1);

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        var suggestion = Assert.Single(row.Suggested);
        Assert.Equal(light.UserId, suggestion.HandlerId);
        Assert.Equal(3, suggestion.Hosts);
        Assert.True(suggestion.WillCreate);
        Assert.Equal("LIGHT(light)", suggestion.Name);
    }

    /// <summary>
    /// 虛擬單生效：HOST-A 只有 bob 看得到，disk 建單決策登記負數單號虛擬單→bob 負載 (1,1)；
    /// ntfs 在 HOST-B 兩人都看得到，amy 原本就是 (1,1)，同分依帳號給 amy。
    /// 沒登記虛擬單時 bob 是 (1,0)、單數較少會被選中。
    /// </summary>
    [Fact]
    public void 待派_建單決策登記虛擬單_單數計入後續選人()
    {
        var pool = AddPoolGroup();
        var amy = AddUser("amy", groupIds: pool.GroupId);
        var bob = AddUser("bob", groupIds: pool.GroupId);
        var a = AddHost("HOST-A");
        var b = AddHost("HOST-B", null, Ntfs());
        Candidate(amy, inPool: true, b.HostId);
        Candidate(bob, inPool: true, a.HostId, b.HostId);
        GiveLoad(amy, 1);

        var dto = Service().GetGaps(null, null, 1);

        Assert.Equal(bob.UserId, Assert.Single(RowOf(dto, "disk").Suggested).HandlerId);
        Assert.Equal(amy.UserId, Assert.Single(RowOf(dto, "ntfs").Suggested).HandlerId);
    }

    [Fact]
    public void 待派_既有進行中單的處理人_WillCreate為false()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var host = AddHost("HOST-A");
        Candidate(h, inPool: true, host.HostId);
        _orderStore.Insert(new WorkOrder
        {
            SourceName = "DISK", EventId = 153, IssueLabel = "DISK 153", HandlerId = h.UserId,
            Origin = WorkOrderOrigins.Manual, CreatedAt = DateTime.Today, CreatedByAccount = "x"
        });

        var suggestion = Assert.Single(RowOf(Service().GetGaps(null, null, 1), "disk").Suggested);

        Assert.False(suggestion.WillCreate);
    }

    [Fact]
    public void 待派_無池群組_no_pool()
    {
        var h = AddUser("h");
        var host = AddHost("HOST-A");
        Candidate(h, inPool: false);

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        Assert.Empty(row.Suggested);
        var u = Assert.Single(row.Unassignable);
        Assert.Equal((WorkOrderDispatcher.NoCandidateNoPool, 1), (u.Reason, u.Hosts));
        Assert.Empty(row.UnseenHostGroups);
        Assert.NotEqual(0, host.HostId);
    }

    [Fact]
    public void 待派_主機群組無人可見_no_visibility並列出群組名()
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var roomA = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        var roomB = _hostGroups.Upsert(new HostGroup { GroupName = "機房B" });
        var seen = AddHost("HOST-SEEN", new[] { roomB.GroupId });
        AddHost("HOST-X", new[] { roomA.GroupId });
        AddHost("HOST-Y", new[] { roomA.GroupId, roomB.GroupId });
        Candidate(h, inPool: true, seen.HostId);

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        var u = Assert.Single(row.Unassignable);
        Assert.Equal((WorkOrderDispatcher.NoCandidateNoVisibility, 2), (u.Reason, u.Hosts));
        Assert.Equal(new[] { "機房A", "機房B" }, row.UnseenHostGroups);
        Assert.Equal(1, Assert.Single(row.Suggested).Hosts);
    }

    [Fact]
    public void 待派_出現點超過上限_TooLarge且不試跑()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");

        var dto = Service(maxOccurrences: 1).GetGaps(null, null, 1);

        Assert.True(dto.TooLarge);
        Assert.Empty(dto.Rows);
        // 上限算的是尚未派出的出現點：必須先查進行中案件排除後才判定（已派出的不佔額度）
        Assert.Equal(1, _caseStore.GetOpenKeysCalls);
        Assert.Throws<DomainException>(() => Service(maxOccurrences: 1).RunAutoDispatch(null, null));
        Assert.Equal(2, Service(maxOccurrences: 2).GetGaps(null, null, 1).Rows.Single().GapHosts);
    }

    [Fact]
    public void 待派_已有進行中案件的出現點不佔上限()
    {
        AddHost("HOST-A");
        AddHost("HOST-B");
        OpenCase("HOST-A", Disk(), 99);

        var dto = Service(maxOccurrences: 1).GetGaps(null, null, 1);

        Assert.False(dto.TooLarge);
        Assert.True(dto.Total > 0);
    }

    [Fact]
    public void 待派_依缺口主機數降冪排序()
    {
        var h = AddUser("h");
        Candidate(h, inPool: false);
        AddHost("HOST-A", null, Ntfs());
        AddHost("HOST-B", null, Disk(), Ntfs());
        AddHost("HOST-C", null, Ntfs());

        var dto = Service().GetGaps(null, null, 1);

        Assert.Equal(new[] { "ntfs", "disk" }, dto.Rows.Select(r => r.Source));
        Assert.Equal(2, dto.Total);
    }

    // ── 抑制 ─────────────────────────────────────────────────────────────────

    private static LogIssueSignature Sshd() => new()
    {
        LogName = "Linux", Source = "sshd", EventId = 0, EntryType = EventLogEntryType.Warning,
        EventKey = "builtin-linux-ssh-bruteforce", Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Security
    };

    private static LogIssueSignature PrtgDown() => new()
    {
        LogName = "PRTG", Source = "PRTG:down", EventId = 0, EntryType = EventLogEntryType.Error,
        EventKey = "prtg:down:1001", Severity = IssueSeverity.High, Count = 1, Category = IssueCategory.Hardware
    };

    private void Suppress(params RuleSuppression[] items) => _suppressionStore.SaveAll(items.ToList());

    private void Rules(params KnownIssueRule[] rules) => _ruleStore.Content = new RuleFileContent { Rules = rules.ToList() };

    private (WebUser H, WebHost[] Hosts) PoolSeeing(params WebHost[] hosts)
    {
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        Candidate(h, inPool: true, hosts.Select(x => x.HostId).ToArray());
        return (h, hosts);
    }

    [Fact]
    public void 抑制_主機層Signature型_只排除該主機()
    {
        PoolSeeing(AddHost("HOST-A"), AddHost("HOST-B"));
        Suppress(new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.Signature, SignatureKey = KeyOf(Disk()).ToUpperInvariant(),
            Scope = SuppressionScopes.Host, Host = "host-a", Reason = "測試"
        });

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipSuppressed]);
        Assert.Equal(1, Assert.Single(row.Suggested).Hosts);
    }

    /// <summary>突變參考：試跑略過抑制計算（Suppressed 恆 false）時這條轉紅</summary>
    [Fact]
    public void 抑制_Group範圍Windows規則_只排除群組內主機()
    {
        var room = _hostGroups.Upsert(new HostGroup { GroupName = "機房A" });
        PoolSeeing(AddHost("HOST-IN", new[] { room.GroupId }), AddHost("HOST-OUT"));
        Rules(new KnownIssueRule { Id = "win-disk", Platform = "windows", SourcePattern = "DISK", EventIds = new[] { 153 } });
        Suppress(new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.Rule, RuleId = "WIN-DISK", Scope = SuppressionScopes.Group,
            HostGroupId = room.GroupId, Reason = "測試"
        });

        var dto = Service().GetGaps(null, null, 1);
        var row = RowOf(dto, "disk");

        Assert.Equal(1, row.Excluded[WorkOrderDispatcher.SkipSuppressed]);
        Assert.Equal(1, Assert.Single(row.Suggested).Hosts);

        var result = Service().RunAutoDispatch(null, null);
        Assert.Equal(1, result.AssignedMembers);
        Assert.Null(_caseStore.GetOpen("HOST-IN", KeyOf(Disk())));
    }

    [Fact]
    public void 抑制_Site範圍Linux規則_全站排除該程式()
    {
        PoolSeeing(AddHost("HOST-A", null, Sshd(), Disk()), AddHost("HOST-B", null, Sshd()));
        Rules(new KnownIssueRule { Id = "linux-ssh", Platform = "linux", ProgramPattern = "sshd" });
        Suppress(new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.Rule, RuleId = "linux-ssh", Scope = SuppressionScopes.Site, Reason = "測試"
        });

        var dto = Service().GetGaps(null, null, 1);

        var sshd = RowOf(dto, "sshd");
        Assert.Equal(0, sshd.EventId);
        Assert.Equal(2, sshd.GapHosts);
        Assert.Equal(2, sshd.Excluded[WorkOrderDispatcher.SkipSuppressed]);
        Assert.Empty(sshd.Suggested);
        Assert.Empty(RowOf(dto, "disk").Excluded);
    }

    [Fact]
    public void 抑制_PRTG規則_依代碼排除()
    {
        PoolSeeing(AddHost("HOST-A", null, PrtgDown(), Disk()));
        Rules(new KnownIssueRule { Id = "prtg-down", Platform = "prtg", PrtgRuleCode = "DOWN" });
        Suppress(new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.Rule, RuleId = "prtg-down", Scope = SuppressionScopes.Site, Reason = "測試"
        });

        var dto = Service().GetGaps(null, null, 1);

        var prtg = RowOf(dto, "PRTG:down");
        Assert.Equal(0, prtg.EventId);
        Assert.Equal(1, prtg.Excluded[WorkOrderDispatcher.SkipSuppressed]);
        Assert.Empty(prtg.Suggested);
        Assert.Single(RowOf(dto, "disk").Suggested);
    }

    [Fact]
    public void 抑制_已到期_不排除()
    {
        PoolSeeing(AddHost("HOST-A"));
        Rules(new KnownIssueRule { Id = "win-disk", Platform = "windows", SourcePattern = "disk", MatchAllEventIds = true });
        Suppress(new RuleSuppression
        {
            TargetType = SuppressionTargetTypes.Rule, RuleId = "win-disk", Scope = SuppressionScopes.Site, Reason = "測試",
            ExpiresAt = DateTime.Now.AddDays(-1)
        });

        var row = RowOf(Service().GetGaps(null, null, 1), "disk");

        Assert.False(row.Excluded.ContainsKey(WorkOrderDispatcher.SkipSuppressed));
        Assert.Single(row.Suggested);
    }

    // ── 試跑＝落盤 ───────────────────────────────────────────────────────────

    private (WebUser Heavy, WebUser Light) SeedMixed()
    {
        var pool = AddPoolGroup();
        var heavy = AddUser("heavy", groupIds: pool.GroupId);
        var light = AddUser("light", groupIds: pool.GroupId);
        var room = _hostGroups.Upsert(new HostGroup { GroupName = "無人機房" });
        var a = AddHost("HOST-A", null, Disk(), Ntfs());
        var b = AddHost("HOST-B");
        var c = AddHost("HOST-C", null, Ntfs());
        var d = AddHost("HOST-D");                                       // 不再打擾
        AddHost("HOST-X", new[] { room.GroupId });                       // 無人可見
        Candidate(heavy, inPool: true, a.HostId, b.HostId, c.HostId, d.HostId);
        Candidate(light, inPool: true, a.HostId, d.HostId);
        GiveLoad(heavy, 1);
        DismissedCase("HOST-D", Disk());
        return (heavy, light);
    }

    /// <summary>突變參考：RunAutoDispatch 不經共用試跑方法、自己另跑一段決策（例如略過不再打擾判定）時這條轉紅</summary>
    [Fact]
    public void 試跑等於落盤_每人建議台數等於實際加入台數_無法派台數相同()
    {
        var (heavy, light) = SeedMixed();
        var service = Service();

        var gaps = service.GetGaps(null, null, 1);
        var suggested = gaps.Rows
            .SelectMany(r => r.Suggested)
            .GroupBy(s => s.HandlerId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Hosts));
        var unassignable = gaps.Rows
            .SelectMany(r => r.Unassignable)
            .GroupBy(u => u.Reason)
            .ToDictionary(g => g.Key, g => g.Sum(u => u.Hosts));

        var result = service.RunAutoDispatch(null, null);

        Assert.Equal(suggested, result.PerHandler.ToDictionary(p => p.HandlerId, p => p.Hosts));
        Assert.Equal(unassignable, result.Unassigned.ToDictionary(u => u.Reason, u => u.Hosts));

        // 實際落盤：以案件 store 逐人數（主機, 問題）
        var actual = _caseStore.GetMany(new[] { "HOST-A", "HOST-B", "HOST-C", "HOST-D", "HOST-X" })
            .Where(x => x.ClosedAt == null && x.HandlerId != null)
            .GroupBy(x => x.HandlerId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(suggested, actual);
        Assert.Equal(1, unassignable[WorkOrderDispatcher.NoCandidateNoVisibility]);
        Assert.DoesNotContain(_caseStore.GetMany(new[] { "HOST-D" }), x => x.ClosedAt == null);
        Assert.Equal(suggested.Values.Sum(), result.AssignedMembers);
        Assert.True(suggested.ContainsKey(light.UserId));
        Assert.True(suggested.ContainsKey(heavy.UserId));
        Assert.Equal(result.CreatedOrders, _orderStore.All.Count(o => o.Origin == WorkOrderOrigins.AutoDispatch));

        // 落盤後再試跑：缺口都有人接了，只剩派不出去的
        var after = service.GetGaps(null, null, 1);
        Assert.Empty(after.Rows.SelectMany(r => r.Suggested));
    }

    [Fact]
    public void 立即派工_不改存檔設定_夜間開關維持關閉()
    {
        SeedMixed();

        var result = Service().RunAutoDispatch(null, null);

        Assert.True(result.CreatedOrders > 0);
        Assert.False(_settingsStore.Get().AutoDispatchEnabled);
    }

    [Fact]
    public void 立即派工_稽核一筆_動作碼與期間()
    {
        SeedMixed();
        var from = Yesterday.AddDays(-2);

        var result = Service().RunAutoDispatch(from, Yesterday);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(AuditActions.WorkOrderAutoDispatchRun, entry.Action);
        Assert.Equal("work_order", entry.TargetKind);
        Assert.Equal($"{from:yyyy-MM-dd}~{Yesterday:yyyy-MM-dd}", entry.TargetId);
        Assert.Contains($"新建 {result.CreatedOrders} 張", entry.Summary);
        Assert.Contains("無法派 1 台", entry.Summary);
    }

    /// <summary>某一組建單擲例外：其餘組照常建單、失敗組列出、稽核仍寫一筆</summary>
    [Fact]
    public void 立即派工_部分失敗_其餘組照常_失敗組列出_稽核一筆()
    {
        var failing = new ThrowingOrderStore(_caseStore) { FailSource = "ntfs" };
        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(failing, _caseStore, _issueHandlingStore, caseCoordinator, _handlingStore, _hosts);
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var a = AddHost("HOST-A", null, Disk(), Ntfs());
        var b = AddHost("HOST-B");
        Candidate(h, inPool: true, a.HostId, b.HostId);
        var service = new WorkOrderBoardService(
            failing, _caseStore, _users, _userGroups, _hosts, _hostGroups, _ruleStore, _suppressionStore, _query, _aggregates,
            _candidates, _issueOwners, _noiseMarks, _settingsStore, coordinator,
            FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, _displayNames, _mail, WorkOrderBoardService.DefaultMaxOccurrences);

        var result = service.RunAutoDispatch(null, null);

        Assert.Equal(1, result.CreatedOrders);
        Assert.Equal(2, result.AssignedMembers);
        var failed = Assert.Single(result.FailedGroups);
        Assert.Equal((h.UserId, "ntfs", 55, 1), (failed.HandlerId, failed.Source, failed.EventId, failed.Hosts));
        Assert.Contains("注入", failed.Message);
        Assert.NotNull(_caseStore.GetOpen("HOST-B", KeyOf(Disk())));
        Assert.Null(_caseStore.GetOpen("HOST-A", KeyOf(Ntfs())));
        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("失敗 1 組", entry.Summary);
    }

    [Fact]
    public void 立即派工_全部組失敗_仍回結果不擲_稽核一筆()
    {
        var failing = new ThrowingOrderStore(_caseStore) { FailSource = "*" };
        var caseCoordinator = new IssueCaseCoordinator(_caseStore, _issueHandlingStore, _handlingStore, _recordStore, _hosts, new FakeIssueOwnerStore());
        var coordinator = new WorkOrderCoordinator(failing, _caseStore, _issueHandlingStore, caseCoordinator, _handlingStore, _hosts);
        var pool = AddPoolGroup();
        var h = AddUser("h", groupIds: pool.GroupId);
        var a = AddHost("HOST-A", null, Disk(), Ntfs());
        Candidate(h, inPool: true, a.HostId);
        var service = new WorkOrderBoardService(
            failing, _caseStore, _users, _userGroups, _hosts, _hostGroups, _ruleStore, _suppressionStore, _query, _aggregates,
            _candidates, _issueOwners, _noiseMarks, _settingsStore, coordinator,
            FakeCurrentUser.WithCapabilities(Capability.Maintain), _audit, _displayNames, _mail, WorkOrderBoardService.DefaultMaxOccurrences);

        var result = service.RunAutoDispatch(null, null);

        Assert.Equal(0, result.CreatedOrders);
        Assert.Equal(2, result.FailedGroups.Count);
        Assert.Contains("失敗 2 組", Assert.Single(_audit.Entries).Summary);
    }

    /// <summary>指定問題來源（"*"＝全部）的建單在寫入時擲例外</summary>
    private sealed class ThrowingOrderStore : FakeWorkOrderStore
    {
        public ThrowingOrderStore(IIssueCaseStore cases) : base(cases) { }

        public string FailSource { get; init; } = string.Empty;

        public override long Insert(WorkOrder order)
        {
            if (FailSource == "*" || string.Equals(order.SourceName, FailSource, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("注入的建單失敗");
            return base.Insert(order);
        }
    }

    // ── 權限標註／路由 ───────────────────────────────────────────────────────

    private static List<Capability[]> PermissionsOf(Type controller) =>
        controller.GetCustomAttributes<PermissionAttribute>(inherit: false)
            .Select(a => (Capability[])a.Arguments![0]!)
            .ToList();

    [Fact]
    public void 權限_看板待派為Assign或ViewAll_立即派工為Maintain()
    {
        Assert.Equal(new[] { Capability.Assign, Capability.ViewAll }, Assert.Single(PermissionsOf(typeof(WorkOrderBoardController))));
        Assert.Equal(new[] { Capability.Maintain }, Assert.Single(PermissionsOf(typeof(WorkOrderAutoDispatchController))));
    }

    [Fact]
    public void 路由_看板與待派是字面段_單張詳情參數帶long約束()
    {
        static string TemplateOf(Type controller, string action) =>
            controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single().Template!;

        Assert.Equal("load-board", TemplateOf(typeof(WorkOrderBoardController), nameof(WorkOrderBoardController.LoadBoard)));
        Assert.Equal("gaps", TemplateOf(typeof(WorkOrderBoardController), nameof(WorkOrderBoardController.Gaps)));
        Assert.Equal("auto-dispatch", TemplateOf(typeof(WorkOrderAutoDispatchController), nameof(WorkOrderAutoDispatchController.AutoDispatch)));
        Assert.Equal("{id:long}", TemplateOf(typeof(WorkOrderViewController), nameof(WorkOrderViewController.Get)));
        Assert.False(long.TryParse("load-board", out _));
    }

    // ── 替身 ─────────────────────────────────────────────────────────────────

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

/// <summary><see cref="KnownIssueCatalog.RuleMayHit"/>：三平台各正反例</summary>
public class KnownIssueCatalogRuleMayHitTests
{
    [Fact]
    public void Windows_來源包含且事件ID在清單或全比對()
    {
        var listed = new KnownIssueRule { Platform = "windows", SourcePattern = "Disk", EventIds = new[] { 7, 153 } };
        var all = new KnownIssueRule { Platform = "windows", SourcePattern = "disk", MatchAllEventIds = true };

        Assert.True(KnownIssueCatalog.RuleMayHit(listed, "Microsoft-DISK-x", 153));
        Assert.False(KnownIssueCatalog.RuleMayHit(listed, "disk", 55));
        Assert.False(KnownIssueCatalog.RuleMayHit(listed, "ntfs", 153));
        Assert.True(KnownIssueCatalog.RuleMayHit(all, "Disk", 9999));
    }

    [Fact]
    public void Linux_來源包含程式名_不看事件ID()
    {
        var rule = new KnownIssueRule { Platform = "Linux", ProgramPattern = "sshd" };

        Assert.True(KnownIssueCatalog.RuleMayHit(rule, "SSHD", 0));
        Assert.False(KnownIssueCatalog.RuleMayHit(rule, "sudo", 0));
    }

    [Fact]
    public void PRTG_來源代碼與規則代碼不分大小寫比對()
    {
        var rule = new KnownIssueRule { Platform = "prtg", PrtgRuleCode = "down" };

        Assert.True(KnownIssueCatalog.RuleMayHit(rule, "prtg:DOWN", 0));
        Assert.False(KnownIssueCatalog.RuleMayHit(rule, "PRTG:flapping", 0));
        Assert.False(KnownIssueCatalog.RuleMayHit(rule, "down", 0));
    }
}
