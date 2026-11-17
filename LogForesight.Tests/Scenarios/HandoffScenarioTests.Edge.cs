using System.Text.Json;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

// S11～S18：派不出去、同時操作、期限、沒有 email、台數一致、優先序、角色、併單。
public sealed partial class HandoffScenarioTests
{
    private static Scenario BuildEdge(string key) => key switch
    {
        "S11無派工池" => BuildS11("no_pool"),
        "S11全部暫停" => BuildS11("all_paused"),
        "S11都看不到" => BuildS11("no_visibility"),
        "S12" => BuildS12(),
        "S13前置" => BuildS13(),
        "S13到期" => BuildS13Due(),
        "S14處理人無email" => BuildS14Handler(),
        "S14管理者無email" => BuildS14Admins(),
        "S15" => BuildS15(),
        "S16" => BuildS16(),
        "S17" => BuildS17(),
        "S18" => BuildS18(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "沒有這個情境")
    };

    // ── S11：夜間派工找不到人 ───────────────────────────────────────────────

    /// <summary>reason：no_pool（沒人在派工池）／all_paused（派工池全部暫停）／no_visibility（池內的人都看不到這台）</summary>
    internal static Scenario BuildS11(string reason)
    {
        var s = new Scenario($"S11 派不出去（{reason}）")
            .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
            .WithUser(Admin("admin1"));
        switch (reason)
        {
            case "no_pool": s.WithUser(Handler("h1", new[] { "G1" }, pool: false)); break;
            case "all_paused": s.WithUser(Handler("h1", new[] { "G1" }, paused: true)); break;
            default: s.WithUser(Handler("h1", new[] { "G2" })); break;
        }

        var resumes = reason == "all_paused";
        s.Day(1, d =>
            {
                d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
                    .Probe("gaps", "admin1", Gaps)
                    .Probe("admin-unread", "admin1", "api/work-orders/unread-count");
            })
            .Day(2, d =>
            {
                d.Events("HOST-A", ScenarioEvents.DiskErrors(12));
                // 派工池全部暫停的情境：第 2 天夜裡仍派不出去，管理者事後恢復接單，驗證第 3 天再試就派得出去
                if (resumes) d.Act(ScenarioActions.SetDispatchPaused("admin1", "h1", false));
            })
            .Day(3, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
            .Days(resumes ? 3 : 2)
            .Expect(Check("S11.d1.no-order", 1, "第 1 天沒有人能接：不建單", "0", d => d.NewOrders.Count().ToString()))
            .Expect(Check("S11.d1.decision", 1, "第 1 天決策＝無候選人（記為延後，計入待派）", "deferred:no_candidate", d => Decision(d, "HOST-A")))
            .Expect(Check("S11.d1.gaps-row", 1, "待派頁籤列出這個問題並帶無法派出的原因", reason, d =>
                d.Probe("gaps").Items("rows").Where(r => r.Str("source") == Disk && r.Int("eventId") == DiskId)
                    .SelectMany(r => r.GetProperty("unassignable").EnumerateArray()).Select(u => u.Str("reason")).FirstOrDefault() ?? "（待派頁沒有這個問題）"))
            .Expect(Check("S11.d1.admin-told", 1, "管理者被主動告知（通知佇列、信件或徽章其一）", "有告知管道", d =>
                Outbox(d, "admin1") > 0 || d.MailsSent.Any(m => m.To.Contains("admin1@scenario.test")) || d.Probe("admin-unread").Int("count") > 0
                    ? "有告知管道" : "沒有任何告知管道"))
            .Expect(Check("S11.d2.retry", 2, "隔天問題仍在時再試一次（第 2 天同樣走過派工並記為無候選人）", "deferred:no_candidate", d => Decision(d, "HOST-A")))
            .KnownDifference("E3-S11-1", "S11.d1.admin-told");
        if (resumes)
            s.Expect(Check("S11.d3.retry-delivers", 3, "管理者恢復 h1 接單後，再隔天問題仍在：再試一次就派出（建單給 h1）", "delivered/h1", d =>
                $"{Decision(d, "HOST-A")}/{HolderOf(d, "HOST-A")}"));
        return s;
    }

    // ── S12：兩位管理者幾乎同時改同一張單（後到者的結果）──────────────────────────

    internal static Scenario BuildS12() => new Scenario("S12 兩位管理者操作同一張單")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Admin("admin2"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(
            ScenarioActions.AdminClose("admin1", OrderRef.Id(1), "resolved", "已確認處理完成"),
            ScenarioActions.AdminClose("admin2", OrderRef.Id(1), "resolved", "已確認處理完成"),
            ScenarioActions.Reassign("admin2", OrderRef.Id(1), "h2"),
            ScenarioActions.Return("admin2", OrderRef.Id(1), "請再確認"),
            ScenarioActions.Cancel("admin2", OrderRef.Id(1), "重複建立")))
        .Days(2)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S12.d2.later-statuses", 2, "後到者的四個操作都得到 4xx（衝突或已結案），不是 5xx", "4xx,4xx,4xx,4xx", d =>
            string.Join(",", d.Actions.Skip(1).Select(a => a.StatusCode / 100 == 4 ? "4xx" : a.StatusCode.ToString()))))
        .Expect(Check("S12.d2.later-messages", 2, "後到者每次都帶可讀的中文訊息", "是,是,是,是", d =>
            string.Join(",", d.Actions.Skip(1).Select(a => Flag(!string.IsNullOrWhiteSpace(a.Message) && a.Message!.Any(c => c > 0x4E00))))))
        .Expect(Check("S12.d2.later-codes", 2, "後到者的錯誤碼不是 server_error", "是,是,是,是", d =>
            string.Join(",", d.Actions.Skip(1).Select(a => Flag(a.ErrorCode is not (null or "server_error"))))));

    // ── S13：觀察中到期（依真實時間，只能驗證前置）────────────────────────────

    internal static Scenario BuildS13() => new Scenario("S13 期限可否設在過去")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(
            ScenarioActions.ReplyWithDue("h1", OrderRef.ForHandler("h1"), "observing", DateTime.Today.AddDays(-1), "先觀察"),
            ScenarioActions.ReplyWithDue("h1", OrderRef.ForHandler("h1"), "in_progress", DateTime.Today.AddDays(-1), "處理中"),
            ScenarioActions.CreateOrderDue("admin1", Disk, DiskId, "h2", new[] { "HOST-B" }, DateTime.Today.AddDays(-1)),
            ScenarioActions.ReplyWithDue("h1", OrderRef.ForHandler("h1"), "observing", DateTime.Today.AddDays(3), "先觀察三天")))
        .Days(2)
        // 這些斷言說明「為什麼 S13 無法以情境模擬」：任何 API 都拒絕把期限設在今天以前
        .Expect(Check("S13.d2.observing-past", 2, "觀察至設在過去：被拒（400）", "400", d => d.Actions[0].StatusCode.ToString()))
        .Expect(Check("S13.d2.inprogress-past", 2, "預計完成日設在過去：被拒（400）", "400", d => d.Actions[1].StatusCode.ToString()))
        .Expect(Check("S13.d2.create-past", 2, "建單帶過去的預計完成日：被拒（400）", "400", d => d.Actions[2].StatusCode.ToString()))
        .Expect(Check("S13.d2.observing-future", 2, "觀察至設在未來：成功並記為觀察中", "200/observing", d =>
            $"{d.Actions[3].StatusCode}/{string.Join(",", OrderOf(d, "h1").Members.Select(m => m.Status).Distinct())}"));

    /// <summary>
    /// 觀察中到期：處理人第 2 天回覆觀察中、期限設成當天（今天可設）；期限當天不算到期，
    /// 隔天（第 3 天）起算到期，列入逾期篩選，處理人與管理者都看得到。
    /// 第 N 天的「今天」＝資料日隔天，最後一天（第 4 天）的今天是真實今天，所以第 2 天的今天＝真實今天 − 2。
    /// </summary>
    internal static Scenario BuildS13Due() => new Scenario("S13 觀察中到期")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.ReplyWithDue("h1", OrderRef.ForHandler("h1"), "observing", DateTime.Today.AddDays(-2), "先觀察到今天"))
            .Probe("admin-overdue", "admin1", "api/work-orders?status=overdue&pageSize=100")
            .Probe("h1-overdue", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=overdue&pageSize=100"))
        .Day(3, d => d
            .Probe("admin-overdue", "admin1", "api/work-orders?status=overdue&pageSize=100")
            .Probe("h1-overdue", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=overdue&pageSize=100"))
        .Day(4, d => d
            .Probe("admin-overdue", "admin1", "api/work-orders?status=overdue&pageSize=100")
            .Probe("h1-overdue", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=overdue&pageSize=100"))
        .Days(4)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S13d.d2.due-today-not-overdue", 2, "觀察期限是今天：不算到期（管理者與處理人的逾期篩選都沒有）", "0/0", d =>
            $"{d.Probe("admin-overdue").Items("items").Count}/{d.Probe("h1-overdue").Items("items").Count}"))
        .Expect(Check("S13d.d3.due-yesterday-overdue", 3, "觀察期限是昨天：算到期，管理者與處理人的逾期篩選都找得到", "1/1", d =>
            $"{d.Probe("admin-overdue").Items("items").Count}/{d.Probe("h1-overdue").Items("items").Count}"))
        .Expect(Check("S13d.d4.still-overdue", 4, "再隔一天仍列在逾期篩選", "1/1", d =>
            $"{d.Probe("admin-overdue").Items("items").Count}/{d.Probe("h1-overdue").Items("items").Count}"));

    // ── S14：沒有 email ─────────────────────────────────────────────────────

    internal static Scenario BuildS14Handler() => new Scenario("S14 處理人沒有 email")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }, email: false))
        .WithUser(Handler("h2", new[] { "G1" }))
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Act(
                ScenarioActions.Reassign("admin1", OrderRef.ForHandler("h1"), "h2"),
                ScenarioActions.Reassign("admin1", OrderRef.ForHandler("h2"), "h1"))
            .Probe("badge", "h1", "api/handlers/me/badge"))
        .Days(1)
        .Expect(Check("S14a.d1.night-order", 1, "夜間派工把單派給沒有 email 的 h1（候選條件不看 email）", "1", d => d.OrdersCreatedFor("h1").ToString()))
        .Expect(Check("S14a.d1.badge", 1, "站內徽章照常提示（我的交辦進行中 1）", "1", d => d.Probe("badge").Int("activeWorkOrders").ToString()))
        .Expect(Check("S14a.d1.reassign-to-email", 1, "改派給有 email 的人：不出現「收不到信」提示", "否", d => NoEmailFlag(d.Actions[0])))
        .Expect(Check("S14a.d1.reassign-no-email", 1, "改派給沒有 email 的人：回應提示對方收不到信", "是", d => NoEmailFlag(d.Actions[1])))
        .Expect(Check("S14a.d1.outbox-kept", 1, "沒有 email 的處理人，通知仍入佇列保留（7 天內補 email 還收得到）", "是", d => Flag(d.OutboxAdded.Any(o => o.Recipient == "h1"))));

    private static string NoEmailFlag(ActionResultView a) =>
        Flag(a.Body?.Contains("\"handlerHasNoEmail\":true", StringComparison.OrdinalIgnoreCase) == true);

    internal static Scenario BuildS14Admins() => new Scenario("S14 管理者都沒有 email")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1", email: false))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "escalated", "備品缺貨，無法自行更換"))
            .Probe("admin-unread", "admin1", "api/work-orders/unread-count")
            .Probe("admin-escalated", "admin1", "api/work-orders?status=escalated&pageSize=100"))
        .Days(2)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S14b.d2.no-mail", 2, "沒有任何管理者有 email：上報信寄不出去", "0", d => d.MailsSent.Count.ToString()))
        .Expect(Check("S14b.d2.badge", 2, "站內徽章照常提示上報的回覆", "1", d => d.Probe("admin-unread").Int("count").ToString()))
        .Expect(Check("S14b.d2.escalated-listed", 2, "管理者在站內以「上報中」篩得到這張單", "1", d => d.Probe("admin-escalated").Items("items").Count.ToString()))
        .Expect(Check("S14b.d2.undelivered-record", 2, "有地方記錄「這次上報沒有人收到信」並讓管理者在站內看得到（稽核或通知記錄）", "有記錄", d =>
            d.AuditAdded.Any(a => a.Action != AuditActions.HandlingStatus) ? "有記錄" : "沒有任何記錄"))
        .KnownDifference("E3-S14-1", "S14b.d2.undelivered-record");

    // ── 進行中台數的五個出口（D6：五處數字一致）──────────────────────────────────

    /// <summary>登記五個出口的探針：總覽列、負載看板、處理人摘要、處理人清單列、徽章（徽章也是儀表板「我的待辦」的來源）</summary>
    private static DayPlan ProbeFivePlaces(DayPlan d) => d
        .Probe("admin-list", "admin1", "api/work-orders?status=all&pageSize=100")
        .Probe("load-board", "admin1", "api/work-orders/load-board")
        .Probe("handler-summary", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders/summary")
        .Probe("handler-list", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=all&pageSize=100")
        .Probe("badge", "h1", "api/handlers/me/badge");

    /// <summary>五個出口的進行中台數，依序：總覽列／負載看板／處理人摘要／處理人清單列／徽章</summary>
    private static string FivePlaces(DayResult d)
    {
        int Active(ProbeView p) => p.Items("items").Select(r => r.GetProperty("counts").Int("active")).FirstOrDefault();
        var board = d.Probe("load-board").Items("rows").FirstOrDefault(r => r.Str("name").Contains("h1", StringComparison.Ordinal)).Int("activeMembers");
        return $"{Active(d.Probe("admin-list"))}/{board}/{d.Probe("handler-summary").Int("activeMembers")}/{Active(d.Probe("handler-list"))}/{d.Probe("badge").Int("activeMembers")}";
    }

    // ── S15：同一問題多台、部分處理 ────────────────────────────────────────────

    internal static Scenario BuildS15() => new Scenario("S15 多台部分處理")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-C") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Events("HOST-C", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.ReplyHosts("h1", OrderRef.ForHandler("h1"), "resolved", new[] { "HOST-A", "HOST-B" }, "已處理"))
            .Probe("admin-list", "admin1", "api/work-orders?status=all&pageSize=100")
            .Probe("load-board", "admin1", "api/work-orders/load-board")
            .Probe("handler-summary", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders/summary")
            .Probe("handler-list", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?status=all&pageSize=100")
            .Probe("badge", "h1", "api/handlers/me/badge")
            .Probe("detail", "admin1", ctx => $"api/work-orders/{OrderRef.Id(1).Resolve(ctx)}"))
        // D6：未結案的 HOST-C 停用後五處都不再計入；重新啟用後一起恢復
        .Day(3, d => ProbeFivePlaces(d.Act(ScenarioActions.SetHostActive("admin1", "HOST-C", false))))
        .Day(4, d => ProbeFivePlaces(d.Act(ScenarioActions.SetHostActive("admin1", "HOST-C", true))))
        .Days(4)
        .Expect(Check("S15.d1.setup", 1, "前置：三台都在 h1 的同一張單", "3", d => OrderOf(d, "h1").Members.Count.ToString()))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S15.d2.state", 2, "有任一成員未結案時單為進行中", "進行中", d => OrderOf(d, "h1").State))
        .Expect(Check("S15.d2.members", 2, "成員：兩台已處理、一台未結案", "resolved,resolved,in_progress", d =>
            string.Join(",", OrderOf(d, "h1").Members.Select(m => m.Status).OrderByDescending(x => x == "resolved").ThenBy(x => x))))
        .Expect(Check("S15.d2.counts", 2, "看板、清單、處理人頁的進行中台數一致（總覽列／負載看板／處理人摘要／處理人清單列／徽章）", "1/1/1/1/1", d =>
        {
            int Active(ProbeView p, string prop = "items") => p.Items(prop).Select(r => r.GetProperty("counts").Int("active")).FirstOrDefault();
            var board = d.Probe("load-board").Items("rows").FirstOrDefault(r => r.Str("name").Contains("h1", StringComparison.Ordinal)).Int("activeMembers");
            return $"{Active(d.Probe("admin-list"))}/{board}/{d.Probe("handler-summary").Int("activeMembers")}/{Active(d.Probe("handler-list"))}/{d.Probe("badge").Int("activeMembers")}";
        }))
        .Expect(Check("S15.d2.total", 2, "總覽列與處理人清單列的成員總數一致", "3/3", d =>
            $"{d.Probe("admin-list").Items("items").Select(r => r.GetProperty("counts").Int("total")).FirstOrDefault()}/{d.Probe("handler-list").Items("items").Select(r => r.GetProperty("counts").Int("total")).FirstOrDefault()}"))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("S15.d3.disabled-host", 3, "未結案的那台停用後，五處進行中台數都扣掉它（總覽列／負載看板／處理人摘要／處理人清單列／徽章）", "0/0/0/0/0", FivePlaces))
        .Expect(Check("S15.d3.total-kept", 3, "停用不改變成員總數，總覽列另標「主機已停用」台數", "3/1", d =>
            $"{d.Probe("admin-list").Items("items").Select(r => r.GetProperty("counts").Int("total")).FirstOrDefault()}/" +
            $"{d.Probe("admin-list").Items("items").Select(r => r.GetProperty("counts").Int("activeOnDisabledHosts")).FirstOrDefault()}"))
        .Expect(Expect.ActionStatus(4, 0, 200))
        .Expect(Check("S15.d4.re-enabled", 4, "主機重新啟用後五處台數一起恢復", "1/1/1/1/1", FivePlaces));

    // ── S16：問題負責人與自動派工同時適用 ─────────────────────────────────────

    internal static Scenario BuildS16() => new Scenario("S16 問題負責人優先於自動派工")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G1" }))
        .Day(1, d => d.Act(ScenarioActions.SetIssueOwners("admin1", Disk, DiskId, "h2")))
        .Day(2, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Days(2)
        .Expect(Expect.ActionStatus(1, 0, 200))
        .Expect(Check("S16.d2.handler", 2, "問題負責人（h2）優先於自動派工的同分選人（h1）", "h2", d => HolderOf(d, "HOST-A")))
        .Expect(Check("S16.d2.origin", 2, "單的來源記為問題負責人規則", WorkOrderOrigins.OwnerRule, d => d.NewOrders.Select(o => o.Origin).FirstOrDefault() ?? "（沒有新單）"));

    // ── S17：沒有處理能力的角色開「我的交辦」 ────────────────────────────────

    internal static Scenario BuildS17() => new Scenario("S17 無處理能力的角色開我的交辦")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(new UserSpec("mgr1", UserRole.Manager))
        .WithUser(new UserSpec("dev1", UserRole.Dev))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Probe("mgr-page", "mgr1", ctx => $"handlers/{ctx.UserId("mgr1")}")
            .Probe("dev-page", "dev1", ctx => $"handlers/{ctx.UserId("dev1")}")
            .Probe("mgr-summary", "mgr1", ctx => $"api/handlers/{ctx.UserId("mgr1")}/work-orders/summary")
            .Probe("dev-summary", "dev1", ctx => $"api/handlers/{ctx.UserId("dev1")}/work-orders/summary")
            .Probe("mgr-list", "mgr1", ctx => $"api/handlers/{ctx.UserId("mgr1")}/work-orders?pageSize=100")
            .Probe("mgr-badge", "mgr1", "api/handlers/me/badge"))
        .Days(1)
        .Expect(Check("S17.page-shell", 1, "頁面殼對 Manager、Dev 都回 200（不會 403/500）", "200/200", d => $"{d.Probe("mgr-page").StatusCode}/{d.Probe("dev-page").StatusCode}"))
        .Expect(Check("S17.api-empty", 1, "資料端點回 200 且沒有被派單（空清單、摘要為 0）", "200/200/0/0", d =>
            $"{d.Probe("mgr-summary").StatusCode}/{d.Probe("dev-summary").StatusCode}/{d.Probe("mgr-list").Items("items").Count}/{d.Probe("mgr-badge").Int("activeWorkOrders")}"))
        // 導覽與頁面文案屬於前端靜態內容：直接讀 layout.js 與 handler-detail.js（沒有瀏覽器可跑；這是靜態判讀）
        .Expect(Check("S17.nav-or-explain", 1, "沒有處理能力的角色：不顯示「我的交辦」導覽（layout.js 該項要求 Handle），或頁面明說「你的角色沒有處理能力」", "有處置", d =>
        {
            var navHidden = NavRequirement("我的交辦") == "Handle";
            var explained = ReadWebFile("wwwroot", "js", "pages", "handler-detail.js").Contains("沒有處理能力", StringComparison.Ordinal);
            return navHidden || explained ? "有處置" : $"沒有處置（導覽條件：{NavRequirement("我的交辦")}，頁面無角色說明）";
        }))
        .KnownDifference("E3-S17-1", "S17.nav-or-explain");

    private static string NavRequirement(string label)
    {
        var text = ReadWebFile("wwwroot", "js", "core", "layout.js");
        var at = text.IndexOf($"label: '{label}'", StringComparison.Ordinal);
        if (at < 0) return "（找不到導覽項）";
        var end = text.IndexOf('}', at);
        var line = text[at..end];
        var m = System.Text.RegularExpressions.Regex.Match(line, @"requires:\s*(null|'[^']*'|\[[^\]]*\])");
        return m.Success ? m.Groups[1].Value.Trim('\'') : "（讀不到條件）";
    }

    private static string ReadWebFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")) && !Directory.Exists(Path.Combine(dir.FullName, "LogForesight.Web")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "LogForesight.Web" }.Concat(segments).ToArray()));
    }

    // ── S18：改派時目標已有同問題的單 ─────────────────────────────────────────

    internal static Scenario BuildS18() => new Scenario("S18 改派併入目標單")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G2" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G2" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.Reassign("admin1", OrderRef.ForHandler("h1"), "h2"))
            .Probe("h1-old-order", "h1", ctx => $"api/work-orders/{OrderRef.Id(1).Resolve(ctx)}")
            .Probe("h1-badge", "h1", "api/handlers/me/badge")
            .Probe("h2-badge", "h2", "api/handlers/me/badge"))
        .Days(2)
        .Expect(Check("S18.d1.setup", 1, "前置：兩人各有一張同問題的單（HOST-A→h1、HOST-B→h2）", "h1:HOST-A/h2:HOST-B", d =>
            $"h1:{string.Join("+", OrderOf(d, "h1").Members.Select(m => m.Host))}/h2:{string.Join("+", OrderOf(d, "h2").Members.Select(m => m.Host))}"))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S18.d2.merged", 2, "併入目標單：回應標示併入既有單", "是", d =>
            Flag(d.Actions[0].Body?.Contains("\"mergedIntoExisting\":true", StringComparison.OrdinalIgnoreCase) == true)))
        .Expect(Check("S18.d2.target-members", 2, "目標單現有兩台", "HOST-A+HOST-B", d =>
            string.Join("+", OrderOf(d, "h2").Members.Select(m => m.Host).OrderBy(x => x, StringComparer.Ordinal))))
        .Expect(Check("S18.d2.old-order", 2, "原單標轉出（推導為已結案 moved）", "已結案(moved)", d => OrderOf(d, "h1").State))
        .Expect(Check("S18.d2.old-readonly", 2, "原處理人仍可唯讀開啟原單（200、標示為原處理人檢視）", "200/是", d =>
            $"{d.Probe("h1-old-order").StatusCode}/{Flag(d.Probe("h1-old-order").Data is { } x && x.Flag("viewerIsPreviousHandler"))}"))
        .Expect(Check("S18.d2.new-handler-notified", 2, "新處理人（目標單）收到通知", "有通知", d => Outbox(d, "h2") > 0 ? "有通知" : "沒有任何通知"))
        .Expect(Check("S18.d2.old-handler-notified", 2, "原處理人收到轉出通知", "transferred", d => OutboxKinds(d, "h1")))
        .Expect(Check("S18.d2.badges", 2, "徽章：原處理人 0 張、目標處理人仍 1 張", "0/1", d =>
            $"{d.Probe("h1-badge").Int("activeWorkOrders")}/{d.Probe("h2-badge").Int("activeWorkOrders")}"));
}

internal static class DayResultExtensions
{
    /// <summary>當天新建、給某處理人的交辦單數</summary>
    public static int OrdersCreatedFor(this DayResult d, string handler) => d.NewOrders.Count(o => o.Handler == handler);
}
