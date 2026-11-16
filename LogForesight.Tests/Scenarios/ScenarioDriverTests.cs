using LogForesight.Core.Models;
using Xunit;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 逐日情境驅動器的自身測試。驅動器用整站（WebApplicationFactory）加真實分析流程，
/// 站台設定走行程層級環境變數，所以與其他會動 NLog 全域設定的測試共用同一個不並行的集合。
/// </summary>
[Collection("NLogGlobalConfig")]
public sealed class ScenarioDriverTests : IClassFixture<ScenarioDriverTests.MinimalFixture>
{
    private readonly MinimalFixture _fx;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ScenarioDriverTests(MinimalFixture fx, Xunit.Abstractions.ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    [Fact]
    public void 輸出預覽_最小情境的Markdown()
    {
        var markdown = _fx.Result.ToMarkdown();
        _output.WriteLine(markdown);
        Assert.False(string.IsNullOrWhiteSpace(markdown));
    }

    /// <summary>最小情境：兩台 Windows 主機、一位管理者、一位處理人（派工池、看得到兩台）、自動派工開啟。</summary>
    internal static Scenario BuildMinimal()
    {
        var scenario = new Scenario("最小情境")
            .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
            .WithUser(new UserSpec("admin1", UserRole.Admin))
            .WithUser(new UserSpec("handler1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true })
            .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
            .Day(2, d => d.Act(ScenarioActions.Reply("handler1", OrderRef.ForHandler("handler1"), "resolved", "已更換硬碟")))
            .Days(3);

        scenario
            .Expect(Expect.HostRisk(1, "HOST-A", RiskLevels.High))
            .Expect(Expect.HostRisk(1, "HOST-B", RiskLevels.Low))
            .Expect(Expect.NewOrdersFor(1, "handler1", 1))
            .Expect(Expect.OutboxCount(1, WorkOrderOutboxKinds.Created, "handler1", 1))
            .Expect(Expect.ActionStatus(2, 0, 200))
            .Expect(Expect.NewOrderCount(3, 0));
        return scenario;
    }

    public sealed class MinimalFixture : IDisposable
    {
        public Scenario Scenario { get; } = BuildMinimal();
        public ScenarioResult Result { get; }

        public MinimalFixture() => Result = ScenarioDriver.Run(Scenario);

        public void Dispose() { }
    }

    [Fact]
    public void 最小情境_第1天高風險_夜間派工建單並入通知佇列()
    {
        var day1 = _fx.Result.Day(1);

        Assert.Equal(RiskLevels.High, day1.Host("HOST-A").RiskLevel);
        Assert.Equal(RiskLevels.Low, day1.Host("HOST-B").RiskLevel);

        var order = Assert.Single(day1.NewOrders);
        Assert.Equal("handler1", order.Handler);
        Assert.Contains("disk", order.Issue);
        var member = Assert.Single(order.Members);
        Assert.Equal("HOST-A", member.Host);

        var notice = Assert.Single(day1.OutboxAdded);
        Assert.Equal(WorkOrderOutboxKinds.Created, notice.Kind);
        Assert.Equal("handler1", notice.Recipient);
        Assert.Equal(order.OrderId, notice.WorkOrderId);
    }

    [Fact]
    public void 最小情境_第2天處理人以API回覆已處理_成員結案且稽核有回覆紀錄()
    {
        var day2 = _fx.Result.Day(2);

        var reply = Assert.Single(day2.Actions);
        Assert.Equal(200, reply.StatusCode);
        Assert.True(reply.Succeeded);

        var order = Assert.Single(day2.Orders);
        Assert.Equal(IssueHandlingStatuses.Resolved, Assert.Single(order.Members).Status);
        Assert.StartsWith("已結案", order.State);

        Assert.Contains(day2.AuditAdded, a => a.Phase == "動作" && a.Account == "handler1");
    }

    [Fact]
    public void 最小情境_第3天無事件_沒有新單()
    {
        var day3 = _fx.Result.Day(3);
        Assert.Empty(day3.NewOrders);
        Assert.Empty(day3.Decisions);
        Assert.Equal(RiskLevels.Low, day3.Host("HOST-A").RiskLevel);
    }

    [Fact]
    public void 最小情境_宣告的預期全部一致()
    {
        _fx.Result.AssertExpectations();
        Assert.True(_fx.Result.Verdict.Ok);
    }

    [Fact]
    public void 確定性_同一情境跑兩次_Markdown逐字相同()
    {
        var first = ScenarioDriver.Run(BuildMinimal()).ToMarkdown();
        var second = ScenarioDriver.Run(BuildMinimal()).ToMarkdown();

        Assert.Equal(first, second);
        Assert.Contains("## 第 1 天", first);
        Assert.Contains("### 主機日風險", first);
        // 輸出不得外洩真實日期：只有「第N天」
        Assert.DoesNotMatch(@"\b20\d{2}-\d{2}-\d{2}\b", first);
    }

    // ── 已知差異機制：四種情況各一例（用同一份結果換預期清單重新比對，不必重跑整站）──────────

    private static Scenario WithExpectations(string expected = RiskLevels.High)
    {
        var s = new Scenario("已知差異");
        s.Expectations.Add(Expect.HostRisk(1, "HOST-A", expected));
        return s;
    }

    [Fact]
    public void 已知差異_一致且清單空_通過()
    {
        var verdict = _fx.Result.Evaluate(WithExpectations(expected: RiskLevels.High));
        Assert.True(verdict.Ok);
        _fx.Result.Evaluate(WithExpectations(expected: RiskLevels.High)).AssertOk();
    }

    [Fact]
    public void 已知差異_不一致且不在清單_失敗並列出預期與實際()
    {
        var scenario = WithExpectations(expected: RiskLevels.Low);
        var ex = Assert.Throws<ScenarioAssertionException>(() => _fx.Result.Evaluate(scenario).AssertOk());
        Assert.Contains("D1.risk.HOST-A", ex.Message);
        Assert.Contains($"預期：{RiskLevels.Low}", ex.Message);
        Assert.Contains($"實際：{RiskLevels.High}", ex.Message);
    }

    [Fact]
    public void 已知差異_不一致但在清單_通過()
    {
        var scenario = WithExpectations(expected: RiskLevels.Low).KnownDifference("E3-01", "D1.risk.HOST-A");
        var verdict = _fx.Result.Evaluate(scenario);
        Assert.True(verdict.Ok);
        Assert.Contains(verdict.StillKnown, s => s.StartsWith("E3-01", StringComparison.Ordinal));
    }

    [Fact]
    public void 已知差異_一致但在清單_失敗並要求移除()
    {
        var scenario = WithExpectations(expected: RiskLevels.High).KnownDifference("E3-01", "D1.risk.HOST-A");
        var ex = Assert.Throws<ScenarioAssertionException>(() => _fx.Result.Evaluate(scenario).AssertOk());
        Assert.Contains("E3-01", ex.Message);
        Assert.Contains("從已知差異清單移除", ex.Message);
    }

    [Fact]
    public void 已知差異_指向不存在的預期_失敗()
    {
        var scenario = WithExpectations().KnownDifference("E9-99", "D9.nothing");
        var ex = Assert.Throws<ScenarioAssertionException>(() => _fx.Result.Evaluate(scenario).AssertOk());
        Assert.Contains("E9-99", ex.Message);
    }

    // ── 人為動作失敗：記到 HTTP 狀態碼，情境不中斷 ───────────────────────────────

    [Fact]
    public void 人為動作失敗_處理人呼叫管理者專用的改派API_記到403且情境繼續()
    {
        var scenario = new Scenario("權限不足")
            .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
            .WithUser(new UserSpec("admin1", UserRole.Admin))
            .WithUser(new UserSpec("handler1") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true })
            .WithUser(new UserSpec("handler2") { AuthorizedHostGroups = new[] { "G1" }, InDispatchPool = true })
            .Day(1, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12))
                .Act(ScenarioActions.Reassign("handler1", OrderRef.Latest(), "handler2")))
            .Day(2, d => d.Act(ScenarioActions.Reply("handler1", OrderRef.ForHandler("handler1"), "resolved")))
            .Days(2);

        var result = ScenarioDriver.Run(scenario);

        var denied = Assert.Single(result.Day(1).Actions);
        Assert.Equal(403, denied.StatusCode);
        Assert.False(denied.Succeeded);
        Assert.Equal("forbidden", denied.ErrorCode);
        // 情境沒有中斷：第 2 天的動作照樣執行
        Assert.Single(result.Day(2).Actions);
        Assert.Contains("403", result.ToMarkdown());
    }

    [Fact]
    public void 人為動作_對象解析不到_記為未送出而不是中斷()
    {
        var scenario = new Scenario("找不到單")
            .WithHost(new HostSpec("HOST-A"))
            .WithUser(new UserSpec("admin1", UserRole.Admin))
            .Day(1, d => d.Act(ScenarioActions.Cancel("admin1", OrderRef.Id(999))))
            .Days(1);

        var result = ScenarioDriver.Run(scenario);
        var action = Assert.Single(result.Day(1).Actions);
        Assert.Equal(0, action.StatusCode);
        Assert.Equal("unresolved", action.ErrorCode);
    }

    [Fact]
    public void 全部動作類型_都能送出並記錄結果_情境不中斷()
    {
        const string secAuditing = "Microsoft-Windows-Security-Auditing";
        var scenario = new Scenario("全部動作")
            .WithHost(new HostSpec("WIN-1") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("WIN-2") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("LNX-1") { IsLinux = true, Groups = new[] { "G2" } })
            .WithHostGroup("G3")
            .WithUser(new UserSpec("admin1", UserRole.Admin))
            .WithUser(new UserSpec("viewer1", UserRole.Manager))
            .WithUser(new UserSpec("h1") { AuthorizedHostGroups = new[] { "G1", "G2" }, InDispatchPool = true })
            .WithUser(new UserSpec("h2") { AuthorizedHostGroups = new[] { "G1", "G2" }, InDispatchPool = true, HasEmail = false })
            .Day(1, d => d
                .Events("WIN-1", ScenarioEvents.DiskErrors(12))
                .Events("WIN-2", ScenarioEvents.FailedLogons(15))
                .Events("LNX-1", ScenarioEvents.LinuxSshFailures(12)))
            .Day(2, d => d
                .Events("WIN-2", ScenarioEvents.FailedLogons(15))
                .Act(
                    ScenarioActions.Reassign("admin1", OrderRef.ForIssue("disk", 153), "h2"),
                    ScenarioActions.SetDispatchPaused("admin1", "h1", true),
                    ScenarioActions.Mute("admin1", secAuditing, 4625, 3, "測試靜音"),
                    ScenarioActions.SetHostGroups("admin1", "WIN-1", "G3"),
                    ScenarioActions.Return("admin1", OrderRef.ForIssue("disk", 153), "請再確認"),
                    ScenarioActions.DisableUser("admin1", "h1"),
                    ScenarioActions.Reply("h1", OrderRef.ForHandler("h1"), "resolved"),
                    ScenarioActions.Unmute("admin1", secAuditing, 4625)))
            .Day(3, d => d
                .Events("WIN-1", ScenarioEvents.DiskErrors(12))
                .Act(
                    ScenarioActions.CreateOrder("admin1", "disk", 153, "h2", new[] { "WIN-1" }, "手動建單"),
                    ScenarioActions.AdminClose("admin1", OrderRef.Latest(), "wont_fix", "不處理"),
                    ScenarioActions.Cancel("admin1", OrderRef.ForHandler("h2"), "取消")));

        var result = ScenarioDriver.Run(scenario);
        _output.WriteLine(result.ToMarkdown());

        foreach (var action in result.Days.SelectMany(d => d.Actions))
            Assert.True(action.StatusCode is not 500, $"{action.Description} 回 500：{action.Message}");
        Assert.Equal(3, result.Days.Count);

        // 第 2 天：改派、暫停接單、靜音、改主機群組、停用使用者都成功；退回沒有「無法處理」成員 → 400；
        // 被停用的 h1 再呼叫 → 401（停用立即生效）
        var day2 = result.Day(2).Actions;
        Assert.All(new[] { 0, 1, 2, 3, 5, 7 }, i => Assert.Equal(200, day2[i].StatusCode));
        Assert.Equal(400, day2[4].StatusCode);
        Assert.Equal(401, day2[6].StatusCode);
        // 第 3 天：手動建單、代為結案、取消
        Assert.All(result.Day(3).Actions, a => Assert.Equal(200, a.StatusCode));
        // 靜音、改群組、停用使用者都有稽核
        var audit = result.Day(2).AuditAdded.Select(a => a.Action).ToList();
        Assert.Contains(AuditActions.IssueMute, audit);
        Assert.Contains(AuditActions.IssueUnmute, audit);
    }
}
