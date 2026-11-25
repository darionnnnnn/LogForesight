using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// K5：同一個問題，人工建立的單與夜間派工建的單，在交辦單單頭（detail）與清單顯示相同的名稱。
/// 兩個情境各自驗到同一個預期字串「disk EventId 153」，所以兩條路徑的顯示名稱必然相同。
/// 使用情境驅動器的測試類別必須標 <c>[Collection("NLogGlobalConfig")]</c>。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class IssueNameScenarioTests
{
    private const string Disk = "disk";
    private const int DiskId = 153;
    private const string Expected = "disk EventId 153";

    private static long LatestOrderId(ScenarioActionContext ctx) =>
        ctx.Site.Get<IWorkOrderStore>().GetActiveByHandler(ctx.UserId("h1")).Max(o => o.WorkOrderId);

    private static ScenarioExpectation Check(string key, string description, Func<DayResult, string> actual) =>
        Expect.Value(key, 1, description, Expected, r => actual(r.Day(1)));

    private static Scenario Base(string title, bool inPool) => new Scenario(title)
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithUser(new UserSpec("admin1", UserRole.Admin) { HasEmail = true })
        .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = inPool, HasEmail = true });

    private static Scenario WithChecks(Scenario s) => s
        .Expect(Check("K5.detail", "單頭顯示的問題名稱", d => d.Probe("detail").Data!.Value.Str("issueLabel")))
        .Expect(Check("K5.list", "清單顯示的問題名稱", d =>
            d.Probe("list").Items("items").Select(o => o.Str("issueLabel")).FirstOrDefault() ?? "（清單沒有單）"));

    [Fact]
    public void 夜間派工建的單_清單與單頭用共用出口的名稱() =>
        ScenarioDriver.Run(WithChecks(Base("K5 夜間派工", inPool: true)
            .Day(1, d => d
                .Events("HOST-A", ScenarioEvents.DiskErrors(12))
                .Probe("detail", "admin1", ctx => $"api/work-orders/{LatestOrderId(ctx)}")
                .Probe("list", "admin1", _ => "api/work-orders"))
            .Days(1))).AssertExpectations();

    [Fact]
    public void 人工建立的單_清單與單頭用共用出口的名稱() =>
        ScenarioDriver.Run(WithChecks(Base("K5 人工建立", inPool: false)
            .Day(1, d => d
                .Events("HOST-A", ScenarioEvents.DiskErrors(12))
                .Act(ScenarioActions.CreateOrder("admin1", Disk, DiskId, "h1", new[] { "HOST-A" }))
                .Probe("detail", "admin1", ctx => $"api/work-orders/{LatestOrderId(ctx)}")
                .Probe("list", "admin1", _ => "api/work-orders"))
            .Days(1))).AssertExpectations();
}
