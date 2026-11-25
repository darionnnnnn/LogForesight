using System.Text.Json;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

// 批次 S：高嚴重度的「不再打擾」滿 30 天解除一次。時間軸平移到生效日期（2026-11-25）之後，不依賴真實日期。
public sealed partial class HandoffScenarioTests
{
    /// <summary>
    /// 第 1 天夜間派工給 h1；第 2 天 h1 回「不處理」（結案日＝第 2 天的執行日）。距結案日 30 天（情境第 32 天）夜間仍不派，
    /// 31 天（第 33 天）再派、新成員帶「先前判定不處理」、單歷程有系統事件；第 34 天 h1 又回不處理 → 自新案件重新計：第 35 天不派。
    /// 第 10 天待派頁的清單對這件高嚴重度顯示剩餘天數。
    /// </summary>
    internal static Scenario BuildDismissalRelease() => new Scenario("S31 高嚴重度不再打擾滿 30 天後回彈")
        .WithAnchor(new DateTime(2027, 1, 20))
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "wont_fix", "備品停產，不處理")))
        .Day(10, d => d.Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Day(32, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(33, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Probe("members", "admin1", ctx => OrderUrl(ctx, "h1") + "/members?pageSize=50")
            .Probe("detail", "admin1", ctx => OrderUrl(ctx, "h1"))
            .Probe("timeline", "admin1", ctx => OrderUrl(ctx, "h1") + "/timeline"))
        .Day(34, d => d.Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "wont_fix", "還是不處理")))
        .Day(35, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)).Probe("dismissed", "admin1", "api/work-orders/dismissed"))
        .Days(35)
        .Expect(Check("S31.d1.setup", 1, "前置：第 1 天派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("S31.d10.remaining", 10, "待派頁清單顯示高嚴重度剩餘天數（距結案 8 天，剩 23 天）", "23", d =>
            DismissedRows(d.Probe("dismissed")).Select(r => r.GetProperty("releaseInDays").GetInt32()).Single().ToString()))
        .Expect(Check("S31.d32.still-blocked", 32, "距結案第 30 天仍不派工", "skipped:gate_dismissed", d => Decision(d, "HOST-A")))
        .Expect(Check("S31.d33.released", 33, "距結案第 31 天夜間再派", "delivered", d => Decision(d, "HOST-A")))
        .Expect(Check("S31.d33.holder", 33, "新的進行中單派給 h1", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Check("S31.d33.mark", 33, "新成員帶先前判定不處理（狀態、判定人）", "wont_fix/h1", d =>
        {
            var prior = d.Probe("members").Items("items").Single().GetProperty("priorDismissal");
            return $"{prior.Str("status")}/{prior.Str("account")}";
        }))
        .Expect(Check("S31.d33.header", 33, "單頭帶這類成員的主機數與回覆提示布林", "1/是", d =>
        {
            var detail = d.Probe("detail").Data!.Value;
            return $"{detail.GetProperty("priorDismissalHosts").GetInt32()}/{Flag(detail.GetProperty("dismissalReleases").GetBoolean())}";
        }))
        .Expect(Check("S31.d33.event", 33, "單歷程有一筆系統事件（合併成一筆）", "1/1 台先前判定不處理已滿 30 天，重新派工", d =>
        {
            var events = d.Probe("timeline").Data!.Value.EnumerateArray()
                .Where(e => e.Str("action") == WorkOrderEventActions.DismissalReleased).ToList();
            return $"{events.Count}/{(events.Count == 1 ? events[0].Str("note") : "")}";
        }))
        .Expect(Expect.ActionStatus(34, 0, 200))
        .Expect(Check("S31.d35.recount", 35, "再回不處理後自新案件重新計：第 35 天不派", "skipped:gate_dismissed", d => Decision(d, "HOST-A")))
        .Expect(Check("S31.d35.listed", 35, "清單重新列出，剩餘天數重新計（距結案 1 天，剩 30 天）", "30", d =>
            DismissedRows(d.Probe("dismissed")).Select(r => r.GetProperty("releaseInDays").GetInt32()).Single().ToString()));

    [Fact]
    public void S31_高嚴重度不再打擾滿30天回彈_帶標記_有系統事件_再回不處理重新計() => R("R31回彈").AssertExpectations();
}
