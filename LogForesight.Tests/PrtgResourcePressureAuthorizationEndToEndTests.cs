using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Core.Configuration;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourcePressureAuthorizationEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-resource-period-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;
    private const long SensorId = 41001;
    private const long DeviceId = 41002;
    private const long HostId = 41003;
    private const string Url = "http://prtg.example.test";

    public PrtgResourcePressureAuthorizationEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings
        {
            Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "resource-period.db")}"
        }, _directory);
    }

    [Fact]
    public void CurrentMaintainTrialCanEnableFormalCpuRiskUsingOpaqueOneUseToken()
    {
        var (backend, settings, store, assessment) = ReadyCpuAssessment(95);
        Assert.Equal(PrtgResourceDecisionKind.Hit, assessment.Decision.Kind);
        Assert.Equal(PrtgResourceDecisionMode.Hint, assessment.Decision.Mode);

        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, DateTime.UtcNow);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(trial.TrialResultId))), trial.TrialResultId);

        // Reopen the SQLite-backed stores between trial issuance and activation.
        var restartedBackend = NewBackend();
        var restartedSettings = new SystemSettingsStore(restartedBackend.Blob("system_settings"));
        var profiles = new PrtgTrustedSamplingProfileStore(restartedBackend);
        var storedProfile = profiles.GetMany([SensorId])[SensorId];
        profiles.RecordProbeResult(RefreshProfile(storedProfile));
        var refreshedAssessment = Assert.Single(new PrtgResourcePeriodConsumer(restartedBackend, restartedSettings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow).Assessments);
        Assert.Equal(assessment.ProfileFingerprint, refreshedAssessment.ProfileFingerprint);
        var restartedAuthorization = new PrtgResourcePressureAuthorizationService(restartedBackend, restartedSettings);
        Assert.True(restartedAuthorization.SetFormalMode(refreshedAssessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));
        Assert.False(restartedAuthorization.SetFormalMode(refreshedAssessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));

        // Simulate passage beyond the one-use trial lifetime while preserving a valid persisted row.
        var modeStore = new PrtgResourcePressureModeStore(restartedBackend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        var expiredAt = DateTime.UtcNow.AddHours(-1);
        modeStore.Update(document =>
        {
            var index = document.Grants.FindIndex(g => g.SensorObjid == SensorId);
            document.Grants[index] = document.Grants[index] with
            { TrialExpiresAtUtc = expiredAt, UpdatedAtUtc = expiredAt.AddMinutes(-1) };
        });
        storedProfile = profiles.GetMany([SensorId])[SensorId];
        profiles.RecordProbeResult(RefreshProfile(storedProfile));
        var postExpiry = Assert.Single(new PrtgResourcePeriodConsumer(restartedBackend, restartedSettings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow).Assessments);
        Assert.Equal(PrtgResourceDecisionMode.FormalRisk, postExpiry.Decision.Mode);

        // Reissuing a trial for the unchanged contract must leave existing formal mode active.
        restartedAuthorization.IssueSuccessfulTrial(postExpiry, maintainAuthorized: true, DateTime.UtcNow);
        var afterNewTrial = Assert.Single(new PrtgResourcePeriodConsumer(restartedBackend, restartedSettings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow).Assessments);
        Assert.Equal(PrtgResourceDecisionMode.FormalRisk, afterNewTrial.Decision.Mode);
        Assert.False(restartedAuthorization.SetFormalMode(afterNewTrial, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));

        var live = new PrtgResourcePeriodConsumer(restartedBackend, restartedSettings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);
        Assert.Contains(live.Assessments, a => a.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
        Assert.Contains(live.FormalFindings, f => f.RuleCode == "resource_cpu_sustained_pressure" &&
            !f.Rule.ElevatesDayRisk && f.Rule.Severity == IssueSeverity.High);
        Assert.NotEmpty(new PrtgResourcePressureHintStore(_backend.Blob(
            PrtgResourcePressureHintStore.BlobKey(HostId))).GetCurrent(HostId, DateTime.UtcNow));
    }

    [Fact]
    public void WrongTokenMissingMaintainAndChangedChannelCannotEnableFormalRisk()
    {
        var (backend, settings, store, assessment) = ReadyCpuAssessment(95);
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, DateTime.UtcNow);

        Assert.False(authorization.SetFormalMode(assessment, "caller-picked-token",
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));
        Assert.Throws<UnauthorizedAccessException>(() => authorization.SetFormalMode(assessment,
            trial.TrialResultId, enabled: true, maintainAuthorized: false, DateTime.UtcNow));
        Assert.True(authorization.SetFormalMode(assessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));

        store.SetObservedChannel(SensorId, "source-current", "channel-changed|resource-period-v1",
            assessment.ResourceGeneration);
        Assert.False(authorization.SetFormalMode(assessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));
        var afterDrift = new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);
        Assert.DoesNotContain(afterDrift.Assessments, a => a.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
    }

    [Fact]
    public async Task ConcurrentActivationOfOneServerIssuedTokenHasOneWinner()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, DateTime.UtcNow);
        using var barrier = new Barrier(2);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return authorization.SetFormalMode(assessment, trial.TrialResultId,
                enabled: true, maintainAuthorized: true, DateTime.UtcNow);
        })));

        Assert.Single(results.Where(result => result));
        Assert.Single(results.Where(result => !result));
    }

    [Fact]
    public void MaintainCanDisableWithoutCurrentProfileOrNewTrial()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, DateTime.UtcNow);
        Assert.True(authorization.SetFormalMode(assessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));

        using (var context = backend.CreateContext())
        {
            var profileKey = PrtgTrustedSamplingProfile.StorePrefix + SensorId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var profileBlob = context.Blobs.Single(row => row.BlobKey == profileKey);
            context.Blobs.Remove(profileBlob);
            context.SaveChanges();
        }

        Assert.True(authorization.DisableFormalMode(HostId, SensorId, maintainAuthorized: true, DateTime.UtcNow));
        Assert.False(new PrtgResourcePressureModeStore(backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId))).Find(HostId, SensorId,
                PrtgResourceFamily.Cpu)!.FormalEnabled);
        Assert.Throws<UnauthorizedAccessException>(() => authorization.DisableFormalMode(HostId, SensorId,
            maintainAuthorized: false, DateTime.UtcNow));
    }

    [Fact]
    public void DefaultCpuHitRemainsOnlyAVisibleHintAndCreatesNoFormalFinding()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var result = new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);

        Assert.Equal(PrtgResourceDecisionMode.Hint, Assert.Single(result.Assessments).Decision.Mode);
        Assert.Equal(PrtgResourceDecisionKind.Hit, Assert.Single(result.Assessments).Decision.Kind);
        Assert.Empty(result.FormalFindings);
        Assert.Equal(PrtgResourceFamily.Cpu, Assert.Single(result.Hints).Family);
    }

    [Fact]
    public void CurrentEnabledRuleMetadataDrivesFindingAndRuleDriftInvalidatesFormalGrant()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var rules = new KnownIssueRuleStore(backend.Blob("rules"));
        var cpu = rules.Load().Content!.Rules.Single(r => r.Id == "builtin-prtg-resource-cpu-pressure");
        cpu.Severity = IssueSeverity.Medium;
        cpu.ElevatesDayRisk = true;
        var currentRules = rules.Load().Content!;
        rules.Save(new RuleFileContent
        {
            SeedVersion = currentRules.SeedVersion,
            Rules = currentRules.Rules.Select(r => r.Id == cpu.Id ? cpu : r).ToList()
        });

        var consumer = new PrtgResourcePeriodConsumer(backend, settings);
        var current = Assert.Single(consumer.EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc,
            DateTime.UtcNow).Assessments);
        Assert.Equal(cpu.Id, current.CurrentRule!.Id);
        Assert.NotEqual(assessment.CurrentRuleFingerprint, current.CurrentRuleFingerprint);
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(current, maintainAuthorized: true, DateTime.UtcNow);
        Assert.True(authorization.SetFormalMode(current, trial.TrialResultId, enabled: true,
            maintainAuthorized: true, DateTime.UtcNow));

        var formal = consumer.EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);
        var finding = Assert.Single(formal.FormalFindings);
        Assert.Equal(cpu.Id, finding.Rule.Id);
        Assert.Equal(IssueSeverity.Medium, finding.Rule.Severity);
        Assert.True(finding.Rule.ElevatesDayRisk);
        var signature = PrtgFindingMapper.ToSignature(finding, assessment.SingleWindowHostDay!.Value);
        Assert.True(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            signature, DateTime.UtcNow));
        var wrongChannel = System.Text.Json.JsonSerializer.Deserialize<LogIssueSignature>(
            System.Text.Json.JsonSerializer.Serialize(signature))!;
        wrongChannel.PrtgChannelGeneration = "old-channel";
        Assert.False(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            wrongChannel, DateTime.UtcNow));

        var cpuJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(cpu))!;
        cpuJson[nameof(KnownIssueRule.Description)] = cpu.Description + " changed";
        cpu = System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(cpuJson.ToJsonString())!;
        var changed = rules.Load().Content!;
        rules.Save(new RuleFileContent
        { SeedVersion = changed.SeedVersion, Rules = changed.Rules.Select(r => r.Id == cpu.Id ? cpu : r).ToList() });
        var drifted = consumer.EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);
        Assert.DoesNotContain(drifted.FormalFindings, f => f.Rule.Id == cpu.Id);
        Assert.DoesNotContain(drifted.Assessments, a => a.Decision.Mode == PrtgResourceDecisionMode.FormalRisk);
        Assert.False(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            signature, DateTime.UtcNow));
    }

    [Fact]
    public void DisabledPressureRuleRemovesItsConsumerOutputAndReadinessExpectation()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var consumer = new PrtgResourcePeriodConsumer(backend, settings);
        Assert.Single(consumer.EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow).Hints);
        var rules = new KnownIssueRuleStore(backend.Blob("rules"));
        var document = rules.Load().Content!;
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            document.Rules.Single(rule => rule.Id == "builtin-prtg-resource-cpu-pressure")))!;
        json[nameof(KnownIssueRule.Enabled)] = false;
        var disabled = System.Text.Json.JsonSerializer.Deserialize<KnownIssueRule>(json.ToJsonString())!;
        rules.Save(new RuleFileContent { SeedVersion = document.SeedVersion,
            Rules = document.Rules.Select(rule => rule.Id == disabled.Id ? disabled : rule).ToList() });
        var result = consumer.EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow);
        Assert.Empty(result.Assessments);
        Assert.Empty(result.Hints);
        Assert.Empty(result.FormalFindings);
        Assert.Empty(result.EnabledFamilies);
        Assert.Empty(result.SelectedSensorObjids);
    }

    [Fact]
    public void FormalOffSwitchRejectsCapturedPressureFindingWithoutNeedingATimeline()
    {
        var (backend, settings, _, assessment) = ReadyCpuAssessment(95);
        var authorization = new PrtgResourcePressureAuthorizationService(backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, true, DateTime.UtcNow);
        Assert.True(authorization.SetFormalMode(assessment, trial.TrialResultId, true, true, DateTime.UtcNow));
        var finding = Assert.Single(new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([SensorId], assessment.EvidenceAsOfUtc, DateTime.UtcNow).FormalFindings);
        var signature = PrtgFindingMapper.ToSignature(finding, assessment.SingleWindowHostDay!.Value);
        Assert.True(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            signature, DateTime.UtcNow));
        Assert.True(authorization.DisableFormalMode(HostId, SensorId, true, DateTime.UtcNow));
        Assert.False(PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, HostId, SensorId,
            signature, DateTime.UtcNow));
    }

    private (StorageBackend Backend, ISystemSettingsStore Settings, EfPrtgStore Store,
        PrtgResourcePeriodAssessment Assessment) ReadyCpuAssessment(double value)
    {
        var backend = _backend;
        var seedRules = KnownIssueSeed.CreateRules();
        new KnownIssueRuleStore(backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = seedRules });
        var settings = new SystemSettingsStore(backend.Blob("system_settings"));
        settings.Update(s => { s.PrtgEnabled = true; s.PrtgUrl = Url; });
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(p =>
        {
            p.Revision = "resource-period-test-revision";
            p.CoreSystemId = "core-test";
            p.SourceGeneration = "source-current";
            p.EndpointHint = EfPrtgObservationStore.SourceHintFor(Url);
            p.ValidFrom = DateTimeOffset.UtcNow.AddDays(-10);
            p.HostIds = [HostId];
            p.SensorIds = [SensorId];
            p.ConfirmedBy = "test-maintainer";
            p.SourceTimeZoneId = "UTC";
            p.SourceCultureName = "en-US";
            p.RawTimestampTimeZoneId = "UTC";
            p.AnalysisTimeZoneId = "UTC";
            p.TimeBasisEvidenceReference = "verified-time-basis";
        });
        using (var context = backend.CreateContext())
        {
            context.PrtgDevices.Add(new PrtgDeviceRow { Objid = DeviceId, Name = "resource-test-device" });
            context.PrtgSensors.Add(new PrtgSensorRow
            {
                Objid = SensorId, DeviceObjid = DeviceId, SensorType = "cpu", Category = PrtgSensorCategories.Cpu
            });
            context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = DeviceId, HostId = HostId });
            context.SaveChanges();
        }
        var store = backend.PrtgStore();
        var identity = store.BindObservedResource(SensorId, HostId, "source-current", "resource-fingerprint");
        identity = store.SetObservedChannel(SensorId, "source-current", "primary-channel|resource-period-v1",
            identity.Generation);

        var strategyDefinition = PrtgFetchStrategy.Profile(settings.Get().PrtgFetchStrategy);
        var strategyStore = new PrtgTrustedSamplingStrategyStateStore(
            backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey));
        var policyValue = policy.Get();
        var strategy = strategyStore.GetCurrent(policyValue,
            PrtgFetchStrategy.Normalize(settings.Get().PrtgFetchStrategy),
            strategyDefinition.SnapshotIntervalMinutes, DateTime.UtcNow.AddDays(-2));
        var now = DateTime.UtcNow;
        var oa = now.ToOADate();
        var profile = PrtgTrustedSamplingProfile.FromProbe(SensorId, identity, "cpu", "primary-channel",
            "CPU Usage", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct",
            "resource-period-v1", strategy.StrategyFingerprint, strategy.StrategyMinutes,
            strategy.EffectiveFromHourUtc, TimeSpan.FromMinutes(strategy.StrategyMinutes), "seconds",
            "UTC", "UTC", "UTC", new DateTimeOffset(now), "metadata-reference-valid",
            "physical-reference-valid", true, value, value, oa, oa);
        new PrtgTrustedSamplingProfileStore(backend).RecordProbeResult(profile);

        var evidenceCutoff = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        // The public pressure contract requires both adjacent completed hours to map to one
        // host-local day. At local 01:xx the newest pair straddles midnight; pick the newest
        // completed pair on one host day rather than making this fixture depend on wall time.
        for (var attempt = 0; attempt < 48; attempt++)
        {
            var firstHostDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-2), TimeZoneInfo.Local).Date;
            var secondHostDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-1), TimeZoneInfo.Local).Date;
            if (firstHostDay == secondHostDay) break;
            evidenceCutoff = evidenceCutoff.AddHours(-1);
        }
        var firstWindowHostDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-2), TimeZoneInfo.Local).Date;
        var secondWindowHostDay = TimeZoneInfo.ConvertTimeFromUtc(evidenceCutoff.AddHours(-1), TimeZoneInfo.Local).Date;
        Assert.Equal(firstWindowHostDay, secondWindowHostDay);
        var previousHour = evidenceCutoff.AddHours(-2);
        var latestHour = evidenceCutoff.AddHours(-1);
        var rows = new[] { previousHour, latestHour }.Select(hour => TrustedHour(profile, hour, value)).ToArray();
        store.MergeSampledValues(rows);
        var result = new PrtgResourcePeriodConsumer(backend, settings)
            .EvaluateBatch([SensorId], evidenceCutoff, DateTime.UtcNow);
        Assert.NotNull(Assert.Single(result.Assessments).SingleWindowHostDay);
        return (backend, settings, store, Assert.Single(result.Assessments));
    }

    private StorageBackend NewBackend() => new(new StorageSettings
    {
        Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "resource-period.db")}"
    }, _directory);

    private static PrtgValueRow TrustedHour(PrtgTrustedSamplingProfile profile, DateTime wallHour, double value)
    {
        var slots = Enumerable.Range(0, profile.StrategyMinutes == 15 ? 4 : 12).Select(slot =>
        {
            var measuredAt = wallHour.AddMinutes(slot * profile.StrategyMinutes);
            var physicalId = Guid.NewGuid().ToString("N");
            return new PrtgTrustedSampleSlot(slot, physicalId, measuredAt, measuredAt.AddSeconds(30), value);
        }).ToArray();
        var sample = new PrtgTrustedSample(profile.SensorObjid, value, profile.SourceGeneration,
            profile.ResourceGeneration, profile.ChannelGeneration,
            profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            profile.SemanticVersion, profile.StrategyFingerprint, profile.StrategyMinutes,
            profile.StrategyEffectiveFromHourUtc, slots[0].MeasuredAt, slots[0].ReceivedAt,
            profile.ConfirmedScanInterval, PrtgTrustedSampleQuality.Good, "physical-hour",
            profile.RawTimestampTimeZoneId, profile.AnalysisTimeZoneId);
        var proof = PrtgTrustedSampleProof.From(sample, slots);
        return new PrtgValueRow
        {
            SensorObjid = profile.SensorObjid,
            PeriodStart = DateTime.SpecifyKind(wallHour, DateTimeKind.Unspecified),
            AvgValue = value, MinValue = value, MaxValue = value, Coverage = 100,
            Quality = PrtgDataQuality.Sampled, TrustVersion = 1,
            TrustedProof = PrtgTrustedSampleProof.Serialize(proof)
        };
    }

    private static PrtgTrustedSamplingProfile RefreshProfile(PrtgTrustedSamplingProfile prior)
    {
        var now = DateTime.UtcNow;
        var oa = now.ToOADate();
        var identity = new PrtgResourceIdentity
        {
            SensorId = prior.SensorObjid, SourceGeneration = prior.SourceGeneration,
            Generation = prior.ResourceGeneration, Epoch = prior.IdentityEpoch,
            ChannelGeneration = prior.ChannelGeneration, Active = true
        };
        return PrtgTrustedSamplingProfile.FromProbe(prior.SensorObjid, identity, prior.SensorType,
            prior.PrimaryChannelId, prior.PrimaryChannelCaption, prior.Quantity, prior.Unit, prior.Scale,
            prior.Direction, prior.SemanticVersion, prior.StrategyFingerprint, prior.StrategyMinutes,
            prior.StrategyEffectiveFromHourUtc, prior.ConfirmedScanInterval, prior.IntervalRawUnit,
            prior.RawTimestampTimeZoneId, prior.SourceApiTimeZoneId, prior.AnalysisTimeZoneId,
            new DateTimeOffset(now), "metadata-reference-refreshed", "physical-reference-refreshed",
            true, prior.ComparedSnapshotValue, prior.ComparedPrimaryChannelValue, oa, oa);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
