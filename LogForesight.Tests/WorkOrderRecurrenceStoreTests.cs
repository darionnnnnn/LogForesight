using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 階段 R-1 新增與改動的 store 查詢：SQLite 合約測試（結果）＋SQL Server <c>ToQueryString</c> 翻譯測試（未連線實際 SQL Server）。
/// </summary>
public sealed class WorkOrderRecurrenceStoreTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 11, 20);
    private static readonly string DiskKey = IssueSignatureKey.For("System", "disk", 153, LogEntryType.Error);

    private readonly EfSqliteFixture _fx = new();
    private int _seq;

    public void Dispose() => _fx.Dispose();

    private EfIssueCaseStore Cases() => new(_fx.NewContext);
    private EfWorkOrderStore Orders() => new(_fx.NewContext, new FixedClock(Today.AddHours(10)));

    private sealed class FixedClock(DateTime local) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    private long NewOrder(long handler = 1, int eventId = 153) => Orders().Insert(new WorkOrder
    {
        SourceName = "disk", EventId = eventId, IssueLabel = $"disk {eventId}", HandlerId = handler, Origin = WorkOrderOrigins.Manual,
        ScopeKind = WorkOrderScopes.Hosts, CreatedByAccount = "admin", CreatedAt = Today.AddDays(-30)
    });

    private IssueCase Add(string host, string status, DateTime created, DateTime? closed = null, long? orderId = null, int eventId = 153,
        string source = "disk", DateTime? due = null, bool cancelled = false, string? note = null, long handler = 1)
    {
        var c = new IssueCase
        {
            CaseId = $"case-{++_seq:D4}", HostName = host, IssueKey = IssueSignatureKey.For("System", source, eventId, LogEntryType.Error),
            IssueLabel = $"{source} {eventId}", Status = status, HandlerId = handler, DueDate = due, FirstLinkedDate = created.Date,
            LastLinkedDate = created.Date, CreatedAt = created, CreatedByAccount = "admin", UpdatedAt = created, ClosedAt = closed,
            WorkOrderId = orderId, Cancelled = cancelled, Note = note
        };
        Cases().Save(c);
        return c;
    }

    // ── GetActiveMemberSummaries ─────────────────────────────────────────────

    [Fact]
    public void 進行中成員精簡查詢_只回進行中且屬於單的_指定單號時只回那幾張_精簡欄位仍帶判定要的值()
    {
        var o1 = NewOrder(1, 153);
        var o2 = NewOrder(2, 154);
        var active = Add("h1", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: o1, due: Today, note: "觀察看看");
        Add("h2", IssueHandlingStatuses.Resolved, Today.AddDays(-9), Today.AddDays(-5), orderId: o1);
        Add("h3", IssueHandlingStatuses.InProgress, Today.AddDays(-2), orderId: o2, eventId: 154);
        Add("h4", IssueHandlingStatuses.InProgress, Today.AddDays(-2));   // 沒掛交辦單

        var all = Cases().GetActiveMemberSummaries(null);
        var onlyO1 = Cases().GetActiveMemberSummaries(new[] { o1 });

        Assert.Equal(new[] { "h1", "h3" }, all.Select(c => c.HostName).OrderBy(x => x).ToArray());
        var one = Assert.Single(onlyO1);
        Assert.Equal(active.CaseId, one.CaseId);
        Assert.Equal((IssueHandlingStatuses.Observing, Today, "觀察看看", o1), (one.Status, one.DueDate, one.Note, one.WorkOrderId));
        Assert.Equal(("disk", 153), (one.SourceName, one.EventId));
        Assert.Empty(Cases().GetActiveMemberSummaries(Array.Empty<long>()));
    }

    // ── GetClosedHistory ─────────────────────────────────────────────────────

    [Fact]
    public void 前案候選_只回已結案未取消_主機與問題相符_結案時間下限含界()
    {
        var hit = Add("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15));
        Add("H1", IssueHandlingStatuses.WontFix, Today.AddDays(-40), Today.AddDays(-35));
        Add("H1", IssueHandlingStatuses.InProgress, Today.AddDays(-2));                                         // 未結案
        Add("H1", IssueHandlingStatuses.Open, Today.AddDays(-30), Today.AddDays(-29), cancelled: true);         // 取消
        Add("H2", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15));                       // 別台主機
        Add("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15), eventId: 154);        // 別的事件
        Add("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15), source: "ntfs");      // 別的來源

        var all = Cases().GetClosedHistory(new[] { "h1" }, new[] { ("DISK", 153) }, null);
        var since = Cases().GetClosedHistory(new[] { "h1" }, new[] { ("disk", 153) }, Today.AddDays(-35));
        var tooLate = Cases().GetClosedHistory(new[] { "h1" }, new[] { ("disk", 153) }, Today.AddDays(-14));

        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { IssueHandlingStatuses.Resolved, IssueHandlingStatuses.WontFix }, all.Select(c => c.Status).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, since.Count);                        // 下限 −35 含界：結案 −35 那件仍在
        Assert.Equal(new[] { hit.CaseId }, Cases().GetClosedHistory(new[] { "h1" }, new[] { ("disk", 153) }, Today.AddDays(-36)).Where(c => c.Status == IssueHandlingStatuses.Resolved).Select(c => c.CaseId).ToArray());
        Assert.Empty(tooLate);
        Assert.Empty(Cases().GetClosedHistory(Array.Empty<string>(), new[] { ("disk", 153) }, null));
        Assert.Empty(Cases().GetClosedHistory(new[] { "h1" }, Array.Empty<(string, int)>(), null));
    }

    [Fact]
    public void 前案候選_事件編號超過預篩上限時不在SQL端篩_結果仍精確()
    {
        Add("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15), eventId: 153);
        Add("H1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15), eventId: 9999);
        var issues = Enumerable.Range(1000, 150).Select(i => ("disk", i)).Append(("disk", 153)).ToList();

        var result = Cases().GetClosedHistory(new[] { "H1" }, issues, null);

        Assert.Equal(153, Assert.Single(result).EventId);
    }

    // ── CountDistinctHosts ───────────────────────────────────────────────────

    [Fact]
    public void 不重複主機數_含已結案成員_主機名不分大小寫去重_零成員的單不出現()
    {
        var o1 = NewOrder(1, 153);
        var empty = NewOrder(2, 154);
        Add("h1", IssueHandlingStatuses.Resolved, Today.AddDays(-20), Today.AddDays(-15), orderId: o1);
        Add("H1", IssueHandlingStatuses.InProgress, Today.AddDays(-2), orderId: o1);
        Add("h2", IssueHandlingStatuses.InProgress, Today.AddDays(-2), orderId: o1);

        var result = Cases().CountDistinctHosts(new[] { o1, empty });

        Assert.Equal(2, result[o1]);
        Assert.False(result.ContainsKey(empty));
    }

    // ── 成員「全部」篩選排序 ─────────────────────────────────────────────────

    [Fact]
    public void QueryMembers全部_依主機鍵再建立日新到舊再案件編號_進行中維持主機鍵再案件編號()
    {
        var o = NewOrder();
        var a1 = Add("HOST-A", IssueHandlingStatuses.Resolved, Today.AddDays(-30), Today.AddDays(-28), orderId: o);
        var a2 = Add("HOST-A", IssueHandlingStatuses.InProgress, Today.AddDays(-1), orderId: o);
        var b = Add("HOST-B", IssueHandlingStatuses.InProgress, Today.AddDays(-9), orderId: o);

        string[] Ids(string status, string[]? hosts = null) => Cases().QueryMembers(new WorkOrderMemberQuery
            { WorkOrderId = o, Status = status, HostNameKeys = hosts, Page = 1, PageSize = 50 }).Items.Select(c => c.CaseId).ToArray();

        Assert.Equal(new[] { a2.CaseId, a1.CaseId, b.CaseId }, Ids(WorkOrderQueries.StatusAll));
        Assert.Equal(new[] { a2.CaseId, a1.CaseId }, Ids(WorkOrderQueries.StatusAll, new[] { "HOST-A" }));
        Assert.Equal(new[] { a2.CaseId, b.CaseId }, Ids(WorkOrderQueries.StatusActive));   // HostNameKey 順序：HOST-A、HOST-B
    }

    // ── 處理人摘要的三個互斥數字、todo 篩選 ────────────────────────────────────

    [Fact]
    public void 處理人摘要_待處理_觀察期內_上報中_互斥相加等於進行中成員數_停用主機不計()
    {
        var o = NewOrder();
        Add("h1", IssueHandlingStatuses.InProgress, Today.AddDays(-3), orderId: o);
        Add("h2", IssueHandlingStatuses.Open, Today.AddDays(-3), orderId: o);
        Add("h3", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: o, due: Today);                // 期內（邊界）
        Add("h4", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: o, due: Today.AddDays(-1));    // 到期
        Add("h5", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: o);                            // 沒有觀察至：不算期內
        Add("h6", IssueHandlingStatuses.Escalated, Today.AddDays(-3), orderId: o);
        Add("off", IssueHandlingStatuses.Escalated, Today.AddDays(-3), orderId: o);

        var s = Orders().HandlerSummary(1, Array.Empty<string>(), new[] { "OFF" });

        Assert.Equal(6, s.ActiveMembers);
        Assert.Equal(4, s.TodoMembers);       // h1、h2、h4、h5
        Assert.Equal(1, s.ObservingMembers);  // h3
        Assert.Equal(1, s.EscalatedMembers);  // h6
        Assert.Equal(s.ActiveMembers, s.TodoMembers + s.ObservingMembers + s.EscalatedMembers);
    }

    [Fact]
    public void QueryOrders_todo_至少一個待處理成員才列_與替身的IsTodoMember同義()
    {
        var observingOnly = NewOrder(1, 201);
        Add("a1", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: observingOnly, eventId: 201, due: Today);
        var expired = NewOrder(2, 202);
        Add("b1", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: expired, eventId: 202, due: Today.AddDays(-1));
        var escalated = NewOrder(3, 203);
        Add("c1", IssueHandlingStatuses.Escalated, Today.AddDays(-3), orderId: escalated, eventId: 203);
        var mixed = NewOrder(4, 204);
        Add("d1", IssueHandlingStatuses.Observing, Today.AddDays(-3), orderId: mixed, eventId: 204, due: Today.AddDays(2));
        Add("d2", IssueHandlingStatuses.Open, Today.AddDays(-3), orderId: mixed, eventId: 204);
        var onDisabled = NewOrder(5, 205);
        Add("off", IssueHandlingStatuses.InProgress, Today.AddDays(-3), orderId: onDisabled, eventId: 205);

        var page = Orders().QueryOrders(new WorkOrderQuery
        {
            Status = WorkOrderQueries.StatusTodo, ExcludedHostKeys = new[] { "OFF" }, Page = 1, PageSize = 100
        });

        Assert.Equal(new[] { expired, mixed }, page.Items.Select(o => o.WorkOrderId).OrderBy(x => x).ToArray());
        Assert.Equal(2, page.Total);
    }

    // ── 觀察開始時間 ─────────────────────────────────────────────────────────

    [Fact]
    public void 觀察開始時間_取該案件觀察中逐日列最早的更新時間_其他狀態與其他案件不影響()
    {
        var store = new EfIssueHandlingStore(_fx.NewContext);
        var start = Today.AddDays(-5).AddHours(9);
        store.SaveMany(new[]
        {
            Row("c1", Today.AddDays(-9), IssueHandlingStatuses.Observing, start),
            Row("c1", Today.AddDays(-4), IssueHandlingStatuses.Observing, start.AddDays(2)),   // 夜間掛接晚一點追加的日子
            Row("c1", Today.AddDays(-8), IssueHandlingStatuses.InProgress, start.AddDays(-4)), // 其他狀態不算
            Row("c2", Today.AddDays(-9), IssueHandlingStatuses.Observing, start.AddDays(1)),
            Row("c3", Today.AddDays(-9), IssueHandlingStatuses.Resolved, start)
        });

        var result = store.GetObservationStarts(new[] { "c1", "c2", "c3", "none" });

        Assert.Equal(start, result["c1"]);
        Assert.Equal(start.AddDays(1), result["c2"]);
        Assert.False(result.ContainsKey("c3"));
        Assert.False(result.ContainsKey("none"));
        Assert.Empty(store.GetObservationStarts(Array.Empty<string>()));
    }

    private static IssueHandling Row(string caseId, DateTime date, string status, DateTime updatedAt) => new()
    {
        HostName = "H-" + caseId, Date = date, IssueKey = DiskKey, Status = status, CaseId = caseId, UpdatedAt = updatedAt,
        DueDate = status == IssueHandlingStatuses.Observing ? Today : null
    };

    // ── SQL Server 翻譯（未連線實際 SQL Server）──────────────────────────────

    private static LfDbContext SqlServerContext() => new(new DbContextOptionsBuilder<LfDbContext>()
        .UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;").Options);

    [Fact]
    public void SqlServer翻譯_進行中成員精簡查詢_只選精簡欄位_不讀PRTG證據與逐日意圖()
    {
        using var ctx = SqlServerContext();

        var all = EfIssueCaseStore.BuildActiveMemberSummaryQuery(ctx, null).ToQueryString();
        var some = EfIssueCaseStore.BuildActiveMemberSummaryQuery(ctx, new long[] { 1, 2 }).ToQueryString();

        Assert.Contains("[closed_at] IS NULL", all);
        Assert.DoesNotContain("prtg_evidence_json", all);
        Assert.DoesNotContain("day_sync_intent", all);
        Assert.DoesNotContain("[work_order_id] IN", all);
        Assert.Contains("[work_order_id] IN (", some);
    }

    [Fact]
    public void SqlServer翻譯_前案候選查詢_主機鍵IN清單_已結案未取消_事件預篩與結案下限可選()
    {
        using var ctx = SqlServerContext();

        var full = EfIssueCaseStore.BuildClosedHistoryQuery(ctx, new[] { "H1", "H2" }, new[] { 153, 154 }, new DateTime(2026, 10, 1)).ToQueryString();
        var bare = EfIssueCaseStore.BuildClosedHistoryQuery(ctx, new[] { "H1" }, null, null).ToQueryString();

        Assert.Contains("[host_name_key] IN (", full);
        Assert.Contains("[closed_at] IS NOT NULL", full);
        Assert.Contains("[cancelled] = CAST(0 AS bit)", full);
        Assert.Contains("[event_id] IN (", full);
        Assert.Contains("[closed_at] >=", full);
        Assert.DoesNotContain("[event_id] IN (", bare);
        Assert.DoesNotContain("[closed_at] >=", bare);
        Assert.DoesNotContain("UPPER(", full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer翻譯_不重複主機數_是GROUP_BY加COUNT_DISTINCT()
    {
        using var ctx = SqlServerContext();

        var sql = EfIssueCaseStore.BuildDistinctHostsQuery(ctx, new long[] { 1, 2 }).ToQueryString();

        Assert.Contains("GROUP BY", sql);
        Assert.Contains("COUNT(DISTINCT", sql);
        Assert.Contains("[host_name_key]", sql);
    }

    [Fact]
    public void SqlServer翻譯_觀察開始時間_GROUP_BY_case_id取MIN更新時間()
    {
        using var ctx = SqlServerContext();

        var sql = EfIssueHandlingStore.BuildObservationStartQuery(ctx, new[] { "c1", "c2" }).ToQueryString();

        Assert.Contains("MIN(", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("[case_id] IN (", sql);
        Assert.Contains("observing", sql);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void 兩後端翻譯_交辦清單todo篩選與處理人摘要新欄位(string provider)
    {
        var builder = new DbContextOptionsBuilder<LfDbContext>();
        if (provider == "sqlserver") builder.UseSqlServer("Server=.;Database=LfTranslateOnly;Trusted_Connection=True;");
        else builder.UseSqlite("Data Source=:memory:");
        using var ctx = new LfDbContext(builder.Options);

        var todo = EfWorkOrderStore.BuildOrderFilterQuery(ctx,
            new WorkOrderQuery { Status = WorkOrderQueries.StatusTodo, ExcludedHostKeys = new[] { "OFF" } }, Today).ToQueryString();
        var summary = EfWorkOrderStore.BuildHandlerSummaryQuery(ctx, 1, new[] { "DISK|153" }, new FixedClock(Today.AddHours(10)), new[] { "OFF" }).ToQueryString();

        Assert.Contains("observing", todo);
        Assert.Contains("escalated", todo);
        Assert.Contains("due_date", todo);
        Assert.Contains("observing", summary);
        Assert.Contains("escalated", summary);
    }
}
