using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    private const long Page100SensorBase = 94_000;
    private const long Page100DeviceBase = 84_000;
    private const int Page100SensorCount = 100;
    private const int Page100HoursPerDay = 24;

    [Fact]
    public void Page100ClosedDayAcceptsFullDayProofsAtBoundAndKeepsFixedRisk()
    {
        var sensorIds = Page100SeedFullDay();
        var consumer = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")));

        var result = consumer.EvaluateClosedHostDayBatch(sensorIds, _completedDay,
            DateTime.UtcNow, evaluationHostIds: [HostId]);

        var closedDay = Assert.IsType<PrtgResourceClosedDayMetadata>(result.ClosedDay);
        Assert.False(closedDay.ExceededBatchRowBound);
        Assert.Empty(closedDay.RejectedSensorObjids);
        Assert.Equal(_completedDay.Date, closedDay.EvidenceDay.Date);
        Assert.Equal(sensorIds, result.SelectedSensorObjids);
        Assert.Equal(sensorIds, closedDay.SelectedSensorObjids);
        Assert.Equal(Page100SensorCount, closedDay.ExpectedWindowCountsBySensor.Count);
        Assert.Equal(Page100SensorCount, closedDay.EvaluatedWindowCountsBySensor.Count);
        Assert.All(sensorIds, sensorId =>
        {
            var expected = closedDay.ExpectedWindowCountsBySensor[sensorId];
            Assert.True(expected > 0);
            Assert.Equal(expected, closedDay.EvaluatedWindowCountsBySensor[sensorId]);
        });

        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        Assert.Equal(PrtgResourcePeriodConsumer.ComputeSelectedSensorFingerprint(policy, [HostId], sensorIds),
            result.SelectedSensorFingerprint);
        var identities = _backend.PrtgStore().GetResourceIdentities(sensorIds);
        Assert.Equal(PrtgResourcePeriodConsumer.ComputeSelectionEpoch(identities.Values), result.SelectionEpoch);
        Assert.Equal(result.SelectionEpoch, closedDay.SelectionEpoch);

        var dayFindings = result.QualifiedFormalFindings
            .Where(item => item.EvidenceDay.Date == _completedDay.Date).ToArray();
        Assert.Equal(Page100SensorCount, dayFindings.Length);
        Assert.Equal(sensorIds, dayFindings.Select(item => item.Finding.SensorObjid!.Value).Order().ToArray());
        Assert.All(dayFindings, item => Assert.Equal(PrtgRuleEvaluator.RuleResourceDiskPressure,
            item.Finding.RuleCode));
        var signatures = dayFindings.Select(item => PrtgFindingMapper.ToSignature(
            item.Finding, _completedDay, HostId)).ToArray();
        Assert.All(signatures, signature => Assert.False(signature.ElevatesDayRisk));
        Assert.Equal(RiskLevels.Medium, PrtgFindingMapper.RiskFromFindings(signatures));
    }

    [Fact]
    public void Page100ClosedDayRejectsOnlySensorWhoseAuthorityChangesBeforeFinalFence()
    {
        var sensorIds = Page100SeedFullDay();
        var changedSensorId = sensorIds[37];
        var store = _backend.PrtgStore();
        var consumer = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
        {
            BeforeClosedDayAuthorityRecheckForTesting = () =>
            {
                var identity = store.GetResourceIdentity(changedSensorId);
                var channelFingerprint = JsonSerializer.Serialize(new
                {
                    ChannelIdentifier = "free",
                    ChannelName = "Rotated Free Space",
                    Unit = "%",
                    Scale = (double?)1,
                    Direction = "descending-danger"
                }) + "|" + PrtgDiskAssessmentService.ParserSemanticVersion;
                store.SetObservedChannel(changedSensorId, identity.SourceGeneration,
                    channelFingerprint, identity.Generation);
            }
        };

        var result = consumer.EvaluateClosedHostDayBatch(sensorIds, _completedDay,
            DateTime.UtcNow, evaluationHostIds: [HostId]);

        var closedDay = Assert.IsType<PrtgResourceClosedDayMetadata>(result.ClosedDay);
        Assert.False(closedDay.ExceededBatchRowBound);
        Assert.Equal(sensorIds, result.SelectedSensorObjids);
        Assert.Equal(new[] { changedSensorId }, closedDay.RejectedSensorObjids.Order().ToArray());
        var dayFindings = result.QualifiedFormalFindings
            .Where(item => item.EvidenceDay.Date == _completedDay.Date).ToArray();
        Assert.Equal(Page100SensorCount - 1, dayFindings.Length);
        Assert.DoesNotContain(dayFindings, item => item.Finding.SensorObjid == changedSensorId);
        Assert.Equal(sensorIds.Where(id => id != changedSensorId),
            dayFindings.Select(item => item.Finding.SensorObjid!.Value).Order());
        Assert.All(sensorIds.Where(id => id != changedSensorId), sensorId =>
            Assert.Equal(closedDay.ExpectedWindowCountsBySensor[sensorId],
                closedDay.EvaluatedWindowCountsBySensor[sensorId]));
        var signatures = dayFindings.Select(item => PrtgFindingMapper.ToSignature(
            item.Finding, _completedDay, HostId)).ToArray();
        Assert.Equal(RiskLevels.Medium, PrtgFindingMapper.RiskFromFindings(signatures));
    }

    private long[] Page100SeedFullDay()
    {
        var sensorIds = Enumerable.Range(0, Page100SensorCount)
            .Select(index => Page100SensorBase + index).ToArray();
        var deviceIds = Enumerable.Range(0, Page100SensorCount)
            .Select(index => Page100DeviceBase + index).ToArray();
        var nowUtc = DateTime.UtcNow;
        var host = _hosts.Upsert(new WebHost
        {
            HostId = HostId, HostName = "DISK-HOST", Source = "netiq", Active = true,
            IpAddress = "192.0.2.81"
        });
        HostId = host.HostId;

        var store = _backend.PrtgStore();
        store.UpsertDevices(deviceIds.Select((deviceId, index) => new PrtgDeviceRow
        {
            Objid = deviceId, Name = $"DISK-HOST device {index}", Ip = "192.0.2.81"
        }).ToArray(), nowUtc);
        store.UpsertSensors(sensorIds.Select((sensorId, index) => new PrtgSensorRow
        {
            Objid = sensorId, DeviceObjid = deviceIds[index], Name = $"Disk {index} free",
            SensorType = "SNMP Disk Free", Category = PrtgSensorCategories.Disk, Status = "Up"
        }).ToArray(), nowUtc);
        store.ReplaceHostMapForDate(_completedDay.Date, deviceIds.Select(deviceId => new PrtgHostMapRow
        {
            DeviceObjid = deviceId, MapDate = _completedDay.Date, HostId = HostId,
            HostName = "DISK-HOST", MapStatus = PrtgMapStatus.Ok, CreatedAt = nowUtc
        }).ToArray());

        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = _url;
            settings.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
        });
        var sourceGeneration = "a1000000000000000000000000000000";
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = "page100-closed-day-fixture";
            policy.CoreSystemId = "page100-test-core";
            policy.SourceGeneration = sourceGeneration;
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(_url);
            policy.ValidFrom = new DateTimeOffset(_completedDay.AddDays(-31));
            policy.HostIds = [HostId];
            policy.SensorIds = sensorIds.ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "page100-synthetic-time-basis";
            policy.SourceCultureName = "en-US";
        });
        var policy = policyStore.Get();
        var strategyProfile = PrtgFetchStrategy.Profile(settingsStore.Get().PrtgFetchStrategy);
        var effectiveFromHourUtc = DateTime.SpecifyKind(_completedDay.AddDays(-32), DateTimeKind.Utc);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(settingsStore.Get().PrtgFetchStrategy),
            strategyProfile.SnapshotIntervalMinutes, effectiveFromHourUtc);
        Assert.True(strategy.Ready);
        Assert.Equal(effectiveFromHourUtc, strategy.EffectiveFromHourUtc);

        var profiles = new Dictionary<long, PrtgTrustedSamplingProfile>();
        for (var index = 0; index < sensorIds.Length; index++)
        {
            var sensorId = sensorIds[index];
            var deviceId = deviceIds[index];
            var identity = store.BindObservedResource(sensorId, HostId, sourceGeneration,
                PrtgTimelineResourceIdentity.BuildResourceFingerprint(deviceId.ToString(), "SNMP Disk Free",
                    $"page100-resource-{sensorId}", 0));
            identity = PrtgResourceFixture.BindChannel(store, identity, "free", $"Disk {index} Free",
                "%", 1, "descending-danger");
            var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(sensorId, identity, "SNMP Disk Free", "free",
                $"Disk {index} Free", PrtgTrustedQuantitySemantic.DiskFreePercent, "%", 1, "direct",
                PrtgDiskAssessmentService.ParserSemanticVersion, strategy.StrategyFingerprint,
                strategy.StrategyMinutes, strategy.EffectiveFromHourUtc, TimeSpan.FromMinutes(15),
                "minutes", "UTC", "UTC", "UTC", new DateTimeOffset(nowUtc),
                "page100-synthetic-channel-profile", $"page100-physical-sample-reference-{sensorId}",
                true, 50, 50, nowUtc.ToOADate(),
                nowUtc.ToOADate());
            var profile = PrtgConsumerProfileFixtureClosure.Publish(_backend, sourceProfile);
            profiles.Add(sensorId, profile);
        }

        var hostDayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostDayEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        Assert.Equal(TimeSpan.FromHours(Page100HoursPerDay), hostDayEndUtc - hostDayStartUtc);
        Assert.Equal(0, hostDayStartUtc.Minute);
        Assert.Equal(0, hostDayStartUtc.Second);
        var dayHours = Enumerable.Range(-1, Page100HoursPerDay + 1)
            .Select(offset => DateTime.SpecifyKind(hostDayStartUtc.AddHours(offset), DateTimeKind.Utc))
            .ToArray();
        var dayRows = sensorIds.SelectMany(sensorId => dayHours.Select(hour =>
            PrtgResourceFixture.TrustedDiskHour(sensorId, hour, profiles[sensorId], value: 5))).ToArray();
        Assert.Equal(Page100SensorCount * (Page100HoursPerDay + 1), dayRows.Length);

        var currentHourUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0,
            DateTimeKind.Utc);
        var liveRows = sensorIds.SelectMany(sensorId => new[]
        {
            PrtgResourceFixture.TrustedDiskHour(sensorId, currentHourUtc.AddHours(-2), profiles[sensorId], value: 50),
            PrtgResourceFixture.TrustedDiskHour(sensorId, currentHourUtc.AddHours(-1), profiles[sensorId], value: 50)
        }).ToArray();
        store.MergeSampledValues(dayRows.Concat(liveRows).ToArray());
        return sensorIds;
    }
}
