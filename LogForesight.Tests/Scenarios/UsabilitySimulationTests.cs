using System.Text;
using LogForesight.Core.Time;
using LogForesight.Core.Models;
using LogForesight.Web.Models.Dto;
using Xunit;
using Xunit.Abstractions;

namespace LogForesight.Tests.Scenarios;

/// <summary>
/// 可用性模擬（第 81 輪）：以逐日情境驅動器跑「N 台 × D 天」——正式分析管線＋夜間派工＋每天由派工池處理人依產能回覆，
/// 逐日記錄積壓（進行中單／台數、逾期、每人負載、通知量），並把整個資料目錄留給站台實測。
///
/// 預設不執行。設 <c>LF_USABILITY_SIM=&lt;輸出目錄&gt;</c> 才跑；<c>LF_USABILITY_HOSTS</c>（預設 300）、
/// <c>LF_USABILITY_DAYS</c>（預設 5）、<c>LF_USABILITY_CAPACITY</c>（每位處理人每天回覆的主機數，預設 40）。
/// 資料目錄經 <c>LF_SCENARIO_KEEP_DATA</c> 複製到 <c>&lt;輸出目錄&gt;/data</c>，逐日指標寫 <c>&lt;輸出目錄&gt;/metrics.tsv</c>。
/// </summary>
public class UsabilitySimulationTests
{
    private readonly ITestOutputHelper _out;

    public UsabilitySimulationTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Handlers = { "h-chen", "h-lin", "h-wang", "h-huang", "h-wu", "h-tsai" };

    [Fact]
    public void 可用性模擬_多日派工與處理量()
    {
        var outDir = Environment.GetEnvironmentVariable("LF_USABILITY_SIM");
        if (string.IsNullOrWhiteSpace(outDir)) return;   // 預設不跑（不是 Skip：避免略過數字變動）

        var hostCount = int.TryParse(Environment.GetEnvironmentVariable("LF_USABILITY_HOSTS"), out var h) ? h : 300;
        var days = int.TryParse(Environment.GetEnvironmentVariable("LF_USABILITY_DAYS"), out var d) ? d : 5;
        var capacity = int.TryParse(Environment.GetEnvironmentVariable("LF_USABILITY_CAPACITY"), out var c) ? c : 40;
        Directory.CreateDirectory(outDir);
        var dataDir = Path.Combine(outDir, "data");
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        Environment.SetEnvironmentVariable("LF_SCENARIO_KEEP_DATA", dataDir);

        var metrics = new List<string> { "day\tordersOpen\tcasesOpen\tcasesOverdue\tordersCreatedToday\trepliedToday\tmaxHandlerCases\tminHandlerCases\tunassignedHighMed" };
        var replied = new Dictionary<int, int>();
        try
        {
            var scenario = Build(hostCount, days, capacity, replied, metrics);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = ScenarioDriver.Run(scenario);
            _out.WriteLine($"{hostCount} 台 × {days} 天，耗時 {sw.Elapsed}");
            var mails = result.Days.Sum(x => x.OutboxAdded.Count);
            _out.WriteLine($"通知佇列總數 {mails}");
            File.WriteAllText(Path.Combine(outDir, "metrics.tsv"), string.Join("\n", metrics) + $"\n#outbox\t{mails}\n#elapsed\t{sw.Elapsed}\n", Encoding.UTF8);
            foreach (var line in metrics) _out.WriteLine(line);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LF_SCENARIO_KEEP_DATA", null);
        }
    }

    private static Scenario Build(int hostCount, int days, int capacity, Dictionary<int, int> replied, List<string> metrics)
    {
        var scenario = new Scenario("usability-sim").Days(days)
            .WithSettings(new ScenarioSettings { AutoDispatchEnabled = true, WorkOrderMailQueue = true });

        // 主機：每 100 台一個機房群組；10% Linux
        var groupCount = Math.Max(1, (hostCount + 99) / 100);
        for (var i = 1; i <= hostCount; i++)
        {
            var linux = i % 10 == 0;
            scenario.WithHost(new HostSpec(linux ? $"sim-lnx{i:D4}" : $"SIM-WIN{i:D4}")
            {
                IsLinux = linux,
                Groups = new[] { $"機房{(i - 1) % groupCount + 1:D2}" },
                Owners = i % 25 == 0 ? new[] { "o-owner" } : Array.Empty<string>(),
                Ip = $"10.{80 + i / 60000}.{i / 250 % 240}.{i % 250 + 1}"
            });
        }

        // 使用者：六位派工池處理人分攤機房（每人看約 1/6，最後一個機房刻意沒人授權——全域缺口）
        var groups = Enumerable.Range(1, groupCount).Select(g => $"機房{g:D2}").ToList();
        var authorized = groups.Take(Math.Max(1, groupCount - 1)).ToList();
        for (var k = 0; k < Handlers.Length; k++)
            scenario.WithUser(new UserSpec(Handlers[k])
            {
                InDispatchPool = true,
                AuthorizedHostGroups = authorized.Where((_, idx) => idx % Handlers.Length == k || idx % Handlers.Length == (k + 1) % Handlers.Length).ToList()
            });
        scenario.WithUser(new UserSpec("a-admin", UserRole.Admin));
        scenario.WithUser(new UserSpec("m-manager", UserRole.Manager));
        scenario.WithUser(new UserSpec("d-dev", UserRole.Dev));
        scenario.WithUser(new UserSpec("o-owner"));
        scenario.WithUser(new UserSpec("v-viewer") { AuthorizedHostGroups = groups.Take(1).ToList() });

        for (var day = 1; day <= days; day++)
        {
            var plan = scenario.Day(day);
            var rnd = new Random(1000 + day);
            foreach (var host in scenario.Hosts)
                plan.Events(host.Name, EventsFor(host, day, days));

            // 處理人依產能回覆：先回期限最近、成員少的單，整張單回得完就整張回，否則回部分主機
            foreach (var handler in Handlers)
                plan.Act(new WorkdayAction(handler, capacity, rnd.Next(), replied, day));

            var dayNo = day;
            plan.Inspect((site, ctx) => metrics.Add(Snapshot(site, ctx, dayNo, replied)));
        }

        return scenario;
    }

    /// <summary>
    /// 每台主機的事件形狀：以主機序號決定「體質」，同主機每天大致相同（趨勢與續掛才有東西可看）。
    ///   - 每台 Windows：DCOM 10016 雜訊；約 30% SCM 7034、20% Application Error、10% NETLOGON 5719；
    ///   - 約 4%：磁碟 153 從某天開始持續；約 2%：WHEA 17；約 3%：4625 暴力嘗試；
    ///   - Linux：一半有 sshd 失敗，2% 達門檻。
    /// </summary>
    private static List<ScenarioEvent> EventsFor(HostSpec host, int day, int totalDays)
    {
        var n = int.Parse(new string(host.Name.Where(char.IsDigit).ToArray()));
        var list = new List<ScenarioEvent>();
        if (host.IsLinux)
        {
            if (n % 20 == 0) list.AddRange(ScenarioEvents.LinuxSshFailures(15 + day));
            else if (n % 2 == 0) list.AddRange(ScenarioEvents.LinuxSshFailures(3));
            return list;
        }

        list.AddRange(ScenarioEvents.Repeat(4 + n % 6, "System", "Microsoft-Windows-DistributedCOM", 10016, ScenarioEventLevel.Warning,
            "The application-specific permission settings do not grant Local Activation permission"));
        if (n % 10 < 3 && (n + day) % 3 != 0)
            list.AddRange(ScenarioEvents.Repeat(1 + n % 3, "System", "Service Control Manager", 7034, ScenarioEventLevel.Error,
                "The Print Spooler service terminated unexpectedly."));
        if (n % 5 == 1 && (n + day) % 2 == 0)
            list.AddRange(ScenarioEvents.Repeat(2, "Application", "Application Error", 1000, ScenarioEventLevel.Error,
                "Faulting application name: app.exe"));
        if (n % 10 == 7)
            list.AddRange(ScenarioEvents.Repeat(3, "System", "NETLOGON", 5719, ScenarioEventLevel.Error,
                "This computer was not able to set up a secure session with a domain controller"));
        if (n % 25 == 3 && day >= 1 + n % Math.Max(1, totalDays))
            list.AddRange(ScenarioEvents.DiskErrors(2 + day));
        if (n % 50 == 11 && (n + day) % 2 == 1)
            list.AddRange(ScenarioEvents.Repeat(2, "System", "Microsoft-Windows-WHEA-Logger", 17, ScenarioEventLevel.Warning,
                "A corrected hardware error has occurred."));
        if (n % 33 == 5)
            list.AddRange(ScenarioEvents.FailedLogons(12 + day, sourceIp: $"203.0.113.{n % 200}"));
        return list;
    }

    private static string Snapshot(ScenarioSite site, ScenarioActionContext ctx, int day, Dictionary<int, int> replied)
    {
        var orders = OrderRef.AllOrders(ctx).Where(o => o.ClosedAt == null).ToList();
        var cases = site.Get<IIssueCaseStore>();
        var open = orders.SelectMany(o => cases.GetByWorkOrder(o.WorkOrderId, 0, 100000).Where(c => c.ClosedAt == null)).ToList();
        var today = site.Clock.LocalNow().Date;
        var perHandler = Handlers.Select(hd => open.Count(c => c.HandlerId == ctx.UserId(hd))).ToList();
        var createdToday = OrderRef.AllOrders(ctx).Count(o => o.CreatedAt.Date == today);
        return string.Join('\t', day, orders.Count, open.Count, open.Count(c => c.DueDate != null && c.DueDate.Value.Date < today),
            createdToday, replied.GetValueOrDefault(day), perHandler.Max(), perHandler.Min(), "-");
    }

    /// <summary>一位處理人一天的工作：產能內依期限、成員數排序回覆（已處理 70%、觀察中 20%、不處理 10%）</summary>
    private sealed class WorkdayAction(string actor, int capacity, int seed, Dictionary<int, int> replied, int day) : ScenarioAction(actor)
    {
        public override string Describe() => $"{Actor} 依產能 {capacity} 台回覆";

        internal override async Task<ActionHttpResult> ExecuteAsync(ScenarioActionContext ctx)
        {
            var rnd = new Random(seed);
            var userId = ctx.UserId(Actor);
            var cases = ctx.Site.Get<IIssueCaseStore>();
            var orders = ctx.Site.Get<IWorkOrderStore>().GetActiveByHandler(userId)
                .Select(o => (Order: o, Open: cases.GetByWorkOrder(o.WorkOrderId, 0, 100000).Where(c => c.ClosedAt == null).ToList()))
                .Where(x => x.Open.Count > 0)
                .OrderBy(x => x.Open.Min(c => c.DueDate ?? DateTime.MaxValue)).ThenBy(x => x.Open.Count)
                .ToList();
            var left = capacity;
            var last = new ActionHttpResult(200, null, null);
            foreach (var (order, open) in orders)
            {
                if (left <= 0) break;
                var take = open.Take(left).ToList();
                var roll = rnd.NextDouble();
                var status = roll < 0.7 ? IssueHandlingStatuses.Resolved : roll < 0.9 ? IssueHandlingStatuses.Observing : IssueHandlingStatuses.WontFix;
                last = await ctx.Site.SendAsync(Actor, HttpMethod.Post, $"api/work-orders/{order.WorkOrderId}/reply",
                    new WorkOrderReplyRequest
                    {
                        Status = status, Note = "模擬回覆",
                        DueDate = status == IssueHandlingStatuses.Observing ? ctx.Site.Clock.LocalNow().Date.AddDays(7) : null,
                        CaseIds = take.Count == open.Count ? null : take.Select(c => c.CaseId).ToList()
                    });
                if (last.StatusCode == 200) { left -= take.Count; replied[day] = replied.GetValueOrDefault(day) + take.Count; }
            }
            return last;
        }
    }
}
