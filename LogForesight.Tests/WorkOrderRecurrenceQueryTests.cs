using System.Reflection;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 R-1：交辦單的復發、主機數、觀察分流（讀取時推導）。單頭／清單列的主機統計、成員清單的復發與第 N 次與排序、
/// 觀察期間再發生日數 K 與「到期且沒有再發生」、處理人 KPI 的三個互斥數字與 <c>todo</c> 篩選。
/// 「今天」一律取自假時鐘（2026-11-20）；store 用記憶體替身。
/// </summary>
public sealed class WorkOrderRecurrenceQueryTests
{
    private static readonly DateTime Today = new(2026, 11, 20);
    private static readonly string DiskKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Error);

    private readonly FakeTimeProvider _clock;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly WebUser _alice;
    private int _caseSeq;

    public WorkOrderRecurrenceQueryTests()
    {
        _clock = LocalClock(Today.AddHours(10));
        _cases.Clock = _clock;
        _orders = new FakeWorkOrderStore(_cases, _clock);
        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲" });
    }

    private static FakeTimeProvider LocalClock(DateTime localNow)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Local);
        return clock;
    }

    private WorkOrderQueryService Service(ICurrentUser user, IAnalysisRecordQuery? records = null, IIssueHandlingStore? handlings = null,
        params long[] visibleHostIds) => new(
        _orders, _cases, _users, _hosts, _hostGroups, new FakeRuleStore(),
        new ScopedVisibility(visibleHostIds.Length > 0 ? visibleHostIds : _hosts.GetAll().Select(h => h.HostId).ToArray()),
        user, new UserDisplayNameService(_settings), new FixedIssueExclusionSource(IssueExclusion.None), _userGroups,
        records ?? _records, _clock, null, handlings ?? _issueHandlings);

    private static ICurrentUser Admin() => FakeCurrentUser.ForUser(9001, new[] { Capability.Assign, Capability.ViewAll });

    private long AddOrder(int eventId = 153, long? handler = null, DateTime? closed = null) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = eventId, IssueLabel = $"disk {eventId}", HandlerId = handler ?? _alice.UserId,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin",
            CreatedAt = Today.AddDays(-40), ClosedAt = closed
        });

    private WebHost Host(string name, bool active = true)
    {
        var existing = _hosts.FindByName(name);
        if (existing != null) return existing;
        _hosts.Upsert(new WebHost { HostName = name, Active = active });
        return _hosts.FindByName(name)!;
    }

    /// <summary>新增一件案件；<paramref name="orderId"/> 為 null＝沒掛在任何單上（歷史前案）</summary>
    private IssueCase AddCase(long? orderId, string host, string status, DateTime createdAt, DateTime? closedAt = null,
        DateTime? due = null, int eventId = 153, string? note = null, bool cancelled = false, string? caseId = null)
    {
        Host(host);
        var c = new IssueCase
        {
            CaseId = caseId ?? $"case-{++_caseSeq:D4}", HostName = host, IssueKey = IssueSignatureKey.For("System", "disk", eventId, LogEntryType.Error),
            IssueLabel = $"disk {eventId}", Status = status, HandlerId = _alice.UserId, DueDate = due, FirstLinkedDate = createdAt.Date,
            LastLinkedDate = createdAt.Date, CreatedAt = createdAt, CreatedByAccount = "admin", UpdatedAt = createdAt,
            ClosedAt = closedAt, WorkOrderId = orderId, Note = note, Cancelled = cancelled
        };
        _cases.Save(c);
        return c;
    }

    private WorkOrderRowDto Row(long orderId) =>
        Assert.Single(Service(Admin()).List(new WorkOrderListRequest { Status = "all", PageSize = 100 }).Items, i => i.WorkOrderId == orderId);

    // ── 單頭／清單列：主機數 ──────────────────────────────────────────────────

    [Fact]
    public void 一張單247件成員100台_總主機數是100_既有Counts欄位語意不變()
    {
        var order = AddOrder();
        // 100 台各有 1 件進行中；另 147 件已結案分散在其中 53 台（47 台各 3 件、6 台各 1 件）
        for (var i = 0; i < 100; i++) AddCase(order, $"H{i:D3}", IssueHandlingStatuses.InProgress, Today.AddDays(-1));
        for (var i = 0; i < 47; i++)
            for (var k = 0; k < 3; k++)
                AddCase(order, $"H{i:D3}", IssueHandlingStatuses.Resolved, Today.AddDays(-30 - k), Today.AddDays(-29 - k));
        for (var i = 47; i < 53; i++) AddCase(order, $"H{i:D3}", IssueHandlingStatuses.Resolved, Today.AddDays(-35), Today.AddDays(-34));

        var row = Row(order);
        var detail = Service(Admin()).Get(order);

        Assert.Equal(247, row.Counts.Total);       // 既有欄位：成員（案件）數
        Assert.Equal(100, row.Counts.Active);
        Assert.Equal(147, row.Counts.Closed);
        Assert.Equal(100, row.TotalHosts);         // 新欄位：不重複主機
        Assert.Equal(100, row.ActiveHosts);
        Assert.Equal(100, detail.TotalHosts);
        Assert.Equal(247, detail.Counts.Total);
    }

    [Fact]
    public void 主機統計_復發_觀察期內_觀察到期_待處理_各自以不重複主機計()
    {
        var order = AddOrder();
        // 復發：前案 5 天前以已處理結案
        AddCase(null, "H1", IssueHandlingStatuses.Resolved, Today.AddDays(-9), Today.AddDays(-5));
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-2));
        // 不算復發：前案是不處理類
        AddCase(null, "H2", IssueHandlingStatuses.WontFix, Today.AddDays(-9), Today.AddDays(-5));
        AddCase(order, "H2", IssueHandlingStatuses.InProgress, Today.AddDays(-2));
        // 觀察期內：觀察至日期是今天（邊界，算期內）；觀察到期：觀察至是昨天（邊界，算到期）
        AddCase(order, "H3", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today);
        AddCase(order, "H4", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(-1));
        AddCase(order, "H5", IssueHandlingStatuses.Escalated, Today.AddDays(-3));
        AddCase(order, "H6", IssueHandlingStatuses.InProgress, Today.AddDays(-3));
        // 停用主機上的進行中成員：不計入進行中相關數字，歷來主機數仍含
        Host("H7", active: false);
        AddCase(order, "H7", IssueHandlingStatuses.InProgress, Today.AddDays(-3));

        var row = Row(order);

        Assert.Equal(6, row.ActiveHosts);
        Assert.Equal(7, row.TotalHosts);
        Assert.Equal(1, row.RecurrenceHosts);
        Assert.Equal(1, row.ObservingHosts);
        Assert.Equal(1, row.ObservationExpiredHosts);
        Assert.Equal(4, row.TodoHosts);   // H1、H2、H4（觀察到期算待處理）、H6；觀察期內與上報中不算
    }

    [Fact]
    public void 觀察至日期_今天算期內_昨天算到期_邊界兩側各一例()
    {
        var order = AddOrder();
        AddCase(order, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today);

        Assert.Equal((1, 0), (Row(order).ObservingHosts, Row(order).ObservationExpiredHosts));

        _clock.SetUtcNow(LocalClock(Today.AddDays(1).AddHours(10)).GetUtcNow());
        _cases.Clock = _clock;

        var after = Row(order);
        Assert.Equal((0, 1), (after.ObservingHosts, after.ObservationExpiredHosts));
    }

    [Fact]
    public void 單頭的復發主機數_復發判定與成員清單同一份_前案在三十天外不算()
    {
        var order = AddOrder();
        AddCase(null, "H1", IssueHandlingStatuses.Resolved, Today.AddDays(-40), Today.AddDays(-30));   // 建立日 −2 起算第 28 天（窗內）
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-2));
        AddCase(null, "H2", IssueHandlingStatuses.Resolved, Today.AddDays(-40), Today.AddDays(-33));   // 第 31 天（窗外）
        AddCase(order, "H2", IssueHandlingStatuses.InProgress, Today.AddDays(-2));

        var row = Row(order);
        var members = Service(Admin()).Members(order, "active", 1, 50).Items;

        Assert.Equal(1, row.RecurrenceHosts);
        Assert.Equal(row.RecurrenceHosts, members.Count(m => m.IsRecurrence));
        Assert.True(members.Single(m => m.HostName == "H1").IsRecurrence);
        Assert.False(members.Single(m => m.HostName == "H2").IsRecurrence);
    }

    // ── 成員清單：復發、上次回覆、第 N 次、排序 ─────────────────────────────────

    [Fact]
    public void 成員_復發_次數_上次回覆_上一件在別張單也算()
    {
        var order = AddOrder();
        var previous = AddCase(null, "H1", IssueHandlingStatuses.Resolved, Today.AddDays(-12), Today.AddDays(-8).AddHours(14), note: "已更換硬碟");
        AddCase(null, "H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-16), note: "更早的說明");
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-1));

        var member = Assert.Single(Service(Admin()).Members(order, "active", 1, 50).Items);

        Assert.True(member.IsRecurrence);
        Assert.Equal(2, member.RecurrenceCount);
        Assert.Equal(3, member.Occurrence);
        Assert.NotNull(member.LastReply);
        Assert.Equal(previous.ClosedAt, member.LastReply!.Date);
        Assert.Equal(IssueHandlingStatuses.Resolved, member.LastReply.Status);
        Assert.Equal("已更換硬碟", member.LastReply.Note);
    }

    [Fact]
    public void 成員_上一件是不處理類_有上次回覆但不算復發()
    {
        var order = AddOrder();
        AddCase(null, "H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15));
        AddCase(null, "H1", IssueHandlingStatuses.FalsePositive, Today.AddDays(-9), Today.AddDays(-7), note: "誤報");
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-1));

        var member = Assert.Single(Service(Admin()).Members(order, "active", 1, 50).Items);

        Assert.False(member.IsRecurrence);
        Assert.Equal(1, member.RecurrenceCount);
        Assert.Equal(IssueHandlingStatuses.FalsePositive, member.LastReply!.Status);
        Assert.Equal("誤報", member.LastReply.Note);
    }

    [Fact]
    public void 成員_沒有前案_欄位都是預設值_上次回覆為null_第1次()
    {
        var order = AddOrder();
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-1));

        var member = Assert.Single(Service(Admin()).Members(order, "all", 1, 50).Items);

        Assert.False(member.IsRecurrence);
        Assert.Equal(0, member.RecurrenceCount);
        Assert.Null(member.LastReply);
        Assert.Equal(1, member.Occurrence);
        Assert.Null(member.ObservedRecurDays);
    }

    [Fact]
    public void 全部篩選_依主機名再依建立日新到舊_同主機新舊案件相鄰_第N次依建立先後編號()
    {
        var order = AddOrder();
        var a1 = AddCase(order, "HOST-A", IssueHandlingStatuses.Resolved, Today.AddDays(-30), Today.AddDays(-28), caseId: "z-a1");
        var a2 = AddCase(order, "HOST-A", IssueHandlingStatuses.Resolved, Today.AddDays(-12), Today.AddDays(-10), caseId: "a-a2");
        var a3 = AddCase(order, "HOST-A", IssueHandlingStatuses.InProgress, Today.AddDays(-1), caseId: "m-a3");
        var b1 = AddCase(order, "HOST-B", IssueHandlingStatuses.InProgress, Today.AddDays(-3), caseId: "b-b1");

        var items = Service(Admin()).Members(order, "all", 1, 50).Items;

        Assert.Equal(new[] { a3.CaseId, a2.CaseId, a1.CaseId, b1.CaseId }, items.Select(i => i.CaseId).ToArray());
        Assert.Equal(new[] { 3, 2, 1, 1 }, items.Select(i => i.Occurrence).ToArray());
    }

    [Fact]
    public void 全部篩選_貼上主機名清單時也依同一規則排序()
    {
        var order = AddOrder();
        AddCase(order, "HOST-A", IssueHandlingStatuses.Resolved, Today.AddDays(-30), Today.AddDays(-28), caseId: "a-old");
        AddCase(order, "HOST-A", IssueHandlingStatuses.InProgress, Today.AddDays(-1), caseId: "z-new");

        var items = Service(Admin()).Members(order, "all", 1, 50, new[] { "host-a" }).Items;

        Assert.Equal(new[] { "z-new", "a-old" }, items.Select(i => i.CaseId).ToArray());
    }

    [Fact]
    public void 進行中篩選_排序維持主機名再案件編號_不受全部篩選的新規則影響()
    {
        var order = AddOrder();
        AddCase(order, "HOST-B", IssueHandlingStatuses.InProgress, Today.AddDays(-1), caseId: "c-b");
        AddCase(order, "HOST-A", IssueHandlingStatuses.InProgress, Today.AddDays(-9), caseId: "d-a1");
        AddCase(order, "HOST-A", IssueHandlingStatuses.InProgress, Today.AddDays(-2), caseId: "a-a2");

        var items = Service(Admin()).Members(order, "active", 1, 50).Items;

        Assert.Equal(new[] { "a-a2", "d-a1", "c-b" }, items.Select(i => i.CaseId).ToArray());
    }

    // ── 觀察期間再發生日數 K 與「到期且沒有再發生」────────────────────────────────

    private void AddObservingRows(IssueCase c, DateTime start) =>
        _issueHandlings.Save(new IssueHandling
        {
            HostName = c.HostName, Date = start.Date, IssueKey = c.IssueKey, Status = IssueHandlingStatuses.Observing,
            CaseId = c.CaseId, UpdatedAt = start
        });

    private void AddRecord(string host, DateTime date, bool withIssue)
    {
        var h = Host(host);
        _records.Add(new DailyAnalysisRecord
        {
            Date = date, HostId = h.HostId, Host = host,
            TopIssues = withIssue
                ? new List<LogIssueSignature> { new() { LogName = "System", Source = "disk", EventId = 153, EntryType = LogEntryType.Error } }
                : new List<LogIssueSignature>()
        });
    }

    [Fact]
    public void K_觀察開始日不含_今天含_以不重複日數計()
    {
        var order = AddOrder();
        var c = AddCase(order, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
        AddObservingRows(c, Today.AddDays(-5).AddHours(9));
        AddRecord("H1", Today.AddDays(-5), withIssue: true);    // 開始日當天：不算
        AddRecord("H1", Today.AddDays(-4), withIssue: true);
        AddRecord("H1", Today.AddDays(-4), withIssue: true);    // 同一天重複：只算一天
        AddRecord("H1", Today.AddDays(-2), withIssue: true);
        AddRecord("H1", Today, withIssue: true);                // 今天：算

        var member = Assert.Single(Service(Admin()).Members(order, "active", 1, 50).Items);

        Assert.Equal(3, member.ObservedRecurDays);
    }

    [Fact]
    public void K_窗內有分析紀錄但問題沒出現_是0_窗內沒有任何紀錄_是無資料null()
    {
        var order = AddOrder();
        var quiet = AddCase(order, "H-QUIET", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
        var noData = AddCase(order, "H-NODATA", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
        var onlyOnStart = AddCase(order, "H-START", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
        foreach (var c in new[] { quiet, noData, onlyOnStart }) AddObservingRows(c, Today.AddDays(-5).AddHours(9));
        AddRecord("H-QUIET", Today.AddDays(-3), withIssue: false);
        AddRecord("H-START", Today.AddDays(-5), withIssue: false);   // 只有開始日當天的紀錄：窗口 (開始日, 今天] 內沒有

        var items = Service(Admin()).Members(order, "active", 1, 50).Items.ToDictionary(m => m.HostName);

        Assert.Equal(0, items["H-QUIET"].ObservedRecurDays);
        Assert.Null(items["H-NODATA"].ObservedRecurDays);
        Assert.Null(items["H-START"].ObservedRecurDays);
    }

    [Fact]
    public void K_非觀察中的成員是null_觀察開始日是今天時沒有可觀察的日子_無資料()
    {
        var order = AddOrder();
        AddCase(order, "H-PROG", IssueHandlingStatuses.InProgress, Today.AddDays(-10));
        var today = AddCase(order, "H-TODAY", IssueHandlingStatuses.Observing, Today.AddHours(1), due: Today.AddDays(3));
        AddObservingRows(today, Today.AddHours(9));
        AddRecord("H-TODAY", Today, withIssue: true);

        var items = Service(Admin()).Members(order, "active", 1, 50).Items.ToDictionary(m => m.HostName);

        Assert.Null(items["H-PROG"].ObservedRecurDays);
        Assert.Null(items["H-TODAY"].ObservedRecurDays);
    }

    [Fact]
    public void K_逐日列還沒寫時退回案件最後更新時間當觀察開始日()
    {
        var order = AddOrder();
        var c = AddCase(order, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
        c.UpdatedAt = Today.AddDays(-3).AddHours(9);
        _cases.Save(c);
        AddRecord("H1", Today.AddDays(-3), withIssue: true);   // 開始日當天不算
        AddRecord("H1", Today.AddDays(-1), withIssue: true);

        var member = Assert.Single(Service(Admin()).Members(order, "active", 1, 50).Items);

        Assert.Equal(1, member.ObservedRecurDays);
    }

    [Fact]
    public void 觀察到期且K為0進單頭的靜默清單_K大於0_無資料_觀察期內都不進()
    {
        var order = AddOrder();
        var quiet = AddCase(order, "H-QUIET", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        var recur = AddCase(order, "H-RECUR", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        var noData = AddCase(order, "H-NODATA", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        var notExpired = AddCase(order, "H-LIVE", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today);
        foreach (var c in new[] { quiet, recur, noData, notExpired }) AddObservingRows(c, Today.AddDays(-6).AddHours(9));
        AddRecord("H-QUIET", Today.AddDays(-2), withIssue: false);
        AddRecord("H-RECUR", Today.AddDays(-2), withIssue: true);
        AddRecord("H-LIVE", Today.AddDays(-2), withIssue: false);   // 觀察期內、K＝0：只有到期的才進清單

        var detail = Service(Admin()).Get(order);

        Assert.Equal(new[] { quiet.CaseId }, detail.ObservationExpiredQuietCaseIds);
        Assert.Equal(3, detail.ObservationExpiredHosts);
        Assert.Equal(1, detail.ObservingHosts);
    }

    [Fact]
    public void 靜默清單_用假時鐘撥過觀察至日_當天還在期內_隔天到期且沒再發生才進清單()
    {
        var order = AddOrder();
        var c = AddCase(order, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today);
        AddObservingRows(c, Today.AddDays(-6).AddHours(9));
        AddRecord("H1", Today.AddDays(-1), withIssue: false);
        AddRecord("H1", Today.AddDays(1), withIssue: false);

        Assert.Empty(Service(Admin()).Get(order).ObservationExpiredQuietCaseIds);   // 觀察至＝今天：還在期內

        _clock.SetUtcNow(LocalClock(Today.AddDays(1).AddHours(10)).GetUtcNow());
        _cases.Clock = _clock;

        Assert.Equal(new[] { c.CaseId }, Service(Admin()).Get(order).ObservationExpiredQuietCaseIds);
    }

    [Fact]
    public void 靜默清單_不含停用主機_受限檢視者只看到自己可見主機的案件()
    {
        var order = AddOrder();
        var visible = AddCase(order, "H-SEE", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        var hidden = AddCase(order, "H-HIDE", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        Host("H-OFF", active: false);
        var disabled = AddCase(order, "H-OFF", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(-1));
        foreach (var c in new[] { visible, hidden, disabled })
        {
            AddObservingRows(c, Today.AddDays(-6).AddHours(9));
            AddRecord(c.HostName, Today.AddDays(-2), withIssue: false);
        }

        var all = Service(Admin()).Get(order).ObservationExpiredQuietCaseIds;
        var restricted = Service(FakeCurrentUser.ForUser(9002, new[] { Capability.Assign }), null, null, Host("H-SEE").HostId)
            .Get(order).ObservationExpiredQuietCaseIds;

        Assert.Equal(new[] { visible.CaseId, hidden.CaseId }.OrderBy(x => x).ToArray(), all.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { visible.CaseId }, restricted);
    }

    // ── 我的交辦：KPI 三個互斥數字與 todo 篩選 ─────────────────────────────────

    [Fact]
    public void 我的交辦KPI_待我處理_觀察期內_上報中_三者相加等於進行中台數_觀察到期算待我處理()
    {
        var o1 = AddOrder(153);
        var o2 = AddOrder(154);
        AddCase(o1, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-3));
        AddCase(o1, "H2", IssueHandlingStatuses.Open, Today.AddDays(-3));
        AddCase(o1, "H3", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today);                 // 期內（邊界）
        AddCase(o1, "H4", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(-1));     // 到期（邊界）→ 待我處理
        AddCase(o2, "H5", IssueHandlingStatuses.Escalated, Today.AddDays(-3), eventId: 154);
        AddCase(o2, "H6", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(5), eventId: 154);
        Host("H7", active: false);
        AddCase(o2, "H7", IssueHandlingStatuses.Escalated, Today.AddDays(-3), eventId: 154);              // 停用主機：三者都不計

        var s = Service(FakeCurrentUser.ForUser(_alice.UserId, new[] { Capability.Handle })).HandlerSummary(_alice.UserId);

        Assert.Equal(6, s.ActiveMembers);
        Assert.Equal(3, s.TodoHosts);        // H1、H2、H4
        Assert.Equal(2, s.ObservingHosts);   // H3、H6
        Assert.Equal(1, s.EscalatedHosts);   // H5
        Assert.Equal(s.ActiveMembers, s.TodoHosts + s.ObservingHosts + s.EscalatedHosts);
    }

    [Fact]
    public void 我的交辦KPI_徽章與摘要同一份數字()
    {
        var order = AddOrder();
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-3));
        AddCase(order, "H2", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(2));

        var svc = Service(FakeCurrentUser.ForUser(_alice.UserId, new[] { Capability.Handle }));
        var badge = svc.MyBadge();
        var summary = svc.HandlerSummary(_alice.UserId);

        Assert.Equal((summary.TodoHosts, summary.ObservingHosts, summary.EscalatedHosts), (badge.TodoHosts, badge.ObservingHosts, badge.EscalatedHosts));
        Assert.Equal((1, 1, 0), (badge.TodoHosts, badge.ObservingHosts, badge.EscalatedHosts));
    }

    private List<WorkOrderRowDto> TodoList() =>
        Service(FakeCurrentUser.ForUser(_alice.UserId, new[] { Capability.Handle }))
            .ListForHandler(_alice.UserId, new WorkOrderListRequest { Status = "todo", PageSize = 100 }).Items;

    [Fact]
    public void todo篩選_只剩觀察期內成員的單不列_有觀察到期成員的單列出_台數以待處理成員計()
    {
        var observingOnly = AddOrder(201);
        AddCase(observingOnly, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today, eventId: 201);
        AddCase(observingOnly, "H2", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(4), eventId: 201);

        var expired = AddOrder(202);
        AddCase(expired, "H3", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(-1), eventId: 202);

        var mixed = AddOrder(203);
        AddCase(mixed, "H4", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(2), eventId: 203);
        AddCase(mixed, "H5", IssueHandlingStatuses.InProgress, Today.AddDays(-3), eventId: 203);
        AddCase(mixed, "H6", IssueHandlingStatuses.Open, Today.AddDays(-3), eventId: 203);

        var escalatedOnly = AddOrder(204);
        AddCase(escalatedOnly, "H7", IssueHandlingStatuses.Escalated, Today.AddDays(-3), eventId: 204);

        var closed = AddOrder(205, closed: Today.AddDays(-1));
        AddCase(closed, "H8", IssueHandlingStatuses.Resolved, Today.AddDays(-5), Today.AddDays(-1), eventId: 205);

        var rows = TodoList();

        Assert.Equal(new[] { expired, mixed }.OrderBy(x => x).ToArray(), rows.Select(r => r.WorkOrderId).OrderBy(x => x).ToArray());
        Assert.Equal(1, rows.Single(r => r.WorkOrderId == expired).TodoHosts);
        Assert.Equal(2, rows.Single(r => r.WorkOrderId == mixed).TodoHosts);      // H5、H6；觀察期內的 H4 不計
        Assert.Equal(3, rows.Single(r => r.WorkOrderId == mixed).ActiveHosts);    // 進行中的主機數仍含觀察期內的
    }

    [Fact]
    public void todo篩選_觀察至日期過了的隔天_原本被排除的單變成待我處理()
    {
        var order = AddOrder();
        AddCase(order, "H1", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today);

        Assert.Empty(TodoList());

        _clock.SetUtcNow(LocalClock(Today.AddDays(1).AddHours(10)).GetUtcNow());
        _cases.Clock = _clock;

        Assert.Equal(order, Assert.Single(TodoList()).WorkOrderId);
    }

    [Fact]
    public void todo篩選_停用主機上的待處理成員不算()
    {
        var order = AddOrder();
        Host("H1", active: false);
        AddCase(order, "H1", IssueHandlingStatuses.InProgress, Today.AddDays(-3));
        AddCase(order, "H2", IssueHandlingStatuses.Observing, Today.AddDays(-3), due: Today.AddDays(3));

        Assert.Empty(TodoList());
    }

    [Fact]
    public void 狀態篩選仍擋掉未知值_todo是合法值()
    {
        var svc = Service(Admin());

        Assert.Empty(svc.List(new WorkOrderListRequest { Status = "todo" }).Items);
        Assert.Throws<DomainException>(() => svc.List(new WorkOrderListRequest { Status = "nonsense" }));
    }

    // ── 效能形狀：查詢次數不隨成員數增加 ───────────────────────────────────────

    [Theory]
    [InlineData(1, 3)]
    [InlineData(5, 60)]
    public void 清單的主機統計_store呼叫次數固定_不隨單數與成員數增加(int orderCount, int membersPerOrder)
    {
        for (var o = 0; o < orderCount; o++)
        {
            var order = AddOrder(300 + o);
            for (var m = 0; m < membersPerOrder; m++)
                AddCase(order, $"H{o}-{m}", IssueHandlingStatuses.InProgress, Today.AddDays(-1), eventId: 300 + o);
        }
        var before = (_cases.GetActiveMemberSummariesCalls, _cases.GetClosedHistoryCalls, _cases.CountDistinctHostsCalls);

        var list = Service(Admin()).List(new WorkOrderListRequest { Status = "all", PageSize = 100 });

        Assert.Equal(orderCount, list.Items.Count);
        Assert.Equal(before.GetActiveMemberSummariesCalls + 1, _cases.GetActiveMemberSummariesCalls);
        Assert.Equal(before.GetClosedHistoryCalls + 1, _cases.GetClosedHistoryCalls);
        Assert.Equal(before.CountDistinctHostsCalls + 1, _cases.CountDistinctHostsCalls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    public void 成員清單的復發與K_批次取一次_不逐成員查(int observingMembers)
    {
        var order = AddOrder();
        for (var i = 0; i < observingMembers; i++)
        {
            var c = AddCase(order, $"H{i:D3}", IssueHandlingStatuses.Observing, Today.AddDays(-10), due: Today.AddDays(3));
            AddObservingRows(c, Today.AddDays(-5).AddHours(9));
            AddRecord(c.HostName, Today.AddDays(-2), withIssue: i % 2 == 0);
        }
        var records = CountingProxy<IAnalysisRecordQuery>.Create(_records);
        var handlings = CountingProxy<IIssueHandlingStore>.Create(_issueHandlings);
        var before = _cases.GetClosedHistoryCalls;

        var page = Service(Admin(), records, handlings).Members(order, "active", 1, 200);

        Assert.Equal(observingMembers, page.Items.Count);
        Assert.Equal(before + 1, _cases.GetClosedHistoryCalls);
        Assert.Equal(1, CountingProxy<IAnalysisRecordQuery>.Calls(records, nameof(IAnalysisRecordQuery.IssueDaysFor)));
        Assert.Equal(1, CountingProxy<IIssueHandlingStore>.Calls(handlings, nameof(IIssueHandlingStore.GetObservationStarts)));
        Assert.True(CountingProxy<IAnalysisRecordQuery>.Calls(records, nameof(IAnalysisRecordQuery.QueryDayStatusLightweight)) <= 1);
        Assert.Equal(observingMembers / 2, page.Items.Count(m => m.ObservedRecurDays == 1));
        Assert.Equal(observingMembers - observingMembers / 2, page.Items.Count(m => m.ObservedRecurDays == 0));
    }

    /// <summary>以 DispatchProxy 轉呼叫真實替身並逐方法計次（介面很大、不值得手寫一個計數包裝）</summary>
    public class CountingProxy<T> : DispatchProxy where T : class
    {
        private T _target = null!;
        private readonly Dictionary<string, int> _calls = new();

        public static T Create(T target)
        {
            var proxy = DispatchProxy.Create<T, CountingProxy<T>>();
            ((CountingProxy<T>)(object)proxy)._target = target;
            return proxy;
        }

        public static int Calls(T proxy, string method) =>
            ((CountingProxy<T>)(object)proxy)._calls.GetValueOrDefault(method);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _calls[targetMethod!.Name] = _calls.GetValueOrDefault(targetMethod.Name) + 1;
            try
            {
                return targetMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }
}
