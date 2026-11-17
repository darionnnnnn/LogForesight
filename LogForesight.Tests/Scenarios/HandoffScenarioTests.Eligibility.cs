using Xunit;

namespace LogForesight.Tests.Scenarios;

// 批次 D：派工資格單一判定（D1-d、D1-e、D2、D3）。S4／S5／S6 的續掛停止在 HandoffScenarioTests.Dispatch.cs。
public sealed partial class HandoffScenarioTests
{
    private static Scenario BuildEligibility(string key) => key switch
    {
        "D1d" => BuildD1Visibility(),
        "D1e" => BuildD1OwnerUnseen(),
        "D2有人接" => BuildD2Regroup(withOtherHandler: true),
        "D2進待派" => BuildD2Regroup(withOtherHandler: false),
        "D3" => BuildD3NotInPool(),
        _ => BuildEdge(key)
    };

    private static string HoldersOf(DayResult d, string handler) =>
        string.Join(",", d.Orders.Where(o => o.Handler == handler && o.State == "進行中")
            .SelectMany(o => o.Members.Select(m => m.Host)).OrderBy(h => h, StringComparer.Ordinal));

    // ── D1-d：處理人看得到 A、看不到 B，同一夜兩台都出現 → 只續掛 A ───────────────

    /// <summary>
    /// 第 1 天管理者以「全站＋續掛」把 HOST-A0 的單建給 h1（G1）；第 2 天 HOST-A（G1）與 HOST-B（G2）同時首次出現。
    /// h1 只看得到 G1：A 續掛進他的單，B 往下走，由看得到 B 的 h2 接手。
    /// </summary>
    internal static Scenario BuildD1Visibility() => new Scenario("D1-d 續掛要看得到新主機")
        .WithHost(new HostSpec("HOST-A0") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G2" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G2" }))
        .WithSettings(new ScenarioSettings { AutoDispatchEnabled = false })
        .Day(1, d => d
            .Events("HOST-A0", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.CreateOrderScoped("admin1", Disk, DiskId, "h1", WorkOrderScopes.All, Array.Empty<string>(), autoAttach: true)))
        .Day(2, d => d
            .Setting(x => x.AutoDispatchEnabled = true)
            .Events("HOST-A0", ScenarioEvents.DiskErrors(12))
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Probe("detail", "admin1", ctx => OrderUrl(ctx, "h1")))
        .Days(2)
        .Expect(Expect.ActionStatus(1, 0, 200))
        .Expect(Check("D1d.d2.a-continued", 2, "看得到的 HOST-A 續掛進 h1 的單", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Check("D1d.d2.h1-only-visible", 2, "h1 的單只含他看得到的主機（HOST-A0、HOST-A）", "HOST-A,HOST-A0", d => HoldersOf(d, "h1")))
        .Expect(Check("D1d.d2.b-next", 2, "看不到的 HOST-B 沒有續掛，改由看得到的 h2 接手", "h2", d => HolderOf(d, "HOST-B")))
        .Expect(Check("D1d.d2.stopped-reason", 2, "原單標示續掛已停止：看不到新主機", "True/host_not_visible", d =>
            $"{d.Probe("detail").Data?.Flag("continuationStopped")}/{d.Probe("detail").Data?.Str("continuationStoppedReason")}"));

    // ── D1-e：問題負責人看不到主機 → 仍派給他（刻意保留） ─────────────────────────

    internal static Scenario BuildD1OwnerUnseen() => new Scenario("D1-e 問題負責人不檢查可見主機")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G2" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }))
        .WithUser(Handler("h2", new[] { "G2" }))
        .Day(1, d => d.Act(ScenarioActions.SetIssueOwners("admin1", Disk, DiskId, "h1")))
        .Day(2, d => d.Events("HOST-A", ScenarioEvents.DiskErrors(12)))
        .Days(2)
        .Expect(Expect.ActionStatus(1, 0, 200))
        .Expect(Check("D1e.d2.owner-gets-it", 2, "問題負責人 h1 看不到 HOST-A（只有 h2 看得到），仍照問題檔案派給他", "h1", d => HolderOf(d, "HOST-A")))
        .Expect(Check("D1e.d2.origin", 2, "單的來源是問題負責人規則", WorkOrderOrigins.OwnerRule, d => d.NewOrders.Select(o => o.Origin).FirstOrDefault() ?? "（沒有新單）"));

    // ── D2：主機改群組後，原處理人只剩案件授與，新問題不可派給他 ─────────────────────

    internal static Scenario BuildD2Regroup(bool withOtherHandler)
    {
        var s = new Scenario(withOtherHandler ? "D2 改群組後新問題改派（有人接）" : "D2 改群組後新問題進待派")
            .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
            .WithHost(new HostSpec("HOST-Z") { Groups = new[] { "G2" } })   // 讓 G2 群組存在（改群組的目標）
            .WithUser(Admin("admin1"))
            .WithUser(Handler("h1", new[] { "G1" }));
        if (withOtherHandler) s.WithUser(Handler("h2", new[] { "G2" }));

        s.Day(1, d => d.Events("HOST-B", ScenarioEvents.DiskErrors(12)))
            .Day(2, d => d
                .Act(ScenarioActions.SetHostGroups("admin1", "HOST-B", "G2"))
                .Probe("h1-detail", "h1", ctx => HandlingUrl(ctx, "HOST-B")))
            .Day(3, d => d
                .Events("HOST-B", ScenarioEvents.FailedLogons(15))
                .Probe("gaps", "admin1", Gaps))
            .Days(3)
            .Expect(Check("D2.d1.setup", 1, "前置：第 1 天 HOST-B 還在 G1，單派給 h1", "h1", d => HolderOf(d, "HOST-B")))
            .Expect(Expect.ActionStatus(2, 0, 200))
            .Expect(Check("D2.d2.case-grant", 2, "前置：改群組後 h1 只剩案件授與，仍打得開自己的處理紀錄", "200", d => d.Probe("h1-detail").StatusCode.ToString()))
            .Expect(Check("D2.d3.not-h1", 3, "新問題不派給只剩案件授與的 h1（沒有新建給他的單）", "0", d => d.NewOrders.Count(o => o.Handler == "h1").ToString()));

        if (withOtherHandler)
            s.Expect(Check("D2.d3.next-handler", 3, "新問題改派給看得到 HOST-B 的 h2", "h2", d =>
                d.NewOrders.Where(o => o.Members.Any(m => m.Host == "HOST-B")).Select(o => o.Handler).FirstOrDefault() ?? "（沒有新單）"));
        else
            s.Expect(Check("D2.d3.deferred", 3, "沒有人看得到 HOST-B：進待派並帶原因（池內都看不到這台）", "deferred:no_candidate/no_visibility", d =>
                $"{Decision(d, "HOST-B", SecAuditing)}/" +
                string.Join(",", d.Probe("gaps").Items("rows").Where(r => r.Str("source") == SecAuditing)
                    .SelectMany(r => r.GetProperty("unassignable").EnumerateArray()).Select(u => u.Str("reason")).Distinct())));
        return s;
    }

    // ── D3：不在派工池、但有可續掛單的處理人 → 看得到的新主機照常續掛 ──────────────────

    internal static Scenario BuildD3NotInPool() => new Scenario("D3 不在派工池的處理人仍可續掛")
        .WithHost(new HostSpec("HOST-A") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-B") { Groups = new[] { "G1" } })
        .WithHost(new HostSpec("HOST-C") { Groups = new[] { "G2" } })
        .WithUser(Admin("admin1"))
        .WithUser(Handler("h1", new[] { "G1" }, pool: false))
        .WithUser(Handler("h2", new[] { "G2" }))
        .WithSettings(new ScenarioSettings { AutoDispatchEnabled = false })
        .Day(1, d => d
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Act(ScenarioActions.CreateOrderScoped("admin1", Disk, DiskId, "h1", WorkOrderScopes.All, Array.Empty<string>(), autoAttach: true)))
        .Day(2, d => d
            .Setting(x => x.AutoDispatchEnabled = true)
            .Events("HOST-A", ScenarioEvents.DiskErrors(12))
            .Events("HOST-B", ScenarioEvents.DiskErrors(12))
            .Events("HOST-C", ScenarioEvents.DiskErrors(12)))
        .Days(2)
        .Expect(Expect.ActionStatus(1, 0, 200))
        .Expect(Check("D3.d2.b-continued", 2, "不在派工池的 h1 看得到 HOST-B：照常續掛", "h1", d => HolderOf(d, "HOST-B")))
        .Expect(Check("D3.d2.c-not-continued", 2, "h1 看不到 HOST-C：不續掛，由池內的 h2 接手", "h2", d => HolderOf(d, "HOST-C")))
        .Expect(Check("D3.d2.not-auto", 2, "h1 不在派工池，不會被自動派工選中（沒有新建給他的單）", "0", d => d.NewOrders.Count(o => o.Handler == "h1").ToString()));

    [Fact]
    public void D1d_處理人看得到A看不到B_同一夜只續掛A() => R("D1d").AssertExpectations();

    [Fact]
    public void D1e_問題負責人看不到主機_仍派給他() => R("D1e").AssertExpectations();

    [Fact]
    public void D2_改群組後原處理人只剩案件授與_新問題改派給看得到的人() => R("D2有人接").AssertExpectations();

    [Fact]
    public void D2_改群組後原處理人只剩案件授與_沒有人看得到則進待派() => R("D2進待派").AssertExpectations();

    [Fact]
    public void D3_不在派工池的處理人_看得到的新主機照常續掛() => R("D3").AssertExpectations();
}
