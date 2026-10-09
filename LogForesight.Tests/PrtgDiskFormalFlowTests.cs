using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

[Collection("KnownIssueCatalogState")]
public sealed partial class PrtgDiskFormalFlowTests : IDisposable
{
    private const long DeviceId = 8101;
    private const long SensorId = 8102;
    private long HostId = 8103;
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly string _dir;
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly DateTime _completedDay = new DateTime(2026, 9, 30);

    public PrtgDiskFormalFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lf-disk-formal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_dir, "formal.db")}"
        }, _dir);
        _hosts = new HostStore(_backend.Blob("hosts"));
        SetDiskRules(enabled: true);
        var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
        _url = $"http://127.0.0.1:{number}";
        _listener.Prefixes.Add(_url + "/"); _listener.Start();
        _ = Task.Run(async () => {
            while (_listener.IsListening) {
                HttpListenerContext request;
                try { request = await _listener.GetContextAsync(); } catch (Exception) { break; }
                var content = request.Request.QueryString["content"];
                object response = content == "channels" ? new { channels = new[] { new { objid="free", channel="Free Space", unit="%", scaling=1, primary=true } } }
                    : content == "sensors" ? new { sensors = new[] { new { objid=SensorId, parentid=DeviceId, type="SNMP Disk Free", status="Up", cumsince="creation-1", sensor="Disk C: free" } } }
                    : request.Request.Url!.AbsolutePath.Contains("historicdata") ? new { histdata = Enumerable.Range(0,24).Select(hour => new {
                        datetime=_completedDay.AddHours(hour).ToString("yyyy-MM-dd HH:mm:ss"), datetime_raw=_completedDay.AddHours(hour).ToOADate(), value_raw=18.3 }).ToArray() }
                    : new { messages = Array.Empty<object>() };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
                request.Response.ContentType="application/json"; request.Response.ContentLength64=bytes.Length;
                await request.Response.OutputStream.WriteAsync(bytes); request.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _listener.Stop(); _listener.Close();
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
        var confirmedIdentity = _backend.PrtgStore().GetResourceIdentity(SensorId);
        var currentRules = PrtgResourceCurrentRuleCatalog.Load(_backend);
        Assert.True(currentRules.DiskTrendEnabled);
        Assert.NotNull(currentRules.For(PrtgResourceFamily.Disk));

        var first = await RunPipeline();
        var findings = first.Registry.For(HostId, _completedDay);
        Assert.True(findings.Count == 1, string.Join(Environment.NewLine, first.Lines));
        var finding = Assert.Single(findings);
        Assert.Equal("PRTG:disk_free_trend", finding.Source);
        Assert.Equal($"prtg:disk_free_trend:{SensorId}:b582b0e038e34b0697d74368fcd266dd:{confirmedIdentity.Generation}", finding.EventKey);
        Assert.Equal(confirmedIdentity.Generation, _backend.PrtgStore().GetResourceIdentity(SensorId).Generation);
        Assert.DoesNotContain("|", finding.EventKey);
        Assert.Equal("builtin-prtg-resource-disk-pressure", finding.RuleId);
        Assert.Equal(new[] { "disk-seven-day-low-water-trend" }, finding.PrtgResourceReasonCodes);
        Assert.Equal("builtin-prtg-disk-free-trend", finding.PrtgTrendSourceRuleId);
        Assert.False(string.IsNullOrWhiteSpace(finding.PrtgTrendSourceRuleFingerprint));
        var issueCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var order = Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Equal(91, order.HandlerId);
        Assert.Equal(order.WorkOrderId, issueCase.WorkOrderId);
        Assert.Contains(_backend.IssueHandlingStore().GetByCase(issueCase.CaseId),
            handling => !string.IsNullOrWhiteSpace(handling.Note));

        var second = await RunPipeline();
        Assert.Single(second.Registry.For(HostId, _completedDay));
        Assert.Equal(confirmedIdentity.Generation, _backend.PrtgStore().GetResourceIdentity(SensorId).Generation);
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
        SetDiskRules(enabled: false);
        var disabled = await RunPipeline();
        Assert.Empty(disabled.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));

    }

    [Fact]
    public async Task 不足28個有效日不產生finding或案件交辦()
    {
        SeedDiskHistory(with28Days: false, descending: true);
        SeedTypedSemanticEvidence();
        SetDiskRules(enabled: true);
        var insufficient = await RunPipeline();
        Assert.Empty(insufficient.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
    }

    [Fact]
    public async Task 今日首次確認語意不可追認既有28日資料()
    {
        SeedDiskHistory(with28Days: true, descending: true, trustedHistory: false); SeedTypedSemanticEvidence();
        var run = await RunPipeline(newSemanticConfirmation: true);
        Assert.Empty(run.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
    }

    private void SeedDiskHistory(bool with28Days, bool descending, bool trustedHistory = true,
        bool excludeOutsideParentDay = false, bool omitPriorAnalysisDayHoursBelongingToParentDay = false,
        bool highNetiqBaseline = false)
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgUrl = _url;
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        var now = DateTime.UtcNow;
        var host = _hosts.Upsert(new WebHost { HostId = HostId, HostName = "DISK-HOST", Source = "netiq", Active = true, IpAddress = "192.0.2.81" });
        HostId = host.HostId;
        new IssueOwnerStore(_backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = "PRTG:disk_free_trend", EventId = 0, OwnerUserIds = new List<long> { 91 }
        });
        _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" }).Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success", AiAnalyzed = false,
            Date = _completedDay, HostId = HostId, Host = "DISK-HOST",
            RiskLevel = highNetiqBaseline ? RiskLevels.High : RiskLevels.Low,
            RiskBasis = highNetiqBaseline ? "fixture independent NetIQ high risk" : "formal flow fixture",
            PrtgBaselineRiskLevel = highNetiqBaseline ? RiskLevels.High : null,
            PrtgBaselineRiskBasis = highNetiqBaseline ? "fixture independent NetIQ high risk" : null
        });
        var prtg = _backend.PrtgStore();
        prtg.UpsertDevices(new[] { new PrtgDeviceRow { Objid = DeviceId, Name = "DISK-HOST", Ip = "192.0.2.81" } }, now);
        prtg.UpsertSensors(new[] { new PrtgSensorRow
        {
            Objid = SensorId, DeviceObjid = DeviceId, Name = "Disk C: free", SensorType = "SNMP Disk Free",
            Category = PrtgSensorCategories.Disk, Status = "Up"
        } }, now);
        prtg.ApplyAutoCategories(PrtgSensorTypeCategoryMap.ParseOverrides(
            new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgSensorTypeCategoryOverrides).Map);

        var days = with28Days ? 28 : 27;
        var firstDay = _completedDay.AddDays(-(days - 1));
        var localParentDayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var pendingHours = new List<(DateTime PeriodStart, double Value)>();
        for (var offset = 0; offset < days; offset++)
        {
            var date = firstDay.AddDays(offset).Date;
            prtg.ReplaceHostMapForDate(date, new[] { new PrtgHostMapRow
            {
                DeviceObjid = DeviceId, MapDate = date, HostId = HostId, HostName = "DISK-HOST",
                MapStatus = PrtgMapStatus.Ok, CreatedAt = now
            } });
            var dailyPercent = descending ? 48d - offset * 1.1 : 48d;
            for (var hour = 0; hour < 24; hour++)
            {
                var period = date.AddHours(hour);
                if (omitPriorAnalysisDayHoursBelongingToParentDay && date < _completedDay.Date &&
                    DateTime.SpecifyKind(period, DateTimeKind.Utc) >= localParentDayStartUtc)
                    continue;
                if (excludeOutsideParentDay && DateTime.SpecifyKind(period, DateTimeKind.Utc) >=
                    TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(_completedDay.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local))
                    continue;
                pendingHours.Add((period, dailyPercent));
            }
        }
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p => { p.Revision = "fixture"; p.CoreSystemId = "core"; p.SourceGeneration = "b582b0e038e34b0697d74368fcd266dd";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(_url); p.ValidFrom = new DateTimeOffset(_completedDay.AddDays(-31));
            p.HostIds = [HostId]; p.SensorIds = [SensorId]; p.SourceTimeZoneId = "UTC";
            p.RawTimestampTimeZoneId = "UTC"; p.AnalysisTimeZoneId = "UTC";
            p.TimeBasisEvidenceReference = "fixture-time-basis-proof"; p.SourceCultureName = "en-US"; });
        var effectiveHour = DateTime.SpecifyKind(_completedDay.Date.AddDays(-32), DateTimeKind.Utc);
        if (trustedHistory)
        {
            var profile = PrtgResourceFixture.ConfigureDiskTrustedProfile(prtg,
                _backend.Blob(PrtgMonitoringPolicyStore.BlobKey),
                _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey),
                SensorId, DeviceId, HostId, "SNMP Disk Free", effectiveHour,
                creationReference: "creation-1", sourceGeneration: "b582b0e038e34b0697d74368fcd266dd");
            prtg.MergeSampledValues(pendingHours.Select(hour => PrtgResourceFixture.TrustedDiskHour(
                SensorId, hour.PeriodStart, profile, hour.Value)).ToArray());
        }
        else
            prtg.UpsertValues(pendingHours.Select(hour => new PrtgValueRow
            {
                SensorObjid = SensorId, PeriodStart = hour.PeriodStart, AvgValue = hour.Value,
                MinValue = hour.Value, MaxValue = hour.Value, Coverage = 100,
                Quality = PrtgDataQuality.Ok, CreatedAt = now
            }).ToArray());
    }

    private static KnownIssueRule DiskRule(bool enabled) => KnownIssueSeed.CreateRules()
        .Single(rule => rule.Id == "builtin-prtg-disk-free-trend").CloneForSeedOverwrite(enabled);

    private void SetDiskRules(bool enabled)
    {
        var rules = KnownIssueSeed.CreateRules()
            .Where(rule => rule.Id is "builtin-prtg-disk-free-trend" or "builtin-prtg-resource-disk-pressure")
            .Select(rule => rule.CloneForSeedOverwrite(enabled)).ToList();
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        {
            SeedVersion = KnownIssueSeed.Version,
            Rules = rules
        });
        KnownIssueCatalog.Initialize(rules);
    }

    private void SeedTypedSemanticEvidence()
    {
        var prtg = _backend.PrtgStore();
        var policyBlob = _backend.Blob(PrtgMonitoringPolicyStore.BlobKey);
        var policyStore = new PrtgMonitoringPolicyStore(policyBlob);
        var policy = policyStore.Get();
        var identity = prtg.GetResourceIdentity(SensorId);
        var expectedChannelFingerprint = JsonSerializer.Serialize(new
        { ChannelIdentifier = "free", ChannelName = "Free Space", Unit = "%", Scale = (double?)1, Direction = "descending-danger" }) +
            "|" + PrtgDiskAssessmentService.ParserSemanticVersion;
        if (!PrtgConsumerProfileFixtureClosure.HasCurrentQualifiedBinding(policyBlob, identity))
        {
            identity = prtg.BindObservedResource(SensorId, HostId, policy.SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), "SNMP Disk Free", "creation-1", 0));
            identity = prtg.SetObservedChannel(SensorId, identity.SourceGeneration,
                expectedChannelFingerprint, identity.Generation);
        }
        var channelFingerprint = identity.ChannelFingerprint
            ?? throw new InvalidOperationException("Typed semantic fixture requires the final channel identity fence.");
        var evidence = new PrtgDiskSemanticEvidenceStore(_backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey));
        evidence.ConfirmManually(new PrtgDiskSemanticContext(SensorId, DeviceId, HostId, "SNMP Disk Free",
            "free", "Free Space", "%", 1, "descending-danger"), 42,
            "Typed probe confirmed the main Free channel is a descending percent-available value.",
            DateTime.UtcNow, PrtgDiskAssessmentService.ParserSemanticVersion, identity.SourceGeneration,
            identity.Generation, identity.ChannelGeneration, identity.Epoch);
        new PrtgDiskVerificationResultStore(_backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Save(
            new PrtgDiskVerificationResult(SensorId, DeviceId, HostId, "SNMP Disk Free", "Verified",
                "Typed channel values matched persisted samples.", "free", "Free Space", "%", 1,
                "descending-danger", 3, true, DateTime.UtcNow, _completedDay,
                PrtgDiskAssessmentService.ParserSemanticVersion, SourceGeneration: identity.SourceGeneration,
                ResourceGeneration: identity.Generation, ChannelGeneration: identity.ChannelGeneration,
                IdentityEpoch: identity.Epoch));

        var revision = _backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + SensorId)).Update(e =>
        {
            e.Bind(SensorId, HostId, identity.SourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), "SNMP Disk Free", "creation-1", 0), identity.Generation,
                identity.Epoch, identity.ChannelGeneration, policy.ValidFrom);
            e.MappingRevision = revision;
            e.DiskSemanticValidFrom = policy.ValidFrom;
            e.DiskSemanticCheckedAt = DateTimeOffset.UtcNow;
            e.DiskSemanticFingerprint = channelFingerprint;
            e.Accept(e.ValidFrom, DateTimeOffset.Now,
                [new(SensorId, e.ValidFrom, "Up", e.SourceGeneration, e.ResourceGeneration)]);
        });
    }

    private async Task<(PrtgFindingsRegistry Registry, List<string> Lines)> RunPipeline(bool newSemanticConfirmation = false, bool withWorkflow = false)
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(s =>
        {
            s.PrtgEnabled = true;
            s.PrtgUrl = _url;
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = CryptoHelper.Encrypt("offline-test-token");
            s.PrtgTimeoutSeconds = 1;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            s.PrtgResourceGuardEnabled = false;
            s.PrtgSensorTypeWhitelist = new List<string> { "SNMP Disk Free" };
        });
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        if (!policyStore.Get().Ready(_url))
        {
            policyStore.Update(p => { p.Revision="fixture"; p.CoreSystemId="core"; p.SourceGeneration="b582b0e038e34b0697d74368fcd266dd";
                p.EndpointHint=EfPrtgObservationStore.SourceHintFor(_url); p.ValidFrom=new DateTimeOffset(_completedDay.AddDays(-31));
                p.HostIds=[HostId]; p.SensorIds=[SensorId]; if (p.SourceTimeZoneId.Length == 0) p.SourceTimeZoneId="UTC"; p.SourceCultureName="en-US"; });
        }
        var profiles = _backend.PrtgStore().GetTrustedSamplingProfiles([SensorId]);
        if (policyStore.Get().SensorIds.Contains(SensorId) &&
            profiles.TryGetValue(SensorId, out var sourceProfile))
        {
            var settings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
            PrtgConsumerProfileFixtureClosure.PublishEfFixture(_backend.PrtgStore(),
                _backend.Blob(PrtgMonitoringPolicyStore.BlobKey), settings.Revision,
                PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), sourceProfile);
        }
        if (newSemanticConfirmation)
            new PrtgSensorTimelineStore(_backend.Blob(PrtgSensorTimelineStore.Prefix + SensorId)).Update(e =>
                e.DiskSemanticValidFrom = DateTimeOffset.Now);
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
        if (withWorkflow)
        {
            var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
            var parent = _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
                .ReadRecent(_completedDay, 1).Single();
            workflow.ParentSucceeded(HostId, "DISK-HOST", _completedDay, "disk-formal-test-parent",
                parent.AuditEventCount, HostDayWorkflowFingerprint.ForParentRecord(parent),
                prtgEnabled: true, aiEnabled: false, parentRecordId: parent.RecordId);
            context = context with { Workflow = workflow, PrtgEnabled = true };
        }

        await PrtgDailyPipeline.RunAsync(context, _backend, _hosts, new[] { _completedDay }, Task.CompletedTask,
            hostIds: null, guard: null, structureSyncGate: new CompletedStructureSyncGate(),
            requestBudget: new PrtgRequestBudget());
        return (registry, console.Lines);
    }

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
