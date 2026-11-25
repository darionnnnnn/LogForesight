using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models.Dto;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 第一波（期限預填、復發、觀察中、不再打擾、已結案略過）的跨批次整合情境：一條時間線、全部經真實 HTTP 端點。
///
/// 時間線（H1、H2 同一個高嚴重度磁碟問題；h1 是派工池處理人；a 是管理員）：
/// D1 兩台出現 → 夜間自動派工建單 O1（預設期限）→ h1 整單回「已處理」；
/// D2 H1 再出現 → 新單 O2、成員標復發 → h1 對 H1 回「觀察中」（觀察至＝第 2 天＋3，即 D5 的「今天」）；
/// D3～D6 H1 只有無關的低嚴重度事件（主機日有紀錄、問題沒再發生）；
/// D4 H2 再出現 → 續掛進 O2 → h1 對 H2 回「不處理」（不再打擾）；D5 H2 又出現 → 被閘門擋下；
/// D6 觀察期滿 → 單頭列出「到期且沒有再發生」的 H1 → h1 以這些案件編號回「已處理」→ 管理員試跑與立即派工、人工交辦預覽與重開 H2 → 一致性檢查。
///
/// 各步驟的當日讀值由動作（<see cref="Call"/>）在當下擷取、存入 <c>sink</c>，因為探針只在當天結束時執行，
/// 而這些斷言要看的是「動作之前／之間」的狀態。使用情境驅動器的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class Wave1IntegrationScenarioTests
{
    private const string Disk = "disk";
    private const int DiskId = 153;

    // ── 輔助 ────────────────────────────────────────────────────────────────

    private static ScenarioExpectation Check(string key, int day, string description, string expected, Func<string> actual) =>
        Expect.Value(key, day, description, expected, _ =>
        {
            try
            {
                return actual();
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException
                                           or NullReferenceException or FormatException or JsonException)
            {
                return $"（讀不到：{ex.Message}）";
            }
        });

    /// <summary>第 <paramref name="nth"/> 張（0 起、依單號）disk 問題的單，含已結案的</summary>
    private static long OrderNo(ScenarioActionContext ctx, int nth)
    {
        var orders = OrderRef.AllOrders(ctx.Site.Get<IWorkOrderStore>())
            .Where(o => o.SourceName == Disk && o.EventId == DiskId).OrderBy(o => o.WorkOrderId).ToList();
        return nth < orders.Count ? orders[nth].WorkOrderId : throw new ScenarioUnresolvedException($"沒有第 {nth + 1} 張 disk 單");
    }

    private static ProbeView View(ActionHttpResult r) => new("", "", "", r.StatusCode, r.Body);

    private static ProbeView View(Dictionary<string, ActionHttpResult> sink, string key) =>
        sink.TryGetValue(key, out var r) ? View(r) : throw new KeyNotFoundException($"沒有擷取「{key}」（動作未執行或解析不到對象）");

    private static DateTime DateOf(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture).Date;

    private static string Flag(bool value) => value ? "是" : "否";

    /// <summary>在動作序列中執行一次擷取：結果（HTTP 或 store 讀值）存進 sink，同時也是當天的一個動作結果</summary>
    private sealed class Call(string actor, string label, Dictionary<string, ActionHttpResult> sink,
        Func<ScenarioActionContext, Task<ActionHttpResult>> run) : ScenarioAction(actor)
    {
        public override string Describe() => label;

        internal override async Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var result = await run(ctx);
            sink[label] = result;
            return result;
        }
    }

    private static ScenarioAction Get(Dictionary<string, ActionHttpResult> sink, string actor, string label,
        Func<ScenarioActionContext, string> url) =>
        new Call(actor, label, sink, ctx => ctx.Site.SendAsync(actor, HttpMethod.Get, url(ctx)));

    private static ScenarioAction Post(Dictionary<string, ActionHttpResult> sink, string actor, string label,
        Func<ScenarioActionContext, string> url, Func<ScenarioActionContext, object> body) =>
        new Call(actor, label, sink, ctx => ctx.Site.SendAsync(actor, HttpMethod.Post, url(ctx), body(ctx)));

    /// <summary>不打 HTTP、直接讀 store 的擷取（回傳文字放在 body）</summary>
    private static ScenarioAction Capture(Dictionary<string, ActionHttpResult> sink, string label, Func<ScenarioActionContext, string> read) =>
        new Call("a", label, sink, ctx => Task.FromResult(new ActionHttpResult(200, null, null, read(ctx))));

    private static IReadOnlyList<IssueCase> CasesOf(ScenarioActionContext ctx, long orderId) =>
        ctx.Site.Get<IIssueCaseStore>().GetByWorkOrder(orderId, 0, 500);

    private static IReadOnlyList<IssueHandling> RowsOf(ScenarioActionContext ctx, string caseId) =>
        ctx.Site.Get<IIssueHandlingStore>().GetByCase(caseId);

    /// <summary>某單某台主機的案件（單內同主機只會有一件進行中；這裡取最新建立的）</summary>
    private static IssueCase CaseOf(ScenarioActionContext ctx, long orderId, string host) =>
        CasesOf(ctx, orderId).Where(c => c.HostName == host).OrderByDescending(c => c.CreatedAt).First();

    private static string Members(ProbeView members) =>
        string.Join(",", members.Items("items").OrderBy(m => m.Str("hostName"), StringComparer.Ordinal)
            .Select(m => $"{m.Str("hostName")}:{m.Str("status")}"));

    /// <summary>
    /// 與 <c>LogForesight.Tests/tools/db-invariants.sql</c> 十項同語意的 store 版檢查；回傳違反的項目與筆數（全部為 0 時回空字串）。
    /// 專案目前沒有 C# 版，所以這裡自己實作；全部以站台的 store 查詢，與 SQL 的差別只在不直接讀表。
    /// </summary>
    private static string Invariants(ScenarioActionContext ctx)
    {
        var orderStore = ctx.Site.Get<IWorkOrderStore>();
        var caseStore = ctx.Site.Get<IIssueCaseStore>();
        var handlingStore = ctx.Site.Get<IIssueHandlingStore>();
        var orders = OrderRef.AllOrders(orderStore);
        var cases = orders.SelectMany(o => caseStore.GetByWorkOrder(o.WorkOrderId, 0, 500)).ToList();
        var ordersById = orders.ToDictionary(o => o.WorkOrderId);
        var rows = cases.ToDictionary(c => c.CaseId, c => handlingStore.GetByCase(c.CaseId));
        var open = cases.Where(c => c.ClosedAt == null).ToList();
        var closedStatuses = IssueHandlingStatuses.Closed;
        var activeStatuses = new[] { IssueHandlingStatuses.InProgress, IssueHandlingStatuses.Observing, IssueHandlingStatuses.Escalated };

        var violations = new List<(string Name, int Count)>
        {
            ("a 進行中案件但所屬單已結案或沒有單",
                open.Count(c => c.WorkOrderId is not { } id || !ordersById.TryGetValue(id, out var o) || o.ClosedAt != null)),
            ("b 進行中的單但沒有進行中成員",
                orders.Count(o => o.ClosedAt == null && !open.Any(c => c.WorkOrderId == o.WorkOrderId))),
            ("c 同主機同問題多件進行中案件",
                open.GroupBy(c => (HostNameKey.Of(c.HostName), c.IssueKey)).Count(g => g.Count() > 1)),
            ("d 同處理人同問題多張進行中單",
                orders.Where(o => o.ClosedAt == null).GroupBy(o => (o.HandlerId, o.SourceName, o.EventId)).Count(g => g.Count() > 1)),
            ("e 逐日列非結案但所屬案件已結案",
                cases.Where(c => c.ClosedAt != null).Sum(c => rows[c.CaseId].Count(h => activeStatuses.Contains(h.Status)))),
            ("f 逐日列已結案狀態但所屬案件仍進行中",
                open.Sum(c => rows[c.CaseId].Count(h => closedStatuses.Contains(h.Status)))),
            ("g 進行中案件沒有任何逐日列",
                open.Count(c => !c.DaySyncPending && rows[c.CaseId].Count == 0)),
            ("h 案件處理人與所屬單處理人不同",
                open.Count(c => c.WorkOrderId is { } id && ordersById.TryGetValue(id, out var o) && c.HandlerId != o.HandlerId)),
            ("i 案件狀態與結案時間矛盾",
                cases.Count(c => (c.ClosedAt == null && closedStatuses.Contains(c.Status))
                                 || (c.ClosedAt != null && (activeStatuses.Contains(c.Status) || c.Status == IssueHandlingStatuses.Open)))),
            ("j 建立後從未有過逐日列的案件（含已結案）",
                cases.Count(c => !c.DaySyncPending && rows[c.CaseId].Count == 0))
        };
        return string.Join("；", violations.Where(v => v.Count > 0).Select(v => $"{v.Name}={v.Count}"));
    }

    // ── 情境 ────────────────────────────────────────────────────────────────

    internal static Scenario Build(Dictionary<string, ActionHttpResult> s)
    {
        var scenario = new Scenario("W1 第一波整合")
            .WithHost(new HostSpec("H1") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("H2") { Groups = new[] { "G1" } })
            .WithUser(new UserSpec("a", UserRole.Admin) { HasEmail = true })
            .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true, HasEmail = true });

        // H1 的無關事件：低嚴重度、單筆，讓主機日有分析紀錄（觀察期間不是「無資料」），又不會與磁碟問題混淆
        List<ScenarioEvent> Unrelated() => new() { SeverityEvents.ServiceState7036(TimeSpan.FromHours(2)) };

        string Url(ScenarioActionContext ctx, int nth, string tail = "") => $"api/work-orders/{OrderNo(ctx, nth)}{tail}";

        // D1：兩台出現 → 自動派工建 O1；先擷取建單當下的狀態，再整單回已處理
        scenario.Day(1, d => d
            .Events("H1", ScenarioEvents.DiskErrors(12))
            .Events("H2", ScenarioEvents.DiskErrors(12))
            .Act(
                Get(s, "h1", "d1.detail", ctx => Url(ctx, 0)),
                Get(s, "h1", "d1.members", ctx => Url(ctx, 0, "/members?status=all&pageSize=50")),
                Capture(s, "d1.rows-before", ctx => string.Join(",", CasesOf(ctx, OrderNo(ctx, 0))
                    .OrderBy(c => c.HostName, StringComparer.Ordinal)
                    .Select(c => $"{c.HostName}:{Flag(RowsOf(ctx, c.CaseId).Count > 0)}"))),
                Post(s, "h1", "d1.reply", ctx => Url(ctx, 0, "/reply"),
                    _ => new WorkOrderReplyRequest { Status = "resolved", Note = "已更換硬碟", CaseIds = new List<string>() }),
                Capture(s, "d1.after", ctx => string.Join(",", CasesOf(ctx, OrderNo(ctx, 0))
                    .OrderBy(c => c.HostName, StringComparer.Ordinal)
                    .Select(c => $"{c.HostName}:{c.Status}:{(c.ClosedAt != null ? "closed" : "open")}:" +
                                 string.Join("/", RowsOf(ctx, c.CaseId).Select(r => r.Status).Distinct()))))));

        // D2：H1 再出現 → O2；擷取復發資訊 → h1 對 H1 回觀察中（觀察至＝第 2 天＋3 天，也就是 D5 的「今天」）
        scenario.Day(2, d => d
            .Events("H1", ScenarioEvents.DiskErrors(12))
            .Act(
                Get(s, "h1", "d2.detail", ctx => Url(ctx, 1)),
                Get(s, "h1", "d2.members", ctx => Url(ctx, 1, "/members?status=all&pageSize=50")),
                Post(s, "h1", "d2.reply", ctx => Url(ctx, 1, "/reply"),
                    ctx => new WorkOrderReplyRequest
                    {
                        Status = "observing", Note = "先觀察三天", DueDate = ctx.DataDateOf(2).AddDays(4),
                        CaseIds = CasesOf(ctx, OrderNo(ctx, 1)).Where(c => c.ClosedAt == null && c.HostName == "H1").Select(c => c.CaseId).ToList()
                    }))
            .Probe("summary", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders/summary")
            .Probe("todo", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=todo&pageSize=50")
            .Probe("active", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=active&pageSize=50")
            .Probe("members", "h1", ctx => Url(ctx, 1, "/members?status=all&pageSize=50")));

        scenario.Day(3, d => d.Events("H1", Unrelated()));

        // D4：H2 再出現 → 續掛進 O2（成員標復發）→ h1 對 H2 回不處理
        scenario.Day(4, d => d
            .Events("H1", Unrelated())
            .Events("H2", ScenarioEvents.DiskErrors(12))
            .Act(
                Get(s, "h1", "d4.members", ctx => Url(ctx, 1, "/members?status=all&pageSize=50")),
                Post(s, "h1", "d4.reply", ctx => Url(ctx, 1, "/reply"),
                    ctx => new WorkOrderReplyRequest
                    {
                        Status = "wont_fix", Note = "這台可接受，不用處理",
                        CaseIds = CasesOf(ctx, OrderNo(ctx, 1)).Where(c => c.ClosedAt == null && c.HostName == "H2").Select(c => c.CaseId).ToList()
                    }),
                Capture(s, "d4.h2-case", ctx =>
                {
                    var h2 = CaseOf(ctx, OrderNo(ctx, 1), "H2");
                    var hostCases = ctx.Site.Get<IIssueCaseStore>().GetMany(new[] { "H2" }).ToList();
                    var gated = DispatchContext.DismissedCases(hostCases, DateTime.Today, TestRules.None).Any(c => c.CaseId == h2.CaseId);
                    // BlocksRedispatch 為 null（處理人回覆沒有明確設定）也算擋住：判定是「不是 false」，與 DispatchContext.DismissedCases 同一條件
                    return $"{h2.Status}|{(h2.ClosedAt != null ? "closed" : "open")}|{(h2.BlocksRedispatch != false ? "blocks" : "free")}|{(gated ? "gated" : "not-gated")}";
                })));

        // D5：H2 又出現 → 閘門擋下；觀察至當天仍在觀察期內，不算到期
        scenario.Day(5, d => d
            .Events("H1", Unrelated())
            .Events("H2", ScenarioEvents.DiskErrors(12))
            .Probe("summary", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders/summary")
            .Probe("detail", "h1", ctx => Url(ctx, 1)));

        // D6：觀察期滿 → 以單頭的到期且沒有再發生清單回已處理 → 管理員試跑／立即派工／人工交辦
        string Range(ScenarioActionContext ctx) =>
            $"from={ctx.DataDateOf(1):yyyy-MM-dd}&to={ctx.DataDateOf(ctx.Day):yyyy-MM-dd}";

        object Create(ScenarioActionContext ctx, IEnumerable<long>? reopen) => new CreateWorkOrderRequest
        {
            Source = Disk, EventId = DiskId, From = ctx.DataDateOf(1), To = ctx.DataDateOf(ctx.Day),
            HostIds = new List<long> { ctx.HostId("H1"), ctx.HostId("H2") },
            AssignMode = "single", HandlerId = ctx.UserId("h1"), ScopeKind = WorkOrderScopes.Hosts,
            ReopenHostIds = reopen?.ToList()
        };

        scenario.Day(6, d => d
            .Events("H1", Unrelated())
            .Act(
                Get(s, "h1", "d6.detail", ctx => Url(ctx, 1)),
                Get(s, "h1", "d6.members", ctx => Url(ctx, 1, "/members?status=active&pageSize=50")),
                Capture(s, "d6.h1-case", ctx => CaseOf(ctx, OrderNo(ctx, 1), "H1").CaseId),
                Capture(s, "d6.h1-k", ctx => View(s, "d6.members").Items("items").Single(m => m.Str("hostName") == "H1")
                    .GetProperty("observedRecurDays").ToString()),
                Post(s, "h1", "d6.reply", ctx => Url(ctx, 1, "/reply"),
                    ctx => new WorkOrderReplyRequest
                    {
                        Status = "resolved", Note = "觀察期滿未再發生",
                        CaseIds = View(s, "d6.detail").Data!.Value.GetProperty("observationExpiredQuietCaseIds")
                            .EnumerateArray().Select(e => e.GetString()!).ToList()
                    }),
                Capture(s, "d6.after-reply", ctx => string.Join(",", CasesOf(ctx, OrderNo(ctx, 1))
                    .OrderBy(c => c.HostName, StringComparer.Ordinal).ThenBy(c => c.CreatedAt)
                    .Select(c => $"{c.HostName}:{c.Status}:{(c.ClosedAt != null ? "closed" : "open")}"))),
                Get(s, "a", "d6.gaps", ctx => $"api/work-orders/gaps?{Range(ctx)}"),
                Capture(s, "d6.orders-before-dispatch", ctx => OrderRef.AllOrders(ctx.Site.Get<IWorkOrderStore>()).Count.ToString(CultureInfo.InvariantCulture)),
                Post(s, "a", "d6.auto-dispatch", _ => "api/work-orders/auto-dispatch",
                    ctx => new AutoDispatchRequest { From = ctx.DataDateOf(1), To = ctx.DataDateOf(ctx.Day) }),
                Capture(s, "d6.orders-after-dispatch", ctx => OrderRef.AllOrders(ctx.Site.Get<IWorkOrderStore>()).Count.ToString(CultureInfo.InvariantCulture)),
                Post(s, "a", "d6.preview", _ => "api/work-orders/preview", ctx => Create(ctx, null)),
                Post(s, "a", "d6.create-empty", _ => "api/work-orders", ctx => Create(ctx, null)),
                Post(s, "a", "d6.preview-reopen", _ => "api/work-orders/preview", ctx => Create(ctx, new[] { ctx.HostId("H2") })),
                Post(s, "a", "d6.create-reopen", _ => "api/work-orders", ctx => Create(ctx, new[] { ctx.HostId("H2") })),
                Capture(s, "d6.old-h2-case", ctx =>
                {
                    var old = CasesOf(ctx, OrderNo(ctx, 1)).Single(c => c.HostName == "H2");
                    return old.BlocksRedispatch?.ToString() ?? "null";
                }),
                Capture(s, "d6.reopened-h2", ctx =>
                {
                    var cases = CasesOf(ctx, OrderNo(ctx, 2));
                    return string.Join(",", cases.Select(c => $"{c.HostName}:{c.Status}:{(c.ClosedAt != null ? "closed" : "open")}:rows={RowsOf(ctx, c.CaseId).Count}"));
                }),
                Capture(s, "d6.restore-audit", ctx => ctx.Site.Get<AuditLogStore>()
                    .Query(new AuditQuery { Ascending = true, Page = 1, PageSize = 200 }).Items
                    .Count(a => a.Action == AuditActions.WorkOrderDismissRestore).ToString(CultureInfo.InvariantCulture)),
                Capture(s, "d6.invariants", Invariants)));
        scenario.Days(6);

        // ── 斷言 ────────────────────────────────────────────────────────────
        // 步驟 1：夜間自動派工建單、預設期限、兩件案件各有逐日列
        scenario
            .Expect(Expect.NewOrdersFor(1, "h1", 1))
            .Expect(Check("W1.d1.due", 1, "步驟 1：單的預設期限＝建立日＋3 天（高嚴重度，DefaultDueDays.High）",
                DefaultDueDays.High.ToString(CultureInfo.InvariantCulture), () =>
                {
                    var data = View(s, "d1.detail").Data!.Value;
                    return (DateOf(data.Str("dueDate")) - DateOf(data.Str("createdAt"))).Days.ToString(CultureInfo.InvariantCulture);
                }))
            .Expect(Check("W1.d1.member-due", 1, "步驟 1：兩件案件的期限都等於單的期限", "H1,H2/是", () =>
            {
                var due = View(s, "d1.detail").Data!.Value.Str("dueDate");
                var items = View(s, "d1.members").Items("items");
                return string.Join(",", items.Select(m => m.Str("hostName")).OrderBy(x => x, StringComparer.Ordinal))
                       + "/" + Flag(items.All(m => m.Str("dueDate") == due));
            }))
            .Expect(Check("W1.d1.cases", 1, "步驟 1：單內兩件進行中案件（H1、H2）", "H1:in_progress,H2:in_progress",
                () => Members(View(s, "d1.members"))))
            .Expect(Check("W1.d1.rows-before", 1, "步驟 1：兩件案件各有逐日列", "H1:是,H2:是", () => s["d1.rows-before"].Body!));

        // 步驟 2：整單回覆（caseIds 為空）已處理
        scenario
            .Expect(Check("W1.d1.reply-status", 1, "步驟 2：整單回覆 HTTP 200", "200", () => s["d1.reply"].StatusCode.ToString(CultureInfo.InvariantCulture)))
            .Expect(Check("W1.d1.reply-result", 1, "步驟 2：成功內容＝處理 2 件、單已結案", "2/是", () =>
            {
                var data = View(s, "d1.reply").Data!.Value;
                return $"{data.Int("cases")}/{Flag(data.Flag("workOrderClosed"))}";
            }))
            .Expect(Check("W1.d1.after-reply", 1, "步驟 2：兩件案件結案、逐日列為已處理",
                "H1:resolved:closed:resolved,H2:resolved:closed:resolved", () => s["d1.after"].Body!));

        // 步驟 3：H1 再出現 → 新單、成員標復發、單頭復發主機數
        scenario
            .Expect(Expect.NewOrdersFor(2, "h1", 1))
            .Expect(Check("W1.d2.member-recurrence", 2, "步驟 3：成員標復發、上次回覆是第 1 天的已處理", "H1:是:resolved:已更換硬碟", () =>
            {
                var m = View(s, "d2.members").Items("items").Single();
                var last = m.GetProperty("lastReply");
                return $"{m.Str("hostName")}:{Flag(m.Flag("isRecurrence"))}:{last.Str("status")}:{last.Str("note")}";
            }))
            .Expect(Check("W1.d2.header", 2, "步驟 3：單頭復發主機數 1", "1",
                () => View(s, "d2.detail").Data!.Value.Int("recurrenceHosts").ToString(CultureInfo.InvariantCulture)));

        // 步驟 4：觀察中——摘要的觀察期內數、待我處理、todo 篩選
        scenario
            .Expect(Check("W1.d2.observing-reply", 2, "步驟 4：回觀察中 HTTP 200", "200", () => s["d2.reply"].StatusCode.ToString(CultureInfo.InvariantCulture)))
            .Expect(CheckDay("W1.d2.members-observing", 2, "步驟 4：H1 成員為觀察中、觀察至＝第 2 天＋3（D5 的今天）", "H1:observing/3", d =>
            {
                var m = d.Probe("members").Items("items").Single();
                var until = DateOf(m.Str("dueDate"));
                var firstToday = DateOf(View(s, "d2.detail").Data!.Value.Str("createdAt"));   // D2 的今天
                return $"{Members(d.Probe("members"))}/{(until - firstToday).Days}";
            }))
            .Expect(CheckDay("W1.d2.summary", 2, "步驟 4：我的交辦摘要 觀察期內 1、待我處理 0（兩者相加＝進行中 1）", "1/0/1", d =>
                $"{d.Probe("summary").Int("observingHosts")}/{d.Probe("summary").Int("todoHosts")}/{d.Probe("summary").Int("activeMembers")}"))
            .Expect(CheckDay("W1.d2.todo-list", 2, "步驟 4：todo 篩選不列這張單（單內只剩觀察期內成員）；active 篩選仍列", "0/1", d =>
                $"{d.Probe("todo").Items("items").Count}/{d.Probe("active").Items("items").Count}"));

        // 步驟 6（前段）：H2 續掛、回不處理 → 不再打擾閘門
        scenario
            .Expect(Expect.NewOrderCount(4, 0))
            .Expect(Check("W1.d4.attach", 4, "步驟 6：H2 再出現續掛進 O2（成員標復發）", "H1:observing,H2:in_progress/H2:是", () =>
            {
                var items = View(s, "d4.members").Items("items");
                return Members(View(s, "d4.members")) + "/H2:" + Flag(items.Single(m => m.Str("hostName") == "H2").Flag("isRecurrence"));
            }))
            .Expect(Check("W1.d4.reply", 4, "步驟 6：對 H2 回不處理 HTTP 200", "200", () => s["d4.reply"].StatusCode.ToString(CultureInfo.InvariantCulture)))
            .Expect(Check("W1.d4.dismissed", 4, "步驟 6：H2 案件以不處理結案、擋住再派（BlocksRedispatch 不是 false）、DispatchContext.DismissedCases 含它",
                "wont_fix|closed|blocks|gated", () => s["d4.h2-case"].Body!))
            .Expect(Expect.NewOrderCount(5, 0))
            .Expect(Expect.Decision(5, "H2", "disk", "skipped:gate_dismissed"));

        // 步驟 5：觀察到期且沒有再發生
        scenario
            .Expect(CheckDay("W1.d5.not-expired", 5, "步驟 5（前一天）：觀察至當天仍在觀察期內，不列為到期", "1/0/0", d =>
                $"{d.Probe("summary").Int("observingHosts")}/{d.Probe("summary").Int("observationExpiredHosts")}/{d.Probe("detail").Items("observationExpiredQuietCaseIds").Count}"))
            .Expect(Check("W1.d6.quiet-ids", 6, "步驟 5：到期隔天單頭 observationExpiredQuietCaseIds 恰為 H1 那件", "是", () =>
            {
                var ids = View(s, "d6.detail").Data!.Value.GetProperty("observationExpiredQuietCaseIds")
                    .EnumerateArray().Select(e => e.GetString()).ToList();
                return Flag(ids.Count == 1 && ids[0] == s["d6.h1-case"].Body);
            }))
            .Expect(Check("W1.d6.members-k", 6, "步驟 5：H1 的觀察期間再發生日數 K＝0（有主機日紀錄、問題沒再發生）", "0", () =>
                s["d6.h1-k"].Body!))
            .Expect(Check("W1.d6.reply", 6, "步驟 5：以這些案件編號回已處理「觀察期滿未再發生」→ 200、處理 1 件、單結案", "200/1/是", () =>
            {
                var r = View(s, "d6.reply");
                return $"{r.StatusCode}/{r.Data!.Value.Int("cases")}/{Flag(r.Data!.Value.Flag("workOrderClosed"))}";
            }))
            .Expect(Check("W1.d6.after-reply", 6, "步驟 5：H1 案件結案（已處理）、H2 維持不處理結案", "H1:resolved:closed,H2:wont_fix:closed", () => s["d6.after-reply"].Body!));

        // 步驟 7：待派試跑與立即派工
        scenario
            .Expect(Check("W1.d6.gaps-skip-reasons", 6, "步驟 7：待派試跑 disk 問題只列不再打擾 1 台（已結案的 H1 不是待派工作，不列入）", "gate_dismissed:1", () =>
            {
                var row = View(s, "d6.gaps").Items("rows").Single(r => r.Int("eventId") == DiskId);
                return string.Join(",", row.GetProperty("excluded").EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}:{p.Value.GetInt32()}"));
            }))
            .Expect(Check("W1.d6.gaps-no-suggestion", 6, "步驟 7：disk 問題的缺口只剩被擋的 H2（1 台），沒有任何建議處理人", "1/0", () =>
            {
                var row = View(s, "d6.gaps").Items("rows").Single(r => r.Int("eventId") == DiskId);
                return $"{row.Int("gapHosts")}/{row.GetProperty("suggested").GetArrayLength()}";
            }))
            .Expect(Check("W1.d6.auto-dispatch", 6, "步驟 7：立即派工不建單、不掛單、單數不變", "200/0/0/0/是", () =>
            {
                var r = View(s, "d6.auto-dispatch");
                var data = r.Data!.Value;
                return $"{r.StatusCode}/{data.Int("createdOrders")}/{data.Int("mergedOrders")}/{data.Int("assignedMembers")}/" +
                       Flag(s["d6.orders-before-dispatch"].Body == s["d6.orders-after-dispatch"].Body);
            }));

        // 步驟 8：人工交辦預覽與重開
        scenario
            .Expect(Check("W1.d6.preview", 6, "步驟 8：預覽 受影響 0 台、已結案略過 1、不再打擾略過 1", "200/0/1/1", () =>
            {
                var r = View(s, "d6.preview");
                var data = r.Data!.Value;
                return $"{r.StatusCode}/{data.Int("affectedHosts")}/{data.Int("closedSkippedHosts")}/{data.Int("dismissedSkippedHosts")}";
            }))
            .Expect(Check("W1.d6.preview-hosts", 6, "步驟 8：預覽主機列的略過原因 H1＝closed、H2＝dismissed", "H1:closed,H2:dismissed", () =>
                string.Join(",", View(s, "d6.preview").Items("hosts").OrderBy(h => h.Str("hostName"), StringComparer.Ordinal)
                    .Select(h => $"{h.Str("hostName")}:{h.Str("closedReason")}"))))
            .Expect(Check("W1.d6.create-empty", 6, "步驟 8：受影響 0 台時建立回驗證錯誤（400）", "400", () => s["d6.create-empty"].StatusCode.ToString(CultureInfo.InvariantCulture)))
            .Expect(Check("W1.d6.preview-reopen", 6, "步驟 8：帶 reopenHostIds=[H2] 預覽 受影響 1 台、已結案略過 1、不再打擾略過 0", "200/1/1/0", () =>
            {
                var r = View(s, "d6.preview-reopen");
                var data = r.Data!.Value;
                return $"{r.StatusCode}/{data.Int("affectedHosts")}/{data.Int("closedSkippedHosts")}/{data.Int("dismissedSkippedHosts")}";
            }))
            .Expect(Check("W1.d6.create-reopen", 6, "步驟 8：帶 reopenHostIds=[H2] 建立成功、新建 1 張單", "200/1/是", () =>
            {
                var r = View(s, "d6.create-reopen");
                var orders = r.Data!.Value.GetProperty("orders");
                return $"{r.StatusCode}/{orders.GetArrayLength()}/{Flag(orders[0].Flag("createdOrder"))}";
            }))
            .Expect(Check("W1.d6.reopened-h2", 6, "步驟 8：H2 建出進行中案件且有逐日列（只有 H2，沒有 H1）", "是", () =>
            {
                var body = s["d6.reopened-h2"].Body!;
                return Flag(body.StartsWith("H2:in_progress:open:rows=", StringComparison.Ordinal) && !body.Contains("H1", StringComparison.Ordinal)
                            && !body.EndsWith("rows=0", StringComparison.Ordinal));
            }))
            .Expect(Check("W1.d6.old-h2-unblocked", 6, "步驟 8：舊 H2 案件 BlocksRedispatch 改為 false", "False", () => s["d6.old-h2-case"].Body!))
            .Expect(Check("W1.d6.restore-audit", 6, "步驟 8：有恢復自動派工的稽核（1 筆）", "1", () => s["d6.restore-audit"].Body!));

        // 步驟 9：收尾一致性檢查
        scenario.Expect(Check("W1.d6.invariants", 6, "步驟 9：db-invariants 十項語意檢查全為 0（含進行中案件沒有逐日列、從未有過逐日列的案件）", "", () => s["d6.invariants"].Body!));

        return scenario;
    }

    private static ScenarioExpectation CheckDay(string key, int day, string description, string expected, Func<DayResult, string> actual) =>
        Expect.Value(key, day, description, expected, r =>
        {
            try
            {
                return actual(r.Day(day));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException
                                           or NullReferenceException or FormatException or JsonException)
            {
                return $"（讀不到：{ex.Message}）";
            }
        });

    [Fact]
    public void 第一波整合_預設期限到復發到觀察到不再打擾到已結案略過_全程經真實端點()
    {
        var sink = new Dictionary<string, ActionHttpResult>();
        ScenarioDriver.Run(Build(sink)).AssertExpectations();
    }
}
