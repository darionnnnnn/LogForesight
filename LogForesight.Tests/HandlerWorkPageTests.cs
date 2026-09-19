using System.Diagnostics;
using System.Text.RegularExpressions;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 處理人工作頁（我的交辦）以交辦單為中心：頁面結構（無頁籤、進階檢視收合、主區塊用詞、無實心主要按鈕）、
/// 回覆後就地更新（不重打 workload、不整頁 load）、跨頁勾選、篩選變更清勾選；
/// 後端期限端點（本人、日期範圍、只改單的期限＋一筆事件）與摘要的可見主機數。
/// 期限端點走 SQLite 暫存庫上的真 store；可見主機數走真的 <see cref="VisibilityService"/>。
/// </summary>
public class HandlerWorkPageTests : IDisposable
{
    // ── 前端結構 ─────────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    private static string ReadWeb(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "LogForesight.Web" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), $"找不到檔案: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string Page() => ReadWeb("Views", "Pages", "HandlerDetail.cshtml");

    private static string Js() => ReadWeb("wwwroot", "js", "pages", "handler-detail.js");

    /// <summary>取出頂層函式主體：自宣告起算到第一個位於行首的 '}' 為止（並確認切到非空）</summary>
    private static string ExtractTopLevel(string js, string declaration)
    {
        var start = js.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到宣告: {declaration}");
        var end = js.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到收尾: {declaration}");
        var body = js.Substring(start, end - start);
        Assert.False(string.IsNullOrWhiteSpace(body));
        return body;
    }

    [Fact]
    public void 頁面_沒有頁籤列_進階檢視收在details_主區塊不出現案件()
    {
        var page = Page();

        Assert.DoesNotContain("id=\"handler-tabs\"", page);
        Assert.Contains("<details", page);
        Assert.Contains("id=\"handler-advanced\"", page);
        Assert.Contains("進階檢視：依主機、被指派的風險日", page);
        Assert.Contains("每台主機上被交辦給你的問題，狀態會跟著交辦單同步", page);

        var main = page.Substring(0, page.IndexOf("<details", StringComparison.Ordinal));
        Assert.Contains("id=\"handler-wo-list\"", main);
        Assert.DoesNotContain("案件", main);
        // 依主機、風險日兩個區塊在進階檢視內
        var advanced = page.Substring(page.IndexOf("<details", StringComparison.Ordinal));
        Assert.Contains("id=\"handler-cases\"", advanced);
        Assert.Contains("id=\"handler-days\"", advanced);
    }

    [Fact]
    public void 頁面與腳本_沒有實心主要按鈕()
    {
        Assert.DoesNotContain("btn-primary", Page());
        Assert.DoesNotContain("btn-primary", Js());
        Assert.Contains("回覆選取的單（全部進行中主機）", Page());
    }

    [Fact]
    public void 就地更新_refreshAfterReply不打workload()
    {
        var body = ExtractTopLevel(Js(), "async function refreshAfterReply(");

        Assert.DoesNotContain("workload", body);
        Assert.Contains("work-orders/summary", body);
        Assert.Contains("window.scrollTo", body);
        Assert.Contains("Promise.all", body);
    }

    [Fact]
    public void 就地更新_每個回覆彈窗的onApplied都不呼叫整頁load()
    {
        var js = Js();
        var calls = Regex.Matches(js, @"openWorkOrderReplyModal\(\{");
        Assert.True(calls.Count >= 3, $"回覆彈窗呼叫數不如預期（{calls.Count}）");

        foreach (Match call in calls)
        {
            var onApplied = js.IndexOf("onApplied:", call.Index, StringComparison.Ordinal);
            Assert.True(onApplied > call.Index, "回覆彈窗呼叫沒有 onApplied");
            var end = js.IndexOf("});", onApplied, StringComparison.Ordinal);
            var handler = js.Substring(onApplied, end - onApplied);
            Assert.DoesNotMatch(new Regex(@"\bload\b"), handler);
            Assert.Contains("scheduleRefresh(", handler);
        }

        // workload 只在進階檢視載入時呼叫
        Assert.Single(Regex.Matches(js, @"/workload\?"));
        Assert.Contains("/workload?", ExtractTopLevel(js, "async function loadAdvanced("));
    }

    [Fact]
    public void 跨頁勾選_renderOrders不刪勾選_刪除只在取消勾選的單一出口()
    {
        var js = Js();
        var render = ExtractTopLevel(js, "function renderOrders(");
        Assert.DoesNotContain("selectedOrderIds.delete", render);
        Assert.DoesNotContain("of [...selectedOrderIds]", render);

        var deletes = Regex.Matches(js, @"selectedOrderIds\.delete\(");
        Assert.Single(deletes);
        Assert.Contains("selectedOrderIds.delete(id)", ExtractTopLevel(js, "function unselectOrder("));
        Assert.Contains("含其他頁", js);
        Assert.Contains("selectedOrderMeta", ExtractTopLevel(js, "function selectedInfo("));
    }

    [Fact]
    public void 篩選排序暫停變更_清除全部勾選並提示()
    {
        var js = Js();
        var start = js.IndexOf("const onOrderFilterChange = () => {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = js.Substring(start, js.IndexOf("\n};", start, StringComparison.Ordinal) - start);

        Assert.Contains("selectedOrderIds.clear()", body);
        Assert.Contains("篩選條件已變更，已清除勾選", body);
        Assert.Contains("statusSelect.addEventListener('change', onOrderFilterChange)", js);
        Assert.Contains("sortSelect.addEventListener('change', onOrderFilterChange)", js);
        Assert.Contains("pausedCheckbox.addEventListener('change', onOrderFilterChange)", js);
    }

    [Fact]
    public void 單內選取主機回覆_同一問題簽章才傳沿用鍵_且不提供下一張()
    {
        var panel = ExtractTopLevel(Js(), "function buildMemberPanel(");
        Assert.Contains("reuseIssueKey: issueKeys.size === 1 ? [...issueKeys][0] : null", panel);
        Assert.DoesNotContain("onNext", panel);

        var single = ExtractTopLevel(Js(), "function openSingleReply(");
        Assert.Contains("onNext:", single);
    }

    // ── 後端：期限端點（SQLite 暫存庫）───────────────────────────────────────

    private const string Source = "disk";
    private static readonly DateTime D2 = DateTime.Today.AddDays(-2);

    private readonly EfSqliteFixture _fx = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly RecordingAuditService _audit = new();
    private readonly EfWorkOrderStore _orders;
    private readonly EfIssueCaseStore _cases;
    private readonly WorkOrderCoordinator _coordinator;
    private readonly MailNotificationService _mail;
    private readonly WebUser _alice;
    private readonly WebUser _bob;

    public HandlerWorkPageTests()
    {
        _orders = new EfWorkOrderStore(_fx.NewContext);
        _cases = new EfIssueCaseStore(_fx.NewContext);
        var issueHandlings = new FakeIssueHandlingStore();
        var caseCoordinator = new IssueCaseCoordinator(_cases, issueHandlings, _handlingLog, _records, _hosts, new FakeIssueOwnerStore());
        _coordinator = new WorkOrderCoordinator(_orders, _cases, issueHandlings, caseCoordinator, _handlingLog, _hosts);

        _alice = _users.Upsert(new WebUser { Account = "DOMAIN\\alice", DisplayName = "愛麗絲", Active = true });
        _bob = _users.Upsert(new WebUser { Account = "DOMAIN\\bob", DisplayName = "鮑伯", Active = true });

        var freshness = new ScheduleFreshnessService(
            new BatchRunStore(_fx.LogStore("batch_runs"), _fx.LogStore("batch_run_logs")),
            new ScheduleOptionsStore(_fx.Blob("schedule_options")));
        _mail = new MailNotificationService(
            new FakeSystemSettingsStore(), new FakeSmtpMailSender(), _hosts, _users, new FakeUserGroupStore(), new FakeGroupAccessStore(),
            new FakeAnalysisRecordQuery(), _handlingLog, new MailNotifyStateStore(_fx.Blob("mail_notify_state")),
            freshness, new FakeIssueOwnerStore());

        foreach (var host in new[] { "H1", "H2" })
        {
            var h = _hosts.Upsert(new WebHost { HostName = host });
            _records.Add(new DailyAnalysisRecord
            {
                Date = D2, HostId = h.HostId, Host = host,
                TopIssues = new List<LogIssueSignature> { Signature(153) }
            });
        }
    }

    public void Dispose() { _fx.Dispose(); GC.SuppressFinalize(this); }

    private static LogIssueSignature Signature(int eventId) =>
        new() { LogName = "System", Source = Source, EventId = eventId, EntryType = EventLogEntryType.Error };

    private WorkOrderReplyController Controller(WebUser user) =>
        new(new WorkOrderReplyService(_coordinator, _orders, _cases, FakeCurrentUser.ForUser(user.UserId, Capability.Handle), _audit, _mail));

    private long CreateOrder(WebUser handler) =>
        _coordinator.Create(new WorkOrderCreateRequest
        {
            Source = Source, EventId = 153, IssueLabel = "disk 153", HandlerId = handler.UserId,
            Members = new[] { "H1", "H2" }.Select(h => new WorkOrderMember
            {
                HostName = h, IssueKey = IssueSignatureKey.For(Signature(153)), IssueLabel = "disk 153", TriggerDate = D2
            }).ToList(),
            Actor = new WorkOrderActor { ActorId = handler.UserId, ActorAccount = handler.Account, OccurredAt = DateTime.Now.AddMinutes(-5) }
        }).WorkOrderId;

    private string Members(long id) => string.Join(";", _cases.GetByWorkOrder(id, 0, 100)
        .OrderBy(c => c.HostName).Select(c => $"{c.HostName}:{c.Status}:{c.DueDate}:{c.Note}:{c.ClosedAt}"));

    [Fact]
    public void 期限端點_他人的單_403_零寫入()
    {
        var id = CreateOrder(_alice);
        var events = _orders.ListEvents(id).Count;

        var ex = Assert.Throws<DomainException>(() =>
            Controller(_bob).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = DateTime.Today.AddDays(3) }));

        Assert.Equal(ApiErrorCodes.Forbidden, ex.Code);
        Assert.Null(_orders.Get(id)!.DueDate);
        Assert.Equal(events, _orders.ListEvents(id).Count);
    }

    [Fact]
    public void 期限端點_早於今天或超過90天_400_零寫入()
    {
        var id = CreateOrder(_alice);
        var events = _orders.ListEvents(id).Count;

        var past = Assert.Throws<DomainException>(() =>
            Controller(_alice).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = DateTime.Today.AddDays(-1) }));
        var far = Assert.Throws<DomainException>(() =>
            Controller(_alice).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = DateTime.Today.AddDays(91) }));

        Assert.Equal(ApiErrorCodes.ValidationFailed, past.Code);
        Assert.Equal(ApiErrorCodes.ValidationFailed, far.Code);
        Assert.Null(_orders.Get(id)!.DueDate);
        Assert.Equal(events, _orders.ListEvents(id).Count);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public void 期限端點_成功_只改單的期限_多一筆事件與稽核_成員不變()
    {
        var id = CreateOrder(_alice);
        var membersBefore = Members(id);
        var eventsBefore = _orders.ListEvents(id).Count;
        var due = DateTime.Today.AddDays(7);

        var result = Controller(_alice).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = due }).Data!;

        Assert.Equal(due, result.DueDate);
        Assert.Equal(due, _orders.Get(id)!.DueDate);
        var events = _orders.ListEvents(id);
        Assert.Equal(eventsBefore + 1, events.Count);
        var evt = Assert.Single(events, e => e.Action == WorkOrderEventActions.DueDateChanged);
        Assert.Equal($"期限 無→{due:yyyy-MM-dd}", evt.Note);
        Assert.Equal(_alice.UserId, evt.ActorId);
        Assert.Equal(membersBefore, Members(id));
        Assert.Equal(AuditActions.WorkOrderDueDate, Assert.Single(_audit.Entries).Action);

        // 清除期限（null）：再一筆事件；同值重送不再寫事件
        Controller(_alice).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = null });
        Controller(_alice).ChangeDueDate(id, new WorkOrderDueDateRequest { DueDate = null });
        Assert.Null(_orders.Get(id)!.DueDate);
        Assert.Equal(2, _orders.ListEvents(id).Count(e => e.Action == WorkOrderEventActions.DueDateChanged));
        Assert.Equal(membersBefore, Members(id));
    }

    /// <summary>稽核動作的中文名稱由 AuditQueryServiceTests 的反射守門涵蓋；這裡釘住時間軸用詞</summary>
    [Fact]
    public void 期限事件的時間軸中文為修改期限()
    {
        Assert.Equal("修改期限", WorkOrderTextHelpers.ActionText(WorkOrderEventActions.DueDateChanged));
    }

    // ── 後端：摘要的可見主機數（真的 VisibilityService）────────────────────

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void 摘要_可見主機數_沒有任何授權的處理人為0(bool granted, int expected)
    {
        var users = new FakeUserStore();
        var userGroups = new FakeUserGroupStore();
        var access = new FakeGroupAccessStore();
        var hosts = new FakeHostStore();
        var group = userGroups.Upsert(new UserGroup { GroupName = "A 部門", Role = UserRole.User });
        var user = users.Upsert(new WebUser { Account = "DOMAIN\\a", DisplayName = "甲", Active = true, GroupIds = new List<long> { group.GroupId } });
        hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } });
        hosts.Upsert(new WebHost { HostName = "SRV-B", GroupIds = new List<long> { 20 } });
        if (granted)
            access.ReplaceAll(new[] { new GroupAccess { UserGroupId = group.GroupId, HostGroupId = 10 } });

        var settings = new FakeSystemSettingsStore();
        var currentUser = FakeCurrentUser.ForUser(user.UserId, Capability.Handle);
        var visibility = new VisibilityService(currentUser, users, userGroups, access, hosts,
            new FakeIssueCaseStore(), settings, new FakeIssueOwnerStore(), new FakeIssueAggregateQuery());
        var caseStore = new FakeIssueCaseStore();
        var svc = new WorkOrderQueryService(new FakeWorkOrderStore(caseStore), caseStore, users, hosts, new FakeHostGroupStore(),
            new FakeRuleStore(), visibility, currentUser, new UserDisplayNameService(settings),
            new FixedIssueExclusionSource(IssueExclusion.None), userGroups);

        var summary = svc.HandlerSummary(user.UserId);

        Assert.Equal(expected, summary.VisibleHostCount);
        Assert.Equal("DOMAIN\\a", summary.Account);
        Assert.True(summary.Active);
    }
}
