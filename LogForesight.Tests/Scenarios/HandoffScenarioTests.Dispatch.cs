using System.Text.Json;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

// S3～S10：上報未處理、處理人狀態變化（停用／暫停／授權移除）、主機環境變化、結案後再出現、靜音。
public sealed partial class HandoffScenarioTests
{
    private static Scenario BuildMore(string key) => key switch
    {
        "S3" => BuildS3(),
        "S3提醒" => BuildS3Reminder(),
        "S4" => BuildContinuation("S4 處理人被停用", ContinuationKind.Disabled),
        "S5" => BuildContinuation("S5 處理人暫停接單", ContinuationKind.Paused),
        "S6" => BuildContinuation("S6 處理人的主機授權被移除", ContinuationKind.NoAccess),
        "S4無人接手" => BuildContinuation("S4 停用後無人接手", ContinuationKind.Disabled, ContinuationVariant.NoTaker),
        "S4問題負責人" => BuildContinuation("S4 停用後由問題負責人接手", ContinuationKind.Disabled, ContinuationVariant.OwnerTakes),
        "S5恢復" => BuildContinuation("S5 恢復接單後續掛", ContinuationKind.Paused, ContinuationVariant.Recover),
        "S7主機停用" => BuildS7Disabled(),
        "S7改群組" => BuildS7Regroup(),
        "S7換負責人" => BuildS7Owner(),
        "S8" => BuildS8(),
        "S9" => BuildS9(),
        "S9恢復" => BuildS9Resumed(),
        "S9迄日" => BuildS9MuteEnd(),
        "S10" => BuildS10(),
        "B3不勾" => BuildAdminCloseRedispatch("B3 代為結案不勾選", block: false),
        "B3勾選" => BuildAdminCloseRedispatch("B3 代為結案勾選", block: true),
        "B4恢復單筆" => BuildRestoreOne(),
        "B4整個問題" => BuildRestoreIssue(),
        "B4再回不處理" => BuildRestoreThenDismissAgain(),
        "B4無權限" => BuildRestoreForbidden(),
        "B6取消" => BuildCancelRevokesGrant(),
        "B6結案" => BuildClosedKeepsGrant(),
        "B6改派" => BuildReassignRevokesGrant(),
        _ => BuildEligibility(key)
    };

    /// <summary>停用使用者或暫停接單的回應裡的進行中單數（<c>activeWorkOrders</c>，可能在回應本體或其 data 內）</summary>
    private static string ActiveWorkOrdersOf(ActionResultView action)
    {
        if (string.IsNullOrEmpty(action.Body)) return "（沒有回應本體）";
        using var doc = JsonDocument.Parse(action.Body);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        return data.TryGetProperty("activeWorkOrders", out var n) ? n.GetInt32().ToString() : "（回應沒有 activeWorkOrders）";
    }

    private static string OrderUrl(ScenarioActionContext ctx, string handler) =>
        $"api/work-orders/{OrderRef.ForHandler(handler).Resolve(ctx)}";

    // ── S3：「無法處理」後管理者一直沒處理 ────────────────────────────────────

    internal static Scenario BuildS3() => new Scenario("S3 無法處理後管理者未處理")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "escalated", "備品缺貨，無法自行更換"))
            .Probe("admin-escalated", "admin1", "api/work-orders?status=escalated&pageSize=100")
            .Probe("admin-members", "admin1", ctx => OrderUrl(ctx, "h1") + "/members?pageSize=50")
            .Probe("admin-timeline", "admin1", ctx => OrderUrl(ctx, "h1") + "/timeline")
            .Probe("admin-unread", "admin1", "api/work-orders/unread-count"))
        .Day(3, d => d.Probe("admin-escalated", "admin1", "api/work-orders?status=escalated&pageSize=100"))
        .Day(4, d => d
            .Probe("admin-escalated", "admin1", "api/work-orders?status=escalated&pageSize=100")
            .Probe("admin-unread", "admin1", "api/work-orders/unread-count"))
        .Days(4)
        .Expect(Check("S3.d2.escalated-list", 2, "上報中的單可在總覽以「上報中」篩出", "1", d => d.Probe("admin-escalated").Items("items").Count.ToString()))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S3.d2.reason-in-members", 2, "經真實上報流程後，詳情的成員資料含上報狀態與處理人寫的上報原因（D9）", "escalated/備品缺貨，無法自行更換", d =>
        {
            var m = d.Probe("admin-members").Items("items").FirstOrDefault();
            return m.ValueKind == JsonValueKind.Object ? $"{m.Str("status")}/{m.Str("note")}" : "（成員清單是空的）";
        }))
        .Expect(Check("S3.d2.reason-in-timeline", 2, "時間軸的回覆事件也帶上報原因", "是", d =>
            Flag(d.Probe("admin-timeline").Data is { ValueKind: JsonValueKind.Array } events
                && events.EnumerateArray().Any(e => (e.Str("note") ?? "").Contains("備品缺貨，無法自行更換", StringComparison.Ordinal)))))
        .Expect(Check("S3.d4.still-listed", 4, "管理者一直沒處理，第 4 天仍可篩出（沒有被漏掉）", "1", d => d.Probe("admin-escalated").Items("items").Count.ToString()))
        .Expect(Check("S3.d4.unread-badge", 4, "管理者未開啟前，側欄徽章持續提示（未讀回覆 1）", "1", d => d.Probe("admin-unread").Int("count").ToString()));

    /// <summary>
    /// S3 的提醒預期：處理人第 2 天回報「無法處理」，管理者一直沒處理，超過數天（暫定 3 天）後管理者應收到提醒。
    /// 提醒功能目前不存在（通知種類沒有提醒、沒有掃「上報中逾 N 天」的邏輯），所以第 4 天管理者的通知佇列沒有任何新項目；
    /// 照第一階段慣例寫成應有行為並列入已知差異 E3-S3-1，提醒做出來時這一項會轉紅、要求從清單移除。
    /// </summary>
    internal static Scenario BuildS3Reminder() => BuildS3()
        .Expect(Check("S3.d4.admin-reminder", 4, "上報後管理者一直沒處理，超過數天後通知佇列有給管理者的提醒", "有提醒",
            d => Outbox(d, "admin1") > 0 ? "有提醒" : "沒有任何提醒"))
        .KnownDifference("E3-S3-1", "S3.d4.admin-reminder");

    // ── S4／S5／S6：處理人被停用／暫停接單／主機授權被移除後，新主機是否仍續掛進他的單 ──────────

    internal enum ContinuationKind { Disabled, Paused, NoAccess }

    /// <summary>Base＝h2 為備援候選人；NoTaker＝沒有任何人能接；OwnerTakes＝問題負責人 h2；Recover＝處理人隔天恢復資格再有新主機</summary>
    internal enum ContinuationVariant { Base, NoTaker, OwnerTakes, Recover }

    private static string StopReasonOf(ContinuationKind kind) => kind switch
    {
        ContinuationKind.Disabled => ContinuationStopReasons.HandlerInactive,
        ContinuationKind.Paused => ContinuationStopReasons.HandlerPaused,
        _ => ContinuationStopReasons.HostNotVisible
    };

    /// <summary>
    /// 第 1 天夜間派工把 HOST-A 的單建給 h1，管理者把單的範圍擴為 G1 並開續掛（併入同處理人同問題的單，範圍取聯集）；
    /// 第 2 天處理人狀態變化；第 3 天 HOST-B、HOST-C 同問題首次出現。續掛（④）與自動派工（⑥）各自該怎麼走由預期決定。
    /// </summary>
    internal static Scenario BuildContinuation(string name, ContinuationKind kind, ContinuationVariant variant = ContinuationVariant.Base)
    {
        var s = new Scenario(name)
            .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("HOST-C") { Groups = new[] { "G1" } })
            .WithUser(Admin("admin1"))
            .WithUser(Handler("h1", new[] { "G1" }))
            // 第 1 天關閉自動派工：HOST-A 的問題先不派，由管理者以「G1 範圍＋續掛」建單給 h1（夜間建的單一律指定主機、不續掛）；
            // 第 2 天起開啟自動派工，第 3 天新主機出現時續掛（④）與自動派工（⑥）才各自面對處理人狀態
            .WithSettings(new ScenarioSettings { AutoDispatchEnabled = false })
            .Day(1, d => d
                .Events("HOST-A", ScenarioEvents.DiskErrors(12))
                .Act(ScenarioActions.CreateOrderScoped("admin1", Disk, DiskId, "h1", WorkOrderScopes.Groups, new[] { "G1" }, autoAttach: true))
                .Probe("detail", "admin1", ctx => OrderUrl(ctx, "h1")))
            .Expect(Expect.ActionStatus(1, 0, 200))
            .Expect(Check("setup.autoattach", 1, "前置：h1 的單已是 G1 範圍且開續掛", "是/Groups", d =>
                $"{Flag(d.Probe("detail").Data is { } x && x.Flag("autoAttach"))}/{d.Probe("detail").Data?.Str("scopeKind")}"))
            .Expect(Check("setup.unseen-zero", 1, "前置：h1 看得到單上的所有主機 → 詳情的「處理人看不到台數」為 0", "0", d =>
                (d.Probe("detail").Data?.Int("handlerUnseenMemberCount") ?? -1).ToString()));

        if (variant != ContinuationVariant.NoTaker) s.WithUser(Handler("h2", new[] { "G1" }));
        if (variant == ContinuationVariant.Recover) s.WithHost(new HostSpec("HOST-D") { Groups = new[] { "G1" } });

        switch (kind)
        {
            case ContinuationKind.Disabled:
                s.Day(2, d => d
                    .Setting(x => x.AutoDispatchEnabled = true)
                    .Act(ScenarioActions.DisableUser("admin1", "h1"))
                    .Act(variant == ContinuationVariant.OwnerTakes
                        ? new[] { ScenarioActions.SetIssueOwners("admin1", Disk, DiskId, "h2") }
                        : Array.Empty<ScenarioAction>())
                    .Probe("inactive-list", "admin1", "api/work-orders?status=active&handlerInactive=true&pageSize=100")
                    .Probe("load-board", "admin1", "api/work-orders/load-board"))
                    .Expect(Expect.ActionStatus(2, 0, 200))
                    .Expect(Check("S4.d2.admin-warned", 2, "停用當下的回應帶此人進行中單數（畫面據此提示並連到交辦總覽）", "1", d =>
                        ActiveWorkOrdersOf(d.Actions[0])))
                    .Expect(Check("S4.d2.inactive-filter", 2, "既有單可由「只看處理人已停用」找到", "1", d => d.Probe("inactive-list").Items("items").Count.ToString()))
                    .Expect(Check("S4.d2.load-board", 2, "負載看板標示 h1 已停用", "否", d =>
                        Flag(d.Probe("load-board").Items("rows").Any(r => r.Str("name").Contains("h1", StringComparison.Ordinal) && r.Flag("active")))));
                break;
            case ContinuationKind.Paused:
                s.Day(2, d => d
                    .Setting(x => x.AutoDispatchEnabled = true)
                    .Act(ScenarioActions.SetDispatchPaused("admin1", "h1", true))
                    .Probe("load-board", "admin1", "api/work-orders/load-board"))
                    .Expect(Expect.ActionStatus(2, 0, 200))
                    .Expect(Check("S5.d2.pause-response", 2, "暫停接單的回應帶此人進行中單數（既有單仍在他名下）", "1", d =>
                        ActiveWorkOrdersOf(d.Actions[0])))
                    .Expect(Check("S5.d2.load-board", 2, "負載看板標示 h1 暫停接單", "是", d =>
                        Flag(d.Probe("load-board").Items("rows").Any(r => r.Str("name").Contains("h1", StringComparison.Ordinal) && r.Flag("paused")))));
                break;
            case ContinuationKind.NoAccess:
                s.Day(2, d => d
                    .Setting(x => x.AutoDispatchEnabled = true)
                    .Act(ScenarioActions.SetUserHostAccess("admin1", "h1"))
                    .Probe("h1-detail", "h1", ctx => OrderUrl(ctx, "h1"))
                    .Probe("h1-members", "h1", ctx => OrderUrl(ctx, "h1") + "/members?pageSize=50")
                    .Probe("admin-detail", "admin1", ctx => OrderUrl(ctx, "h1")))
                    .Expect(Expect.ActionStatus(2, 0, 200))
                    .Expect(Check("S6.d2.handler-still-opens", 2, "處理人仍開得到被交辦的單", "200", d => d.Probe("h1-detail").StatusCode.ToString()))
                    .Expect(Check("S6.d2.handler-sees-member", 2, "處理人在單上仍看得到被交辦的主機（案件授與）", "HOST-A", d =>
                        string.Join(",", d.Probe("h1-members").Items("items").Select(i => i.Str("hostName")))))
                    .Expect(Check("S6.d2.admin-sees-gap", 2, "管理者在單上看得到「處理人目前看不到 N 台」（只剩案件授與的 HOST-A，N＝1）", "1", d =>
                        (d.Probe("admin-detail").Data?.Int("handlerUnseenMemberCount") ?? -1).ToString()));
                break;
        }

        s.Day(3, d =>
            {
                d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
                    .Events("HOST-B", ScenarioEvents.DiskErrors(12))
                    .Events("HOST-C", ScenarioEvents.DiskErrors(12))
                    .Probe("order-detail", "admin1", ctx => OrderUrl(ctx, "h1"))
                    .Probe("order-list", "admin1", "api/work-orders?status=active&pageSize=100")
                    .Probe("gaps", "admin1", Gaps);
                if (kind == ContinuationKind.Paused)
                    d.Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "in_progress", "處理中"));
                if (variant == ContinuationVariant.Recover)
                    d.Act(ScenarioActions.SetDispatchPaused("admin1", "h1", false));
            })
            .Days(variant == ContinuationVariant.Recover ? 4 : 3)
            .Expect(Check($"{name[..2]}.d3.h1-order-members", 3, "之後的夜間派工不再把新主機續掛進原處理人的單（單內只剩 HOST-A）", "HOST-A", d =>
                string.Join(",", OrderOf(d, "h1").Members.Select(m => m.Host))))
            .Expect(Check($"{name[..2]}.d3.no-new-order", 3, "h1 不再收到新派工（第 3 天沒有新建給 h1 的單）", "0", d => d.NewOrders.Count(o => o.Handler == "h1").ToString()));

        var tag = name[..2];
        var stopReason = StopReasonOf(kind);

        // 續掛被擋後往下走：由問題負責人、自動派工接手；都沒有人才進待派並帶原因
        switch (variant)
        {
            case ContinuationVariant.NoTaker:
                s.Expect(Check($"{tag}.d3.b-deferred", 3, "沒有任何人能接：HOST-B、HOST-C 延後進待派（無候選人）", "deferred:no_candidate/deferred:no_candidate", d =>
                        $"{Decision(d, "HOST-B")}/{Decision(d, "HOST-C")}"))
                    .Expect(Check($"{tag}.d3.gaps-reason", 3, "待派頁帶原因：派工池沒有人（停用的 h1 不在候選人內）", "no_pool", d =>
                        d.Probe("gaps").Items("rows").Where(r => r.Str("source") == Disk && r.Int("eventId") == DiskId)
                            .SelectMany(r => r.GetProperty("unassignable").EnumerateArray()).Select(u => u.Str("reason")).FirstOrDefault() ?? "（待派頁沒有這個問題）"));
                break;
            case ContinuationVariant.OwnerTakes:
                s.Expect(Expect.ActionStatus(2, 1, 200))
                    .Expect(Check($"{tag}.d3.owner-takes", 3, "續掛被擋後由問題負責人 h2 接手（單來源是問題負責人規則）", "h2/h2/owner_rule", d =>
                        $"{HolderOf(d, "HOST-B")}/{HolderOf(d, "HOST-C")}/{d.NewOrders.Select(o => o.Origin).FirstOrDefault()}"));
                break;
            default:
                s.Expect(Check($"{tag}.d3.b-holder", 3, "HOST-B 的新問題改由其他候選人（h2）接手", "h2", d => HolderOf(d, "HOST-B")))
                    .Expect(Check($"{tag}.d3.c-holder", 3, "HOST-C 的新問題改由其他候選人（h2）接手", "h2", d => HolderOf(d, "HOST-C")));
                break;
        }

        // D4-b：交辦單詳情與交辦總覽讀得到「續掛已停止」與原因
        s.Expect(Check($"{tag}.d3.stopped-detail", 3, "交辦單詳情標示續掛已停止與原因（含顯示名稱）", $"True/{stopReason}/有名稱", d =>
            {
                var data = d.Probe("order-detail").Data;
                return $"{data?.Flag("continuationStopped")}/{data?.Str("continuationStoppedReason")}/{(string.IsNullOrEmpty(data?.Str("continuationStoppedLabel")) ? "無名稱" : "有名稱")}";
            }))
            .Expect(Check($"{tag}.d3.stopped-list", 3, "交辦總覽的列同樣標示續掛已停止與原因", $"True/{stopReason}", d =>
                RowOf(d.Probe("order-list"), OrderOf(d, "h1").OrderId) is { } row
                    ? $"{row.Flag("continuationStopped")}/{row.Str("continuationStoppedReason")}"
                    : "（總覽沒有這張單）"));


        if (kind == ContinuationKind.Paused)
            s.Expect(Check("S5.d3.existing-reply", 3, "暫停接單者既有單照常可回覆", "200", d => d.Actions[0].StatusCode.ToString()));

        // D4-c：恢復接單後，下一夜新主機續掛；被擋期間已派給別人的仍在別人那裡
        if (variant == ContinuationVariant.Recover)
        {
            s.Day(4, d => d
                    .Events("HOST-A", ScenarioEvents.DiskErrors(12))
                    .Events("HOST-B", ScenarioEvents.DiskErrors(12))
                    .Events("HOST-C", ScenarioEvents.DiskErrors(12))
                    .Events("HOST-D", ScenarioEvents.DiskErrors(12))
                    .Probe("order-detail", "admin1", ctx => OrderUrl(ctx, "h1"))
                    .Probe("order-list", "admin1", "api/work-orders?status=active&pageSize=100"))
                .Expect(Expect.ActionStatus(3, 1, 200))
                .Expect(Check("S5r.d4.d-continued", 4, "恢復接單後下一夜的新主機 HOST-D 續掛進 h1 的單", "HOST-A,HOST-D", d =>
                    string.Join(",", OrderOf(d, "h1").Members.Select(m => m.Host).OrderBy(h => h, StringComparer.Ordinal))))
                .Expect(Check("S5r.d4.others-stay", 4, "被擋期間已派給 h2 的 HOST-B、HOST-C 仍在 h2 那裡，不追回", "h2/h2", d =>
                    $"{HolderOf(d, "HOST-B")}/{HolderOf(d, "HOST-C")}"))
                .Expect(Check("S5r.d4.stop-cleared", 4, "續掛成功後「續掛已停止」標示清掉（詳情與總覽）", "False/False", d =>
                    $"{d.Probe("order-detail").Data?.Flag("continuationStopped")}/" +
                    $"{(RowOf(d.Probe("order-list"), OrderOf(d, "h1").OrderId) is { } row ? row.Flag("continuationStopped").ToString() : "（總覽沒有這張單）")}"));
        }

        return s;
    }

    // ── S7：主機環境變化 ────────────────────────────────────────────────────

    internal static Scenario BuildS7Disabled() => new Scenario("S7 主機停用")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => ProbeFivePlaces(d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))))
        .Day(2, d => ProbeFivePlaces(d
            .Act(ScenarioActions.SetHostActive("admin1", "HOST-B", false)))
            .Probe("members", "admin1", ctx => OrderUrl(ctx, "h1") + "/members?pageSize=50"))
        .Day(3, d => ProbeFivePlaces(d
            .Act(ScenarioActions.SetHostActive("admin1", "HOST-B", true)))
            .Probe("members", "admin1", ctx => OrderUrl(ctx, "h1") + "/members?pageSize=50"))
        .Days(3)
        .Expect(Check("S7a.d1.badge", 1, "前置：停用前 h1 手上 2 台", "2", d => d.Probe("badge").Int("activeMembers").ToString()))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S7a.d2.badge", 2, "停用主機上的進行中成員不再算處理人的待辦（徽章台數）", "1", d => d.Probe("badge").Int("activeMembers").ToString()))
        .Expect(Check("S7a.d2.load-board", 2, "負載看板的進行中台數同步扣除", "1", d =>
            d.Probe("load-board").Items("rows").FirstOrDefault(r => r.Str("name").Contains("h1", StringComparison.Ordinal)).Int("activeMembers").ToString()))
        .Expect(Check("S7a.d2.five-places", 2, "五處進行中台數一致且都不含停用主機（總覽列／負載看板／處理人摘要／處理人清單列／徽章）", "1/1/1/1/1", FivePlaces))
        .Expect(Check("S7a.d2.overview-disabled", 2, "總覽列的成員總數不變（2），另標主機已停用 1 台", "2/1", d =>
            $"{d.Probe("admin-list").Items("items").Select(r => r.GetProperty("counts").Int("total")).FirstOrDefault()}/" +
            $"{d.Probe("admin-list").Items("items").Select(r => r.GetProperty("counts").Int("activeOnDisabledHosts")).FirstOrDefault()}"))
        .Expect(Check("S7a.d2.reason-visible", 2, "管理者在單的成員表看得到該台被排除的原因（HOST-B 帶主機已停用標記、HOST-A 沒有）", "HOST-B/HOST-A=否", d =>
        {
            var items = d.Probe("members").Items("items");
            var marked = items.Where(i => i.Flag("hostDisabled")).Select(i => i.Str("hostName"));
            var a = items.FirstOrDefault(i => i.Str("hostName") == "HOST-A");
            return $"{string.Join(",", marked)}/HOST-A={Flag(a.ValueKind == JsonValueKind.Object && a.Flag("hostDisabled"))}";
        }))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("S7a.d3.five-places", 3, "主機重新啟用後五處台數一起恢復計入", "2/2/2/2/2", FivePlaces))
        .Expect(Check("S7a.d3.flag-cleared", 3, "重新啟用後成員列不再標主機已停用", "0", d =>
            d.Probe("members").Items("items").Count(i => i.Flag("hostDisabled")).ToString()));

    internal static Scenario BuildS7Regroup() => new Scenario("S7 主機改群組")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G2" }))
        .Day(1, d => d
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h2-hosts", "h2", "api/hosts"))
        .Day(2, d => d
            .Act(ScenarioActions.SetHostGroups("admin1", "HOST-B", "G2"))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h2-hosts", "h2", "api/hosts"))
        .Days(2)
        .Expect(Check("S7b.d1.visibility", 1, "前置：改群組前 h1 看得到 HOST-B、h2 看不到", "是/否", d =>
            $"{Flag(HostNames(d.Probe("h1-hosts")).Contains("HOST-B"))}/{Flag(HostNames(d.Probe("h2-hosts")).Contains("HOST-B"))}"))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S7b.d2.visibility", 2, "改群組後可見範圍隨之更新：h2 看得到 HOST-B", "是", d => Flag(HostNames(d.Probe("h2-hosts")).Contains("HOST-B"))));

    private static List<string> HostNames(ProbeView p) => p.Items("items").Select(i => i.Str("hostName")).ToList();

    internal static Scenario BuildS7Owner() => new Scenario("S7 換主機負責人")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G3" }, Owners = new[] { "h1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", Array.Empty<string>()))
        .WithUser(Handler("h2", Array.Empty<string>()))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.SetHostOwners("admin1", "HOST-A", "h2")))
        .Day(3, d => d.Events("HOST-A", ScenarioEvents.FailedLogons(15)))
        .Days(3)
        .Expect(Check("S7c.d1.owner-gets-order", 1, "前置：負責人 h1 是唯一看得到 HOST-A 的候選人，單派給他", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S7c.d3.new-issue-handler", 3, "換負責人後新問題（登入失敗）走新負責人 h2", "h2", d =>
            d.Orders.Where(o => o.IsNew).Select(o => o.Handler).FirstOrDefault() ?? "（沒有新單）"));

    // ── S8：問題結案後再出現 ────────────────────────────────────────────────

    internal static Scenario BuildS8()
    {
        var hosts = new[] { "HOST-A", "HOST-B", "HOST-C", "HOST-D" };
        var s = new Scenario("S8 結案後再出現")
            .WithUser(Admin("admin1"))
            // a1 帳號排在 z1 之前，兩人負載同分時 a1 會贏；z1 上次修好，預期延續性偏好把單派回 z1
            .WithUser(Handler("a1", new[] { "G1" }, paused: true))
            .WithUser(Handler("z1", new[] { "G1" }));
        foreach (var h in hosts) s.WithHost(new HostSpec(h) { Groups = new[] { "G1" } });

        s.Day(1, d =>
            {
                foreach (var h in hosts) d.Events(h, ScenarioEvents.DiskErrors(12));
            })
            .Day(2, d => d.Act(
                ScenarioActions.SetDispatchPaused("admin1", "a1", false),
                ScenarioActions.ReplyHosts("z1", OrderRef.ForHandler("z1"), "resolved", new[] { "HOST-A" }, "已更換"),
                ScenarioActions.ReplyHosts("z1", OrderRef.ForHandler("z1"), "wont_fix", new[] { "HOST-B" }, "舊機器不處理"),
                ScenarioActions.ReplyHosts("z1", OrderRef.ForHandler("z1"), "false_positive", new[] { "HOST-C" }, "誤報"),
                ScenarioActions.ReplyHosts("z1", OrderRef.ForHandler("z1"), "known_noise", new[] { "HOST-D" }, "已知雜訊")))
            .Day(3, d =>
            {
                foreach (var h in hosts) d.Events(h, ScenarioEvents.DiskErrors(12));
                d.Probe("gaps", "admin1", Gaps);
                d.Probe("dismissed", "admin1", "api/work-orders/dismissed");
            })
            .Days(3)
            .Expect(Check("S8.d1.setup", 1, "前置：四台都派給 z1（a1 暫停接單）", "z1:4", d => $"z1:{d.Orders.Where(o => o.Handler == "z1").Sum(o => o.Members.Count)}"))
            .Expect(Check("S8.d2.replies", 2, "前置：四種結案回覆都成功，單已結案", "200,200,200,200/已結案(all_closed)", d =>
                $"{string.Join(",", d.Actions.Skip(1).Select(a => a.StatusCode))}/{OrderOf(d, "z1").State}"))
            .Expect(Check("S8.d3.resolved-recurs", 3, "已處理後再出現：重新派工（HOST-A 有新的進行中單）", "是", d => Flag(HolderOf(d, "HOST-A") != "（沒有進行中的單）")))
            .Expect(Check("S8.d3.resolved-handler", 3, "復發優先派給上次處理的人（z1，雖然 a1 同分且帳號在前）", "z1", d => HolderOf(d, "HOST-A")))
            .Expect(Check("S8.d3.recurrence-flag", 3, "派工彙總標「復發」", "是", d => Flag(d.Dispatch.PerHandlerLines.Any(l => l.Contains("復發 1 台", StringComparison.Ordinal)))))
            .Expect(Check("S8.d3.wont-fix", 3, "不處理後再出現：依規格不再派工", "skipped:gate_dismissed", d => Decision(d, "HOST-B")))
            .Expect(Check("S8.d3.false-positive", 3, "誤報後再出現：依規格不再派工", "skipped:gate_dismissed", d => Decision(d, "HOST-C")))
            .Expect(Check("S8.d3.known-noise", 3, "已知雜訊後再出現：依規格不再派工", "skipped:gate_dismissed", d => Decision(d, "HOST-D")))
            .Expect(Check("S8.d3.gaps-count", 3, "待派頁看得到「不再打擾」排除了 3 台", "3", d =>
                d.Probe("gaps").Items("rows").Where(r => r.Str("source") == Disk && r.Int("eventId") == DiskId)
                    .Sum(r => r.TryGetProperty("excluded", out var ex) && ex.TryGetProperty("gate_dismissed", out var n) ? n.GetInt32() : 0).ToString()))
            .Expect(Check("S8.d3.dismissed-lists-hosts", 3, "待派頁的「不再自動派工」清單列出被排除的主機（HOST-B、HOST-C、HOST-D），管理者才知道要恢復哪幾台", "HOST-B,HOST-C,HOST-D", d =>
                string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")).OrderBy(h => h, StringComparer.Ordinal))))
            .Expect(Check("S8.d3.dismissed-says-why", 3, "清單帶出先前結案狀態（不處理、誤報、已知雜訊），已處理的不列", "false_positive,known_noise,wont_fix", d =>
                string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("status")).OrderBy(x => x, StringComparer.Ordinal))))
            .Expect(Check("S8.d3.dismissed-fields", 3, "每列有問題、結案的人（處理人 z1）與結案日期", "disk 153/有/有", d =>
            {
                var group = d.Probe("dismissed").Items("groups").Single();
                var row = group.GetProperty("rows").EnumerateArray().First(r => r.Str("hostName") == "HOST-B");
                return $"{group.Str("issueLabel")}/{(row.Str("closedBy").Contains("z1", StringComparison.Ordinal) ? "有" : "沒有")}"
                       + $"/{(string.IsNullOrEmpty(row.Str("closedAt")) ? "沒有" : "有")}";
            }));
        return s;
    }

    // ── S9：靜音（暫停）→ 解除 ───────────────────────────────────────────────

    internal static Scenario BuildS9() => new Scenario("S9 靜音暫停再解除")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.Mute("admin1", Disk, DiskId, 3, "維修窗口", "pause"))
            .Probe("admin-all", "admin1", "api/work-orders?status=active&pageSize=100")
            .Probe("h1-default", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?pageSize=100")
            .Probe("h1-badge", "h1", "api/handlers/me/badge"))
        .Day(3, d => d
            .Act(ScenarioActions.Unmute("admin1", Disk, DiskId))
            .Probe("admin-all", "admin1", "api/work-orders?status=active&pageSize=100")
            .Probe("h1-default", "h1", ctx => $"api/handlers/{ctx.UserId("h1")}/work-orders?pageSize=100")
            .Probe("h1-badge", "h1", "api/handlers/me/badge"))
        .Days(3)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S9.d2.derived-paused", 2, "靜音期間單推導為暫停（總覽標暫停並帶靜音至）", "是/有", d =>
        {
            var row = RowOf(d.Probe("admin-all"), OrderOf(d, "h1").OrderId);
            return row is { } r ? $"{Flag(r.Flag("paused"))}/{(string.IsNullOrEmpty(r.Str("mutedUntil")) ? "無" : "有")}" : "（總覽沒有這張單）";
        }))
        .Expect(Check("S9.d2.handler-list-excludes", 2, "處理人清單預設排除暫停的單", "0", d => d.Probe("h1-default").Items("items").Count.ToString()))
        .Expect(Check("S9.d2.handler-badge", 2, "處理人徽章不計暫停單（進行中 0）、另列暫停 1", "0/1", d =>
            $"{d.Probe("h1-badge").Int("activeWorkOrders")}/{d.Probe("h1-badge").Int("pausedWorkOrders")}"))
        .Expect(Check("S9.d2.handler-informed", 2, "暫停時處理人有一則暫停通知，含靜音迄日（第 2 天起 3 天）與原因", $"mute_paused::維修窗口:{DateTime.Today.AddDays(-1 + 2):yyyy-MM-dd}:False", d => OutboxDetail(d, "h1")))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("S9.d3.resumed-paused-flag", 3, "解除後單不再是暫停", "否", d => RowOf(d.Probe("admin-all"), OrderOf(d, "h1").OrderId) is { } r ? Flag(r.Flag("paused")) : "（總覽沒有這張單）"))
        .Expect(Check("S9.d3.handler-list-back", 3, "解除後處理人清單恢復列出", "1", d => d.Probe("h1-default").Items("items").Count.ToString()))
        .Expect(Check("S9.d3.handler-informed", 3, "提前解除時處理人有一則恢復通知", "mute_resumed", d => OutboxKinds(d, "h1")));

    // ── S9 補充：靜音恢復紀錄與迄日邊界（需要假時鐘讓第 N 天之間真的隔了 N 天）──────────

    private const string ResumedFilterUrl = "api/work-orders?status=active&resumedFromMute=true&pageSize=100";

    /// <summary>第 2 天靜音 5 天、第 4 天解除：解除後「只看自靜音恢復」找得到這張單，靜音期間找不到</summary>
    internal static Scenario BuildS9Resumed() => new Scenario("S9 解除後只看自靜音恢復")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.Mute("admin1", Disk, DiskId, 5, "維修窗口", "pause")))
        .Day(3, d => d.Probe("resumed", "admin1", ResumedFilterUrl))
        .Day(4, d => d
            .Act(ScenarioActions.Unmute("admin1", Disk, DiskId))
            .Probe("resumed", "admin1", ResumedFilterUrl)
            .Probe("admin-all", "admin1", "api/work-orders?status=active&pageSize=100"))
        .Days(4)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S9r.d3.muting-not-resumed", 3, "靜音中（尚未解除）「只看自靜音恢復」不列這張單", "0", d => d.Probe("resumed").Items("items").Count.ToString()))
        .Expect(Expect.ActionStatus(4, 0, 200))
        .Expect(Check("S9r.d4.resumed-found", 4, "第 4 天解除後，「只看自靜音恢復」找得到這張單", "1", d =>
            RowOf(d.Probe("resumed"), OrderOf(d, "h1").OrderId) is { } ? "1" : d.Probe("resumed").Items("items").Count.ToString()))
        .Expect(Check("S9r.d4.resumed-mark", 4, "總覽的列標出恢復日（有恢復日、不再是暫停）", "有/否", d =>
        {
            var row = RowOf(d.Probe("admin-all"), OrderOf(d, "h1").OrderId);
            return row is { } r ? $"{(string.IsNullOrEmpty(r.Str("resumedFromMuteAt")) ? "無" : "有")}/{Flag(r.Flag("paused"))}" : "（總覽沒有這張單）";
        }));

    /// <summary>第 2 天靜音 2 天（迄日＝第 3 天）：迄日當天仍靜音，隔天（第 4 天）不再靜音</summary>
    internal static Scenario BuildS9MuteEnd() => new Scenario("S9 靜音迄日邊界")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.Mute("admin1", Disk, DiskId, 2, "維修窗口", "pause")))
        .Day(3, d => d.Probe("admin-all", "admin1", "api/work-orders?status=active&pageSize=100"))
        .Day(4, d => d.Probe("admin-all", "admin1", "api/work-orders?status=active&pageSize=100"))
        .Days(4)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S9e.d2.paused-notice", 2, "靜音當天處理人只有一則暫停通知", "mute_paused", d => OutboxKinds(d, "h1")))
        .Expect(Check("S9e.d3.no-notice", 3, "迄日當天沒有新通知", "", d => OutboxKinds(d, "h1")))
        .Expect(Check("S9e.d4.no-expire-notice", 4, "靜音自然到期（迄日隔天）不發通知", "", d => OutboxKinds(d, "h1")))
        .Expect(Check("S9e.d3.still-muted", 3, "靜音迄日（第 3 天）當天仍靜音", "是", d =>
            RowOf(d.Probe("admin-all"), OrderOf(d, "h1").OrderId) is { } r ? Flag(r.Flag("paused")) : "（總覽沒有這張單）"))
        .Expect(Check("S9e.d4.not-muted", 4, "迄日隔天（第 4 天）不再靜音", "否", d =>
            RowOf(d.Probe("admin-all"), OrderOf(d, "h1").OrderId) is { } r ? Flag(r.Flag("paused")) : "（總覽沒有這張單）"));

    // ── S10：靜音（代為結案）→ 解除 → 問題再出現 ──────────────────────────────

    internal static Scenario BuildS10() => new Scenario("S10 靜音代為結案後再出現")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.Mute("admin1", Disk, DiskId, 3, "維修窗口", "close")))
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.Unmute("admin1", Disk, DiskId)))
        .Day(4, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Days(4)
        .Expect(Check("S10.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S10.d2.closed", 2, "代為結案：單已結案", "已結案(admin_closed)", d => OrderOf(d, "h1").State))
        .Expect(Check("S10.d2.only-closed-notice", 2, "靜音並代為結案：處理人只有結案一則（不另發暫停），帶靜音迄日與原因",
            $"admin_closed:wont_fix:靜音：維修窗口:{DateTime.Today.AddDays(-2 + 2):yyyy-MM-dd}:False", d => OutboxDetail(d, "h1")))
        .Expect(Check("S10.d3.muted-skip", 3, "靜音期間再出現不派工（muted）", "skipped:muted", d => Decision(d, "HOST-A")))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("S10.d4.dispatch-again", 4, "解除後問題再出現時照常派工", "delivered", d => Decision(d, "HOST-A")))
        .Expect(Check("S10.d4.holder", 4, "解除後再出現有進行中的單（派給 h1）", "h1", d => HolderOf(d, "HOST-A")));
}
