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
        .Day(3, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Days(3)
        .Expect(Check("B3.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("B3.d3.decision", 3, block ? "勾選不再派工：隔夜同問題再出現不派工" : "不勾選：隔夜同問題再出現照常派工",
            block ? "skipped:gate_dismissed" : "delivered", d => Decision(d, "HOST-A")))
        .Expect(Check("B3.d3.holder", 3, block ? "勾選時沒有新的進行中單" : "不勾選時有新的進行中單（派給 h1）",
            block ? "（沒有進行中的單）" : "h1", d => HolderOf(d, "HOST-A")));

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
    public void B6a_取消交辦後原處理人下一個請求看不到該主機() => R("B6取消").AssertExpectations();

    [Fact]
    public void B6b_結案的案件原處理人仍打得開處理紀錄() => R("B6結案").AssertExpectations();

    [Fact]
    public void B6c_改派出去後原處理人不再有案件授與() => R("B6改派").AssertExpectations();
}
