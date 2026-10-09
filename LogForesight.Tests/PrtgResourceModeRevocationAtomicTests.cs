using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgResourceModeRevocationAtomicTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private const long HostId = 83_001;
    private const long CpuSensorId = 83_101;
    private const long OtherSensorId = 83_102;
    private const string SourceGeneration = "1234567890abcdef1234567890abcdef";
    private static readonly string CpuGeneration = Guid.NewGuid().ToString("N");
    private static readonly string OtherGeneration = Guid.NewGuid().ToString("N");
    private static readonly DateTime Day = new(2026, 10, 5);
    private EfAnalysisRecordStore Store() => new(_fixture.NewContext, "sqlite-mode-revocation");
    private static readonly IReadOnlySet<string> NoPatterns = new HashSet<string>();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("prtg.resource.cpu-sustained-pressure")]
    [InlineData(PrtgRuleEvaluator.RuleResourceCpuPressure)]
    public void DisableAfterEvaluationRejectsAttachThenCurrentRevisionWithdrawsExactGenerationAtomically(string pressureRuleCode)
    {
        var targetCpu = Signature(pressureRuleCode, CpuSensorId, CpuGeneration, IssueSeverity.High);
        var otherCpu = Signature("prtg.resource.cpu-sustained-pressure", OtherSensorId, OtherGeneration, IssueSeverity.High);
        otherCpu.Severity = IssueSeverity.Low;
        var manual = new LogIssueSignature { LogName = "System", Source = "manual", EventId = 41, EventKey = "manual:keep" };
        var parent = SeedParent([targetCpu, otherCpu, manual]);
        var modeStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(Grant()));

        // This is the evaluation-time (grant, BlobRow.Version) snapshot carried into final attach.
        var evaluated = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        Assert.True(evaluated.Grant!.FormalEnabled);
        Assert.True(modeStore.DisableFormalMode(HostId, CpuSensorId, DateTime.UtcNow));
        var disabledRevision = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu).BlobVersion;
        Assert.True(disabledRevision > evaluated.BlobVersion);
        Assert.True(modeStore.DisableFormalMode(HostId, CpuSensorId, DateTime.UtcNow));
        Assert.Equal(disabledRevision, modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu).BlobVersion);

        var staleBatch = BatchWithOtherResource(evaluated.BlobVersion,
            new(CpuSensorId, SourceGeneration, CpuGeneration),
            new(OtherSensorId, SourceGeneration, OtherGeneration), otherCpu.EventKey);
        var staleManifest = Manifest(parent, [targetCpu, otherCpu, manual], evaluated.BlobVersion);
        Assert.False(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [], NoPatterns,
            out _, aiConfigured: true, manifest: staleManifest, reconciliation: staleBatch));
        var afterRace = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Contains(afterRace.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
        Assert.Equal(RiskLevels.High, afterRace.RiskLevel);
        Assert.Null(afterRace.PrtgManifest);

        // The next existing unified evaluation carries the new exact mode revision and complete
        // CPU/memory generation map; the parent transaction removes only the stale target finding.
        var current = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        Assert.False(current.Grant!.FormalEnabled);
        Assert.NotNull(current.Revocation);
        var exactRevocationBatch = Batch(current.BlobVersion,
            new(CpuSensorId, SourceGeneration, CpuGeneration), otherCpu.EventKey);
        Assert.True(Store().AttachPrtgModeRevocationsWithReconciliation(HostId, Day, [], NoPatterns,
            out _, aiConfigured: true, reconciliation: exactRevocationBatch));
        var afterReconcile = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.DoesNotContain(afterReconcile.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
        Assert.Contains(afterReconcile.TopIssues, issue => issue.EventKey == otherCpu.EventKey);
        Assert.Contains(afterReconcile.TopIssues, issue => issue.EventKey == manual.EventKey);
        Assert.Equal(RiskLevels.Low, afterReconcile.RiskLevel);
        Assert.False(afterReconcile.AiAnalyzed);
    }

    [Fact]
    public void FailedReconciliationTransactionLeavesFindingsRiskAndManifestUntouched()
    {
        var targetCpu = Signature("prtg.resource.cpu-sustained-pressure", CpuSensorId, CpuGeneration, IssueSeverity.High);
        var parent = SeedParent([targetCpu]);
        var modeStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(Grant()));
        var captured = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        modeStore.DisableFormalMode(HostId, CpuSensorId, DateTime.UtcNow);
        var current = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        var unrelatedInsert = Signature(PrtgRuleEvaluator.RuleWarning, OtherSensorId, OtherGeneration, IssueSeverity.Medium);
        using (var context = _fixture.NewContext())
            context.Database.ExecuteSqlRaw("CREATE TRIGGER fail_mode_attach BEFORE INSERT ON lf_top_issues BEGIN SELECT RAISE(ABORT, 'mode attach failure'); END;");

        Assert.ThrowsAny<Exception>(() => Store().AttachPrtgFindingsWithReconciliation(HostId, Day,
            [unrelatedInsert], NoPatterns, out _, aiConfigured: true,
            manifest: Manifest(parent, [targetCpu], current.BlobVersion),
            reconciliation: BatchWithOtherResource(current.BlobVersion,
                new(CpuSensorId, SourceGeneration, CpuGeneration),
                new(OtherSensorId, SourceGeneration, OtherGeneration), unrelatedInsert.EventKey)));

        var afterFailure = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Contains(afterFailure.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
        Assert.DoesNotContain(afterFailure.TopIssues, issue => issue.EventKey == unrelatedInsert.EventKey);
        Assert.Equal(RiskLevels.High, afterFailure.RiskLevel);
        Assert.Null(afterFailure.PrtgManifest);
        Assert.True(captured.BlobVersion < current.BlobVersion);
    }

    [Fact]
    public void CapturedAbsentModeRevisionRejectsFirstEnableBeforeAttach()
    {
        var targetCpu = Signature("prtg.resource.cpu-sustained-pressure", CpuSensorId, CpuGeneration, IssueSeverity.High);
        var parent = SeedParent([targetCpu]);
        var modeStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        var absent = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        Assert.Equal(0, absent.BlobVersion);

        modeStore.Update(document => document.Grants.Add(Grant()));
        Assert.False(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [], NoPatterns,
            out _, aiConfigured: true, manifest: Manifest(parent, [targetCpu], absent.BlobVersion),
            reconciliation: Batch(absent.BlobVersion,
                new(CpuSensorId, SourceGeneration, CpuGeneration))));
        var afterRace = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Contains(afterRace.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
        Assert.Null(afterRace.PrtgManifest);
    }

    [Fact]
    public void ExpiredActivatedGrantCanBeDisabledAndReadWithoutReopeningExpiredTrial()
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(5);
        var trial = Grant() with
        {
            FormalEnabled = false,
            TrialConsumed = false,
            UpdatedAtUtc = now,
            TrialExpiresAtUtc = expires
        };
        var modeStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.UpsertTrial(trial);
        var activatedAt = now.AddMinutes(1);
        Assert.True(modeStore.TryConsumeTrial(trial with { FormalEnabled = true },
            trial.TrialResultHash, activatedAt));
        Assert.True(modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu).Grant!.FormalEnabled);

        var disabledAt = expires.AddSeconds(1);
        Assert.True(modeStore.DisableFormalMode(HostId, CpuSensorId, disabledAt));
        var afterExpiry = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        Assert.False(afterExpiry.Grant!.FormalEnabled);
        Assert.Equal(SourceGeneration, afterExpiry.Revocation!.SourceGeneration);
        Assert.Equal(CpuGeneration, afterExpiry.Revocation.ResourceGeneration);
        Assert.Equal(afterExpiry.Revocation, Assert.Single(modeStore.ReadHostSnapshot(HostId).Revocations));

        var expiredTokenGrant = Grant() with
        {
            HostId = HostId + 1,
            SensorObjid = CpuSensorId + 1,
            FormalEnabled = false,
            TrialConsumed = false,
            UpdatedAtUtc = now,
            TrialExpiresAtUtc = expires
        };
        var expiredTokenStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId + 1)));
        expiredTokenStore.UpsertTrial(expiredTokenGrant);
        Assert.False(expiredTokenStore.TryConsumeTrial(expiredTokenGrant with { FormalEnabled = true },
            expiredTokenGrant.TrialResultHash, disabledAt));
        Assert.False(expiredTokenStore.ReadSnapshot(HostId + 1, CpuSensorId + 1,
            PrtgResourceFamily.Cpu).Grant!.FormalEnabled);

        Assert.True(modeStore.DisableFormalMode(HostId, CpuSensorId, disabledAt));
        Assert.Equal(afterExpiry.BlobVersion,
            modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu).BlobVersion);
    }

    [Theory]
    [InlineData("prtg.resource.cpu-sustained-pressure")]
    [InlineData(PrtgRuleEvaluator.RuleResourceCpuPressure)]
    [InlineData("prtg.resource.memory-sustained-pressure")]
    [InlineData(PrtgRuleEvaluator.RuleResourceMemoryPressure)]
    public void LegacyPressureParentWithoutModeManifestRejectsAiResult(string pressureRuleCode)
    {
        var targetCpu = Signature(pressureRuleCode, CpuSensorId, CpuGeneration, IssueSeverity.High);
        var parent = SeedParent([targetCpu], aiAnalyzed: false);
        Assert.Null(parent.PrtgManifest);
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        var outcome = new AiOutcome("stale headline", "stale summary", "trend", "action", RiskLevels.High,
            "AI basis", true, 0, [], null, [], InputPrtgFingerprint: prtgFingerprint);
        Assert.False(Store().TryAttachAiResult(Day, outcome, parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtgFingerprint));
        var afterRejectedAi = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.False(afterRejectedAi.AiAnalyzed);
        Assert.Equal(parent.Headline, afterRejectedAi.Headline);
        Assert.Equal(parent.RiskLevel, afterRejectedAi.RiskLevel);
    }

    [Theory]
    [InlineData("prtg.resource.cpu-sustained-pressure")]
    [InlineData(PrtgRuleEvaluator.RuleResourceCpuPressure)]
    [InlineData("prtg.resource.memory-sustained-pressure")]
    [InlineData(PrtgRuleEvaluator.RuleResourceMemoryPressure)]
    public void AiResultCapturedBeforeModeOffCannotAttachToPressureBearingParent(string pressureRuleCode)
    {
        var targetCpu = Signature(pressureRuleCode, CpuSensorId, CpuGeneration, IssueSeverity.High);
        var parent = SeedParent([targetCpu]);
        var modeStore = new PrtgResourcePressureModeStore(_fixture.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        modeStore.Update(document => document.Grants.Add(Grant()));
        var capturedMode = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
        var manifest = Manifest(parent, [targetCpu], capturedMode.BlobVersion);
        Assert.True(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [targetCpu], NoPatterns,
            out _, aiConfigured: true, manifest: manifest,
            reconciliation: Batch(capturedMode.BlobVersion,
                new(CpuSensorId, SourceGeneration, CpuGeneration), targetCpu.EventKey)));
        var attached = Assert.Single(Store().ReadRecent(Day, 1));
        var decisionFingerprint = HostDayWorkflowFingerprint.ForRecord(attached);
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(attached);

        Assert.True(modeStore.DisableFormalMode(HostId, CpuSensorId, DateTime.UtcNow));
        var outcome = new AiOutcome("stale headline", "stale summary", "trend", "action", RiskLevels.High,
            "AI basis", true, 0, [], null, [], InputPrtgFingerprint: prtgFingerprint);
        Assert.False(Store().TryAttachAiResult(Day, outcome, attached.RecordId,
            decisionFingerprint, prtgFingerprint));

        var afterRejectedAi = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Equal(attached.Headline, afterRejectedAi.Headline);
        Assert.Equal(attached.Summary, afterRejectedAi.Summary);
        Assert.Equal(attached.RiskLevel, afterRejectedAi.RiskLevel);
        Assert.False(afterRejectedAi.AiAnalyzed);
        Assert.Equal(attached.PrtgManifest?.ResourceModeBlobVersion,
            afterRejectedAi.PrtgManifest?.ResourceModeBlobVersion);
    }

    [Fact]
    public void RealConsumerCarriesExactOffMarkerWhenProfileIsMissingIntoAtomicParentWithdrawal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-mode-revocation-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "consumer.db")}"
            }, directory);
            var sourceUrl = "http://mode-revocation.example.test";
            var settings = new SystemSettingsStore(backend.Blob("system_settings"));
            settings.Update(value => { value.PrtgEnabled = true; value.PrtgUrl = sourceUrl; });
            new KnownIssueRuleStore(backend.Blob("rules")).Save(new RuleFileContent
            { SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules() });
            var policyStore = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            policyStore.Update(policy =>
            {
                policy.Revision = "mode-revocation-consumer-test";
                policy.SourceGeneration = SourceGeneration;
                policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(sourceUrl);
                policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-2);
                policy.HostIds = [HostId]; policy.SensorIds = [CpuSensorId];
                policy.ConfirmedBy = "fixture";
                policy.SourceTimeZoneId = "UTC"; policy.SourceCultureName = "en-US";
                policy.RawTimestampTimeZoneId = "UTC"; policy.AnalysisTimeZoneId = "UTC";
                policy.TimeBasisEvidenceReference = "verified-fixture-time-basis";
            });
            using (var context = backend.CreateContext())
            {
                context.PrtgDevices.Add(new PrtgDeviceRow { Objid = 83_201, Name = "mode-revocation-device" });
                context.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = CpuSensorId, DeviceObjid = 83_201, SensorType = "cpu",
                    Category = PrtgSensorCategories.Cpu
                });
                context.PrtgManualMaps.Add(new PrtgManualMapRow { DeviceObjid = 83_201, HostId = HostId });
                context.SaveChanges();
            }
            var identity = backend.PrtgStore().BindObservedResource(CpuSensorId, HostId, SourceGeneration,
                "mode-revocation-resource-fingerprint");
            identity = backend.PrtgStore().SetObservedChannel(CpuSensorId, SourceGeneration,
                "cpu-channel|mode-revocation-v1", identity.Generation);
            var targetCpu = Signature("prtg.resource.cpu-sustained-pressure", CpuSensorId,
                identity.Generation, IssueSeverity.High);
            var parent = backend.RecordStore(new HostKey { HostId = HostId, HostName = "consumer-host" });
            parent.Append(new DailyAnalysisRecord
            {
                HostId = HostId, Host = "consumer-host", Date = Day,
                LogSource = AnalysisLogSource.Netiq, RiskLevel = RiskLevels.High, RiskBasis = "PRTG",
                PrtgBaselineRiskLevel = RiskLevels.Low, PrtgBaselineRiskBasis = "NetIQ baseline",
                TopIssues = [targetCpu]
            });

            var modeStore = new PrtgResourcePressureModeStore(backend.Blob(
                PrtgResourcePressureModeStore.BlobKey(HostId)));
            modeStore.Update(document => document.Grants.Add(Grant(identity.Generation)));
            Assert.True(new PrtgResourcePressureAuthorizationService(backend, settings)
                .DisableFormalMode(HostId, CpuSensorId, maintainAuthorized: true, DateTime.UtcNow));
            var cutoff = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
            var consumption = new PrtgResourcePeriodConsumer(backend, settings)
                .EvaluateBatch([CpuSensorId], cutoff, DateTime.UtcNow, diskReasonObservations: null,
                    evaluationHostIds: [HostId]);

            var revocation = Assert.Single(consumption.PendingModeRevocations);
            Assert.Equal(identity.Generation, revocation.ResourceGeneration);
            Assert.Equal(SourceGeneration, revocation.SourceGeneration);
            var modeVersion = consumption.ModeBlobVersionsByHost[HostId];
            var batch = Batch(modeVersion,
                new(revocation.SensorObjid, revocation.SourceGeneration, revocation.ResourceGeneration));
            Assert.True(backend.RecordStore(new HostKey { HostId = HostId, HostName = "consumer-host" })
                .AttachPrtgModeRevocationsWithReconciliation(HostId, Day, [], NoPatterns,
                    out _, aiConfigured: true, batch));

            var afterWithdrawal = Assert.Single(backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "consumer-host" }).ReadRecent(Day, 1));
            Assert.DoesNotContain(afterWithdrawal.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
            Assert.Equal(RiskLevels.Low, afterWithdrawal.RiskLevel);

            // Re-enable the exact same generation. The old off event remains in the bounded
            // audit document but no longer authorizes withdrawal while this grant is newer.
            var firstOff = Assert.Single(consumption.PendingModeRevocations);
            var reenabledAt = firstOff.DisabledAtUtc.AddTicks(10);
            modeStore.Update(document =>
            {
                var index = document.Grants.FindIndex(grant => grant.SensorObjid == CpuSensorId &&
                    grant.Family == PrtgResourceFamily.Cpu);
                document.Grants[index] = document.Grants[index] with
                { FormalEnabled = true, UpdatedAtUtc = reenabledAt };
            });
            Assert.Contains(modeStore.Get().Revocations, marker => marker == firstOff);
            var reenabledSnapshot = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
            Assert.True(reenabledSnapshot.Grant!.FormalEnabled);
            Assert.Null(reenabledSnapshot.Revocation);

            var currentParent = Assert.Single(backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "consumer-host" }).ReadRecent(Day, 1));
            Assert.True(backend.RecordStore(new HostKey { HostId = HostId, HostName = "consumer-host" })
                .AttachPrtgFindingsWithReconciliation(HostId, Day, [targetCpu], NoPatterns, out _,
                    aiConfigured: true,
                    manifest: Manifest(currentParent, [], reenabledSnapshot.BlobVersion),
                    reconciliation: Batch(reenabledSnapshot.BlobVersion,
                        new(CpuSensorId, SourceGeneration, identity.Generation), targetCpu.EventKey)));
            var reenabledParent = Assert.Single(backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "consumer-host" }).ReadRecent(Day, 1));
            Assert.Contains(reenabledParent.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
            Assert.Equal(RiskLevels.Medium, reenabledParent.RiskLevel);

            // Missing profile cannot qualify new pressure evidence. The superseded historical
            // marker is filtered from the actual consumer output, preserving the current risk.
            var missingProfileConsumption = new PrtgResourcePeriodConsumer(backend, settings)
                .EvaluateBatch([CpuSensorId], cutoff, DateTime.UtcNow, diskReasonObservations: null,
                    evaluationHostIds: [HostId]);
            Assert.Empty(missingProfileConsumption.PendingModeRevocations);
            var afterMissingProfile = Assert.Single(backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "consumer-host" }).ReadRecent(Day, 1));
            Assert.Contains(afterMissingProfile.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
            Assert.Equal(RiskLevels.Medium, afterMissingProfile.RiskLevel);

            var secondOffAt = reenabledAt.AddTicks(10);
            Assert.True(new PrtgResourcePressureAuthorizationService(backend, settings)
                .DisableFormalMode(HostId, CpuSensorId, maintainAuthorized: true, secondOffAt));
            var secondOff = modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu);
            Assert.True(secondOff.Revocation!.DisabledAtUtc > firstOff.DisabledAtUtc);
            Assert.True(new PrtgResourcePeriodConsumer(backend, settings)
                .EvaluateBatch([CpuSensorId], cutoff, DateTime.UtcNow, diskReasonObservations: null,
                    evaluationHostIds: [HostId]).PendingModeRevocations.Any(marker =>
                        marker.SourceGeneration == SourceGeneration &&
                        marker.ResourceGeneration == identity.Generation));
            Assert.True(new PrtgResourcePressureAuthorizationService(backend, settings)
                .DisableFormalMode(HostId, CpuSensorId, maintainAuthorized: true, secondOffAt));
            Assert.Equal(secondOff.BlobVersion,
                modeStore.ReadSnapshot(HostId, CpuSensorId, PrtgResourceFamily.Cpu).BlobVersion);

            var secondOffConsumption = new PrtgResourcePeriodConsumer(backend, settings)
                .EvaluateBatch([CpuSensorId], cutoff, DateTime.UtcNow, diskReasonObservations: null,
                    evaluationHostIds: [HostId]);
            var latestOff = Assert.Single(secondOffConsumption.PendingModeRevocations);
            Assert.True(backend.RecordStore(new HostKey { HostId = HostId, HostName = "consumer-host" })
                .AttachPrtgModeRevocationsWithReconciliation(HostId, Day, [], NoPatterns,
                    out _, aiConfigured: true, Batch(secondOffConsumption.ModeBlobVersionsByHost[HostId],
                        new(latestOff.SensorObjid, latestOff.SourceGeneration, latestOff.ResourceGeneration))));
            var afterSecondWithdrawal = Assert.Single(backend.RecordStore(new HostKey
                { HostId = HostId, HostName = "consumer-host" }).ReadRecent(Day, 1));
            Assert.DoesNotContain(afterSecondWithdrawal.TopIssues, issue => issue.EventKey == targetCpu.EventKey);
            Assert.Equal(RiskLevels.Low, afterSecondWithdrawal.RiskLevel);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void CaseAttachmentUsesRegisteredParentRecordForDurableIntentAndResultState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lf-mode-revocation-case-seam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(directory, "case.db")}"
            }, directory);
            const string hostName = "mode-revocation-case-host";
            var records = backend.RecordStore(new HostKey { HostId = HostId, HostName = hostName });
            records.Append(new DailyAnalysisRecord
            {
                HostId = HostId, Host = hostName, Date = Day, LogSource = AnalysisLogSource.Netiq,
                RiskLevel = RiskLevels.Low, AiAnalyzed = true, TopIssues = []
            });
            var parent = Assert.Single(records.ReadRecent(Day, 1));
            var hosts = new HostStore(backend.Blob("hosts"));
            hosts.Upsert(new WebHost { HostId = HostId, HostName = hostName });
            var owners = new IssueOwnerStore(backend.Blob("issue_owners"));
            var cases = backend.IssueCaseStore();
            var issueHandlings = backend.IssueHandlingStore();
            var handlingLog = backend.RecordHandlingStore();
            var coordinator = new IssueCaseCoordinator(cases, issueHandlings, handlingLog, records, hosts, owners);
            var dispatch = NightlyDispatchFakes.Create(cases, issueHandlings, handlingLog, hosts, owners, coordinator);
            var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(backend));
            workflow.ParentSucceeded(HostId, hostName, Day, "parent-run", 0,
                Hash("case-seam-decision"), prtgEnabled: true, aiEnabled: true,
                now: DateTime.UtcNow, parentRecordId: parent.RecordId);
            var signature = new LogIssueSignature
            {
                LogName = "System", Source = "case-seam", EventId = 412, EventKey = "case-seam:event-412",
                Severity = IssueSeverity.Medium, Count = 1, SampleMessages = ["case workflow seam"]
            };
            var captured = workflow.CaptureDeliveryVersion(HostId, Day, parent.RecordId);
            Assert.NotNull(captured);
            workflow.RecordCaseIntents(HostId, Day, [IssueSignatureKey.For(signature)], captured);
            var waiting = workflow.Get(HostId, Day)!;
            Assert.False(waiting.CaseIsComplete);
            Assert.NotEqual(WorkflowLegState.Waiting, waiting.CaseState);

            HostDayPostProcessor.AttachCase(coordinator, dispatch, hostName, Day, [signature],
                "[PRTG] ", workflow, HostId, parent.RecordId);

            var resolved = workflow.Get(HostId, Day)!;
            Assert.True(resolved.CasePlanClosed);
            Assert.Contains(resolved.CaseIntents, intent => intent.Length == 64);
            Assert.NotEqual(WorkflowLegState.Waiting, resolved.CaseState);
            Assert.True(resolved.CaseDeliveredIntents.Length > 0 || resolved.CaseSkippedIntents.Length > 0 ||
                resolved.CaseFailedIntents.Length > 0);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private DailyAnalysisRecord SeedParent(IReadOnlyList<LogIssueSignature> issues, bool aiAnalyzed = true)
    {
        Store().Append(new DailyAnalysisRecord
        {
            HostId = HostId, Host = "mode-revocation-host", Date = Day,
            LogSource = AnalysisLogSource.Netiq, RiskLevel = RiskLevels.High, RiskBasis = "PRTG",
            PrtgBaselineRiskLevel = RiskLevels.Low, PrtgBaselineRiskBasis = "NetIQ baseline",
            TopIssues = issues.ToList(), AiAnalyzed = aiAnalyzed
        });
        return Assert.Single(Store().ReadRecent(Day, 1));
    }

    private static PrtgResourcePressureModeGrant Grant(string? resourceGeneration = null)
    {
        var now = DateTime.UtcNow;
        return new(HostId, CpuSensorId, PrtgResourceFamily.Cpu, Hash("profile"),
            PrtgResourcePressureEvaluator.RulesVersion, SourceGeneration, resourceGeneration ?? CpuGeneration,
            "channel-generation-v1", "1", "semantic-v1", "strategy-v1", "cpu-rule", Hash("rule"),
            Hash("trial"), true, true, now, now.AddHours(24), TrialConsumed: true);
    }

    private static PrtgStateReconciliationBatch Batch(long modeVersion,
        PrtgResourceGenerationFence cpuGeneration, params string[] currentKeys) => new(SourceGeneration,
        new Dictionary<long, string>(), new Dictionary<long, string>(),
        currentKeys.ToHashSet(StringComparer.Ordinal), [cpuGeneration], modeVersion,
        resourceModeFenceRequired: true);

    private static PrtgStateReconciliationBatch BatchWithOtherResource(long modeVersion,
        PrtgResourceGenerationFence cpuGeneration, PrtgResourceGenerationFence otherGeneration,
        params string[] currentKeys) => new(SourceGeneration,
        new Dictionary<long, string>(), new Dictionary<long, string>(),
        currentKeys.ToHashSet(StringComparer.Ordinal), [cpuGeneration, otherGeneration], modeVersion,
        resourceModeFenceRequired: true);

    private static PrtgDecisionManifest Manifest(DailyAnalysisRecord parent,
        IReadOnlyList<LogIssueSignature> oldIssues, long modeVersion) => new()
    {
        ParentRecordId = parent.RecordId,
        ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
        ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(oldIssues.Where(PrtgFindingMapper.IsPrtg)),
        SourceGeneration = SourceGeneration, ResourceModeBlobVersion = modeVersion,
        ResourceModeFenceRequired = true,
        ResourceFingerprint = Hash("resource"), SemanticFingerprint = Hash("semantic"),
        StrategyFingerprint = Hash("strategy"), HostMappingFingerprint = Hash("mapping"),
        RuleFingerprint = Hash("rules"), EvidenceFingerprint = Hash("evidence"),
        FindingFingerprint = PrtgFindingMapper.Fingerprint(Array.Empty<LogIssueSignature>()),
        CompletedAtUtc = DateTime.UtcNow, Outcome = "complete"
    };

    private static LogIssueSignature Signature(string ruleCode, long sensorId, string generation,
        IssueSeverity severity)
    {
        var rule = KnownIssueSeed.CreateRules().FirstOrDefault(item => item.PrtgRuleCode == ruleCode) ??
            new KnownIssueRule { Id = "fixture-" + ruleCode, Platform = "prtg", Enabled = true,
                PrtgRuleCode = ruleCode, Severity = severity, Category = IssueCategory.Service, Description = "fixture" };
        var finding = new PrtgFinding(90_000 + sensorId, sensorId, ruleCode, "resource pressure", 95, rule)
        {
            SourceGeneration = SourceGeneration, ResourceGeneration = generation,
            SensorCategory = ruleCode.Contains("memory", StringComparison.Ordinal)
                ? PrtgSensorCategories.Memory : PrtgSensorCategories.Cpu
        };
        return PrtgFindingMapper.ToSignature(finding, Day, HostId);
    }

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);
}
