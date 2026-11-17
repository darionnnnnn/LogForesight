using System.Text.Json;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 第 76 輪 E3：管理者與處理人的串接情境（S1～S18）。
/// 預期行為定稿在評估記錄的「E3 串接情境 → 預期行為」表，這裡照表逐句寫成預期，跑現況，
/// 與預期不同的列入已知差異（鍵 E3-S{情境}-{序號}）。看不到的東西（例如「管理者被主動告知」）
/// 斷言「存在告知管道」：通知佇列、寄出的信、徽章 API 或清單篩選找得到。
///
/// 時間：交辦、靜音、觀察中的「現在」「今天」取自站台 DI 的 <see cref="TimeProvider"/>，驅動器每天開始前把假時鐘撥到
/// 該天的執行當天（資料日隔天 09:00），所以建單、稽核、靜音起算、期限驗證與到期判定都隨「第 N 天」前進。
/// S3 的提醒功能尚未存在，照現況列已知差異 E3-S3-1。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed partial class HandoffScenarioTests : IClassFixture<HandoffScenarioTests.Fixture>
{
    private readonly Fixture _fx;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public HandoffScenarioTests(Fixture fx, Xunit.Abstractions.ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    private const string Disk = "disk";
    private const int DiskId = 153;
    private const string SecAuditing = "Microsoft-Windows-Security-Auditing";

    // ── 共用輔助 ──────────────────────────────────────────────────────────

    private static UserSpec Admin(string account, bool email = true) =>
        new(account, UserRole.Admin) { HasEmail = email };

    private static UserSpec Handler(string account, string[] groups, bool paused = false, bool email = true, bool pool = true) =>
        new(account) { AuthorizedHostGroups = groups, InDispatchPool = pool, DispatchPaused = paused, HasEmail = email };

    private static string D(ScenarioActionContext ctx, int day) => ctx.DataDateOf(day).ToString("yyyy-MM-dd");

    private static string Gaps(ScenarioActionContext ctx) =>
        $"api/work-orders/gaps?from={D(ctx, 1)}&to={D(ctx, ctx.Day)}";

    /// <summary>取實際值；讀不到時把原因當實際值，訊息才讀得懂</summary>
    private static ScenarioExpectation Check(string key, int day, string description, string expected, Func<DayResult, string> actual) =>
        Expect.Value(key, day, description, expected, r =>
        {
            try
            {
                return actual(r.Day(day));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException
                                           or NullReferenceException or FormatException)
            {
                return $"（讀不到：{ex.Message}）";
            }
        });

    private static OrderView OrderOf(DayResult d, string handler, int nth = 0) =>
        d.Orders.Where(o => o.Handler == handler).OrderBy(o => o.OrderId).ElementAt(nth);

    private static string Orders(DayResult d, string handler) =>
        string.Join(",", d.Orders.Where(o => o.Handler == handler).OrderBy(o => o.OrderId)
            .Select(o => $"#{o.OrderId}[{string.Join("+", o.Members.Select(m => m.Host))}]{o.State}"));

    private static JsonElement? RowOf(ProbeView p, long orderId) =>
        p.Items("items").Select(e => (JsonElement?)e).FirstOrDefault(e => e!.Value.Long("workOrderId") == orderId);

    private static string Flag(bool value) => value ? "是" : "否";

    private static int Outbox(DayResult d, string recipient) => d.OutboxAdded.Count(o => o.Recipient == recipient);

    private static string OutboxKinds(DayResult d, string recipient) =>
        string.Join(",", d.OutboxAdded.Where(o => o.Recipient == recipient).Select(o => o.Kind).OrderBy(k => k, StringComparer.Ordinal));

    private static string Decision(DayResult d, string host, string issueContains = Disk)
    {
        var x = d.Decisions.FirstOrDefault(v => v.Host == host && v.Issue.Contains(issueContains, StringComparison.Ordinal));
        return x == null ? "（沒有決策）" : x.Reason == null ? x.Outcome : $"{x.Outcome}:{x.Reason}";
    }

    private static string HolderOf(DayResult d, string host) =>
        d.Orders.Where(o => o.State == "進行中").SelectMany(o => o.Members.Where(m => m.Host == host && m.Status != IssueHandlingStatuses.Resolved)
            .Select(_ => o.Handler)).FirstOrDefault() ?? "（沒有進行中的單）";

    // ── S1：夜間派工 → 處理人回覆已處理 ───────────────────────────────────────

    internal static Scenario BuildS1() => new Scenario("S1 夜間派工到處理人回覆已處理")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-C") { Groups = new[] { "G2" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G2" }))
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-C", ScenarioEvents.DiskErrors(12))
            .Probe("badge-h1", "h1", "api/handlers/me/badge")
            .Probe("badge-h2", "h2", "api/handlers/me/badge")
            .Probe("admin-active", "admin1", "api/work-orders?status=active&pageSize=100"))
        .Day(2, d => d
            .Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "resolved", "已更換硬碟"))
            .Probe("badge-h1", "h1", "api/handlers/me/badge")
            .Probe("admin-closed", "admin1", "api/work-orders?status=closed&pageSize=100")
            .Probe("admin-active", "admin1", "api/work-orders?status=active&pageSize=100"))
        .Days(2)
        .Expect(Check("S1.d1.h1-order", 1, "第 1 天自動派工把 HOST-A 的單建給看得到它的 h1", "1", d => d.NewOrders.Count(o => o.Handler == "h1" && o.Origin == WorkOrderOrigins.AutoDispatch).ToString()))
        .Expect(Check("S1.d1.h2-order", 1, "第 1 天 HOST-C 只有 h2 看得到，單建給 h2", "1", d => d.NewOrders.Count(o => o.Handler == "h2").ToString()))
        .Expect(Check("S1.d1.no-cross", 1, "h1 的單不含他看不到的 HOST-C", "HOST-A", d => string.Join(",", OrderOf(d, "h1").Members.Select(m => m.Host))))
        .Expect(Expect.OutboxCount(1, WorkOrderOutboxKinds.Created, "h1", 1))
        .Expect(Expect.OutboxCount(1, WorkOrderOutboxKinds.Created, "h2", 1))
        .Expect(Check("S1.d1.badge", 1, "處理人徽章（我的交辦）看得到 1 張進行中單", "1/1", d => $"{d.Probe("badge-h1").Int("activeWorkOrders")}/{d.Probe("badge-h2").Int("activeWorkOrders")}"))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S1.d2.member", 2, "第 2 天 h1 的成員已處理", IssueHandlingStatuses.Resolved, d => OrderOf(d, "h1").Members.Single().Status))
        .Expect(Check("S1.d2.state", 2, "第 2 天 h1 的單推導為已結案（全部成員結案）", "已結案(all_closed)", d => OrderOf(d, "h1").State))
        .Expect(Check("S1.d2.audit", 2, "稽核有處理人的回覆紀錄", "1", d => d.AuditAdded.Count(a => a.Action == AuditActions.HandlingStatus && a.Account == "h1").ToString()))
        .Expect(Check("S1.d2.admin-closed", 2, "管理者總覽（已結案）看得到這張單與結案原因", "all_closed", d =>
            RowOf(d.Probe("admin-closed"), OrderOf(d, "h1").OrderId) is { } row ? row.Str("closedReason") : "（總覽沒有這張單）"))
        .Expect(Check("S1.d2.admin-active", 2, "管理者的進行中清單不再列出已結案的單，仍列出 h2 的單", "1", d => d.Probe("admin-active").Items("items").Count.ToString()))
        .Expect(Check("S1.d2.badge", 2, "h1 徽章歸零", "0", d => d.Probe("badge-h1").Int("activeWorkOrders").ToString()));

    // ── S2：處理人回「無法處理」→ 管理者退回／改派／代為結案 ─────────────────────

    internal static Scenario BuildS2(string branch)
    {
        var s = new Scenario($"S2 無法處理後{branch}")
            .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
            .WithUser(Admin("admin1"))
            .WithUser(Handler("h1", new[] { "G1" }))
            .WithUser(Handler("h2", new[] { "G1" }))
            .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
            .Day(2, d => d
                .Act(
                    ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "escalated"),
                    ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "escalated", "備品缺貨，無法自行更換"))
                .Probe("admin-escalated", "admin1", "api/work-orders?status=escalated&pageSize=100")
                .Probe("admin-unread", "admin1", "api/work-orders/unread-count"))
            .Expect(Expect.ActionStatus(2, 0, 400))
            .Expect(Check("S2.d2.reply-needs-note-message", 2, "無原因的「無法處理」被擋，訊息說明要填原因", "是", d => Flag(d.Actions[0].Message?.Contains("原因", StringComparison.Ordinal) == true)))
            .Expect(Expect.ActionStatus(2, 1, 200))
            .Expect(Check("S2.d2.member", 2, "成員狀態為上報中", IssueHandlingStatuses.Escalated, d => OrderOf(d, "h1").Members.Single().Status))
            .Expect(Check("S2.d2.escalated-list", 2, "管理者以「上報中」篩選找得到這張單（上報中成員 1）", "1", d =>
                RowOf(d.Probe("admin-escalated"), OrderOf(d, "h1").OrderId) is { } row ? row.GetProperty("counts").Int("escalated").ToString() : "（找不到）"))
            .Expect(Check("S2.d2.admin-mail", 2, "管理者收到上報信（寄給 admin1 的信 1 封，內含單號與原因）", "1/是", d =>
            {
                var mails = d.MailsSent.Where(m => m.To.Contains("admin1@scenario.test")).ToList();
                return $"{mails.Count}/{Flag(mails.Any(m => m.Body.Contains("備品缺貨", StringComparison.Ordinal) && m.Body.Contains($"單號 {OrderOf(d, "h1").OrderId}", StringComparison.Ordinal)))}";
            }))
            .Expect(Check("S2.d2.unread-badge", 2, "管理者側欄徽章（未讀回覆）有數字", "1", d => d.Probe("admin-unread").Int("count").ToString()));

        switch (branch)
        {
            case "退回":
                s.Day(3, d => d
                    .Act(ScenarioActions.Return("admin1", OrderRef.ForHandler("h1"), "請改用備品庫的硬碟再試一次"))
                    .Probe("h1-badge", "h1", "api/handlers/me/badge"))
                    .Days(3)
                    .Expect(Expect.ActionStatus(3, 0, 200))
                    .Expect(Check("S2.d3.member", 3, "退回後成員回處理中", IssueHandlingStatuses.InProgress, d => OrderOf(d, "h1").Members.Single().Status))
                    .Expect(Expect.OutboxCount(3, WorkOrderOutboxKinds.Returned, "h1", 1))
                    .Expect(Check("S2.d3.h1-badge", 3, "處理人徽章仍看得到這張單", "1", d => d.Probe("h1-badge").Int("activeWorkOrders").ToString()));
                break;
            case "改派":
                s.Day(3, d => d
                    .Act(ScenarioActions.Reassign("admin1", OrderRef.ForHandler("h1"), "h2")))
                    .Days(3)
                    .Expect(Expect.ActionStatus(3, 0, 200))
                    .Expect(Check("S2.d3.new-handler", 3, "新處理人收到建立通知", "created", d => OutboxKinds(d, "h2")))
                    .Expect(Check("S2.d3.old-handler", 3, "原處理人收到轉出通知", "transferred", d => OutboxKinds(d, "h1")))
                    .Expect(Check("S2.d3.order-moved", 3, "單改由新處理人持有且仍進行中", "進行中", d => OrderOf(d, "h2").State));
                break;
            case "代為結案":
                s.Day(3, d => d
                    .Act(ScenarioActions.AdminClose("admin1", OrderRef.ForHandler("h1"), "wont_fix", "備品停產，不處理")))
                    .Days(3)
                    .Expect(Expect.ActionStatus(3, 0, 200))
                    .Expect(Check("S2.d3.state", 3, "代為結案後單已結案", "已結案(admin_closed)", d => OrderOf(d, "h1").State))
                    .Expect(Check("S2.d3.handler-notified", 3, "處理人通知佇列有這次結案的通知（任何種類）", "有通知", d => Outbox(d, "h1") > 0 ? "有通知" : "沒有任何通知"))
                    .KnownDifference("E3-S2-1", "S2.d3.handler-notified");
                break;
        }

        return s;
    }

    // ── 情境目錄與測試 ─────────────────────────────────────────────────────

    internal static Scenario Build(string key) => key switch
    {
        "S1" => BuildS1(),
        "S2退回" => BuildS2("退回"),
        "S2改派" => BuildS2("改派"),
        "S2代為結案" => BuildS2("代為結案"),
        _ => BuildMore(key)
    };

    private ScenarioResult R(string key) => _fx.Get(key);

    [Fact]
    public void S1_夜間派工到處理人回覆已處理() => R("S1").AssertExpectations();

    [Fact]
    public void S2_無法處理後退回() => R("S2退回").AssertExpectations();

    [Fact]
    public void S2_無法處理後改派() => R("S2改派").AssertExpectations();

    [Fact]
    public void S2_無法處理後代為結案() => R("S2代為結案").AssertExpectations();

    [Fact]
    public void S3_無法處理後管理者未處理() => R("S3").AssertExpectations();

    [Fact]
    public void S3_上報超過數天未處理_管理者收到提醒() => R("S3提醒").AssertExpectations();

    [Fact]
    public void S4_處理人被停用() => R("S4").AssertExpectations();

    [Fact]
    public void S5_處理人暫停接單() => R("S5").AssertExpectations();

    [Fact]
    public void S6_處理人的主機授權被移除() => R("S6").AssertExpectations();

    [Fact]
    public void S7_主機停用() => R("S7主機停用").AssertExpectations();

    [Fact]
    public void S7_主機改群組() => R("S7改群組").AssertExpectations();

    [Fact]
    public void S7_換主機負責人() => R("S7換負責人").AssertExpectations();

    [Fact]
    public void S8_結案後再出現() => R("S8").AssertExpectations();

    [Fact]
    public void S9_靜音暫停再解除() => R("S9").AssertExpectations();

    [Fact]
    public void S9_解除後只看自靜音恢復找得到() => R("S9恢復").AssertExpectations();

    [Fact]
    public void S9_靜音迄日當天仍靜音_隔天不靜音() => R("S9迄日").AssertExpectations();

    [Fact]
    public void S10_靜音代為結案後再出現() => R("S10").AssertExpectations();

    [Fact]
    public void S11_派不出去_無派工池() => R("S11無派工池").AssertExpectations();

    [Fact]
    public void S11_派不出去_派工池全部暫停() => R("S11全部暫停").AssertExpectations();

    [Fact]
    public void S11_派不出去_池內都看不到主機() => R("S11都看不到").AssertExpectations();

    [Fact]
    public void S12_兩位管理者操作同一張單() => R("S12").AssertExpectations();

    [Fact]
    public void S13_期限不可設在過去() => R("S13前置").AssertExpectations();

    [Fact]
    public void S13_觀察中到期後列入逾期篩選() => R("S13到期").AssertExpectations();

    [Fact]
    public void S14_處理人沒有email() => R("S14處理人無email").AssertExpectations();

    [Fact]
    public void S14_管理者都沒有email() => R("S14管理者無email").AssertExpectations();

    [Fact]
    public void S15_多台部分處理() => R("S15").AssertExpectations();

    [Fact]
    public void S16_問題負責人優先於自動派工() => R("S16").AssertExpectations();

    [Fact]
    public void S17_無處理能力的角色開我的交辦() => R("S17").AssertExpectations();

    [Fact]
    public void S18_改派併入目標單() => R("S18").AssertExpectations();

    // ── 三個已確認缺口的直接重現（不經預期機制，直接斷言現況；缺口修掉時這些會紅，同步移除已知差異）──────────

    [Fact]
    public void 直接重現_S10_靜音代為結案後解除_問題再出現時不派工()
    {
        var result = R("S10");
        var d4 = result.Day(4);
        // 靜音以「不處理」代為結案，等同處理人判定的不處理：「不再打擾」擋住，沒有新單
        Assert.Equal("skipped:gate_dismissed", Decision(d4, "HOST-A"));
        Assert.Empty(d4.NewOrders);
        Assert.Equal(IssueHandlingStatuses.WontFix, OrderOf(result.Day(2), "h1").Members.Single().Status);
        Assert.Equal("h1", result.Day(1).Orders.Single().Handler);
    }

    [Fact]
    public void 直接重現_S2_代為結案後處理人通知佇列沒有任何一筆()
    {
        var d3 = R("S2代為結案").Day(3);
        Assert.True(d3.Actions[0].Succeeded);
        Assert.StartsWith("已結案", OrderOf(d3, "h1").State);
        Assert.Empty(d3.OutboxAdded);
        Assert.Empty(d3.MailsSent);
    }

    [Theory]
    [InlineData("S4")]
    [InlineData("S5")]
    public void 直接重現_S4_S5_處理人停用或暫停後_新主機仍被續掛進他的單(string key)
    {
        var result = R(key);
        Assert.True(result.Day(2).Actions[0].Succeeded);
        var d3 = result.Day(3);
        Assert.Equal("h1", HolderOf(d3, "HOST-B"));
        Assert.Equal("h1", HolderOf(d3, "HOST-C"));
        Assert.Contains(d3.OutboxAdded, o => o.Kind == WorkOrderOutboxKinds.Appended && o.Recipient == "h1");
        Assert.DoesNotContain(d3.Orders, o => o.Handler == "h2");
    }

    /// <summary>每個情境只在第一次被讀取時才跑（一次約 1 秒）；各情境互相獨立</summary>
    public sealed class Fixture : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ScenarioResult>> _runs = new();

        public ScenarioResult Get(string key) =>
            _runs.GetOrAdd(key, k => new Lazy<ScenarioResult>(() => ScenarioDriver.Run(Build(k)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        public void Dispose() { }
    }

    /// <summary>輸出預覽：把每個情境當天的動作回應、探針、通知與決策印出來，方便對照預期（永遠通過）</summary>
    internal static string Dump(ScenarioResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {result.Name}");
        foreach (var day in result.Days)
        {
            sb.AppendLine($"## 第 {day.Day} 天");
            foreach (var x in day.Decisions) sb.AppendLine($"  決策 {x.Host} {x.Issue}：{x.Outcome}:{x.Reason} 單#{x.WorkOrderId}");
            foreach (var l in day.Dispatch.PerHandlerLines) sb.AppendLine($"  派工彙總 {l}");
            foreach (var o in day.Orders) sb.AppendLine($"  單#{o.OrderId} {o.Handler} {o.Origin} {o.State} [{string.Join(";", o.Members.Select(m => m.Host + ":" + m.Status))}]");
            foreach (var o in day.OutboxAdded) sb.AppendLine($"  通知 {o.Phase} {o.Kind}→{o.Recipient} 單#{o.WorkOrderId}");
            foreach (var m in day.MailsSent) sb.AppendLine($"  信 →{string.Join(",", m.To)}｜{m.Subject}");
            foreach (var a in day.AuditAdded) sb.AppendLine($"  稽核 {a.Account} {a.Action}｜{a.Summary}");
            foreach (var a in day.Actions) sb.AppendLine($"  動作 {a.Actor}：{a.Description} → {a.StatusCode} {a.ErrorCode} {a.Message} {Trim(a.Body)}");
            foreach (var p in day.Probes) sb.AppendLine($"  探針 {p.Name}（{p.Actor}）→ {p.StatusCode} {Trim(p.Body)}");
        }
        return sb.ToString();
    }

    private static string Trim(string? s) => s == null ? "" : s.Length <= 600 ? s : s[..600] + "…";

    [Theory]
    [InlineData("S1")]
    [InlineData("S2退回")]
    [InlineData("S2改派")]
    [InlineData("S2代為結案")]
    [InlineData("S3")]
    [InlineData("S3提醒")]
    [InlineData("S4")]
    [InlineData("S5")]
    [InlineData("S6")]
    [InlineData("S7主機停用")]
    [InlineData("S7改群組")]
    [InlineData("S7換負責人")]
    [InlineData("S8")]
    [InlineData("S9")]
    [InlineData("S9恢復")]
    [InlineData("S9迄日")]
    [InlineData("S10")]
    [InlineData("S11無派工池")]
    [InlineData("S11全部暫停")]
    [InlineData("S11都看不到")]
    [InlineData("S12")]
    [InlineData("S13前置")]
    [InlineData("S13到期")]
    [InlineData("S14處理人無email")]
    [InlineData("S14管理者無email")]
    [InlineData("S15")]
    [InlineData("S16")]
    [InlineData("S17")]
    [InlineData("S18")]
    public void 輸出預覽(string key) => _output.WriteLine(Dump(R(key)));
}
