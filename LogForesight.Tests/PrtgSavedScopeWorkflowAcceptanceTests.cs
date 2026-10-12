using System.Globalization;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// Fixed synthetic acceptance shape for the saved PRTG scope workflow. These rows are test
/// fixtures only; they do not claim native PRTG compatibility or field behavior.
/// </summary>
public sealed class PrtgSavedScopeWorkflowAcceptanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-prtg-saved-scope-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private readonly HostStore _hosts;

    public PrtgSavedScopeWorkflowAcceptanceTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(
            new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "scope.db")}" },
            _directory);
        _hosts = new HostStore(_backend.Blob("hosts"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SavedOverrideFlowsThroughFixed20DeviceScopeAndBusinessSnapshotSelection()
    {
        var prtg = _backend.PrtgStore();
        var now = DateTime.Now;
        var businessDevices = Enumerable.Range(1, 18).Select(id => (long)id).ToArray();
        var conflictDevices = new long[] { 19, 20 };
        var guardDevices = new long[] { 21, 22, 23 };
        var businessSensors = businessDevices.Select(id => 30_000 + id).ToArray();
        var conflictSensors = conflictDevices.Select(id => 30_000 + id).ToArray();
        var guardSensors = Enumerable.Range(1, 6).Select(id => 40_000L + id).ToArray();
        var hosts = businessDevices.Select(id => _hosts.Upsert(new WebHost
        {
            HostName = $"synthetic-business-{id}",
            IpAddress = $"10.53.0.{id}",
            Active = true
        })).ToArray();

        prtg.UpsertDevices(businessDevices.Select((id, index) => new PrtgDeviceRow
            { Objid = id, Name = $"Business-{id}", Ip = $"10.53.0.{id}" })
            .Concat(conflictDevices.Select((id, index) => new PrtgDeviceRow
                { Objid = id, Name = $"Conflict-{id}", Ip = $"10.53.0.{index + 1}" }))
            .Concat(new[]
            {
                new PrtgDeviceRow { Objid = 21, Name = "Guard-PRTG", Ip = "10.53.0.201" },
                new PrtgDeviceRow { Objid = 22, Name = "Guard-Sentinel-A", Ip = "10.53.0.202" },
                new PrtgDeviceRow { Objid = 23, Name = "Guard-Sentinel-B", Ip = "10.53.0.203" }
            }).ToArray(), now);
        prtg.UpsertSensors(businessDevices.Select((id, index) => new PrtgSensorRow
            { Objid = businessSensors[index], DeviceObjid = id, Name = $"Business-ping-{id}", SensorType = "ping", Category = "availability" })
            .Concat(conflictDevices.Select((id, index) => new PrtgSensorRow
                { Objid = conflictSensors[index], DeviceObjid = id, Name = $"Conflict-ping-{id}", SensorType = "ping", Category = "availability" }))
            .Concat(guardDevices.SelectMany((deviceId, deviceIndex) => new[]
            {
                new PrtgSensorRow { Objid = guardSensors[deviceIndex * 2], DeviceObjid = deviceId,
                    Name = $"Guard-CPU-{deviceId}", SensorType = "cpu", Category = PrtgSensorCategories.Cpu },
                new PrtgSensorRow { Objid = guardSensors[deviceIndex * 2 + 1], DeviceObjid = deviceId,
                    Name = $"Guard-Memory-{deviceId}", SensorType = "memory", Category = PrtgSensorCategories.Memory }
            })).ToArray(), now);

        var mapRows = hosts.Select((host, index) => new PrtgHostMapRow
        {
            MapDate = DateTime.Today,
            DeviceObjid = businessDevices[index],
            HostId = host.HostId,
            HostName = host.HostName,
            Ip = host.IpAddress,
            MapStatus = PrtgMapStatus.Ok,
            CreatedAt = now
        }).Concat(conflictDevices.Select((id, index) => new PrtgHostMapRow
        {
            MapDate = DateTime.Today,
            DeviceObjid = id,
            HostId = hosts[index].HostId,
            HostName = hosts[index].HostName,
            Ip = hosts[index].IpAddress,
            MapStatus = PrtgMapStatus.Conflict,
            CreatedAt = now
        })).ToArray();
        prtg.ReplaceHostMapForDate(DateTime.Today, mapRows);

        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(settings =>
        {
            settings.PrtgUrl = "https://10.53.0.201";
            settings.PrtgApiTokenEnc = CryptoHelper.Encrypt("synthetic-token");
            settings.PrtgEnabled = true;
            settings.PrtgSensorTypeWhitelist = ["ping"];
            settings.PrtgResourceGuardEnabled = true;
            settings.PrtgResourceGuardSensorObjids = new List<string>();
        });
        var settingsBeforeSave = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        Assert.Empty(settingsBeforeSave.PrtgResourceGuardSensorObjids);

        var sentinels = new Sentinel[]
        {
            new() { Name = "Synthetic Sentinel A", BaseUrl = "https://10.53.0.202:8443" },
            new() { Name = "Synthetic Sentinel B", BaseUrl = "https://10.53.0.203:8443" }
        };
        var detection = PrtgResourceGuardTargets.Detect(new PrtgMirrorGuardSource(prtg), settingsBeforeSave,
            sentinels, new SilentConsole(), new PrtgAddressResolver());
        Assert.Equal(guardDevices, detection.SentinelDeviceObjids.Concat(detection.PrtgDeviceObjids).Distinct().Order().ToArray());
        Assert.Equal(guardSensors, detection.Targets.SensorObjids.Order().ToArray());

        // Preview/detection only reports the six targets. The explicit settings save below is
        // the only step that persists them for the next runtime read.
        settingsStore.Update(settings => settings.PrtgResourceGuardSensorObjids = detection.Targets.SensorObjids
            .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList());
        var savedSettings = new SystemSettingsStore(_backend.Blob("system_settings")).Get();
        Assert.Equal(guardSensors.Select(id => id.ToString(CultureInfo.InvariantCulture)), savedSettings.PrtgResourceGuardSensorObjids);

        var scope = PrtgScopeDevices.Compute(prtg, _hosts, new PrtgMirrorGuardSource(prtg), savedSettings,
            sentinels, new SilentConsole(), new PrtgAddressResolver(), hostIds: null);
        Assert.Equal(20, scope.Mapped + scope.Conflict);
        Assert.Equal(23, prtg.GetAllDevices().Count);
        Assert.Equal(18, scope.Mapped);
        Assert.Equal(2, scope.Conflict);
        Assert.Equal(3, scope.Guard);
        Assert.Equal(23, scope.DeviceObjids.Count);
        Assert.Equal(businessDevices.Concat(conflictDevices).Concat(guardDevices).ToHashSet(), scope.DeviceObjids);

        var policy = new PrtgMonitoringPolicy
        {
            CoreSystemId = "synthetic-core",
            SourceGeneration = "synthetic-generation",
            EndpointHint = EfPrtgObservationStore.SourceHintFor(savedSettings.PrtgUrl),
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
            HostIds = hosts.Select(host => host.HostId).ToList(),
            SensorIds = businessSensors.ToList(),
            SourceTimeZoneId = "UTC",
            SourceCultureName = "en-US",
            RawTimestampTimeZoneId = "UTC",
            AnalysisTimeZoneId = "UTC",
            TimeBasisEvidenceReference = "controlled-scope-time-basis",
            ConfirmedBy = "synthetic-fixture"
        };
        Assert.True(policy.Ready(savedSettings.PrtgUrl));
        var snapshotTargets = PrtgSnapshotTargetResolver.Resolve(_backend, _hosts, savedSettings, sentinels, policy);
        Assert.Equal(18, snapshotTargets.ActiveMappedDeviceCount);
        Assert.Equal(businessSensors.Concat(guardSensors).Order().ToArray(), snapshotTargets.SensorObjids);
        Assert.DoesNotContain(conflictSensors, id => snapshotTargets.SensorObjids.Contains(id));
        Assert.Equal(guardSensors, PrtgResourceGuardTargets.Resolve(new PrtgMirrorGuardSource(prtg), savedSettings,
            sentinels, new SilentConsole(), new PrtgAddressResolver()).SensorObjids.Order().ToArray());

        // Scope and target resolution are read-only: no sample values, cases, or notification intents appear.
        Assert.Empty(_backend.PrtgStore().GetValues(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Empty(_backend.IssueCaseStore().GetOpenKeys());
        var mailState = new MailNotifyStateStore(_backend.Blob("mail_notify_state")).Get();
        Assert.Empty(mailState.UrgentOutbox);
        Assert.Empty(mailState.UrgentSentKeys);
    }

    private sealed class SilentConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
