using System.Diagnostics;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit.Abstractions;

namespace LogForesight.Tests;

public class WorkOrderScaleBenchmarks
{
    private readonly ITestOutputHelper _out;

    public WorkOrderScaleBenchmarks(ITestOutputHelper output) => _out = output;

    private ScaleDataSet GenerateData(ScaleProfile profile)
    {
        _out.WriteLine($"===== {profile} =====");
        _out.WriteLine("");
        _out.WriteLine("[產生資料]");

        var generateSw = Stopwatch.StartNew();
        var data = ScaleDataSet.Generate(profile, _out.WriteLine);
        generateSw.Stop();

        var s = data.Stats;
        _out.WriteLine($"  合計：DB 檔 {s.DbFileBytes / 1024 / 1024:N0} MB、產生耗時 {generateSw.Elapsed.TotalSeconds:N1} 秒");
        _out.WriteLine("");
        return data;
    }

    [ScaleFact]
    public void 交辦建單_4000台同步段與背景展開()
    {
        var profile = new ScaleProfile("4000x60", HostCount: 4000, Days: 60, DistinctIssues: 600, IssuesPerRecord: 15, HandlingRowsPerHostDay: 0, OpenCaseCount: 0, TombstoneCount: 0);
        using var data = GenerateData(profile);
        var services = new ScaleServices(data);

        var sig = data.Signatures[0];
        var to = DateTime.Today;
        var from = to.AddDays(-profile.Days + 1);

        var req = new CreateWorkOrderRequest
        {
            Source = sig.Source,
            EventId = sig.EventId,
            From = from,
            To = to,
            HostIds = data.LivingHosts.Select(h => h.HostId).ToList(),
            ScopeKind = WorkOrderScopes.Hosts,
            AssignMode = "single",
            HandlerId = 2
        };

        var createSw = Stopwatch.StartNew();
        var result = services.WorkOrderCommands.Create(req);
        createSw.Stop();
        var createMs = createSw.ElapsedMilliseconds;

        var targetCreate = createMs < 5000 ? "達標" : "未達標";
        _out.WriteLine($"[同步段建單] 耗時：{createMs:N0} ms（目標 < 5,000 ms，{targetCreate}）");

        var createdCases = result.Orders.Sum(o => o.NewCases);
        _out.WriteLine($"[案件建立] 建立案件數：{createdCases:N0} 件");

        var batchTimes = new List<long>();
        var batchSw = Stopwatch.StartNew();
        while (true)
        {
            var sw = Stopwatch.StartNew();
            var count = services.CaseCoordinator.ApplyPendingCaseDays(500);
            sw.Stop();
            if (count == 0) break;
            batchTimes.Add(sw.ElapsedMilliseconds);
        }
        batchSw.Stop();

        var batchCount = batchTimes.Count;
        var maxBatchMs = batchCount > 0 ? batchTimes.Max() : 0;
        var avgBatchMs = batchCount > 0 ? batchTimes.Average() : 0;
        _out.WriteLine($"[背景展開] 總耗時：{batchSw.ElapsedMilliseconds:N0} ms，總批數：{batchCount} 批，最大每批：{maxBatchMs:N0} ms，平均每批：{avgBatchMs:F1} ms");
    }

    [ScaleFact]
    public void 交辦回覆_750成員同步段()
    {
        var profile = new ScaleProfile("4000x10", HostCount: 4000, Days: 10, DistinctIssues: 600, IssuesPerRecord: 15, HandlingRowsPerHostDay: 0, OpenCaseCount: 0, TombstoneCount: 0);
        using var data = GenerateData(profile);
        var services = new ScaleServices(data);

        var sig = data.Signatures[0];
        var to = DateTime.Today;
        var from = to.AddDays(-profile.Days + 1);
        const long handlerId = 2;

        var req = new CreateWorkOrderRequest
        {
            Source = sig.Source,
            EventId = sig.EventId,
            From = from,
            To = to,
            HostIds = data.LivingHosts.Take(750).Select(h => h.HostId).ToList(),
            ScopeKind = WorkOrderScopes.Hosts,
            AssignMode = "single",
            HandlerId = handlerId
        };

        var created = services.WorkOrderCommands.Create(req);
        var orderId = created.Orders[0].WorkOrderId;

        while (services.CaseCoordinator.ApplyPendingCaseDays(500) > 0)
        {
        }

        var replyService = services.ReplyServiceAs(handlerId);
        var replySw = Stopwatch.StartNew();
        var replyResult = replyService.Reply(orderId, new WorkOrderReplyRequest
        {
            Status = IssueHandlingStatuses.Resolved
        });
        replySw.Stop();
        var replyMs = replySw.ElapsedMilliseconds;

        var targetReply = replyMs < 3000 ? "達標" : "未達標";
        _out.WriteLine($"[交辦回覆] 耗時：{replyMs:N0} ms（目標 < 3,000 ms，{targetReply}），回覆案件數：{replyResult.Cases:N0}，結案：{replyResult.WorkOrderClosed}");
    }

    [ScaleFact]
    public void 夜間掛接含自動派工_4000主機日()
    {
        var profile = new ScaleProfile("4000x2", HostCount: 4000, Days: 2, DistinctIssues: 600, IssuesPerRecord: 15, HandlingRowsPerHostDay: 0, OpenCaseCount: 0, TombstoneCount: 0);
        var lastDate = DateTime.Today;

        // 第一輪：CreateUnavailable（對照）
        long ms1;
        using (var data1 = GenerateData(profile))
        {
            var services1 = new ScaleServices(data1);
            var records1 = data1.Backend.RecordStore().ReadRecent(lastDate, 1);

            var ctx1 = DispatchContext.CreateUnavailable(services1.Cases);
            var dispatch1 = new NightlyDispatch(services1.WorkOrders, ctx1, services1.Hosts);

            var sw1 = Stopwatch.StartNew();
            foreach (var r in records1)
            {
                HostDayPostProcessor.AttachCase(services1.CaseCoordinator, dispatch1, r.Host, r.Date, r.TopIssues);
            }
            dispatch1.FlushRun(DateTime.Now);
            sw1.Stop();
            ms1 = sw1.ElapsedMilliseconds;
        }

        // 第二輪：換一份新產生的同 profile 資料集，用 Build 且設定 AutoDispatchEnabled = true
        long ms2;
        NightlyDispatchSummary summary2;
        using (var data2 = GenerateData(profile))
        {
            var services2 = new ScaleServices(data2);
            var records2 = data2.Backend.RecordStore().ReadRecent(lastDate, 1);

            var candidate = new DispatchCandidate
            {
                UserId = 2,
                Account = "scale-user",
                InPool = true,
                Paused = false,
                VisibleHostIds = data2.LivingHosts.Select(h => h.HostId).ToHashSet()
            };
            var pool = new DispatchCandidatePool
            {
                ByUserId = new Dictionary<long, DispatchCandidate> { [candidate.UserId] = candidate },
                PoolMemberCount = 1,
                ActivePoolMemberCount = 1
            };

            var settings = new SystemSettings { AutoDispatchEnabled = true };
            var issueOwners = new IssueOwnerStore(data2.Backend.Blob("issue_owners"));
            var noiseMarks = new NoiseMarkStore(data2.Backend.Blob("noise_marks"));
            var ctx2 = DispatchContext.Build(
                pool,
                issueOwners,
                services2.WorkOrderStore,
                services2.Cases,
                noiseMarks,
                settings,
                DateTime.Now);
            var dispatch2 = new NightlyDispatch(services2.WorkOrders, ctx2, services2.Hosts);

            var sw2 = Stopwatch.StartNew();
            foreach (var r in records2)
            {
                HostDayPostProcessor.AttachCase(services2.CaseCoordinator, dispatch2, r.Host, r.Date, r.TopIssues);
            }
            summary2 = dispatch2.FlushRun(DateTime.Now);
            sw2.Stop();
            ms2 = sw2.ElapsedMilliseconds;
        }

        var diffPercent = ms1 > 0 ? (double)Math.Abs(ms2 - ms1) / ms1 * 100.0 : 0.0;
        var targetDiff = diffPercent < 20.0 ? "達標" : "未達標";

        _out.WriteLine($"[第一輪：不派工對照組] 耗時：{ms1:N0} ms");
        _out.WriteLine($"[第二輪：含自動派工] 耗時：{ms2:N0} ms");
        _out.WriteLine($"[兩輪差異] 差異百分比：{diffPercent:F1}%（目標差 < 20%，{targetDiff}）");
        _out.WriteLine($"[第二輪產出] 建單數：{summary2.CreatedOrders:N0} 張，掛入台數：{summary2.AttachedMembers:N0} 台");
    }

    [ScaleFact]
    public void 缺口試跑_2000主機()
    {
        var profile = new ScaleProfile("2000x30", HostCount: 2000, Days: 30, DistinctIssues: 600, IssuesPerRecord: 15, HandlingRowsPerHostDay: 0, OpenCaseCount: 0, TombstoneCount: 0);
        using var data = GenerateData(profile);
        var services = new ScaleServices(data);

        var sw = Stopwatch.StartNew();
        var gapsDto = services.WorkOrderBoard.GetGaps(null, null, 1);
        sw.Stop();
        var elapsedMs = sw.ElapsedMilliseconds;

        var targetGaps = elapsedMs < 5000 ? "達標" : "未達標";
        _out.WriteLine($"[缺口試跑] 耗時：{elapsedMs:N0} ms（目標 < 5,000 ms，{targetGaps}），缺口筆數：{gapsDto.Total:N0} 筆");
    }
}
