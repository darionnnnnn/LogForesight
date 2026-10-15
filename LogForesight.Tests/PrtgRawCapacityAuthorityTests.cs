using System.Text.Json.Nodes;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

[Collection("PrtgSnapshotSharedBudget")]
public sealed class PrtgRawCapacityAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-raw-capacity-authority-" +
        Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly SystemSettingsStore _settings;
    private readonly HostStore _hosts;
    private readonly PrtgMonitoringPolicyStore _policy;
    private readonly long _hostId;
    private const long DeviceId = 71;
    private const long SensorId = 711;

    public PrtgRawCapacityAuthorityTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}"
        }, _directory);
        _settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        _hosts = new HostStore(_backend.Blob("hosts"));
        _policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));

        var host = _hosts.Upsert(new WebHost
        {
            HostName = "raw-capacity-host", IpAddress = "192.0.2.71", Active = true
        });
        _hostId = host.HostId;
        _settings.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = "https://raw-capacity.example.test";
            settings.PrtgAuthMode = PrtgAuthModes.Token;
            settings.PrtgApiTokenEnc = "synthetic-capacity-token";
            settings.PrtgTimeoutSeconds = 30;
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            settings.PrtgSensorTypeWhitelist = ["ping"];
        });

        var now = DateTimeOffset.UtcNow;
        _policy.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.CoreSystemId = "raw-capacity-core";
            policy.SourceGeneration = "raw-capacity-source";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(_settings.Get().PrtgUrl);
            policy.ValidFrom = now;
            policy.HostIds = [_hostId];
            policy.SensorIds = [SensorId];
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "raw-capacity-time-basis-A";
        });

        _backend.PrtgStore().ReplaceHostMapForDate(DateTime.Today,
        [new PrtgHostMapRow
        {
            MapDate = DateTime.Today, DeviceObjid = DeviceId, HostId = _hostId,
            HostName = host.HostName, Ip = host.IpAddress, MapStatus = PrtgMapStatus.Ok
        }]);
        _backend.PrtgStore().UpsertSensors(
            [new PrtgSensorRow { Objid = SensorId, DeviceObjid = DeviceId, SensorType = "ping" }], DateTime.Now);

        SnapshotAdmissionTestFixture.Seed(_backend, _hosts, _settings, _hostId, DeviceId,
            (SensorId, "ping"));
        _policy.Update(policy =>
        {
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "raw-capacity-time-basis-A";
        });
        SnapshotAdmissionTestFixture.PublishCurrentScope(_backend, _hosts, _settings, _policy.Get());
    }

    [Fact]
    public void Date_format_only_policy_revision_change_keeps_raw_capacity_plan_current()
    {
        var plan = CurrentPlan();
        var before = _policy.Get();
        var beforeContext = PrtgCapacityRuntimeAdmission.RawAuthorityContext(_settings.Get(), before);

        _policy.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.MessageDateFormat = "yyyy-MM-dd HH:mm:ss";
        });

        var after = _policy.Get();
        Assert.NotEqual(plan.PolicyRevision, after.Revision);
        Assert.Equal(before.RawAuthorityRevision, after.RawAuthorityRevision);
        Assert.Equal(beforeContext, PrtgCapacityRuntimeAdmission.RawAuthorityContext(_settings.Get(), after));
        Assert.True(TryAdmit(out _, out var reason), reason);
    }

    [Fact]
    public void Raw_authority_A_to_B_to_A_rejects_old_capacity_plan()
    {
        var originalPlan = CurrentPlan();
        var originalRevision = _policy.Get().RawAuthorityRevision;
        ChangeRawEvidence("raw-capacity-time-basis-B");
        Assert.False(TryAdmit(out _, out _));
        var afterB = _policy.Get();
        Assert.True(afterB.RawAuthorityRevision > originalRevision);
        Assert.NotEqual(originalPlan.RawAuthorityContextFingerprint,
            PrtgCapacityRuntimeAdmission.RawAuthorityContext(_settings.Get(), afterB));

        ChangeRawEvidence("raw-capacity-time-basis-A");
        var afterA = _policy.Get();
        Assert.True(afterA.RawAuthorityRevision > afterB.RawAuthorityRevision);
        Assert.Equal("raw-capacity-time-basis-A", afterA.TimeBasisEvidenceReference);
        Assert.NotEqual(originalPlan.RawAuthorityContextFingerprint,
            PrtgCapacityRuntimeAdmission.RawAuthorityContext(_settings.Get(), afterA));
        Assert.False(TryAdmit(out _, out _));
    }

    [Fact]
    public void Legacy_capacity_plan_without_raw_context_fails_closed()
    {
        _ = CurrentPlan();
        var planBlob = _backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey);
        planBlob.Mutate(raw =>
        {
            var document = JsonNode.Parse(raw!)!.AsObject();
            document.Remove(nameof(PrtgCapacityAdmissionPlan.RawAuthorityContextFingerprint));
            return (document.ToJsonString(), true);
        });

        Assert.Null(new PrtgCapacityAdmissionPlanStore(planBlob).ReadCurrent(DateTimeOffset.UtcNow));
        Assert.False(TryAdmit(out _, out _));
    }

    private PrtgCapacityAdmissionPlan CurrentPlan() =>
        Assert.IsType<PrtgCapacityAdmissionPlan>(new PrtgCapacityAdmissionPlanStore(
            _backend.Blob(PrtgCapacityAdmissionPlanStore.BlobKey)).ReadCurrent(DateTimeOffset.UtcNow));

    private bool TryAdmit(out PrtgCapacityAdmissionPlan? plan, out string reason)
    {
        var settings = _settings.Get();
        var selection = PrtgSnapshotTargetResolver.Resolve(_backend, _hosts, settings, Array.Empty<Sentinel>());
        return PrtgCapacityRuntimeAdmission.TryGetCurrent(_backend, _hosts, settings, selection,
            out plan, out reason);
    }

    private void ChangeRawEvidence(string reference)
    {
        _policy.Update(policy =>
        {
            policy.Revision = Guid.NewGuid().ToString("N");
            policy.TimeBasisEvidenceReference = reference;
        });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
