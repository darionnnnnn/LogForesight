using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

[Collection("KnownIssueCatalogState")]
public sealed class PrtgDiskFormalFlowTests : IDisposable
{
    private const long DeviceId = 8101;
    private const long SensorId = 8102;
    private long HostId = 8103;
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly DateTime _completedDay = DateTime.Today.AddDays(-1);

    public PrtgDiskFormalFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-disk-formal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "formal.db")}"
        }, _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
        KnownIssueCatalog.Initialize(new List<KnownIssueRule> { DiskRule(enabled: true) });
    }

    public void Dispose()
    {
        KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SQLite正式路徑以28日與typed語意證據產生穩定finding並重跑冪等()
    {
        SeedDiskHistory(with28Days: true, descending: true);
        SeedTypedSemanticEvidence();

        var first = await RunPipeline();
        var findings = first.Registry.For(HostId, _completedDay);
        Assert.True(findings.Count == 1, string.Join(Environment.NewLine, first.Lines));
        var finding = Assert.Single(findings);
        Assert.Equal("PRTG:disk_free_trend", finding.Source);
        Assert.Equal($"prtg:disk_free_trend:{SensorId}", finding.EventKey);
        Assert.DoesNotContain("|", finding.EventKey);
        Assert.Equal("builtin-prtg-disk-free-trend", finding.RuleId);
        var issueCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var order = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Equal(91, order.HandlerId);
        Assert.Equal(order.WorkOrderId, issueCase.WorkOrderId);
        Assert.Contains(_backend.IssueHandlingStore().GetByCase(issueCase.CaseId),
            handling => !string.IsNullOrWhiteSpace(handling.Note));

        var second = await RunPipeline();
        Assert.Single(second.Registry.For(HostId, _completedDay));
        Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Contains(_backend.IssueHandlingStore().GetByCase(issueCase.CaseId),
            handling => !string.IsNullOrWhiteSpace(handling.Note));
    }

    [Fact]
    public async Task 停用規則及不足28個有效日均不派送()
    {
        SeedDiskHistory(with28Days: true, descending: true);
        SeedTypedSemanticEvidence();
        KnownIssueCatalog.Initialize(new List<KnownIssueRule> { DiskRule(enabled: false) });
        var disabled = await RunPipeline();
        Assert.Empty(disabled.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));

    }

    [Fact]
    public async Task 不足28個有效日不產生finding或案件交辦()
    {
        SeedDiskHistory(with28Days: false, descending: true);
        SeedTypedSemanticEvidence();
        KnownIssueCatalog.Initialize(new List<KnownIssueRule> { DiskRule(enabled: true) });
        var insufficient = await RunPipeline();
        Assert.Empty(insufficient.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
    }

    private void SeedDiskHistory(bool with28Days, bool descending)
    {
        var now = DateTime.UtcNow;
        var host = _hosts.Upsert(new WebHost { HostId = HostId, HostName = "DISK-HOST", Active = true, IpAddress = "192.0.2.81" });
        HostId = host.HostId;
        new IssueOwnerStore(_backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = "PRTG:disk_free_trend", EventId = 0, OwnerUserIds = new List<long> { 91 }
        });
        _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" }).Append(new DailyAnalysisRecord
        {
            Date = _completedDay, HostId = HostId, Host = "DISK-HOST", RiskLevel = RiskLevels.Low,
            RiskBasis = "formal flow fixture"
        });
        var prtg = _backend.PrtgStore();
        prtg.UpsertDevices(new[] { new PrtgDeviceRow { Objid = DeviceId, Name = "DISK-HOST", Ip = "192.0.2.81" } }, now);
        prtg.UpsertSensors(new[] { new PrtgSensorRow
        {
            Objid = SensorId, DeviceObjid = DeviceId, Name = "Disk C: free", SensorType = "SNMP Disk Free",
            Category = PrtgSensorCategories.Disk, Status = "Up"
        } }, now);

        var days = with28Days ? 28 : 27;
        var firstDay = _completedDay.AddDays(-(days - 1));
        for (var offset = 0; offset < days; offset++)
        {
            var date = firstDay.AddDays(offset).Date;
            prtg.ReplaceHostMapForDate(date, new[] { new PrtgHostMapRow
            {
                DeviceObjid = DeviceId, MapDate = date, HostId = HostId, HostName = "DISK-HOST",
                MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            } });
            var dailyPercent = descending ? 48d - offset * 1.1 : 48d;
            var values = new List<PrtgValueRow>();
            for (var hour = 0; hour < 24; hour++)
                values.Add(new PrtgValueRow
                {
                    SensorObjid = SensorId, PeriodStart = date.AddHours(hour), AvgValue = dailyPercent,
                    MinValue = dailyPercent, MaxValue = dailyPercent, Coverage = 100,
                    Quality = PrtgDataQuality.Ok, CreatedAt = now
                });
            prtg.UpsertValues(values);
        }
    }

    private void SeedTypedSemanticEvidence()
    {
        var evidence = new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        evidence.ConfirmManually(new PrtgDiskSemanticContext(SensorId, DeviceId, HostId, "SNMP Disk Free",
            "free", "Free", "%", 1, "descending-danger"), 42,
            "Typed probe confirmed the main Free channel is a descending percent-available value.",
            DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion);
        new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(
            new PrtgDiskVerificationResult(SensorId, DeviceId, HostId, "SNMP Disk Free", "Verified",
                "Typed channel values matched persisted samples.", "free", "Free", "%", 1,
                "descending-danger", 3, true, DateTime.UtcNow, _completedDay,
                PrtgDiskAssessmentService.ParserSemanticVersion));
    }

    private async Task<(PrtgFindingsRegistry Registry, List<string> Lines)> RunPipeline()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = "http://192.0.2.81:1";
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("offline-test-token");
            s.PrtgTimeoutSeconds = 1;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgResourceGuardEnabled = false;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free" };
        });
        var console = new CapturingConsole();
        var registry = new PrtgFindingsRegistry();
        var coordinator = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts,
            new IssueOwnerStore(_backend.Blob("issue_owners")));
        var orders = _backend.WorkOrderStore();
        var pool = new DispatchCandidatePool
        {
            ByUserId = new Dictionary<long, DispatchCandidate>
            {
                [91] = new DispatchCandidate { UserId = 91, Account = "disk-owner", InPool = true,
                    VisibleHostIds = new HashSet<long> { HostId } }
            },
            PoolMemberCount = 1,
            ActivePoolMemberCount = 1
        };
        var dispatchContext = DispatchContext.Build(pool, new IssueOwnerStore(_backend.Blob("issue_owners")),
            orders, _backend.IssueCaseStore(), new FakeNoiseMarkStore(), new SystemSettings { AutoDispatchEnabled = true }, DateTime.Now);
        var dispatch = new NightlyDispatch(new WorkOrderCoordinator(orders, _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), coordinator, _backend.RecordHandlingStore(), _hosts), dispatchContext, _hosts);
        var recorder = new BatchRunRecorder(new BatchRunStore(_backend.LogStore("batch_runs"),
            _backend.LogStore("batch_run_logs")), "disk-formal-test", Array.Empty<string>());
        var context = new AnalysisRunContext(new RunRequest(), new AppSettings(), new RetentionOptions(), console,
            CancellationToken.None, new EventLogService(), coordinator, _backend.RiskyEventStore(), recorder,
            new OrchestratorResult(), false, null, registry, dispatch);

        await PrtgDailyPipeline.RunAsync(context, _backend, _hosts, new[] { _completedDay }, Task.CompletedTask,
            hostIds: null, guard: null, structureSyncGate: new CompletedStructureSyncGate());
        return (registry, console.Lines);
    }

    private static KnownIssueRule DiskRule(bool enabled) => new()
    {
        Id = "builtin-prtg-disk-free-trend", Origin = "builtin", Enabled = enabled, Scope = "all",
        Platform = "prtg", PrtgRuleCode = PrtgDiskRuleDecision.RuleCode, PrtgThreshold = 0,
        PrtgSensorCategory = PrtgSensorCategories.Disk, PrtgDiskTrendThresholds = PrtgDiskTrendThresholds.Provisional,
        Category = IssueCategory.Storage, Severity = IssueSeverity.High,
        Description = "Disk free trend", PlainExplanation = "Disk free space is declining."
    };

    private sealed class CapturingConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    private sealed class CompletedStructureSyncGate : IPrtgStructureSyncGate
    {
        public bool IsRunning => true;
        public Task<bool> WaitUntilIdleAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
