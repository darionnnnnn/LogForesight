using System.Text.Json;
using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 階段 R-1 的情境測試：同一台主機的問題先被回「已處理」、隔天又再出現，新成員要標復發並帶出上次回覆。
/// 兩條建案路徑都驗：續掛（④，進既有單、新建成員案件）與自動派工（⑥，延續性偏好派回上次修好的人、建新單）。
/// 復發是讀取時推導（不存欄位），所以路徑本身不需要任何標記動作——這裡驗的是兩條路徑建出來的案件讀得到同樣的結果。
/// 使用情境驅動器的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>（該集合關閉並行）。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class RecurrenceScenarioTests
{
    private const string Disk = "disk";
    private const int DiskId = 153;

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

    private static long LatestOrderId(ScenarioActionContext ctx, string handler) =>
        ctx.Site.Get<IWorkOrderStore>().GetActiveByHandler(ctx.UserId(handler)).Max(o => o.WorkOrderId);

    private static string MembersUrl(ScenarioActionContext ctx, string handler) =>
        $"api/work-orders/{LatestOrderId(ctx, handler)}/members?status=all&pageSize=50";

    private static string DetailUrl(ScenarioActionContext ctx, string handler) =>
        $"api/work-orders/{LatestOrderId(ctx, handler)}";

    /// <summary>成員列的摘要：主機:是否復發:次數:第幾次:上次回覆狀態:上次回覆說明（逐列以「｜」相接）</summary>
    private static string MemberSummary(ProbeView probe) =>
        string.Join("｜", probe.Items("items").Select(m =>
        {
            var last = m.TryGetProperty("lastReply", out var l) && l.ValueKind == JsonValueKind.Object ? l : (JsonElement?)null;
            return $"{m.Str("hostName")}:{(m.Flag("isRecurrence") ? "復發" : "非復發")}:{m.Int("recurrenceCount")}:{m.Int("occurrence")}:" +
                   $"{(last == null ? "無" : last.Value.Str("status") + "/" + last.Value.Str("note"))}";
        }));

    private static string Flag(bool value) => value ? "是" : "否";

    /// <summary>
    /// 自動派工（⑥）：第 1 天派給 h1，第 2 天 h1 回已處理（單結案），第 3 天同一台主機同一問題再出現——
    /// 延續性偏好把它派回 h1，建新單；新單的成員讀得到復發與上次回覆，單頭的復發主機數是 1。
    /// </summary>
    internal static Scenario BuildAutoDispatchRecurrence() => new Scenario("R1 自動派工的復發")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(new UserSpec("admin1", UserRole.Admin) { HasEmail = true })
        .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true, HasEmail = true })
        .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Day(2, d => d.Act(ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "resolved", "已更換硬碟")))
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Probe("members", "admin1", ctx => MembersUrl(ctx, "h1"))
            .Probe("detail", "admin1", ctx => DetailUrl(ctx, "h1")))
        .Days(3)
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("R1.d3.new-order", 3, "第 3 天自動派工（⑥）派回 h1，建了一張新單", "1/h1", d =>
            $"{d.NewOrders.Count()}/{d.NewOrders.FirstOrDefault()?.Handler}"))
        .Expect(Check("R1.d3.member", 3, "新單的成員標復發（前案已處理、30 天內），帶上次回覆", "HOST-A:復發:1:2:resolved/已更換硬碟",
            d => MemberSummary(d.Probe("members"))))
        .Expect(Check("R1.d3.header", 3, "單頭：進行中 1 台、歷來 1 台、其中復發 1 台", "1/1/1", d =>
            $"{d.Probe("detail").Data?.Int("activeHosts")}/{d.Probe("detail").Data?.Int("totalHosts")}/{d.Probe("detail").Data?.Int("recurrenceHosts")}"))
        .Expect(Check("R1.d3.last-reply-date", 3, "上次回覆有日期（日期值本身由服務層測試以固定時鐘驗證）", "是", d =>
            Flag(d.Probe("members").Items("items").Single().GetProperty("lastReply").Str("date").Length > 0)));

    /// <summary>
    /// 續掛（④）：管理者以「G1 範圍＋續掛」建單給 h1，第 1 天 HOST-A、HOST-B 都在單內；第 2 天 h1 只回覆 HOST-A 已處理（單仍進行中，HOST-B 還在）；
    /// 第 3 天 HOST-A 同問題再出現，續掛進同一張單、建出新的成員案件。全部篩選下 HOST-A 新舊兩列相鄰、新的在前，第 N 次依建立先後。
    /// </summary>
    internal static Scenario BuildContinuationRecurrence() => new Scenario("R1 續掛的復發")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(new UserSpec("admin1", UserRole.Admin) { HasEmail = true })
        .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true, HasEmail = true })
        .WithSettings(new ScenarioSettings { AutoDispatchEnabled = false })
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.CreateOrderScoped("admin1", Disk, DiskId, "h1", WorkOrderScopes.Groups, new[] { "G1" }, autoAttach: true)))
        .Day(2, d => d
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.ReplyHosts("h1", OrderRef.ForHandler("h1"), "resolved", new[] { "HOST-A" }, "已更換硬碟")))
        .Day(3, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Probe("members", "admin1", ctx => MembersUrl(ctx, "h1"))
            .Probe("detail", "admin1", ctx => DetailUrl(ctx, "h1")))
        .Days(3)
        .Expect(Expect.ActionStatus(1, 0, 200))
        .Expect(Expect.ActionStatus(2, 0, 200))
        .Expect(Check("R1.d1.setup", 1, "前置：單內有 HOST-A、HOST-B 兩台", "HOST-A,HOST-B", d =>
            string.Join(",", d.Orders.Single().Members.Select(m => m.Host).OrderBy(x => x, StringComparer.Ordinal))))
        .Expect(Check("R1.d3.members", 3, "HOST-A 新案件標復發（第 2 次）、舊案件在後（第 1 次）；HOST-B 不是復發",
            "HOST-A:復發:1:2:resolved/已更換硬碟｜HOST-A:非復發:0:1:無｜HOST-B:非復發:0:1:無",
            d => MemberSummary(d.Probe("members"))))
        .Expect(Check("R1.d3.header", 3, "單頭：進行中 2 台、歷來 2 台、其中復發 1 台、成員 3 件", "2/2/1/3", d =>
            $"{d.Probe("detail").Data?.Int("activeHosts")}/{d.Probe("detail").Data?.Int("totalHosts")}/{d.Probe("detail").Data?.Int("recurrenceHosts")}/" +
            $"{d.Probe("detail").Data?.GetProperty("counts").Int("total")}"));

    [Fact]
    public void 自動派工_第6步派回上次修好的人_新單成員標復發並帶上次回覆() =>
        ScenarioDriver.Run(BuildAutoDispatchRecurrence()).AssertExpectations();

    [Fact]
    public void 續掛_第4步進既有單建新成員案件_標復發_全部篩選新舊相鄰() =>
        ScenarioDriver.Run(BuildContinuationRecurrence()).AssertExpectations();
}
