using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

/// <summary>Actual workflow claim and case-coordinator seam for formal CPU pressure starts.</summary>
[Collection("KnownIssueCatalogState")]
public sealed class PrtgFormalCaseStartClaimTests
{
    [Fact]
    public void ModeOffBeforeClaimDefersOnlyPressureAndKeepsNetIqCaseWork()
    {
        using var fixture = new Fixture();
        var netiq = NetIqIssue();
        fixture.SeedParent([fixture.PressureIssue, netiq]);
        fixture.AddOpenCase(fixture.PressureIssue);
        fixture.AddOpenCase(netiq);
        fixture.DisableMode();

        var workflow = fixture.StartWorkflow();
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();
        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue, netiq], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.Contains(handlings.GetForDay(Fixture.HostName, fixture.Day), row => row.IssueKey == IssueSignatureKey.For(netiq));
        Assert.DoesNotContain(handlings.GetForDay(Fixture.HostName, fixture.Day), row => row.IssueKey == IssueSignatureKey.For(fixture.PressureIssue));
        Assert.Empty(workflow.Get(Fixture.HostId, fixture.Day)!.FormalCaseStartClaims);
    }

    [Fact]
    public void ModeOffAfterDurableClaimAllowsInflightCaseAndDuplicateRestart()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        fixture.AddOpenCase(fixture.PressureIssue);
        var workflow = fixture.StartWorkflow();
        var toggled = false;
        var caseStore = new ToggleModeOnFirstHostRead(fixture.InnerCases, () =>
        {
            Assert.Single(fixture.WorkflowState().FormalCaseStartClaims);
            fixture.DisableMode();
            toggled = true;
        });
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow(caseStore);

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.True(toggled);
        Assert.Contains(handlings.GetForDay(Fixture.HostName, fixture.Day), row => row.IssueKey == IssueSignatureKey.For(fixture.PressureIssue));
        Assert.False(fixture.ModeStore.ReadHostSnapshot(Fixture.HostId).Grants.Single().FormalEnabled);

        // A process restart sees the committed claim and can finish the in-flight idempotent retry.
        var restartedWorkflow = fixture.StartWorkflow();
        var (restartedCoordinator, restartedDispatch, restartedHandlings, _) = fixture.CreateCaseFlow();
        HostDayPostProcessor.AttachCase(restartedCoordinator, restartedDispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue], workflow: restartedWorkflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);
        Assert.Single(fixture.WorkflowState().FormalCaseStartClaims);
        Assert.Single(restartedHandlings.GetForDay(Fixture.HostName, fixture.Day), row => row.IssueKey == IssueSignatureKey.For(fixture.PressureIssue));
    }

    [Fact]
    public void CurrentStartPreservesLocalParentFingerprintWhenSqlDateLosesKind()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        var workflow = fixture.StartWorkflow();
        using var ctx = fixture.Backend.CreateContext();
        var row = ctx.DailyRecords.Single(item => item.RecordId == fixture.ParentRecordId);
        var parent = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
        Assert.Equal(DateTimeKind.Local, parent.Date.Kind);
        Assert.Equal(DateTimeKind.Unspecified, row.RecordDate.Kind);
        Assert.Equal(parent.Date.Date, row.RecordDate.Date);
        Assert.True(PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(ctx, Fixture.HostId,
            fixture.ParentRecordId, fixture.PressureIssue, null, out _));
    }

    [Fact]
    public void CurrentStartRejectsParentCalendarDayDifferentFromSqlColumn()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        using var ctx = fixture.Backend.CreateContext();
        var row = ctx.DailyRecords.Single(item => item.RecordId == fixture.ParentRecordId);
        var parent = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
        parent.Date = parent.Date.AddDays(-1);
        parent.PrtgManifest!.ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent);
        row.ContentJson = JsonSerializer.Serialize(parent);
        ctx.SaveChanges();
        Assert.False(PrtgResourceFormalDeliveryFence.TryValidateCurrentStart(ctx, Fixture.HostId,
            fixture.ParentRecordId, fixture.PressureIssue, null, out _));
    }

    [Fact]
    public void ReenabledModeDoesNotReviveUnclaimedIssueFromOlderManifestVersion()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        fixture.AddOpenCase(fixture.PressureIssue);
        fixture.DisableMode();
        fixture.ReenableSameGrant();
        var workflow = fixture.StartWorkflow();
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.Empty(handlings.GetForDay(Fixture.HostName, fixture.Day));
        Assert.Empty(fixture.WorkflowState().FormalCaseStartClaims);
    }

    [Fact]
    public void ProfileInvalidatedAfterParentQualificationDefersPressureClaim()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        fixture.AddOpenCase(fixture.PressureIssue);
        using (var ctx = fixture.Backend.CreateContext())
        {
            var sensor = ctx.PrtgSensors.Single(row => row.Objid == Fixture.SensorId);
            sensor.Paused = true;
            ctx.SaveChanges();
        }
        var workflow = fixture.StartWorkflow();
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.Empty(handlings.GetForDay(Fixture.HostName, fixture.Day));
        Assert.Empty(fixture.WorkflowState().FormalCaseStartClaims);
    }

    [Fact]
    public void SuppressedPersistedPressureFindingCannotBeClaimed()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        fixture.SetPersistedFindingSuppressed();
        fixture.AddOpenCase(fixture.PressureIssue);
        var workflow = fixture.StartWorkflow();
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [fixture.PressureIssue], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.Empty(handlings.GetForDay(Fixture.HostName, fixture.Day));
        Assert.Empty(fixture.WorkflowState().FormalCaseStartClaims);
    }

    [Fact]
    public void EventKeyRuleCodeMustMatchFindingSourceFamily()
    {
        using var fixture = new Fixture();
        var mismatched = JsonSerializer.Deserialize<LogIssueSignature>(
            JsonSerializer.Serialize(fixture.PressureIssue))!;
        mismatched.EventKey = fixture.PressureIssue.EventKey.Replace(PrtgRuleEvaluator.RuleResourceCpuPressure,
            PrtgRuleEvaluator.RuleResourceMemoryPressure, StringComparison.Ordinal);
        fixture.SeedParent([mismatched]);
        fixture.AddOpenCase(mismatched);
        var workflow = fixture.StartWorkflow();
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();

        HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
            [mismatched], workflow: workflow, hostId: Fixture.HostId,
            parentRecordId: fixture.ParentRecordId);

        Assert.Empty(handlings.GetForDay(Fixture.HostName, fixture.Day));
        Assert.Empty(fixture.WorkflowState().FormalCaseStartClaims);
    }

    [Fact]
    public void TargetPressureDetectionToleratesNullEventKey()
    {
        var issue = new LogIssueSignature
        {
            Source = "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure,
            EventKey = null!
        };

        Assert.True(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(issue));
        Assert.False(PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(null));
    }

    [Fact]
    public void ClaimTransactionRollbackLeavesNoClaimOrCaseSideEffect()
    {
        using var fixture = new Fixture();
        fixture.SeedParent([fixture.PressureIssue]);
        fixture.AddOpenCase(fixture.PressureIssue);
        var workflow = fixture.StartWorkflow();
        var blobKey = $"workflow_{Fixture.HostId}_{fixture.Day:yyyyMMdd}";
        using (var ctx = fixture.Backend.CreateContext())
            ctx.Database.ExecuteSqlRaw($"CREATE TRIGGER reject_formal_claim BEFORE UPDATE ON lf_blobs WHEN OLD.blob_key = '{blobKey}' BEGIN SELECT RAISE(ABORT, 'claim rejected'); END;");
        var (coordinator, dispatch, handlings, _) = fixture.CreateCaseFlow();

        try
        {
            HostDayPostProcessor.AttachCase(coordinator, dispatch, Fixture.HostName, fixture.Day,
                [fixture.PressureIssue], workflow: workflow, hostId: Fixture.HostId,
                parentRecordId: fixture.ParentRecordId);
        }
        finally
        {
            using var ctx = fixture.Backend.CreateContext();
            ctx.Database.ExecuteSqlRaw("DROP TRIGGER IF EXISTS reject_formal_claim;");
        }

        Assert.Empty(fixture.WorkflowState().FormalCaseStartClaims);
        Assert.Empty(handlings.GetForDay(Fixture.HostName, fixture.Day));
    }

    private static LogIssueSignature NetIqIssue() => new()
    {
        LogName = "System", Source = "Synthetic NetIQ service", EventId = 53053,
        EntryType = System.Diagnostics.EventLogEntryType.Error, Severity = IssueSeverity.High
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-formal-case-claim-" + Guid.NewGuid().ToString("N"));
        private const string SourceGeneration = "source-generation-claim-test";
        private readonly KnownIssueRule _rule;
        private readonly string _ruleFingerprint;
        private readonly PrtgResourcePressureModeGrant _grant;
        private readonly PrtgTrustedSamplingProfile _profile;

        public const long HostId = 61001;
        public const long SensorId = 61002;
        public long ParentRecordId { get; private set; }
        public const string HostName = "FORMAL-CLAIM-HOST";
        public DateTime Day { get; } = DateTime.Today.AddDays(-1);
        public StorageBackend Backend { get; }
        public PrtgResourcePressureModeStore ModeStore { get; }
        public FakeIssueCaseStore InnerCases { get; } = new();
        public LogIssueSignature PressureIssue { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Backend = new StorageBackend(new StorageSettings
            {
                Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(_directory, "case-claim.db")}"
            }, _directory);
            KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
            new KnownIssueRuleStore(Backend.Blob("rules")).Save(new RuleFileContent
            {
                SeedVersion = KnownIssueSeed.Version, Rules = KnownIssueSeed.CreateRules()
            });
            _rule = PrtgResourceCurrentRuleCatalog.Load(Backend).For(PrtgResourceFamily.Cpu)!.Rule;
            _ruleFingerprint = PrtgResourceCurrentRuleCatalog.ComputeRuleFingerprint(_rule);
            var settingsStore = new SystemSettingsStore(Backend.Blob("system_settings"));
            settingsStore.Update(settings =>
            {
                settings.PrtgEnabled = true;
                settings.PrtgUrl = "http://prtg.formal-claim.test";
            });
            var policyStore = new PrtgMonitoringPolicyStore(Backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
            policyStore.Update(policy =>
            {
                policy.Revision = "policy-claim-test";
                policy.CoreSystemId = "formal-claim-core";
                policy.SourceGeneration = SourceGeneration;
                policy.EndpointHint = EfPrtgObservationStore.SourceHintFor("http://prtg.formal-claim.test");
                policy.ValidFrom = DateTimeOffset.UtcNow.AddDays(-2);
                policy.HostIds = [HostId];
                policy.SensorIds = [SensorId];
                policy.ConfirmedBy = "maintainer-test";
                policy.SourceTimeZoneId = "UTC";
                policy.SourceCultureName = "en-US";
                policy.RawTimestampTimeZoneId = "UTC";
                policy.AnalysisTimeZoneId = "UTC";
                policy.TimeBasisEvidenceReference = "claim-test-time-basis";
            });
            using (var ctx = Backend.CreateContext())
            {
                ctx.PrtgDevices.Add(new PrtgDeviceRow { Objid = 61004, Name = "formal-claim-device" });
                ctx.PrtgSensors.Add(new PrtgSensorRow
                {
                    Objid = SensorId, DeviceObjid = 61004, SensorType = "cpu", Category = PrtgSensorCategories.Cpu
                });
                ctx.PrtgManualMaps.Add(new PrtgManualMapRow
                {
                    DeviceObjid = 61004, HostId = HostId, CreatedBy = "formal-claim-test", CreatedAt = DateTime.Now
                });
                ctx.SaveChanges();
            }
            var identity = Backend.PrtgStore().BindObservedResource(SensorId, HostId, SourceGeneration, "claim-resource-fingerprint");
            identity = Backend.PrtgStore().SetObservedChannel(SensorId, SourceGeneration,
                "primary-channel|resource-period-v1", identity.Generation);
            var strategyDefinition = PrtgFetchStrategy.Profile(settingsStore.Get().PrtgFetchStrategy);
            var strategy = new PrtgTrustedSamplingStrategyStateStore(
                Backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(
                policyStore.Get(), PrtgFetchStrategy.Normalize(settingsStore.Get().PrtgFetchStrategy),
                strategyDefinition.SnapshotIntervalMinutes, DateTime.UtcNow.AddDays(-2));
            var now = DateTime.UtcNow;
            var sampleTime = now.ToOADate();
            var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(SensorId, identity, "cpu", "primary-channel",
                "CPU Usage", PrtgTrustedQuantitySemantic.CpuLoadPercent, "%", 1, "direct", "cpu-semantic-v1",
                strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
                TimeSpan.FromMinutes(strategy.StrategyMinutes), "seconds", "UTC", "UTC", "UTC",
                new DateTimeOffset(now), "claim-metadata-reference", "claim-physical-reference", true,
                50, 50, sampleTime, sampleTime);
            _profile = PrtgConsumerProfileFixtureClosure.Publish(Backend, sourceProfile);
            identity = Backend.PrtgStore().GetResourceIdentity(SensorId);
            var profileContext = new PrtgResourceCurrentContext(SensorId, _profile.SourceGeneration,
                _profile.ResourceGeneration, _profile.ChannelGeneration,
                _profile.IdentityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _profile.SemanticVersion, _profile.StrategyFingerprint, _profile.StrategyMinutes,
                _profile.StrategyEffectiveFromHourUtc, _profile.ConfirmedScanInterval,
                _profile.RawTimestampTimeZoneId, _profile.AnalysisTimeZoneId,
                PrtgResourcePeriodConsumer.StableProfileFingerprint(_profile));
            var profileFingerprint = PrtgResourcePressureEvaluator.GetProfileFingerprint(PrtgResourceFamily.Cpu, profileContext);
            _grant = new PrtgResourcePressureModeGrant(HostId, SensorId, PrtgResourceFamily.Cpu,
                profileFingerprint, PrtgResourcePressureEvaluator.RulesVersion, SourceGeneration, identity.Generation,
                identity.ChannelGeneration, identity.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _profile.SemanticVersion, _profile.StrategyFingerprint, _rule.Id, _ruleFingerprint,
                Sha("trial"), true, true, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), TrialConsumed: true);
            ModeStore = new PrtgResourcePressureModeStore(Backend.Blob(PrtgResourcePressureModeStore.BlobKey(HostId)));
            ModeStore.Update(document => document.Grants.Add(_grant));
            PressureIssue = new LogIssueSignature
            {
                LogName = PrtgFindingMapper.PrtgLogName,
                Source = "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure,
                EventId = 0, EventKey = $"prtg:{PrtgRuleEvaluator.RuleResourceCpuPressure}:{SensorId}:{_profile.SourceGeneration}:{_profile.ResourceGeneration}",
                EntryType = System.Diagnostics.EventLogEntryType.Warning, Severity = IssueSeverity.High,
                RuleId = _rule.Id, PrtgSourceGeneration = _profile.SourceGeneration, PrtgResourceGeneration = _profile.ResourceGeneration,
                PrtgChannelGeneration = _profile.ChannelGeneration,
                PrtgRuleAdmissionFingerprint = PrtgResourceCurrentRuleCatalog.Load(Backend).AdmissionFingerprintFor(PrtgResourceFamily.Cpu),
                KnownIssue = _rule.Description, Count = 1
            };
        }

        public void SeedParent(IReadOnlyList<LogIssueSignature> issues)
        {
            var record = new DailyAnalysisRecord
            {
                RecordId = ParentRecordId, HostId = HostId, Host = HostName, Date = Day, LogSource = AnalysisLogSource.Netiq,
                LatestNetiqAttemptStatus = "success", RiskLevel = RiskLevels.Low, TopIssues = issues.ToList(),
                AuditEventCount = issues.Count
            };
            Backend.RecordStore(new HostKey { HostId = HostId, HostName = HostName }).Append(record);
            ParentRecordId = record.RecordId;
            Assert.True(ParentRecordId > 0);
            var modeVersion = Backend.Blob(PrtgResourcePressureModeStore.BlobKey(HostId)).ReadVersion();
            record.PrtgManifest = new PrtgDecisionManifest
            {
                ParentRecordId = record.RecordId, ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(record),
                ParentFindingFingerprint = Sha("parent-findings"), PolicyRevision = "policy-claim-test",
                SourceGeneration = SourceGeneration, ResourceAuthorityRevision = Backend.PrtgStore().ReadResourceAuthorityRevision(HostId),
                ResourceModeBlobVersion = modeVersion, ResourceModeFenceRequired = true,
                ResourceFingerprint = Sha("resource"), SemanticFingerprint = Sha("semantic"),
                StrategyFingerprint = Sha("strategy"), HostMappingFingerprint = Sha("host-map"),
                WaitReasonCodes = [], RuleFingerprint = Sha("ruleset"), EvidenceFingerprint = Sha("evidence"),
                FindingFingerprint = PrtgFindingMapper.Fingerprint(record.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
                CompletedAtUtc = DateTime.UtcNow, Outcome = "complete"
            };
            using var ctx = Backend.CreateContext();
            var row = ctx.DailyRecords.Single(item => item.RecordId == record.RecordId);
            row.ContentJson = JsonSerializer.Serialize(record);
            ctx.SaveChanges();
        }

        public void AddOpenCase(LogIssueSignature issue) => InnerCases.Save(new IssueCase
        {
            CaseId = Guid.NewGuid().ToString("N"), HostName = HostName, IssueKey = IssueSignatureKey.For(issue),
            Status = IssueHandlingStatuses.InProgress, FirstLinkedDate = Day.AddDays(-1), LastLinkedDate = Day.AddDays(-1),
            CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now
        });

        public void SetPersistedFindingSuppressed()
        {
            using var ctx = Backend.CreateContext();
            var row = ctx.DailyRecords.Single(item => item.RecordId == ParentRecordId);
            var record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
            var persisted = record.TopIssues.Single(issue => IssueSignatureKey.For(issue) == IssueSignatureKey.For(PressureIssue));
            persisted.Suppressed = true;
            record.PrtgManifest!.FindingFingerprint = PrtgFindingMapper.Fingerprint(
                record.TopIssues.Where(PrtgFindingMapper.IsPrtg));
            row.ContentJson = JsonSerializer.Serialize(record);
            ctx.SaveChanges();
        }

        public void DisableMode() => ModeStore.DisableFormalMode(HostId, SensorId, DateTime.UtcNow);

        public void ReenableSameGrant() => ModeStore.Update(document =>
        {
            var index = document.Grants.FindIndex(grant => grant.SensorObjid == SensorId && grant.Family == PrtgResourceFamily.Cpu);
            document.Grants[index] = document.Grants[index] with { FormalEnabled = true, UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1) };
        });

        public HostDayWorkflowService StartWorkflow()
        {
            var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(Backend));
            var parent = Backend.RecordStore(new HostKey { HostId = HostId, HostName = HostName }).ReadRecent(Day, 1).Single();
            workflow.ParentSucceeded(HostId, HostName, Day, "parent-claim-test", parent.AuditEventCount,
                HostDayWorkflowFingerprint.ForParentRecord(parent), prtgEnabled: true, aiEnabled: false,
                parentRecordId: parent.RecordId);
            return workflow;
        }

        public HostDayWorkflowState WorkflowState() => new HostDayWorkflowStore(Backend).Get(HostId, Day)!;

        public (IssueCaseCoordinator Coordinator, NightlyDispatch Dispatch, FakeIssueHandlingStore Handlings, FakeHandlingStore DayHandlings)
            CreateCaseFlow(IIssueCaseStore? cases = null)
        {
            var caseStore = cases ?? InnerCases;
            var issueHandlings = new FakeIssueHandlingStore();
            var dayHandlings = new FakeHandlingStore();
            var hosts = new FakeHostStore();
            var owners = new FakeIssueOwnerStore();
            var records = new FakeAnalysisRecordQuery();
            var coordinator = new IssueCaseCoordinator(caseStore, issueHandlings, dayHandlings, records, hosts, owners);
            var dispatch = NightlyDispatchFakes.Create(caseStore, issueHandlings, dayHandlings, hosts, owners, coordinator);
            return (coordinator, dispatch, issueHandlings, dayHandlings);
        }

        public void Dispose()
        {
            KnownIssueCatalog.Initialize(KnownIssueSeed.CreateRules());
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }

        private static string Sha(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private sealed class ToggleModeOnFirstHostRead(IIssueCaseStore inner, Action toggle) : IIssueCaseStore
    {
        private bool _done;
        public IssueCase? GetOpen(string hostName, string issueKey) => inner.GetOpen(hostName, issueKey);
        public List<IssueCase> GetOpenForHost(string hostName) { if (!_done) { _done = true; toggle(); } return inner.GetOpenForHost(hostName); }
        public List<IssueCase> GetMany(IEnumerable<string> hostNames) => inner.GetMany(hostNames);
        public List<IssueCase> GetOpenByHandler(long userId) => inner.GetOpenByHandler(userId);
        public List<IssueCase> GetByHandler(long userId) => inner.GetByHandler(userId);
        public bool HasCaseOnHost(long handlerId, string hostName) => inner.HasCaseOnHost(handlerId, hostName);
        public HashSet<string> IssueKeysOnHost(long handlerId, string hostName) => inner.IssueKeysOnHost(handlerId, hostName);
        public List<string> HostNamesWithCases(long handlerId) => inner.HostNamesWithCases(handlerId);
        public List<IssueCase> GetResolvedSince(DateTime since) => inner.GetResolvedSince(since);
        public IssueCase? Get(string caseId) => inner.Get(caseId);
        public void Save(IssueCase issueCase) => inner.Save(issueCase);
        public void SaveMany(IEnumerable<IssueCase> cases) => inner.SaveMany(cases);
        public List<(string HostNameKey, string IssueKey)> GetOpenKeys() => inner.GetOpenKeys();
        public List<IssueCase> GetOpenByIssue(string source, int eventId) => inner.GetOpenByIssue(source, eventId);
        public List<IssueCase> GetOpenMany(IEnumerable<string> hostNames, string source, int eventId) => inner.GetOpenMany(hostNames, source, eventId);
        public List<IssueCase> GetByWorkOrder(long workOrderId, int skip, int take) => inner.GetByWorkOrder(workOrderId, skip, take);
        public int CountByWorkOrder(long workOrderId) => inner.CountByWorkOrder(workOrderId);
        public (List<IssueCase> Items, int Total) QueryMembers(WorkOrderMemberQuery q) => inner.QueryMembers(q);
        public List<IssueCase> GetDaySyncPending(int take) => inner.GetDaySyncPending(take);
        public int CountDaySyncPending() => inner.CountDaySyncPending();
        public bool ClearDaySyncPendingIfUnchanged(string caseId, CaseDayIntent intent) => inner.ClearDaySyncPendingIfUnchanged(caseId, intent);
    }
}
