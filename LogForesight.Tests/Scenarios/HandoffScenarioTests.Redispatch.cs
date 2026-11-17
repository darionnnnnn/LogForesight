using Xunit;

namespace LogForesight.Tests.Scenarios;

// 「不再打擾」閘門的結案欄位（B3）與取消收回案件授與（B6）。
public sealed partial class HandoffScenarioTests
{
    private static string HandlingUrl(ScenarioActionContext ctx, string host) =>
        $"api/records/{ctx.HostId(host)}/{ctx.DataDateOf(1):yyyy-MM-dd}/handling";

    /// <summary>管理者代為結案選「不處理」；<paramref name="block"/> 為對話框的「之後不再自動派工」勾選</summary>
    internal static Scenario BuildAdminCloseRedispatch(string name, bool block) => new Scenario(name)
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.AdminClose("admin1", OrderRef.ForHandler("h1"), "wont_fix", "備品停產，不處理", block)))
        .Day(3, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Days(3)
        .Expect(Check("B3.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("B3.d3.decision", 3, block ? "勾選不再派工：隔夜同問題再出現不派工" : "不勾選：隔夜同問題再出現照常派工",
            block ? "skipped:gate_dismissed" : "delivered", d => Decision(d, "HOST-A")))
        .Expect(Check("B3.d3.holder", 3, block ? "勾選時沒有新的進行中單" : "不勾選時有新的進行中單（派給 h1）",
            block ? "（沒有進行中的單）" : "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Check("B3.d3.listed", 3, block ? "勾選時待派頁的「不再自動派工」清單列出這一項" : "不勾選時清單沒有這一項",
            block ? "HOST-A" : "（空）", d =>
            {
                var hosts = DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")).ToList();
                return hosts.Count == 0 ? "（空）" : string.Join(",", hosts);
            }));

    private static List<System.Text.Json.JsonElement> DismissedRows(ProbeView probe) =>
        probe.Items("groups").SelectMany(g => g.GetProperty("rows").EnumerateArray()).ToList();

    /// <summary>兩台主機同一個問題：管理者第 2 天代為結案並勾選不再派工</summary>
    private static Scenario BlockedPair(string name) => new Scenario(name)
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(new UserSpec("mgr1", UserRole.Manager))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.AdminClose("admin1", OrderRef.ForHandler("h1"), "wont_fix", "備品停產，不處理", true)));

    /// <summary>B4-b、B4-f：恢復一筆後下一夜會派工；同一問題其他主機仍被擋；稽核摘要含主機與問題</summary>
    internal static Scenario BuildRestoreOne() => BlockedPair("B4 恢復單筆")
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.RestoreDismissed("admin1", "HOST-A", Disk, DiskId))
            .Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Day(4, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Days(4)
        .Expect(Check("B4b.d3.both-blocked", 3, "前置：恢復前兩台都不派工", "skipped:gate_dismissed/skipped:gate_dismissed", d =>
            $"{Decision(d, "HOST-A")}/{Decision(d, "HOST-B")}"))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("B4b.d3.list-after", 3, "恢復後清單只剩 HOST-B", "HOST-B", d => string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")))))
        .Expect(Check("B4b.d4.restored-delivers", 4, "恢復的 HOST-A 下一夜照常派工", "delivered", d => Decision(d, "HOST-A")))
        .Expect(Check("B4b.d4.other-blocked", 4, "同一問題的 HOST-B 仍被擋", "skipped:gate_dismissed", d => Decision(d, "HOST-B")))
        .Expect(Check("B4b.d4.list", 4, "清單仍列 HOST-B", "HOST-B", d => string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")))))
        .Expect(Check("B4f.d3.audit", 3, "稽核有恢復紀錄，摘要含主機與問題", "1/是/是", d =>
        {
            var a = d.AuditAdded.Where(x => x.Action == AuditActions.WorkOrderDismissRestore).ToList();
            return $"{a.Count}/{Flag(a.Any(x => x.Summary.Contains("HOST-A", StringComparison.Ordinal)))}/{Flag(a.Any(x => x.Summary.Contains("disk", StringComparison.Ordinal)))}";
        }));

    /// <summary>B4-c：整個問題一起恢復 → 該問題全部主機下一夜都會判斷</summary>
    internal static Scenario BuildRestoreIssue() => BlockedPair("B4 恢復整個問題")
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.RestoreDismissedIssue("admin1", Disk, DiskId))
            .Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Day(4, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12)))
        .Days(4)
        .Expect(Check("B4c.d3.both-blocked", 3, "前置：恢復前兩台都不派工", "skipped:gate_dismissed/skipped:gate_dismissed", d =>
            $"{Decision(d, "HOST-A")}/{Decision(d, "HOST-B")}"))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("B4c.d3.list-empty", 3, "恢復後清單是空的", "0", d => DismissedRows(d.Probe("dismissed")).Count.ToString()))
        .Expect(Check("B4c.d4.both-delivered", 4, "兩台下一夜都照常派工", "delivered/delivered", d => $"{Decision(d, "HOST-A")}/{Decision(d, "HOST-B")}"))
        .Expect(Check("B4f.d3.audit-count", 3, "整個問題恢復的稽核摘要含台數", "是", d =>
            Flag(d.AuditAdded.Any(x => x.Action == AuditActions.WorkOrderDismissRestore && x.Summary.Contains("2 台", StringComparison.Ordinal)))));

    /// <summary>B4-d：恢復後處理人再回不處理 → 又被擋</summary>
    internal static Scenario BuildRestoreThenDismissAgain() => new Scenario("B4 恢復後再回不處理")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.AdminClose("admin1", OrderRef.ForHandler("h1"), "wont_fix", "備品停產，不處理", true)))
        .Day(3, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.RestoreDismissed("admin1", "HOST-A", Disk, DiskId)))
        .Day(4, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "wont_fix", "還是不處理")))
        .Day(5, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Days(5)
        .Expect(Check("B4d.d3.blocked", 3, "前置：恢復前被擋", "skipped:gate_dismissed", d => Decision(d, "HOST-A")))
        .Expect(Check("B4d.d4.delivered", 4, "恢復後下一夜派工", "delivered", d => Decision(d, "HOST-A")))
        .Expect(Expect.ActionStatus(4, 0, 200))
        .Expect(Check("B4d.d5.blocked-again", 5, "處理人再回不處理後又被擋", "skipped:gate_dismissed", d => Decision(d, "HOST-A")))
        .Expect(Check("B4d.d5.listed-again", 5, "清單重新列出（新案件欄位為空值）", "HOST-A", d =>
            string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")))));

    /// <summary>B4-e：沒有指派（立即派工）能力的角色呼叫恢復端點 → 403，且不改任何案件</summary>
    internal static Scenario BuildRestoreForbidden() => BlockedPair("B4 無權限恢復")
        .WithUser(new UserSpec("dev1", UserRole.Dev))
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Act(
                ScenarioActions.RestoreDismissed("mgr1", "HOST-A", Disk, DiskId),
                ScenarioActions.RestoreDismissedIssue("mgr1", Disk, DiskId),
                ScenarioActions.RestoreDismissed("h1", "HOST-A", Disk, DiskId),
                ScenarioActions.RestoreDismissedIssue("dev1", Disk, DiskId))
            .Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Day(4, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Events("HOST-B", ScenarioEvents.DiskErrors(12)))
        .Days(4)
        .Expect(Expect.ActionStatus(3, 0, 403))
        .Expect(Expect.ActionStatus(3, 1, 403))
        .Expect(Expect.ActionStatus(3, 2, 403))
        .Expect(Expect.ActionStatus(3, 3, 403))
        .Expect(Check("B4e.d3.unchanged", 3, "被拒絕後清單不變", "HOST-A,HOST-B", d =>
            string.Join(",", DismissedRows(d.Probe("dismissed")).Select(r => r.Str("hostName")).OrderBy(h => h, StringComparer.Ordinal))))
        .Expect(Check("B4e.d4.still-blocked", 4, "下一夜仍不派工", "skipped:gate_dismissed/skipped:gate_dismissed", d =>
            $"{Decision(d, "HOST-A")}/{Decision(d, "HOST-B")}"));

    /// <summary>
    /// 處理人只靠案件授與看得到 HOST-A（群組授權已移除）：第 2 天確認看得到，第 3 天取消交辦後看不到。
    /// </summary>
    internal static Scenario BuildCancelRevokesGrant() => new Scenario("B6 取消交辦收回案件授與")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(ScenarioActions.SetUserHostAccess("admin1", "h1"))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h1-detail", "h1", ctx => HandlingUrl(ctx, "HOST-A")))
        .Day(3, d => d
            .Act(ScenarioActions.Cancel("admin1", OrderRef.ForHandler("h1"), "管理者取消"))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h1-detail", "h1", ctx => HandlingUrl(ctx, "HOST-A")))
        .Days(3)
        .Expect(Check("B6a.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Check("B6a.d2.granted", 2, "前置：移除群組授權後，案件授與讓 h1 打得開 HOST-A 的處理紀錄（一般主機清單不列案件授與主機）", "0/200",
            d => $"{HostNames(d.Probe("h1-hosts")).Count}/{d.Probe("h1-detail").StatusCode}"))
        .Expect(Expect.ActionStatus(3, 0, 200))
        .Expect(Check("B6a.d3.revoked", 3, "取消交辦後，h1 下一個請求詳情 404（清單本來就不列案件授與主機）", "0/404",
            d => $"{HostNames(d.Probe("h1-hosts")).Count}/{d.Probe("h1-detail").StatusCode}"));

    /// <summary>結案的案件：處理人移除群組授權後仍打得開自己的處理紀錄</summary>
    internal static Scenario BuildClosedKeepsGrant() => new Scenario("B6 結案保留案件授與")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(
                ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "resolved", "已更換"),
                ScenarioActions.SetUserHostAccess("admin1", "h1"))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h1-detail", "h1", ctx => HandlingUrl(ctx, "HOST-A")))
        .Days(2)
        .Expect(Check("B6b.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Expect.ActionStatus(2, 1, 200))
        .Expect(Check("B6b.d2.still-opens", 2, "已結案的案件讓 h1 仍打得開 HOST-A 的處理紀錄", "0/200",
            d => $"{HostNames(d.Probe("h1-hosts")).Count}/{d.Probe("h1-detail").StatusCode}"));

    /// <summary>改派出去後，原處理人不再有該主機的案件授與</summary>
    internal static Scenario BuildReassignRevokesGrant() => new Scenario("B6 改派收回案件授與")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d
            .Act(
                ScenarioActions.SetUserHostAccess("admin1", "h1"),
                ScenarioActions.Reassign("admin1", OrderRef.ForHandler("h1"), "h2"))
            .Probe("h1-hosts", "h1", "api/hosts")
            .Probe("h1-detail", "h1", ctx => HandlingUrl(ctx, "HOST-A"))
            .Probe("h2-hosts", "h2", "api/hosts"))
        .Days(2)
        .Expect(Check("B6c.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Expect.ActionStatus(2, 1, 200))
        .Expect(Check("B6c.d2.old-handler", 2, "改派出去後 h1 下一個請求詳情 404", "0/404",
            d => $"{HostNames(d.Probe("h1-hosts")).Count}/{d.Probe("h1-detail").StatusCode}"))
        .Expect(Check("B6c.d2.new-handler", 2, "新處理人 h2 看得到 HOST-A", "HOST-A", d => string.Join(",", HostNames(d.Probe("h2-hosts")))));

    [Fact]
    public void B3a_管理者代為結案選不處理不勾選_隔夜再出現會派工() => R("B3不勾").AssertExpectations();

    [Fact]
    public void B3b_管理者代為結案選不處理且勾選_隔夜再出現不派工() => R("B3勾選").AssertExpectations();

    [Fact]
    public void B4b_恢復一筆後下一夜派工_同問題其他主機仍被擋() => R("B4恢復單筆").AssertExpectations();

    [Fact]
    public void B4c_整個問題一起恢復_全部主機下一夜都判斷() => R("B4整個問題").AssertExpectations();

    [Fact]
    public void B4d_恢復後處理人再回不處理_又被擋() => R("B4再回不處理").AssertExpectations();

    [Fact]
    public void B4e_沒有指派能力的角色呼叫恢復端點_403() => R("B4無權限").AssertExpectations();

    [Fact]
    public void B6a_取消交辦後原處理人下一個請求看不到該主機() => R("B6取消").AssertExpectations();

    [Fact]
    public void B6b_結案的案件原處理人仍打得開處理紀錄() => R("B6結案").AssertExpectations();

    [Fact]
    public void B6c_改派出去後原處理人不再有案件授與() => R("B6改派").AssertExpectations();
}
