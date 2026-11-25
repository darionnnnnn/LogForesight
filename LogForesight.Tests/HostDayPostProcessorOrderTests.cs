using System.Collections.Concurrent;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機日落庫後的後續處理單一入口（<see cref="HostDayPostProcessor.RunAsync"/>）：本機與 NetIQ 兩條路徑
/// 的步驟順序只有一種。以 <see cref="HostDayPostProcessor.StepObserver"/> 記錄進入順序，
/// 兩條路徑各自帶的掛鉤（計時、結果計數、權限異動）都在各自的步驟上觸發。
/// </summary>
public sealed class HostDayPostProcessorOrderTests : IDisposable
{
    private static readonly HostDayPostStep[] Canonical =
    [
        HostDayPostStep.RecordTiming,
        HostDayPostStep.RegisterParent,
        HostDayPostStep.AttachPrtgFindings,
        HostDayPostStep.FinalizeNonAiReport,
        HostDayPostStep.RecordPrtgLeg,
        HostDayPostStep.RecordResult,
        HostDayPostStep.AttachCase,
        HostDayPostStep.ReplaceRiskyEvents,
        HostDayPostStep.RecordPermissionChanges
    ];

    private static readonly DateTime Day = new(2026, 9, 15);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-post-order-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly FakeHostStore _hosts = new();
    private readonly FakeIssueCaseStore _cases = new();
    private readonly FakeIssueHandlingStore _issueHandlings = new();
    private readonly FakeHandlingStore _handlingLog = new();
    private readonly FakeIssueOwnerStore _owners = new();
    private readonly FakeAnalysisRecordQuery _records = new();
    private readonly FakeWorkOrderStore _orders;
    private readonly IssueCaseCoordinator _caseCoordinator;

    public HostDayPostProcessorOrderTests()
    {
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _root);
        _orders = new FakeWorkOrderStore(_cases);
        _caseCoordinator = new IssueCaseCoordinator(_cases, _issueHandlings, _handlingLog, _records, _hosts, _owners);
    }

    public void Dispose()
    {
        HostDayPostProcessor.StepObserver.Value = null;
        try { Directory.Delete(_root, true); } catch { /* 暫存目錄清不掉不影響結果 */ }
        GC.SuppressFinalize(this);
    }

    private NightlyDispatch Dispatch()
    {
        var pool = new DispatchCandidatePool { ByUserId = new Dictionary<long, DispatchCandidate>() };
        var ctx = DispatchContext.Build(pool, _owners, _orders, _cases, new FakeNoiseMarkStore(), new SystemSettings(), DateTime.Now, TestRules.None);
        var coordinator = new WorkOrderCoordinator(_orders, _cases, _issueHandlings, _caseCoordinator, _handlingLog, _hosts);
        return new NightlyDispatch(coordinator, ctx, _hosts);
    }

    private HostDayPostRequest Request(WebHost host, DailyAnalysisRecord record, List<string> hooks, bool netiqShape,
        HostDayWorkflowService? workflow = null, bool needsAi = false) => new()
    {
        Record = record, HostId = host.HostId, HostName = host.HostName, Date = Day,
        Events = new List<EventLogEntryData>(), EventCount = 0, RunId = "run-1",
        PrtgEnabled = false, AiConfigured = false, NeedsAi = needsAi,
        PrtgFindings = PrtgFindingsRegistry.Empty, Store = _backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName }),
        Workflow = workflow, CaseCoordinator = _caseCoordinator, Dispatch = Dispatch(),
        RawEventRetentionDays = 14,
        RecordTiming = netiqShape ? () => hooks.Add("hook:RecordTiming") : null,
        FinalizeNonAiReport = _ => { hooks.Add("hook:FinalizeNonAiReport"); return Task.CompletedTask; },
        RecordResult = () => hooks.Add("hook:RecordResult"),
        PermissionChangeStore = null,
        PermissionHostDayClaims = netiqShape ? new ConcurrentDictionary<string, byte>() : null
    };

    private static DailyAnalysisRecord Record(WebHost host) => new()
    {
        Date = Day, HostId = host.HostId, Host = host.HostName, RecordId = 42, RiskLevel = RiskLevels.Low
    };

    private async Task<List<HostDayPostStep>> Observe(Func<Task> run)
    {
        var steps = new List<HostDayPostStep>();
        HostDayPostProcessor.StepObserver.Value = steps.Add;
        try { await run(); }
        finally { HostDayPostProcessor.StepObserver.Value = null; }
        return steps;
    }

    [Fact]
    public async Task 本機形狀與NetIQ形狀的步驟順序相同且只有一種()
    {
        var local = _hosts.Upsert(new WebHost { HostName = "LOCAL" });
        var netiq = _hosts.Upsert(new WebHost { HostName = "NETIQ" });

        var localHooks = new List<string>();
        var localSteps = await Observe(() =>
            HostDayPostProcessor.RunAsync(Request(local, Record(local), localHooks, netiqShape: false)));
        var netiqHooks = new List<string>();
        var netiqSteps = await Observe(() =>
            HostDayPostProcessor.RunAsync(Request(netiq, Record(netiq), netiqHooks, netiqShape: true)));

        Assert.Equal(Canonical, localSteps);
        Assert.Equal(Canonical, netiqSteps);
        // 路徑專屬掛鉤各自落在自己的步驟：本機沒有計時掛鉤，兩邊的結果計數都在 PRTG 腿之後、案件掛接之前
        Assert.Equal(new[] { "hook:FinalizeNonAiReport", "hook:RecordResult" }, localHooks);
        Assert.Equal(new[] { "hook:RecordTiming", "hook:FinalizeNonAiReport", "hook:RecordResult" }, netiqHooks);
    }

    [Fact]
    public async Task 掛鉤與共通步驟交錯的實際順序_計時先於父紀錄登記_結果計數先於案件掛接()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "H" });
        var timeline = new List<string>();
        HostDayPostProcessor.StepObserver.Value = step => timeline.Add(step.ToString());
        var request = Request(host, Record(host), timeline, netiqShape: true);
        await HostDayPostProcessor.RunAsync(request);

        var order = timeline.Select(t => t.Replace("hook:", "")).ToList();
        // 每個有掛鉤的步驟：步驟標記之後緊接著掛鉤
        Assert.Equal(["RecordTiming", "RecordTiming", "RegisterParent", "AttachPrtgFindings", "FinalizeNonAiReport",
            "FinalizeNonAiReport", "RecordPrtgLeg", "RecordResult", "RecordResult", "AttachCase", "ReplaceRiskyEvents",
            "RecordPermissionChanges"], order);
    }

    /// <summary>邊界兩側：需要 AI（報告留給 AI 排程）不補定案報告；不需要 AI 才補。步驟標記兩側都照常進入</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task 需要AI時不補定案報告_不需要時才補_步驟順序不變(bool needsAi, bool expectFinalize)
    {
        var host = _hosts.Upsert(new WebHost { HostName = "H" });
        var hooks = new List<string>();
        var steps = await Observe(() =>
            HostDayPostProcessor.RunAsync(Request(host, Record(host), hooks, netiqShape: false, needsAi: needsAi)));

        Assert.Equal(Canonical, steps);
        Assert.Equal(expectFinalize, hooks.Contains("hook:FinalizeNonAiReport"));
    }

    [Fact]
    public async Task 父紀錄登記用ForParentRecord指紋_且紀錄ID帶進工作流()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "H" });
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var record = Record(host);

        await HostDayPostProcessor.RunAsync(Request(host, record, new List<string>(), netiqShape: false, workflow: workflow));

        var state = workflow.Get(host.HostId, Day)!;
        Assert.Equal(WorkflowLegState.Succeeded, state.Parent);
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(record), state.DecisionFingerprint);
        Assert.Equal(42, state.ParentRecordId);
    }

    /// <summary>
    /// 守門：兩條路徑不得再自己呼叫後續步驟的零件（各寫一份就是順序分岔的起點），一律走 RunAsync。
    /// </summary>
    [Theory]
    [InlineData("LogForesight.Core/Service/NetiqPipelineService.cs")]
    [InlineData("LogForesight.Core/Service/AnalysisOrchestrator.cs")]
    public void 兩條路徑只經RunAsync呼叫後續步驟(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

        Assert.Contains("HostDayPostProcessor.RunAsync(", text);
        foreach (var part in new[] { "AttachPrtgFindings(", "AttachCase(", "ReplaceRiskyEvents(", "RecordPermissionChanges(" })
            Assert.DoesNotContain("HostDayPostProcessor." + part, text);
        Assert.DoesNotContain(".ParentSucceeded(", text);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
            dir = dir.Parent;
        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }
}
