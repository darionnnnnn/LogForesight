using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 階段 P-1（W1）：夜間自動派工新建的單、成員依問題嚴重度帶預設期限（高 3 天）。
/// 情境：第 1 天 HOST-A 出現磁碟錯誤（高）→ 自動派給 h1、建單，單與成員期限＝建立日＋3；第 2 天 HOST-B 同問題續掛進同一張單，
/// 新成員沿用單的期限（不是第 2 天＋3）；到期日當天（建立後第 3 天）不逾期、隔天（第 4 天）逾期。
/// 中嚴重度（7 天）的邊界由 <c>NightlyDefaultDueDateTests</c> 的單元測試覆蓋（預設規則庫沒有穩定產生「中」的單一事件片段）。
/// 使用情境驅動器的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class DefaultDueScenarioTests
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

    private static long LatestOrderId(ScenarioActionContext ctx) =>
        ctx.Site.Get<IWorkOrderStore>().GetActiveByHandler(ctx.UserId("h1")).Max(o => o.WorkOrderId);

    private static string MembersUrl(ScenarioActionContext ctx) =>
        $"api/work-orders/{LatestOrderId(ctx)}/members?status=active&pageSize=50";

    private static string DetailUrl(ScenarioActionContext ctx) => $"api/work-orders/{LatestOrderId(ctx)}";

    private static string MemberDues(ProbeView probe) =>
        string.Join(",", probe.Items("items").OrderBy(m => m.Str("hostName"), StringComparer.Ordinal)
            .Select(m => $"{m.Str("hostName")}:{m.Str("dueDate")}"));

    private static string Overdues(ProbeView probe) =>
        string.Join(",", probe.Items("items").OrderBy(m => m.Str("hostName"), StringComparer.Ordinal)
            .Select(m => $"{m.Str("hostName")}:{(m.Flag("overdue") ? "逾期" : "未逾期")}"));

    private static DateTime DateOf(string text) => DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture).Date;

    internal static Scenario BuildHighSeverity() => new Scenario("P1 夜間派工預設期限（高）")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithUser(new UserSpec("admin1", UserRole.Admin) { HasEmail = true })
        .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true, HasEmail = true })
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Probe("detail", "admin1", ctx => DetailUrl(ctx))
            .Probe("members", "admin1", ctx => MembersUrl(ctx)))
        .Day(2, d => d
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Probe("members", "admin1", ctx => MembersUrl(ctx)))
        .Day(4, d => d.Probe("members", "admin1", ctx => MembersUrl(ctx)))
        .Day(5, d => d.Probe("members", "admin1", ctx => MembersUrl(ctx)))
        .Days(5)
        .Expect(Check("P1.d1.order", 1, "第 1 天自動派工建了一張單給 h1", "1/h1", d =>
            $"{d.NewOrders.Count()}/{d.NewOrders.FirstOrDefault()?.Handler}"))
        .Expect(Check("P1.d1.order-due", 1, "單的期限＝建立日＋3 天（嚴重度高）", "3", d =>
            (DateOf(d.Probe("detail").Data!.Value.Str("dueDate")) - DateOf(d.Probe("detail").Data!.Value.Str("createdAt"))).Days.ToString()))
        .Expect(Check("P1.d1.member-due", 1, "第 1 天的成員期限等於單的期限", "是", d =>
            d.Probe("members").Items("items").Single().Str("dueDate") == d.Probe("detail").Data!.Value.Str("dueDate")
                ? "是" : "否"))
        .Expect(Check("P1.d2.attached", 2, "第 2 天 HOST-B 續掛進同一張單，成員有 HOST-A、HOST-B", "HOST-A,HOST-B", d =>
            string.Join(",", d.Probe("members").Items("items").Select(m => m.Str("hostName")).OrderBy(x => x, StringComparer.Ordinal))))
        .Expect(Check("P1.d2.same-due", 2, "新成員沿用單的期限（兩台期限相同）", "1", d =>
            d.Probe("members").Items("items").Select(m => m.Str("dueDate")).Distinct().Count().ToString()))
        .Expect(Check("P1.d4.not-overdue", 4, "建立後第 3 天（期限當天）不逾期", "HOST-A:未逾期,HOST-B:未逾期", d => Overdues(d.Probe("members"))))
        .Expect(Check("P1.d5.overdue", 5, "建立後第 4 天逾期", "HOST-A:逾期,HOST-B:逾期", d => Overdues(d.Probe("members"))));

    [Fact]
    public void 夜間自動派工_高嚴重度新單帶3天期限_續掛沿用單的期限_第3天不逾期第4天逾期() =>
        ScenarioDriver.Run(BuildHighSeverity()).AssertExpectations();
}
