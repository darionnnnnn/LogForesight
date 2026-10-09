using LogForesight.Core.Persistence;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    private const long CpuPressureSensorId = 8202;
    private const long MemoryPressureSensorId = 8203;

    [Fact]
    public async Task DailyReplayKeepsDefaultCpuAndMemoryHitsAsHintsWithoutFormalRisk()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedDefaultCpuAndMemoryRules();
        var store = _backend.PrtgStore();
        var cpuProfile = SeedPressureSensor(CpuPressureSensorId, "CPU Usage",
            PrtgSensorCategories.Cpu, PrtgTrustedQuantitySemantic.CpuLoadPercent);
        var memoryProfile = SeedPressureSensor(MemoryPressureSensorId, "Memory Used",
            PrtgSensorCategories.Memory, PrtgTrustedQuantitySemantic.MemoryUsedPercent);
        // This scenario asserts CPU and memory replay hints only. The disk fixture
        // is seeded by SeedDiskHistory but is outside this test's intended scope.
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
            policy.SensorIds.Remove(SensorId));
        var currentHour = FloorUtcHour(DateTime.UtcNow);
        foreach (var profile in new[] { cpuProfile, memoryProfile })
            store.MergeSampledValues(new[] { currentHour.AddHours(-2), currentHour.AddHours(-1) }
                .Select(hour => PrtgResourceFixture.TrustedDiskHour(profile.SensorObjid, hour, profile, 95)).ToArray());

        var consumer = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")));
        var liveBeforeReplay = consumer.EvaluateBatch([CpuPressureSensorId, MemoryPressureSensorId],
            currentHour, DateTime.UtcNow, diskReasonObservations: null, evaluationHostIds: [HostId]);
        Assert.Equal(2, liveBeforeReplay.Assessments.Count);
        Assert.All(liveBeforeReplay.Assessments, assessment =>
        {
            Assert.Equal(PrtgResourceDecisionKind.Hit, assessment.Decision.Kind);
            Assert.Equal(PrtgResourceDecisionMode.Hint, assessment.Decision.Mode);
        });
        var hintStore = new PrtgResourcePressureHintStore(_backend.Blob(
            PrtgResourcePressureHintStore.BlobKey(HostId)));
        var hintsBeforeReplay = hintStore.GetCurrent(HostId, DateTime.UtcNow);
        var hintsBeforeJson = System.Text.Json.JsonSerializer.Serialize(hintsBeforeReplay);

        var run = await RunPipeline();

        Assert.Empty(run.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var parent = Assert.Single(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        Assert.Equal(RiskLevels.Low, parent.RiskLevel);
        var hints = hintStore.GetCurrent(HostId, DateTime.UtcNow);
        Assert.Equal(2, hints.Count);
        Assert.Equal(new[] { PrtgResourceFamily.Cpu, PrtgResourceFamily.Memory },
            hints.Select(hint => hint.Family).Order().ToArray());
        Assert.All(hints, hint =>
        {
            Assert.Equal(PrtgResourceDecisionKind.Hit, hint.Kind);
        });
        Assert.Equal(hintsBeforeJson, System.Text.Json.JsonSerializer.Serialize(hints));
        var modes = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        Assert.Null(modes.Find(HostId, CpuPressureSensorId, PrtgResourceFamily.Cpu));
        Assert.Null(modes.Find(HostId, MemoryPressureSensorId, PrtgResourceFamily.Memory));
    }

    [Fact]
    public async Task SameResourceLatestAndEarlierHitsProduceOneStableDailyFindingAndCase()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        EnableResourceConsumerSettings();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var hostStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var candidateHours = Enumerable.Range(0, 28).Select(index => FloorUtcHour(hostStartUtc).AddHours(index)).ToArray();
        var sameDayPairs = candidateHours.Take(candidateHours.Length - 1).Where(hour =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date &&
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour.AddHours(1), DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date).ToArray();
        var firstPair = sameDayPairs.First();
        var latestPair = sameDayPairs.Last();
        ReplaceFixtureSamples(store, SensorId, candidateHours.Where(hour => hour >= hostStartUtc.AddHours(-1) && hour < hostEndUtc)
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile,
                hour == firstPair || hour == firstPair.AddHours(1) || hour == latestPair || hour == latestPair.AddHours(1)
                    ? 5 : 30)).ToArray());
        var direct = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
            .EvaluateClosedHostDayBatch([SensorId], _completedDay, DateTime.UtcNow,
                evaluationHostIds: [HostId]);
        Assert.Single(direct.QualifiedFormalFindings.Where(item =>
            item.ReasonCodes.Contains("disk-two-hour-low-water", StringComparer.Ordinal)));
        Assert.Single(direct.FormalFindings);

        var firstRun = await RunPipeline(withWorkflow: true);
        var firstFinding = Assert.Single(firstRun.Registry.For(HostId, _completedDay));
        var firstCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        var secondRun = await RunPipeline(withWorkflow: true);
        var secondFinding = Assert.Single(secondRun.Registry.For(HostId, _completedDay));

        Assert.Equal(firstFinding.EventKey, secondFinding.EventKey);
        Assert.Equal(firstCase.CaseId,
            Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST")).CaseId);
    }

    [Fact]
    public void AuthorityDriftBeforeFinalHistoricalFenceRemovesDayCloseAndHistoricalFindings()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var hostStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var candidateHours = Enumerable.Range(0, 28).Select(index => FloorUtcHour(hostStartUtc).AddHours(index)).ToArray();
        var sameDayPairs = candidateHours.Take(candidateHours.Length - 1).Where(hour =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date &&
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour.AddHours(1), DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date).ToArray();
        var firstPair = sameDayPairs.First();
        var latestPair = sameDayPairs.Last();
        ReplaceFixtureSamples(store, SensorId, candidateHours.Where(hour => hour >= hostStartUtc.AddHours(-1) && hour < hostEndUtc)
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile,
                hour == firstPair || hour == firstPair.AddHours(1) || hour == latestPair || hour == latestPair.AddHours(1)
                    ? 5 : 30)).ToArray());
        var consumer = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
        {
            BeforeClosedDayAuthorityRecheckForTesting = () =>
            {
                var currentIdentity = store.GetResourceIdentity(SensorId);
                var rotatedFingerprint = System.Text.Json.JsonSerializer.Serialize(new
                {
                    ChannelIdentifier = "free", ChannelName = "Free Space (rotated)", Unit = "%",
                    Scale = (double?)1, Direction = "descending-danger"
                }) + "|" + PrtgDiskAssessmentService.ParserSemanticVersion;
                store.SetObservedChannel(SensorId, currentIdentity.SourceGeneration, rotatedFingerprint,
                    currentIdentity.Generation);
            }
        };

        var result = consumer.EvaluateClosedHostDayBatch([SensorId], _completedDay, DateTime.UtcNow,
            evaluationHostIds: [HostId]);

        Assert.Contains(SensorId, result.ClosedDay!.RejectedSensorObjids);
        Assert.Empty(result.QualifiedFormalFindings);
        Assert.Empty(result.FormalFindings);
    }

    [Fact]
    public async Task ClosedDayFindingIsNotPublishedWithoutAQualifiedDailyParent()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var hostStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var firstWallHour = FloorUtcHour(hostStartUtc);
        var candidateHours = Enumerable.Range(0, 28).Select(index => firstWallHour.AddHours(index)).ToArray();
        var firstSameDayPair = candidateHours.Take(candidateHours.Length - 1)
            .First(hour => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date &&
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour.AddHours(1), DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date);
        ReplaceFixtureSamples(store, SensorId, candidateHours.Where(hour => hour >= hostStartUtc.AddHours(-1) && hour < hostEndUtc &&
                (hour == firstSameDayPair || hour == firstSameDayPair.AddHours(1)))
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile, 5)).ToArray());
        Assert.Equal(1, _backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .DeleteDays([_completedDay]));

        var run = await RunPipeline();

        Assert.Empty(run.Registry.For(HostId, _completedDay));
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
    }

    [Fact]
    public void ClosedDayHistoryRetainsEarlyHitAlongsideHealthyLatestWindow()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        EnableResourceConsumerSettings();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var hostStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var firstWallHour = FloorUtcHour(hostStartUtc);
        var candidateHours = Enumerable.Range(0, 28).Select(index => firstWallHour.AddHours(index)).ToArray();
        var firstSameDayPair = candidateHours.Take(candidateHours.Length - 1)
            .First(hour => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date &&
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour.AddHours(1), DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date);
        ReplaceFixtureSamples(store, SensorId, candidateHours.Where(hour => hour >= hostStartUtc.AddHours(-1) && hour < hostEndUtc)
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile,
                hour == firstSameDayPair || hour == firstSameDayPair.AddHours(1) ? 5 : 30)).ToArray());

        var result = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
            .EvaluateClosedHostDayBatch([SensorId], _completedDay, DateTime.UtcNow,
                evaluationHostIds: [HostId]);

        Assert.Equal(PrtgResourceDecisionKind.Recovery, Assert.Single(result.Assessments).Decision.Kind);
        var finding = Assert.Single(result.QualifiedFormalFindings.Where(item =>
            item.ReasonCodes.Contains("disk-two-hour-low-water", StringComparer.Ordinal)));
        Assert.Equal(_completedDay.Date, finding.EvidenceDay.Date);
        Assert.True(result.ClosedDay!.EvaluatedWindowCountsBySensor[SensorId] > 1);
    }

    [Fact]
    public async Task ClosedDayKeepsEarlyLowWaterWhenLateHoursAreMissingAndDoesNotRewindLiveEpisode()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        EnableResourceConsumerSettings();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var hostStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var hostEndUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var firstWallHour = FloorUtcHour(hostStartUtc);
        var candidateHours = Enumerable.Range(0, 28).Select(index => firstWallHour.AddHours(index))
            .ToArray();
        var firstSameDayPair = candidateHours.Take(candidateHours.Length - 1)
            .First(hour => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour, DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date &&
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hour.AddHours(1), DateTimeKind.Utc), TimeZoneInfo.Local).Date == _completedDay.Date);
        var lateCutoffWall = TimeZoneInfo.ConvertTimeFromUtc(hostEndUtc, TimeZoneInfo.Utc);
        var latestCompleted = new DateTime(lateCutoffWall.Year, lateCutoffWall.Month, lateCutoffWall.Day,
            lateCutoffWall.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
        var latestPairStart = latestCompleted.AddHours(-1);

        ReplaceFixtureSamples(store, SensorId, candidateHours.Where(hour => hour >= hostStartUtc.AddHours(-1) && hour < hostEndUtc)
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile,
                hour == firstSameDayPair || hour == firstSameDayPair.AddHours(1) ? 5 : 30)).ToArray());
        using (var db = _backend.CreateContext())
        {
            var latestIds = db.PrtgValues.Where(row => row.SensorObjid == SensorId &&
                (row.PeriodStart == latestPairStart || row.PeriodStart == latestPairStart.AddHours(1)))
                .Select(row => row.Id).ToArray();
            foreach (var row in db.PrtgValues.Where(row => latestIds.Contains(row.Id)).ToArray()) db.PrtgValues.Remove(row);
            db.SaveChanges();
        }

        // Establish a newer live episode before replaying the closed day.
        var liveCutoff = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
            DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
        // This scenario requires a newer, single-host-day live episode. At local 01:00,
        // the latest completed pair straddles midnight; use the most recent same-day
        // pair instead. Cross-host-day qualification is covered by its separate test.
        for (var shift = 0; shift < 3 &&
            TimeZoneInfo.ConvertTimeFromUtc(liveCutoff.AddHours(-2), TimeZoneInfo.Local).Date !=
            TimeZoneInfo.ConvertTimeFromUtc(liveCutoff.AddHours(-1), TimeZoneInfo.Local).Date; shift++)
            liveCutoff = liveCutoff.AddHours(-1);
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(liveCutoff.AddHours(-2), TimeZoneInfo.Local).Date,
            TimeZoneInfo.ConvertTimeFromUtc(liveCutoff.AddHours(-1), TimeZoneInfo.Local).Date);
        Assert.True(liveCutoff > hostEndUtc);
        store.MergeSampledValues(new[] { liveCutoff.AddHours(-2), liveCutoff.AddHours(-1) }
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile, 5)).ToArray());
        var live = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
            .EvaluateBatch([SensorId], liveCutoff, DateTime.UtcNow,
                diskReasonObservations: null, evaluationHostIds: [HostId]);
        Assert.Equal(PrtgResourceDecisionKind.Hit, Assert.Single(live.Assessments).Decision.Kind);
        var episodeStore = new PrtgResourceFormalEpisodeStore(_backend.Blob(
            PrtgResourceFormalEpisodeStore.BlobKey(HostId)));
        var liveAssessment = Assert.Single(live.Assessments);
        Assert.NotNull(liveAssessment.SingleWindowHostDay);
        Assert.NotNull(liveAssessment.CurrentRule);
        // The unified pipeline refreshes the actual latest window before its read-only
        // closed-day scan. Establish that same latest projection before taking the
        // snapshot, including the legitimate insufficient window at local 01:00.
        var latestAuthorityNow = DateTime.UtcNow;
        var latestLiveCutoff = FloorUtcHour(latestAuthorityNow);
        new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")))
            .EvaluateBatch([SensorId], latestLiveCutoff, latestAuthorityNow,
                diskReasonObservations: null, evaluationHostIds: [HostId]);
        var beforeReplay = episodeStore.GetCurrent(HostId, SensorId);
        Assert.NotNull(beforeReplay);
        Assert.Equal(latestLiveCutoff, beforeReplay.EvidenceAsOfUtc);
        Assert.True(beforeReplay is not null,
            "Live disk episode missing; owned fixture state: " +
            System.Text.Json.JsonSerializer.Serialize(episodeStore.Get()));
        var hintStore = new PrtgResourcePressureHintStore(_backend.Blob(
            PrtgResourcePressureHintStore.BlobKey(HostId)));
        var beforeHint = Assert.Single(hintStore.GetCurrent(HostId, DateTime.UtcNow));

        var firstRun = await RunPipeline(withWorkflow: true);
        var dayFinding = Assert.Single(firstRun.Registry.For(HostId, _completedDay)
            .Where(finding => finding.PrtgResourceReasonCodes?.Contains("disk-two-hour-low-water") == true));
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend)).Get(HostId, _completedDay);
        Assert.NotNull(workflow);
        Assert.False(workflow!.PrtgReadinessComplete);
        Assert.Equal("insufficient", workflow.PrtgReadinessState);
        var afterReplay = episodeStore.GetCurrent(HostId, SensorId);
        Assert.Equal(beforeReplay!.EvidenceAsOfUtc, afterReplay!.EvidenceAsOfUtc);
        Assert.Equal(beforeReplay.ReasonSetFingerprint, afterReplay.ReasonSetFingerprint);
        var afterHint = Assert.Single(hintStore.GetCurrent(HostId, DateTime.UtcNow));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeHint),
            System.Text.Json.JsonSerializer.Serialize(afterHint));

        var secondRun = await RunPipeline(withWorkflow: true);
        var repeatedFinding = Assert.Single(secondRun.Registry.For(HostId, _completedDay)
            .Where(finding => finding.PrtgResourceReasonCodes?.Contains("disk-two-hour-low-water") == true));
        Assert.Equal(dayFinding.EventKey, repeatedFinding.EventKey);
        var afterRerun = episodeStore.GetCurrent(HostId, SensorId);
        Assert.Equal(beforeReplay.EvidenceAsOfUtc, afterRerun!.EvidenceAsOfUtc);
        Assert.Equal(beforeReplay.ReasonSetFingerprint, afterRerun.ReasonSetFingerprint);
    }

    [Fact]
    public void ClosedDayAssignsCrossHostDayWindowToTheLaterHourHostDay()
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true);
        SeedTypedSemanticEvidence();
        EnableResourceConsumerSettings();
        var store = _backend.PrtgStore();
        var profile = new PrtgTrustedSamplingProfileStore(_backend).GetMany([SensorId])[SensorId];
        var boundaryUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(_completedDay.Date.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var firstWallHour = FloorUtcHour(boundaryUtc).AddHours(-2);
        ReplaceFixtureSamples(store, SensorId, Enumerable.Range(0, 4).Select(index =>
        {
            var hour = firstWallHour.AddHours(index);
            var value = hour == boundaryUtc.AddHours(-1) || hour == boundaryUtc ? 5 : 30;
            return PrtgResourceFixture.TrustedDiskHour(SensorId, hour, profile, value);
        }).ToArray());

        var consumer = new PrtgResourcePeriodConsumer(_backend,
            new SystemSettingsStore(_backend.Blob("system_settings")));
        var earlierDay = consumer.EvaluateClosedHostDayBatch([SensorId], _completedDay, DateTime.UtcNow,
            evaluationHostIds: [HostId]);
        Assert.DoesNotContain(earlierDay.QualifiedFormalFindings, finding =>
            finding.ReasonCodes.Contains("disk-two-hour-low-water", StringComparer.Ordinal));

        var laterHourHostDay = _completedDay.AddDays(1);
        store.ReplaceHostMapForDate(laterHourHostDay, [new PrtgHostMapRow
        {
            DeviceObjid = DeviceId, MapDate = laterHourHostDay, HostId = HostId,
            HostName = "DISK-HOST", MapStatus = PrtgMapStatus.Ok, CreatedAt = DateTime.UtcNow
        }]);
        var laterDay = consumer.EvaluateClosedHostDayBatch([SensorId], laterHourHostDay, DateTime.UtcNow,
            evaluationHostIds: [HostId]);
        var finding = Assert.Single(laterDay.QualifiedFormalFindings.Where(item =>
            item.ReasonCodes.Contains("disk-two-hour-low-water", StringComparer.Ordinal)));
        Assert.Equal(laterHourHostDay.Date, finding.EvidenceDay.Date);
    }

    private PrtgTrustedSamplingProfile SeedPressureSensor(long sensorId, string name, string category,
        PrtgTrustedQuantitySemantic quantity)
    {
        var now = DateTime.UtcNow;
        var prtg = _backend.PrtgStore();
        prtg.UpsertSensors([new PrtgSensorRow
        {
            Objid = sensorId, DeviceObjid = DeviceId, Name = name, SensorType = "SNMP Disk Free",
            Category = category, Status = "Up"
        }], now);
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        var currentSettings = settings.Get();
        if (!currentSettings.PrtgEnabled || !StringComparer.Ordinal.Equals(currentSettings.PrtgUrl, _url) ||
            PrtgFetchStrategy.Normalize(currentSettings.PrtgFetchStrategy) != PrtgFetchStrategy.Conservative)
        {
            settings.Update(value =>
            {
                value.PrtgEnabled = true;
                value.PrtgUrl = _url;
                value.PrtgFetchStrategy = PrtgFetchStrategy.Conservative;
            });
            currentSettings = settings.Get();
        }
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = "daily-pressure-fixture";
            policy.CoreSystemId = "daily-pressure-consumer-fixture";
            if (string.IsNullOrWhiteSpace(policy.SourceGeneration))
                policy.SourceGeneration = "daily-pressure-source-generation";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl);
            if (policy.ValidFrom == default)
                policy.ValidFrom = new DateTimeOffset(_completedDay.AddDays(-31));
            policy.HostIds = policy.HostIds.Append(HostId).Distinct().ToList();
            policy.SensorIds = policy.SensorIds.Append(sensorId).Distinct().ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "daily-pressure-fixture-time-basis";
        });
        var policy = policyStore.Get();
        var strategyProfile = PrtgFetchStrategy.Profile(settings.Get().PrtgFetchStrategy);
        var effectiveFrom = DateTime.SpecifyKind(_completedDay.Date.AddDays(-32), DateTimeKind.Utc);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(settings.Get().PrtgFetchStrategy),
            strategyProfile.SnapshotIntervalMinutes, effectiveFrom);
        const string semanticVersion = "resource-period-v1";
        var identity = prtg.BindObservedResource(sensorId, HostId, policy.SourceGeneration,
            PrtgTimelineResourceIdentity.BuildResourceFingerprint(DeviceId.ToString(), "SNMP Disk Free",
                $"pressure-{sensorId}", 0));
        var channelId = quantity == PrtgTrustedQuantitySemantic.CpuLoadPercent ? "cpu-load" : "memory-used";
        identity = prtg.SetObservedChannel(sensorId, policy.SourceGeneration,
            $"{channelId}|{name}|%|1|direct|{semanticVersion}", identity.Generation);
        var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(sensorId, identity, "SNMP Disk Free", channelId,
            name, quantity, "%", 1, "direct", semanticVersion,
            strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromMinutes(strategy.StrategyMinutes), "minutes", "UTC", "UTC", "UTC",
            new DateTimeOffset(now), "daily-pressure-metadata", $"physical-{sensorId}", true, 95, 95,
            now.ToOADate(), now.ToOADate());
        var profile = PrtgConsumerProfileFixtureClosure.Publish(_backend, sourceProfile);
        return profile;
    }

    private static DateTime FloorUtcHour(DateTime value) => new(value.Year, value.Month, value.Day, value.Hour,
        0, 0, DateTimeKind.Utc);

    private void EnableResourceConsumerSettings()
    {
        var profiles = _backend.PrtgStore().GetTrustedSamplingProfiles([SensorId]);
        var sourceProfile = profiles.TryGetValue(SensorId, out var existingProfile)
            ? existingProfile
            : throw new InvalidOperationException("Synthetic disk consumer fixture requires its seeded source profile.");
        var settingsStore = new SystemSettingsStore(_backend.Blob("system_settings"));
        settingsStore.Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = _url;
        });
        var settings = settingsStore.Get();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(settings.PrtgUrl);
            policy.HostIds = policy.HostIds.Append(HostId).Distinct().ToList();
            policy.SensorIds = policy.SensorIds.Append(SensorId).Distinct().ToList();
        });
        PrtgConsumerProfileFixtureClosure.PublishEfFixture(_backend.PrtgStore(),
            _backend.Blob(PrtgMonitoringPolicyStore.BlobKey), settings.Revision,
            PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy), sourceProfile);
    }

    private void ReplaceFixtureSamples(EfPrtgStore store, long sensorId, IReadOnlyList<PrtgValueRow> desiredRows)
    {
        Assert.NotEmpty(desiredRows);
        Assert.All(desiredRows, row => Assert.Equal(sensorId, row.SensorObjid));
        var targetHours = desiredRows.Select(row => row.PeriodStart).Distinct().ToArray();
        using (var db = _backend.CreateContext())
        {
            var existing = db.PrtgValues.Where(row => row.SensorObjid == sensorId &&
                targetHours.Contains(row.PeriodStart)).ToArray();
            db.PrtgValues.RemoveRange(existing);
            db.SaveChanges();
        }
        store.MergeSampledValues(desiredRows);
    }

    private void SeedDefaultCpuAndMemoryRules()
    {
        var ruleStore = new KnownIssueRuleStore(_backend.Blob("rules"));
        var existingRules = ruleStore.Load().Content?.Rules ?? new List<KnownIssueRule>();
        var pressureRuleIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "builtin-prtg-resource-cpu-pressure",
            "builtin-prtg-resource-memory-pressure"
        };
        var rules = existingRules.Where(rule => !pressureRuleIds.Contains(rule.Id))
            .Concat(KnownIssueSeed.CreateRules().Where(rule => pressureRuleIds.Contains(rule.Id)))
            .ToList();
        ruleStore.Save(new RuleFileContent { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
    }
}
