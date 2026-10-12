using System.Text.Json;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgJointCapacityEstimateControllerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-joint-estimate-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;
    private readonly SettingsController _controller;

    public PrtgJointCapacityEstimateControllerTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
        _controller = new SettingsController(new StubSettingsService(), new AiUsageStore(_backend.Blob("ai_usage")),
            new RecordingAuditService(), backend: _backend, hosts: _hosts);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Estimate_reports_joint_profile_group_deadline_failure_when_both_components_qualify()
    {
        var dto = SeedEstimate(snapshotSensorCount: 5, profileSensorCount: 5,
            snapshotP95Milliseconds: 1000, profileP95MillisecondsPerSensor: 3000);

        Assert.Equal("capacity-qualified", dto.SnapshotCapacityStatus);
        Assert.Equal("capacity-qualified", dto.ProfileCapacityStatus);
        Assert.Equal(5, dto.ProfileCapacityTargetSensors);
        Assert.Equal(12, dto.ProfileCapacityExpectedTableRequests);
        Assert.Equal(180d, dto.SnapshotCapacityWindowSeconds);
        Assert.Equal(62_100d, dto.ProfileCapacityWindowSeconds);
        Assert.Equal(135d, dto.JointSnapshotWindowSeconds!.Value);
        Assert.Equal("capacity-exceeded", dto.JointCapacityStatus);
        Assert.Equal("profile_probe_group_deadline_exceeded", dto.JointCapacityReason);
        Assert.Equal(1, dto.QualificationEligibleSensors);
        Assert.Equal(4, dto.QualificationWorstCaseTableRequests);
        Assert.Equal(1, dto.QualificationWorstCaseHistoricRequests);
        Assert.Equal("capacity-unverified", dto.QualificationCapacityStatus);
        Assert.Equal("shared-table-plan-missing-or-stale", dto.QualificationCapacityReason);
        Assert.Null(dto.QualificationRequiredSeconds);
        AssertFiniteJson(dto);
    }

    [Fact]
    public void Estimate_reports_joint_shared_quota_failure_when_both_components_qualify()
    {
        var dto = SeedEstimate(snapshotSensorCount: 13_400, profileSensorCount: 13_400,
            snapshotP95Milliseconds: 1000, profileP95MillisecondsPerSensor: 10);

        Assert.Equal("capacity-qualified", dto.SnapshotCapacityStatus);
        Assert.Equal("capacity-qualified", dto.ProfileCapacityStatus);
        Assert.Equal(13_400, dto.ProfileCapacityTargetSensors);
        Assert.Equal(PrtgProfileTransportCapacityEvaluator.ExpectedScopeRequests(13_400),
            dto.ProfileCapacityExpectedTableRequests);
        Assert.Equal(180d, dto.SnapshotCapacityWindowSeconds);
        Assert.Equal(62_100d, dto.ProfileCapacityWindowSeconds);
        Assert.Equal(135d, dto.JointSnapshotWindowSeconds!.Value);
        Assert.Equal("capacity-exceeded", dto.JointCapacityStatus);
        Assert.Equal("joint_table_quota_or_traffic_exceeded", dto.JointCapacityReason);
        Assert.Equal(1, dto.QualificationEligibleSensors);
        Assert.Equal(4, dto.QualificationWorstCaseTableRequests);
        Assert.Equal(1, dto.QualificationWorstCaseHistoricRequests);
        AssertFiniteJson(dto);
    }

    [Fact]
    public void Estimate_uses_selected_qualification_duration_and_attempts_for_initialization_cost()
    {
        var dto = SeedEstimate(snapshotSensorCount: 5, profileSensorCount: 5,
            snapshotP95Milliseconds: 1000, profileP95MillisecondsPerSensor: 3000,
            qualificationDurationHours: 24, qualificationMaximumAttempts: 2);

        Assert.Equal(24, dto.QualificationDurationHours);
        Assert.Equal(2, dto.QualificationAttemptsAssumed);
        Assert.Equal(8, dto.QualificationWorstCaseTableRequests);
        Assert.Equal(2, dto.QualificationWorstCaseHistoricRequests);
    }

    private PrtgValueFetchScopeEstimateDto SeedEstimate(int snapshotSensorCount, int profileSensorCount, int snapshotP95Milliseconds,
        int profileP95MillisecondsPerSensor, int qualificationDurationHours = 72, int qualificationMaximumAttempts = 1)
    {
        const string url = "https://prtg.example.test";
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(s =>
        {
            s.PrtgUrl = url;
            s.PrtgAuthMode = PrtgAuthModes.Token;
            s.PrtgApiTokenEnc = "capacity-fixture-token";
            s.PrtgTimeoutSeconds = 30;
            s.PrtgEnabled = true;
            s.PrtgFetchStrategy = PrtgFetchStrategy.Aggressive;
            s.PrtgValueFetchScope = PrtgValueFetchScope.AllMapped;
            s.PrtgSensorTypeWhitelist = ["cpu"];
        });

        var host = _hosts.Upsert(new WebHost { HostName = "joint-capacity-host", IpAddress = "192.0.2.10", Active = true });
        var now = DateTimeOffset.UtcNow;
        var sensorIds = Enumerable.Range(0, profileSensorCount).Select(index => 10_000L + index).ToArray();
        var prtg = _backend.PrtgStore();
        prtg.ReplaceHostMapForDate(DateTime.Today, [new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = 100, HostId = host.HostId,
            HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok
        }]);
        var mirroredSensorCount = Math.Max(snapshotSensorCount, PrtgProfileTransportCapacityPilot.MaximumSensorIds);
        prtg.UpsertSensors(sensorIds.Take(mirroredSensorCount).Select(id => new PrtgSensorRow
        {
            Objid = id, DeviceObjid = 100, Name = $"sensor-{id}",
            SensorType = id < 10_000 + snapshotSensorCount ? "cpu" : "other", Status = "Up", Paused = false
        }).ToArray(), DateTime.Now);

        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(p =>
        {
            p.Revision = "joint-capacity-policy-r1";
            p.CoreSystemId = "joint-capacity-fixture";
            p.SourceGeneration = "joint-capacity-generation";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(url);
            p.ValidFrom = now;
            p.HostIds = [host.HostId];
            p.SensorIds = sensorIds.ToList();
            p.SourceTimeZoneId = "UTC";
            p.SourceCultureName = "en-US";
            p.RawTimestampTimeZoneId = "UTC";
            p.AnalysisTimeZoneId = "UTC";
            p.TimeBasisEvidenceReference = "joint-capacity-time-basis";
        });
        foreach (var id in sensorIds.Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds))
            prtg.BindObservedResource(id, host.HostId, "joint-capacity-generation",
                PrtgTimelineResourceIdentity.BuildResourceFingerprint("100",
                    id < 10_000 + snapshotSensorCount ? "cpu" : "other", "created", 0));
        var unqualifiedSensorId = sensorIds[0];
        var identity = prtg.GetResourceIdentity(unqualifiedSensorId);
        new PrtgTrustedSamplingBindingStore(_backend).Save(new PrtgTrustedSamplingBindingUpdate(
            unqualifiedSensorId, settings.Get().Revision, policyStore.Get().Revision, identity.Epoch,
            identity.ChannelGeneration, 0, "3", "CPU", PrtgTrustedQuantitySemantic.CpuLoadPercent,
            "%", 1, "direct", "seconds", "UTC", "UTC", "joint-capacity-time-basis"));

        var selection = PrtgSnapshotTargetResolver.Resolve(_backend, _hosts, settings.Get(), [], policyStore.Get());
        selection = PrtgSnapshotTargetResolver.WithPrimaryChannelMode(selection,
            PrtgSnapshotTargetResolver.PrimaryChannelModeProvided);
        Assert.Equal(snapshotSensorCount, selection.SensorObjids.Count);
        var snapshotStore = new PrtgSnapshotCapacityStore(_backend.Blob(PrtgSnapshotCapacityStore.BlobKey));
        for (var sample = 0; sample < 5; sample++)
            snapshotStore.Record(new PrtgSnapshotCapacitySample(selection.ScopeFingerprint, selection.EndpointFingerprint,
                selection.RequestShapeFingerprint, now.AddSeconds(-sample), snapshotP95Milliseconds,
                selection.CapacitySampleBatchSize, "success"));

        var currentSettings = settings.Get();
        var currentPolicy = policyStore.Get();
        var profileIds = currentPolicy.SensorIds.Take(PrtgProfileTransportCapacityPilot.MaximumSensorIds).ToArray();
        var contract = PrtgProfileTransportCapacityPilot.BuildContract(_backend, _hosts, currentSettings, currentPolicy, profileIds);
        var profileStore = new PrtgProfileTransportCapacityStore(_backend.Blob(PrtgProfileTransportCapacityStore.BlobKey));
        for (var sample = 0; sample < 5; sample++)
            profileStore.Record(new PrtgProfileTransportSample(contract.SourceFingerprint, contract.ScopeFingerprint,
                contract.StrategyFingerprint, contract.RequestShapeFingerprint, now.AddSeconds(-sample),
                profileP95MillisecondsPerSensor * profileIds.Length, profileIds.Length,
                PrtgProfileTransportCapacityEvaluator.ExpectedRequests(profileIds.Length),
                PrtgProfileTransportCapacityEvaluator.ExpectedRequests(profileIds.Length), "success", null, contract.VersionFingerprint));

        return _controller.EstimatePrtgFetchScope(PrtgValueFetchScope.AllMapped, PrtgFetchStrategy.Aggressive,
            qualificationDurationHours, qualificationMaximumAttempts).Data!;
    }

    private static void AssertFiniteJson(PrtgValueFetchScopeEstimateDto dto)
    {
        var json = JsonSerializer.Serialize(dto);
        using var document = JsonDocument.Parse(json);
        foreach (var name in new[]
        {
            nameof(dto.JointSharedTableRequestsPerSecond), nameof(dto.JointSnapshotTableRequestsPerSecond),
            nameof(dto.JointProfileTableRequestsPerSecond), nameof(dto.JointGeneralResidualTableRequestsPerSecond),
            nameof(dto.JointSnapshotEstimatedSeconds), nameof(dto.JointSnapshotWindowSeconds),
            nameof(dto.JointProfileEstimatedSeconds), nameof(dto.JointProfileWindowSeconds),
            nameof(dto.QualificationRequiredSeconds), nameof(dto.QualificationAvailableSeconds)
        })
        {
            var value = document.RootElement.GetProperty(name);
            Assert.True(value.ValueKind == JsonValueKind.Null ||
                value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()),
                $"{name} must serialize as a finite number or null; got {value}");
        }
    }

    private sealed class StubSettingsService : ISystemSettingsService
    {
        public SystemSettingsDto Get() => new();
        public SystemSettingsDto Update(UpdateSystemSettingsRequest request) => throw new NotSupportedException();
        public SystemSettingsDto UpdatePrtg(UpdatePrtgSettingsRequest request) => throw new NotSupportedException();
        public HashSet<string>? GetVisibleSeverities() => null;
        public IReadOnlySet<string>? GetVisibleDayRiskLevels() => null;
        public TestAdConnectionResultDto TestAdConnection(TestAdConnectionRequest request) => throw new NotSupportedException();
        public Task<TestMailResultDto> TestMail(TestMailRequest request) => throw new NotSupportedException();
        public Task<TestPrtgConnectionResultDto> TestPrtgAsync(TestPrtgConnectionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
