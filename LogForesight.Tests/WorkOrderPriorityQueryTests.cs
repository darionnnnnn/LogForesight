using LogForesight.Core.Analysis;
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
/// 階段 P-1：交辦單清單的嚴重度推導（H12）、清單列標記（今天新增、被退回）、處理優先序等三個推導排序（H7，跨頁成立）、
/// 標記篩選與我的交辦「今天需要你處理」KPI（F01-2、F01-3）。
/// 「今天」一律取自假時鐘（2026-11-20 10:00）；store 用記憶體替身，嚴重度的最近出現點由 <see cref="FakeIssueAggregateQuery"/> 提供。
/// 每條邊界兩側各一例（昨天 23:59／今天 00:00、期限今天＋3／＋4、退回後已回覆／未回覆）。
/// </summary>
public sealed class WorkOrderPriorityQueryTests
{
    private static readonly DateTime Today = new(2026, 11, 20);
    private const int High = (int)IssueSeverity.High;
    private const int Medium = (int)IssueSeverity.Medium;
    private const int Low = (int)IssueSeverity.Low;

    private readonly FakeTimeProvider _clock;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeIssueAggregateQuery _aggregates = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly FakeSystemSettingsStore _settings = new();
    private readonly WebUser _alice;
    private int _caseSeq;

    public WorkOrderPriorityQueryTests()
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

    private WorkOrderQueryService Service(ICurrentUser user, FixedIssueExclusionSource? exclusion = null) => new(
        _orders, _cases, _users, _hosts, _hostGroups, new FakeRuleStore(),
        new ScopedVisibility(_hosts.GetAll().Select(h => h.HostId).ToArray()),
        user, new UserDisplayNameService(_settings), exclusion ?? new FixedIssueExclusionSource(IssueExclusion.None), _userGroups,
        _records, _clock, null, _issueHandlings, _aggregates);

    private static ICurrentUser Admin() => FakeCurrentUser.ForUser(9001, new[] { Capability.Assign, Capability.ViewAll });
    private ICurrentUser Alice() => FakeCurrentUser.ForUser(_alice.UserId, new[] { Capability.Handle });

    private static string KeyOf(int eventId) => IssueSignatureKey.For("System", "disk", eventId, LogEntryType.Error);

    /// <param name="ageDays">建立時間＝今天往前幾天（越小越新）；排序同分時依建立時間新到舊</param>
    private long AddOrder(int eventId, int ageDays = 10, DateTime? dueDate = null, DateTime? closed = null, long? handler = null) =>
        _orders.Insert(new WorkOrder
        {
            SourceName = "disk", EventId = eventId, IssueLabel = $"disk {eventId}", HandlerId = handler ?? _alice.UserId,
            Origin = WorkOrderOrigins.Manual, ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin",
            CreatedAt = Today.AddDays(-ageDays).AddHours(8), DueDate = dueDate, ClosedAt = closed
        });

    private WebHost Host(string name)
    {
        var existing = _hosts.FindByName(name);
        if (existing != null) return existing;
        _hosts.Upsert(new WebHost { HostName = name });
        return _hosts.FindByName(name)!;
    }

    private IssueCase AddMember(long? orderId, string host, string status = IssueHandlingStatuses.InProgress, DateTime? due = null,
        DateTime? createdAt = null, DateTime? closedAt = null, int eventId = 153, string? note = null)
    {
        Host(host);
        var created = createdAt ?? Today.AddDays(-5);
        var c = new IssueCase
        {
            CaseId = $"case-{++_caseSeq:D4}", HostName = host, IssueKey = KeyOf(eventId), IssueLabel = $"disk {eventId}",
            Status = status, HandlerId = _alice.UserId, DueDate = due, FirstLinkedDate = created.Date, LastLinkedDate = created.Date,
            CreatedAt = created, CreatedByAccount = "admin", UpdatedAt = created, ClosedAt = closedAt, WorkOrderId = orderId, Note = note
        };
        _cases.Save(c);
        return c;
    }

    private void Occurrence(string host, int eventId, int rank, bool elevates = false) =>
        _aggregates.LatestOccurrencesResult.Add(new HostIssueOccurrence
        {
            HostId = Host(host).HostId, IssueKey = KeyOf(eventId), LastSeen = Today.AddDays(-1), SeverityRank = rank, ElevatesDayRisk = elevates
        });

    private void Event(long orderId, string action, DateTime at, string? note = null) =>
        _orders.AppendEvent(new WorkOrderEvent { WorkOrderId = orderId, Action = action, ActorAccount = "x", CreatedAt = at, Note = note });

    private WorkOrderListDto List(WorkOrderListRequest req, ICurrentUser? user = null) =>
        Service(user ?? Admin()).List(req);

    private static List<long> Ids(WorkOrderListDto dto) => dto.Items.Select(i => i.WorkOrderId).ToList();

    // ══ H12 嚴重度推導 ════════════════════════════════════════════════════════

    [Fact]
    public void 嚴重度_取單內進行中成員最近出現的最高_重大旗標任一命中即是_舊資料Critical視為High()
    {
        var order = AddOrder(100);
        AddMember(order, "H1", eventId: 100);
        AddMember(order, "H2", eventId: 100);
        Occurrence("H1", 100, Low);
        Occurrence("H2", 100, High, elevates: true);

        var row = Assert.Single(List(new WorkOrderListRequest()).Items);

        Assert.Equal("High", row.Severity);
        Assert.True(row.Critical);

        // 舊資料的 Critical(3) 先正規化成 High，不是第四級
        _aggregates.LatestOccurrencesResult.Clear();
        Occurrence("H1", 100, (int)IssueSeverity.Critical);
        row = Assert.Single(List(new WorkOrderListRequest()).Items);
        Assert.Equal("High", row.Severity);
    }

    [Fact]
    public void 嚴重度_只看進行中成員_已結案成員的高嚴重度不算()
    {
        var order = AddOrder(100);
        AddMember(order, "H1", eventId: 100);
        AddMember(order, "H2", status: IssueHandlingStatuses.Resolved, closedAt: Today.AddDays(-2), eventId: 100);
        Occurrence("H1", 100, Low);
        Occurrence("H2", 100, High);

        var row = Assert.Single(List(new WorkOrderListRequest()).Items);

        Assert.Equal("Low", row.Severity);
        Assert.False(row.Critical);
    }

    [Fact]
    public void 嚴重度_成員全結案的單取全部成員最後已知值_推導不到為null()
    {
        var closedOrder = AddOrder(100, closed: Today.AddDays(-1));
        AddMember(closedOrder, "H1", status: IssueHandlingStatuses.Resolved, closedAt: Today.AddDays(-1), eventId: 100);
        Occurrence("H1", 100, Medium);
        var unknownOrder = AddOrder(101);
        AddMember(unknownOrder, "H2", eventId: 101);   // 沒有任何出現點

        var rows = List(new WorkOrderListRequest { Status = "all" }).Items.ToDictionary(r => r.WorkOrderId);

        Assert.Equal("Medium", rows[closedOrder].Severity);
        Assert.Null(rows[unknownOrder].Severity);
        Assert.False(rows[unknownOrder].Critical);
    }

    [Fact]
    public void 嚴重度_一頁的單只查一次出現點_不逐張查()
    {
        for (var i = 0; i < 6; i++)
        {
            var order = AddOrder(200 + i);
            AddMember(order, $"HX{i}", eventId: 200 + i);
            Occurrence($"HX{i}", 200 + i, Medium);
        }
        var closed = AddOrder(300, closed: Today.AddDays(-1));
        AddMember(closed, "HC", status: IssueHandlingStatuses.Resolved, closedAt: Today.AddDays(-1), eventId: 300);
        Occurrence("HC", 300, High);

        var dto = List(new WorkOrderListRequest { Status = "all", PageSize = 100 });

        Assert.Equal(7, dto.Items.Count);
        Assert.Equal(1, _aggregates.LatestOccurrencesCalls);
    }

    [Fact]
    public void 嚴重度_單頭詳情也帶嚴重度與重大旗標()
    {
        var order = AddOrder(100);
        AddMember(order, "H1", eventId: 100);
        Occurrence("H1", 100, High, elevates: true);

        var detail = Service(Admin()).Get(order);

        Assert.Equal("High", detail.Severity);
        Assert.True(detail.Critical);
    }

    // ══ H7 排序 ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 建 8 張單，建立時間讓「建立時間新到舊」剛好與預期順序相反（最後預期的最新）——排序若沒用到各個鍵，結果一定不同。
    /// 預期：被退回 → 逾期 → 高（有期限在前）→ 高（無期限）→ 中（有期限在前）→ 中（無期限）→ 低 → 推導不到。
    /// </summary>
    private (Dictionary<string, long> Ids, List<string> Expected) BuildPriorityFixture()
    {
        var ids = new Dictionary<string, long>();
        long Make(string key, int eventId, int age, int? rank, DateTime? due = null, string status = IssueHandlingStatuses.InProgress)
        {
            var order = AddOrder(eventId, ageDays: age);
            AddMember(order, $"H-{key}", status: status, due: due, eventId: eventId, note: key == "A" ? "請重新處理" : null);
            if (rank != null) Occurrence($"H-{key}", eventId, rank.Value);
            return ids[key] = order;
        }

        Make("A", 401, 30, Low);                            // 被退回（嚴重度低也排最前）
        Make("B", 402, 28, Low, due: Today.AddDays(-1));    // 逾期
        Make("H", 403, 26, High, due: Today.AddDays(5));    // 高、有期限
        Make("C", 404, 24, High);                           // 高、無期限
        Make("M2", 405, 22, Medium, due: Today.AddDays(2)); // 中、期限近
        Make("M0", 406, 20, Medium);                        // 中、無期限
        Make("L", 407, 18, Low, due: Today.AddDays(9));     // 低
        Make("N", 408, 16, null);                           // 推導不到嚴重度
        Event(ids["A"], WorkOrderEventActions.Returned, Today.AddDays(-1), "請重新處理");
        return (ids, new List<string> { "A", "B", "H", "C", "M2", "M0", "L", "N" });
    }

    [Fact]
    public void 優先序_各鍵各差一張單_順序固定_與建立時間順序相反()
    {
        var (ids, expected) = BuildPriorityFixture();

        var byCreated = Ids(List(new WorkOrderListRequest { Sort = "created_desc" }));
        var byPriority = Ids(List(new WorkOrderListRequest { Sort = "priority" }));

        Assert.Equal(expected.Select(k => ids[k]), byPriority);
        Assert.Equal(expected.Select(k => ids[k]).Reverse(), byCreated);   // 沒排序鍵時是最新的在前，證明上面不是巧合
    }

    [Fact]
    public void 優先序_同分的單依建立時間新到舊_單號降冪()
    {
        var older = AddOrder(501, ageDays: 9);
        var newer = AddOrder(502, ageDays: 3);
        var sameTimeLowId = AddOrder(503, ageDays: 3);
        foreach (var (order, eventId) in new[] { (older, 501), (newer, 502), (sameTimeLowId, 503) })
        {
            AddMember(order, $"HT{eventId}", eventId: eventId);
            Occurrence($"HT{eventId}", eventId, Medium);
        }

        var ids = Ids(List(new WorkOrderListRequest { Sort = "priority" }));

        Assert.Equal(new[] { sameTimeLowId, newer, older }, ids);
    }

    [Fact]
    public void 優先序_逾期含觀察到期_觀察期內不算逾期()
    {
        var expired = AddOrder(601, ageDays: 9);
        AddMember(expired, "HE", status: IssueHandlingStatuses.Observing, due: Today.AddDays(-1), eventId: 601);
        var active = AddOrder(602, ageDays: 3);
        AddMember(active, "HA", status: IssueHandlingStatuses.Observing, due: Today, eventId: 602);   // 觀察至今天＝仍在觀察期內

        var ids = Ids(List(new WorkOrderListRequest { Sort = "priority" }));

        Assert.Equal(new[] { expired, active }, ids);
    }

    [Fact]
    public void 期限最近_由近到遠_無期限最後_進行中成員沒有期限時退回單本身的期限()
    {
        var far = AddOrder(701, ageDays: 5);
        AddMember(far, "H1", due: Today.AddDays(10), eventId: 701);
        var near = AddOrder(702, ageDays: 6);
        AddMember(near, "H2", due: Today.AddDays(2), eventId: 702);
        var none = AddOrder(703, ageDays: 1);
        AddMember(none, "H3", eventId: 703);
        var mid = AddOrder(704, ageDays: 7);
        AddMember(mid, "H4", due: Today.AddDays(5), eventId: 704);
        var orderDue = AddOrder(705, ageDays: 8, dueDate: Today.AddDays(1));   // 成員沒有期限，單本身有
        AddMember(orderDue, "H5", eventId: 705);

        var ids = Ids(List(new WorkOrderListRequest { Sort = "dueSoon" }));

        Assert.Equal(new[] { orderDue, near, mid, far, none }, ids);
    }

    [Fact]
    public void 嚴重度排序_高到低_同級重大在前_推導不到最後_同分依建立時間()
    {
        var critical = AddOrder(801, ageDays: 20);
        AddMember(critical, "HC", eventId: 801);
        Occurrence("HC", 801, High, elevates: true);
        var high = AddOrder(802, ageDays: 1);
        AddMember(high, "HH", eventId: 802);
        Occurrence("HH", 802, High);
        var mediumNew = AddOrder(803, ageDays: 2);
        AddMember(mediumNew, "HM1", eventId: 803);
        Occurrence("HM1", 803, Medium);
        var mediumOld = AddOrder(804, ageDays: 8);
        AddMember(mediumOld, "HM2", eventId: 804);
        Occurrence("HM2", 804, Medium);
        var low = AddOrder(805, ageDays: 3);
        AddMember(low, "HL", eventId: 805);
        Occurrence("HL", 805, Low);
        var unknown = AddOrder(806, ageDays: 0);
        AddMember(unknown, "HU", eventId: 806);

        var ids = Ids(List(new WorkOrderListRequest { Sort = "severity" }));

        Assert.Equal(new[] { critical, high, mediumNew, mediumOld, low, unknown }, ids);
    }

    [Fact]
    public void 排序在分頁之前_最該先處理的單即使很舊也在第一頁_頁與頁相接等於全體順序()
    {
        var (ids, expected) = BuildPriorityFixture();
        var all = expected.Select(k => ids[k]).ToList();

        var p1 = List(new WorkOrderListRequest { Sort = "priority", Page = 1, PageSize = 3 });
        var p2 = List(new WorkOrderListRequest { Sort = "priority", Page = 2, PageSize = 3 });
        var p3 = List(new WorkOrderListRequest { Sort = "priority", Page = 3, PageSize = 3 });

        Assert.Equal(8, p1.Total);
        Assert.Equal(all.Take(3), Ids(p1));
        Assert.Equal(ids["A"], p1.Items[0].WorkOrderId);   // 建立時間最舊的那張，只排當頁的話會落在最後一頁
        Assert.Equal(all.Skip(3).Take(3), Ids(p2));
        Assert.Equal(all.Skip(6), Ids(p3));
    }

    [Fact]
    public void 排序鍵驗證_既有三個保留_未知鍵照現行擲驗證錯誤()
    {
        var a = AddOrder(901, ageDays: 5);
        AddMember(a, "HA", eventId: 901);
        var b = AddOrder(902, ageDays: 2);
        AddMember(b, "HB", eventId: 902);
        AddMember(b, "HB2", eventId: 902);

        Assert.Equal(new[] { b, a }, Ids(List(new WorkOrderListRequest { Sort = "created_desc" })));
        Assert.Equal(new[] { b, a }, Ids(List(new WorkOrderListRequest { Sort = "members_desc" })));
        Assert.Equal(new[] { a, b }, Ids(List(new WorkOrderListRequest { Sort = "unreplied_oldest" })));
        var ex = Assert.Throws<DomainException>(() => List(new WorkOrderListRequest { Sort = "bogus" }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    // ══ 清單列標記 ═══════════════════════════════════════════════════════════

    [Fact]
    public void 今天新增_昨天2359不算_今天0000算_同主機多成員只算一台()
    {
        var yesterdayOrder = AddOrder(1001);
        AddMember(yesterdayOrder, "HY", createdAt: Today.AddSeconds(-1), eventId: 1001);
        var todayOrder = AddOrder(1002);
        AddMember(todayOrder, "HT", createdAt: Today, eventId: 1002);
        var twoOnOneHost = AddOrder(1003);
        AddMember(twoOnOneHost, "HD", createdAt: Today.AddHours(3), eventId: 1003);
        AddMember(twoOnOneHost, "HD", createdAt: Today.AddHours(4), eventId: 1003);
        var closedToday = AddOrder(1004);
        AddMember(closedToday, "HZ", status: IssueHandlingStatuses.Resolved, createdAt: Today.AddHours(2), closedAt: Today.AddHours(5), eventId: 1004);

        var rows = List(new WorkOrderListRequest { Status = "all" }).Items.ToDictionary(r => r.WorkOrderId);

        Assert.Equal(0, rows[yesterdayOrder].NewTodayHosts);
        Assert.Equal(1, rows[todayOrder].NewTodayHosts);
        Assert.Equal(1, rows[twoOnOneHost].NewTodayHosts);
        Assert.Equal(0, rows[closedToday].NewTodayHosts);   // 已結案的成員不算進行中
        Assert.Equal(new[] { twoOnOneHost, todayOrder }.OrderBy(i => i),
            Ids(List(new WorkOrderListRequest { Flag = "newToday" })).OrderBy(i => i));
    }

    [Fact]
    public void 被退回_退回後處理人尚未回覆才算_已回覆不算_退回在回覆之後又算()
    {
        // A：退回後沒回覆，兩個成員的說明是退回說明；另一個夜間續掛新增的成員說明不同，不算
        var a = AddOrder(1101);
        AddMember(a, "A1", note: "請重新處理", eventId: 1101);
        AddMember(a, "A2", note: "請重新處理", eventId: 1101);
        AddMember(a, "A3", note: "夜間派工", eventId: 1101);
        Event(a, WorkOrderEventActions.Replied, Today.AddDays(-3));
        Event(a, WorkOrderEventActions.Returned, Today.AddDays(-2), "請重新處理");
        // B：退回後處理人已回覆
        var b = AddOrder(1102);
        AddMember(b, "B1", note: "已修", eventId: 1102);
        Event(b, WorkOrderEventActions.Returned, Today.AddDays(-3), "請重新處理");
        Event(b, WorkOrderEventActions.Replied, Today.AddDays(-2));
        // C：從來沒被退回
        var c = AddOrder(1103);
        AddMember(c, "C1", eventId: 1103);
        Event(c, WorkOrderEventActions.Replied, Today.AddDays(-2));
        // D：先退回、回覆、又被退回——最近一筆退回之後沒有回覆
        var d = AddOrder(1104);
        AddMember(d, "D1", note: "再退一次", eventId: 1104);
        Event(d, WorkOrderEventActions.Returned, Today.AddDays(-5), "第一次退回");
        Event(d, WorkOrderEventActions.Replied, Today.AddDays(-4));
        Event(d, WorkOrderEventActions.Returned, Today.AddDays(-1), "再退一次");
        // E：退回後處理人只回覆了其中一台，另一台說明仍是退回說明——整張單已回覆，不算被退回
        // （守住「之後有回覆就不算」：只靠說明比對時這台仍會被數到）
        var e = AddOrder(1105);
        AddMember(e, "E1", note: "請重新處理", eventId: 1105);
        AddMember(e, "E2", note: "已修", eventId: 1105);
        Event(e, WorkOrderEventActions.Returned, Today.AddDays(-3), "請重新處理");
        Event(e, WorkOrderEventActions.Replied, Today.AddDays(-2));

        var rows = List(new WorkOrderListRequest()).Items.ToDictionary(r => r.WorkOrderId);
        Assert.Equal(0, rows[e].ReturnedMembers);

        Assert.Equal(2, rows[a].ReturnedMembers);
        Assert.Equal(0, rows[b].ReturnedMembers);
        Assert.Equal(0, rows[c].ReturnedMembers);
        Assert.Equal(1, rows[d].ReturnedMembers);
        Assert.Equal(new[] { a, d }.OrderBy(i => i), Ids(List(new WorkOrderListRequest { Flag = "returned" })).OrderBy(i => i));
    }

    [Fact]
    public void 被退回_同一時間點的退回與回覆以事件序為準()
    {
        var order = AddOrder(1201);
        AddMember(order, "X1", note: "請重新處理", eventId: 1201);
        var at = Today.AddDays(-1);
        Event(order, WorkOrderEventActions.Replied, at);                          // 事件序較小：回覆在前
        Event(order, WorkOrderEventActions.Returned, at, "請重新處理");           // 事件序較大：退回在後

        Assert.Equal(1, Assert.Single(List(new WorkOrderListRequest()).Items).ReturnedMembers);
    }

    // ══ 標記篩選 ═════════════════════════════════════════════════════════════

    [Fact]
    public void 三天內到期_今天與今天加3天算_加4天與昨天不算_觀察中的日期不算()
    {
        long Make(int eventId, string host, DateTime? due, string status = IssueHandlingStatuses.InProgress)
        {
            var order = AddOrder(eventId);
            AddMember(order, host, status: status, due: due, eventId: eventId);
            return order;
        }
        var d0 = Make(1301, "D0", Today);
        var d3 = Make(1302, "D3", Today.AddDays(3));
        var d4 = Make(1303, "D4", Today.AddDays(4));
        var dm1 = Make(1304, "DM1", Today.AddDays(-1));
        var obs = Make(1305, "DO", Today.AddDays(2), IssueHandlingStatuses.Observing);
        var none = Make(1306, "DN", null);

        var hit = Ids(List(new WorkOrderListRequest { Flag = "dueSoon" })).OrderBy(i => i).ToList();

        Assert.Equal(new[] { d0, d3 }.OrderBy(i => i), hit);
        Assert.DoesNotContain(d4, hit);
        Assert.DoesNotContain(dm1, hit);
        Assert.DoesNotContain(obs, hit);
        Assert.DoesNotContain(none, hit);
    }

    [Fact]
    public void 觀察到期_觀察至昨天算_觀察至今天不算()
    {
        var expired = AddOrder(1401);
        AddMember(expired, "OE", status: IssueHandlingStatuses.Observing, due: Today.AddDays(-1), eventId: 1401);
        var stillObserving = AddOrder(1402);
        AddMember(stillObserving, "OS", status: IssueHandlingStatuses.Observing, due: Today, eventId: 1402);

        Assert.Equal(new[] { expired }, Ids(List(new WorkOrderListRequest { Flag = "observationExpired" })));
    }

    [Fact]
    public void 復發_上一件已處理且在30天內結案才算_超過30天不算()
    {
        var hit = AddOrder(1501);
        AddMember(hit, "R1", createdAt: Today.AddDays(-1), eventId: 1501);
        AddMember(null, "R1", status: IssueHandlingStatuses.Resolved, createdAt: Today.AddDays(-40), closedAt: Today.AddDays(-10), eventId: 1501);
        var miss = AddOrder(1502);
        AddMember(miss, "R2", createdAt: Today.AddDays(-1), eventId: 1502);
        AddMember(null, "R2", status: IssueHandlingStatuses.Resolved, createdAt: Today.AddDays(-80), closedAt: Today.AddDays(-32), eventId: 1502);

        var ids = Ids(List(new WorkOrderListRequest { Flag = "recurrence" }));

        Assert.Equal(new[] { hit }, ids);
        Assert.Equal(1, List(new WorkOrderListRequest { Status = "all" }).Items.Single(r => r.WorkOrderId == hit).RecurrenceHosts);
    }

    [Fact]
    public void 標記篩選與狀態篩選取交集_含todo_標記只看進行中成員()
    {
        // X：今天新增、但成員在觀察期內（不是待處理）
        var x = AddOrder(1601);
        AddMember(x, "X1", status: IssueHandlingStatuses.Observing, due: Today.AddDays(5), createdAt: Today.AddHours(1), eventId: 1601);
        // Y：今天新增、待處理
        var y = AddOrder(1602);
        AddMember(y, "Y1", createdAt: Today.AddHours(1), eventId: 1602);
        // Z：成員是上報中（不是待處理）也是今天新增
        var z = AddOrder(1603);
        AddMember(z, "Z1", status: IssueHandlingStatuses.Escalated, createdAt: Today.AddHours(1), eventId: 1603);
        // W：觀察到期（算待處理）
        var w = AddOrder(1604);
        AddMember(w, "W1", status: IssueHandlingStatuses.Observing, due: Today.AddDays(-1), eventId: 1604);

        Assert.Equal(new[] { x, y, z }.OrderBy(i => i), Ids(List(new WorkOrderListRequest { Flag = "newToday" })).OrderBy(i => i));
        Assert.Equal(new[] { y }, Ids(List(new WorkOrderListRequest { Status = "todo", Flag = "newToday" })));
        Assert.Equal(new[] { z }, Ids(List(new WorkOrderListRequest { Status = "escalated", Flag = "newToday" })));
        Assert.Equal(new[] { w }, Ids(List(new WorkOrderListRequest { Status = "todo", Flag = "observationExpired" })));
        Assert.Empty(Ids(List(new WorkOrderListRequest { Status = "closed", Flag = "newToday" })));
    }

    [Fact]
    public void 標記篩選_與排序可並用_不認得的標記擲驗證錯誤_大小寫不分()
    {
        var older = AddOrder(1701, ageDays: 9);
        AddMember(older, "F1", createdAt: Today.AddHours(1), eventId: 1701);
        var newer = AddOrder(1702, ageDays: 2);
        AddMember(newer, "F2", createdAt: Today.AddHours(2), eventId: 1702);
        AddMember(newer, "F3", createdAt: Today.AddHours(2), eventId: 1702);

        Assert.Equal(new[] { newer, older }, Ids(List(new WorkOrderListRequest { Flag = "NEWTODAY", Sort = "members_desc" })));
        Assert.Equal(new[] { older, newer }, Ids(List(new WorkOrderListRequest { Flag = "newToday", Sort = "unreplied_oldest" })));
        var ex = Assert.Throws<DomainException>(() => List(new WorkOrderListRequest { Flag = "nope" }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(2, List(new WorkOrderListRequest { Flag = "  " }).Total);   // 空白＝不篩
    }

    [Fact]
    public void 標記篩選_處理人入口_只列自己的單()
    {
        var mine = AddOrder(1801);
        AddMember(mine, "M1", createdAt: Today.AddHours(1), eventId: 1801);
        var bob = _users.Upsert(new WebUser { Account = "DOMAIN\\bob", DisplayName = "鮑伯" });
        var others = AddOrder(1802, handler: bob.UserId);
        AddMember(others, "M2", createdAt: Today.AddHours(1), eventId: 1802);

        var dto = Service(Alice()).ListForHandler(_alice.UserId, new WorkOrderListRequest { Flag = "newToday" });

        Assert.Equal(new[] { mine }, Ids(dto));
    }

    // ══ 我的交辦 KPI ═════════════════════════════════════════════════════════

    [Fact]
    public void KPI_今天需要你處理五個數字_等於清單各列同名欄位相加_不含已結案與暫停的單()
    {
        var a = AddOrder(1901);   // 今天新增 2 台、被退回 2 件、3 天內到期 1 件
        AddMember(a, "K1", createdAt: Today.AddHours(1), note: "請重新處理", due: Today.AddDays(3), eventId: 1901);
        AddMember(a, "K2", createdAt: Today.AddHours(2), note: "請重新處理", eventId: 1901);
        Event(a, WorkOrderEventActions.Returned, Today.AddHours(3), "請重新處理");
        var b = AddOrder(1902);   // 觀察到期 1 台、復發 1 台
        AddMember(b, "K3", status: IssueHandlingStatuses.Observing, due: Today.AddDays(-2), createdAt: Today.AddDays(-1), eventId: 1902);
        AddMember(null, "K3", status: IssueHandlingStatuses.Resolved, createdAt: Today.AddDays(-30), closedAt: Today.AddDays(-5), eventId: 1902);
        var closed = AddOrder(1903, closed: Today.AddDays(-1));   // 已結案：不算
        AddMember(closed, "K4", status: IssueHandlingStatuses.Resolved, createdAt: Today.AddHours(1), closedAt: Today.AddHours(2), eventId: 1903);
        var paused = AddOrder(1904);   // 暫停中的單（問題靜音）：不算
        AddMember(paused, "K5", createdAt: Today.AddHours(1), due: Today.AddDays(1), eventId: 1904);
        var exclusion = new FixedIssueExclusionSource(IssueExclusion.From(
            new List<IssueProfile>
            {
                new() { SourceName = "disk", EventId = 1904, Mutes = new List<MuteInterval> { new() { From = Today.AddDays(-1), To = Today.AddDays(3) } } }
            }, Today));

        var svc = Service(Alice(), exclusion);
        var summary = svc.HandlerSummary(_alice.UserId);
        var rows = svc.ListForHandler(_alice.UserId, new WorkOrderListRequest { PageSize = 100 }).Items;

        Assert.Equal(2, summary.NewTodayHosts);
        Assert.Equal(1, summary.RecurrenceHosts);
        Assert.Equal(2, summary.ReturnedMembers);
        Assert.Equal(1, summary.ObservationExpiredHosts);
        Assert.Equal(1, summary.DueSoonMembers);
        Assert.Equal(rows.Sum(r => r.NewTodayHosts), summary.NewTodayHosts);
        Assert.Equal(rows.Sum(r => r.RecurrenceHosts), summary.RecurrenceHosts);
        Assert.Equal(rows.Sum(r => r.ReturnedMembers), summary.ReturnedMembers);
        Assert.Equal(rows.Sum(r => r.ObservationExpiredHosts), summary.ObservationExpiredHosts);
    }

    [Fact]
    public void KPI_沒有任何單_全為0_既有存量欄位不受影響()
    {
        var summary = Service(Alice()).HandlerSummary(_alice.UserId);

        Assert.Equal(0, summary.NewTodayHosts + summary.RecurrenceHosts + summary.ReturnedMembers
            + summary.ObservationExpiredHosts + summary.DueSoonMembers);
        Assert.Equal(0, summary.ActiveWorkOrders);
    }

    [Fact]
    public void KPI_側欄徽章不填今天需要你處理_不多跑查詢()
    {
        var a = AddOrder(2001);
        AddMember(a, "B1", createdAt: Today.AddHours(1), eventId: 2001);

        var badge = Service(Alice()).MyBadge();

        Assert.Equal(1, badge.ActiveWorkOrders);
        Assert.Equal(0, badge.NewTodayHosts);
    }
}
