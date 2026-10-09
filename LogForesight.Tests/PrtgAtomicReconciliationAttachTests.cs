using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogForesight.Tests;

public sealed class PrtgAtomicReconciliationAttachTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private EfAnalysisRecordStore Store() => new(_fixture.NewContext, "sqlite-atomic-prtg");
    private static readonly IReadOnlySet<string> NoPatterns = new HashSet<string>();
    private const long HostId = 93_501;
    private static readonly DateTime Day = new(2026, 10, 5);
    private static readonly string StateGeneration = Guid.NewGuid().ToString("N");
    private const string SourceGeneration = "1234567890abcdef1234567890abcdef";

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReconcileAndAttach_AreOneTransaction_WithPartialFindingAndExactFinalHashes()
    {
        var oldDown = Signature(PrtgRuleEvaluator.RuleDown, 93501, StateGeneration, IssueSeverity.High);
        var oldDisk = Signature(PrtgDiskRuleDecision.RuleCode, 93501, StateGeneration, IssueSeverity.High);
        var manual = new LogIssueSignature { LogName = "System", Source = "manual", EventId = 41, EventKey = "manual:power" };
        var parent = SeedParent([oldDown, oldDisk, manual], RiskLevels.High, RiskLevels.Low);
        SeedObservation(Finding(PrtgRuleEvaluator.RuleDown, 93501, StateGeneration));
        var priorFingerprint = PrtgFindingMapper.Fingerprint([oldDown, oldDisk]);
        var warning = Signature(PrtgRuleEvaluator.RuleWarning, 93501, StateGeneration, IssueSeverity.Medium);
        var revisedDisk = Signature(PrtgDiskRuleDecision.RuleCode, 93501, StateGeneration, IssueSeverity.Medium);
        revisedDisk.Severity = IssueSeverity.Medium;
        var incoming = new[] { warning, revisedDisk };
        var batch = new PrtgStateReconciliationBatch(SourceGeneration,
            new Dictionary<long, string> { [93501] = StateGeneration },
            new Dictionary<long, string> { [93501] = StateGeneration },
            incoming.Select(issue => issue.EventKey).ToHashSet(StringComparer.Ordinal));
        var manifest = Manifest(parent, priorFingerprint, incoming, outcome: "partial");

        Assert.True(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, incoming, NoPatterns,
            out _, aiConfigured: true, manifest: manifest, reconciliation: batch));

        var persisted = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.DoesNotContain(persisted.TopIssues, issue => issue.EventKey == oldDown.EventKey);
        Assert.Contains(persisted.TopIssues, issue => issue.EventKey == warning.EventKey);
        var persistedDisk = Assert.Single(persisted.TopIssues.Where(issue => issue.EventKey == revisedDisk.EventKey));
        Assert.Equal(IssueSeverity.Medium, persistedDisk.Severity);
        Assert.Contains(persisted.TopIssues, issue => issue.EventKey == manual.EventKey);
        Assert.Equal(RiskLevels.Low, persisted.PrtgBaselineRiskLevel);
        Assert.NotEqual(RiskLevels.High, persisted.RiskLevel);
        Assert.Equal(parent.RecordId, persisted.PrtgManifest!.ParentRecordId);
        Assert.Equal(priorFingerprint, persisted.PrtgManifest.ParentFindingFingerprint);
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(persisted), persisted.PrtgManifest.ParentFingerprint);
        Assert.Equal(PrtgFindingMapper.Fingerprint(persisted.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
            persisted.PrtgManifest.FindingFingerprint);
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(persisted));
        using (var context = _fixture.NewContext())
        {
            var normalized = context.TopIssues.Where(issue => issue.RecordId == parent.RecordId).ToArray();
            Assert.DoesNotContain(normalized, issue => issue.EventKey == oldDown.EventKey);
            Assert.Contains(normalized, issue => issue.EventKey == warning.EventKey);
            var normalizedDisk = Assert.Single(normalized.Where(issue => issue.EventKey == revisedDisk.EventKey));
            Assert.Equal((int)IssueSeverity.Medium, normalizedDisk.SeverityRank);
            var observation = Assert.Single(context.PrtgObservations.Where(row => row.HostId == HostId));
            Assert.Null(observation.ActiveKey);
            Assert.Equal("superseded", observation.SupplementStatus);
        }
    }

    [Fact]
    public void ReconciliationBatchAllowsSharedSensorOnlyForMatchingGeneration()
    {
        var sensorId = 93503L;
        var matching = new PrtgStateReconciliationBatch(SourceGeneration,
            new Dictionary<long, string> { [sensorId] = StateGeneration },
            new Dictionary<long, string> { [sensorId] = StateGeneration },
            new HashSet<string>(StringComparer.Ordinal));
        var drifted = new PrtgStateReconciliationBatch(SourceGeneration,
            new Dictionary<long, string> { [sensorId] = StateGeneration },
            new Dictionary<long, string> { [sensorId] = Guid.NewGuid().ToString("N") },
            new HashSet<string>(StringComparer.Ordinal));
        var sharedMap = Enumerable.Range(1, 10_000).ToDictionary(id => (long)id, _ => StateGeneration);
        var sharedWithinLimit = new PrtgStateReconciliationBatch(SourceGeneration, sharedMap, sharedMap,
            new HashSet<string>(StringComparer.Ordinal));
        var extraSensors = Enumerable.Range(10_001, 5_001).ToDictionary(id => (long)id, _ => StateGeneration);
        var uniqueIdsOverLimit = new PrtgStateReconciliationBatch(SourceGeneration, sharedMap,
            sharedMap.Concat(extraSensors).ToDictionary(pair => pair.Key, pair => pair.Value),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.True(matching.IsValid());
        Assert.False(drifted.IsValid());
        Assert.True(sharedWithinLimit.IsValid());
        Assert.False(uniqueIdsOverLimit.IsValid());
        Assert.False(new PrtgStateReconciliationBatch("not-a-policy-generation",
            new Dictionary<long, string>(), new Dictionary<long, string>(), new HashSet<string>()).IsValid());
    }

    [Fact]
    public void ConcurrentParentPrtgAppendAfterFinalFence_RejectsWithoutReconciliationOrManifest()
    {
        var oldDown = Signature(PrtgRuleEvaluator.RuleDown, 93601, StateGeneration, IssueSeverity.High);
        var parent = SeedParent([oldDown], RiskLevels.High, RiskLevels.Low);
        var warning = Signature(PrtgRuleEvaluator.RuleWarning, 93601, StateGeneration, IssueSeverity.Medium);
        var batch = StateBatch(93601, StateGeneration, warning.EventKey);
        var staleManifest = Manifest(parent, PrtgFindingMapper.Fingerprint([oldDown]), [warning], "partial");

        var concurrent = Signature(PrtgRuleEvaluator.RuleFlapping, 93602, Guid.NewGuid().ToString("N"), IssueSeverity.Medium);
        Assert.True(Store().AttachPrtgFindings(HostId, Day, [concurrent], NoPatterns, out _));
        var before = Assert.Single(Store().ReadRecent(Day, 1));
        var beforeFindingFingerprint = PrtgFindingMapper.Fingerprint(before.TopIssues.Where(PrtgFindingMapper.IsPrtg));

        Assert.False(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [warning], NoPatterns,
            out _, aiConfigured: true, manifest: staleManifest, reconciliation: batch));
        var after = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Contains(after.TopIssues, issue => issue.EventKey == oldDown.EventKey);
        Assert.Contains(after.TopIssues, issue => issue.EventKey == concurrent.EventKey);
        Assert.DoesNotContain(after.TopIssues, issue => issue.EventKey == warning.EventKey);
        Assert.Null(after.PrtgManifest);
        Assert.Equal(beforeFindingFingerprint, PrtgFindingMapper.Fingerprint(after.TopIssues.Where(PrtgFindingMapper.IsPrtg)));

        // The legacy-shaped manifest call is fenced by the same captured preimage.
        Assert.False(Store().AttachPrtgFindings(HostId, Day, [warning], NoPatterns, out _, aiConfigured: true,
            manifest: staleManifest));
        Assert.Null(Assert.Single(Store().ReadRecent(Day, 1)).PrtgManifest);
    }

    [Fact]
    public void QualifiedZeroFindingRecovery_RemovesOnlyOwnedStateAndPersistsCompleteManifest()
    {
        var oldDown = Signature(PrtgRuleEvaluator.RuleDown, 93701, StateGeneration, IssueSeverity.High);
        var manual = new LogIssueSignature { LogName = "System", Source = "manual", EventId = 41, EventKey = "manual:keep" };
        var parent = SeedParent([oldDown, manual], RiskLevels.High, RiskLevels.Low);
        var batch = StateBatch(93701, StateGeneration);
        var manifest = Manifest(parent, PrtgFindingMapper.Fingerprint([oldDown]), [], "complete");

        Assert.True(Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [], NoPatterns,
            out _, aiConfigured: false, manifest: manifest, reconciliation: batch));

        var persisted = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.DoesNotContain(persisted.TopIssues, issue => issue.EventKey == oldDown.EventKey);
        Assert.Contains(persisted.TopIssues, issue => issue.EventKey == manual.EventKey);
        Assert.Equal(RiskLevels.Low, persisted.RiskLevel);
        Assert.Equal("complete", persisted.PrtgManifest!.Outcome);
        Assert.Empty(persisted.PrtgManifest.WaitReasonCodes);
        Assert.Equal(PrtgFindingMapper.Fingerprint(Array.Empty<LogIssueSignature>()), persisted.PrtgManifest.FindingFingerprint);
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(persisted));
    }

    [Fact]
    public void InsertFailure_RollsBackWithdrawalNormalizedRowsObservationAndManifest()
    {
        var oldDown = Signature(PrtgRuleEvaluator.RuleDown, 93801, StateGeneration, IssueSeverity.High);
        var parent = SeedParent([oldDown], RiskLevels.High, RiskLevels.Low);
        SeedObservation(Finding(PrtgRuleEvaluator.RuleDown, 93801, StateGeneration));
        var warning = Signature(PrtgRuleEvaluator.RuleWarning, 93801, StateGeneration, IssueSeverity.Medium);
        var manifest = Manifest(parent, PrtgFindingMapper.Fingerprint([oldDown]), [warning], "partial");
        using (var context = _fixture.NewContext())
            context.Database.ExecuteSqlRaw("CREATE TRIGGER fail_atomic_prtg_insert BEFORE INSERT ON lf_top_issues BEGIN SELECT RAISE(ABORT, 'atomic attach failure'); END;");

        Assert.ThrowsAny<Exception>(() => Store().AttachPrtgFindingsWithReconciliation(HostId, Day, [warning], NoPatterns,
            out _, aiConfigured: true, manifest: manifest,
            reconciliation: StateBatch(93801, StateGeneration, warning.EventKey)));

        var persisted = Assert.Single(Store().ReadRecent(Day, 1));
        Assert.Contains(persisted.TopIssues, issue => issue.EventKey == oldDown.EventKey);
        Assert.DoesNotContain(persisted.TopIssues, issue => issue.EventKey == warning.EventKey);
        Assert.Null(persisted.PrtgManifest);
        using (var context = _fixture.NewContext())
        {
            Assert.Contains(context.TopIssues.Where(issue => issue.RecordId == parent.RecordId).Select(issue => issue.EventKey),
                key => key == oldDown.EventKey);
            Assert.DoesNotContain(context.TopIssues.Where(issue => issue.RecordId == parent.RecordId).Select(issue => issue.EventKey),
                key => key == warning.EventKey);
            var observation = Assert.Single(context.PrtgObservations.Where(row => row.HostId == HostId));
            Assert.NotNull(observation.ActiveKey);
            Assert.Equal("pending", observation.SupplementStatus);
        }
    }

    [Fact]
    public void IdenticalReplayIsIdempotent_ButDifferentPayloadWithSameEvidenceIsNotSkipped()
    {
        var parent = SeedParent([], RiskLevels.Low, RiskLevels.Low);
        var warning = Signature(PrtgRuleEvaluator.RuleWarning, 93901, StateGeneration, IssueSeverity.Medium);
        var batch = StateBatch(93901, StateGeneration, warning.EventKey);
        var manifest = Manifest(parent, PrtgFindingMapper.Fingerprint([]), [warning], "partial");
        var store = Store();

        Assert.True(store.AttachPrtgFindingsWithReconciliation(HostId, Day, [warning], NoPatterns,
            out _, aiConfigured: true, manifest: manifest, reconciliation: batch));
        var once = Assert.Single(store.ReadRecent(Day, 1));
        Assert.True(store.AttachPrtgFindingsWithReconciliation(HostId, Day, [warning], NoPatterns,
            out _, aiConfigured: true, manifest: manifest, reconciliation: batch));
        var twice = Assert.Single(store.ReadRecent(Day, 1));
        Assert.Single(twice.TopIssues.Where(PrtgFindingMapper.IsPrtg));
        Assert.Equal(once.PrtgManifest!.FindingFingerprint, twice.PrtgManifest!.FindingFingerprint);

        var differentPayload = Signature(PrtgRuleEvaluator.RuleWarning, 93901, StateGeneration, IssueSeverity.Medium);
        differentPayload.SampleMessages = ["different qualified result"];
        var replayManifest = Manifest(parent, PrtgFindingMapper.Fingerprint([]), [differentPayload], "partial");
        Assert.False(store.AttachPrtgFindingsWithReconciliation(HostId, Day, [differentPayload], NoPatterns,
            out _, aiConfigured: true, manifest: replayManifest, reconciliation: batch));
        Assert.Equal(once.PrtgManifest.FindingFingerprint,
            Assert.Single(store.ReadRecent(Day, 1)).PrtgManifest!.FindingFingerprint);
    }

    private DailyAnalysisRecord SeedParent(IReadOnlyList<LogIssueSignature> issues, string risk, string baselineRisk)
    {
        var record = new DailyAnalysisRecord
        {
            HostId = HostId, Host = "atomic-prtg-host", Date = Day,
            LogSource = AnalysisLogSource.Netiq, RiskLevel = risk, RiskBasis = "NetIQ baseline",
            PrtgBaselineRiskLevel = baselineRisk, PrtgBaselineRiskBasis = "NetIQ baseline",
            TopIssues = issues.ToList()
        };
        Store().Append(record);
        return Assert.Single(Store().ReadRecent(Day, 1));
    }

    private void SeedObservation(PrtgFinding finding)
    {
        var signature = PrtgFindingMapper.ToSignature(finding, Day, HostId);
        Assert.Equal(1, new EfPrtgObservationStore(_fixture.NewContext).Capture(HostId, Day, "settings-v1",
            [(finding, signature)], "https://prtg.example", 1));
    }

    private static PrtgStateReconciliationBatch StateBatch(long sensorId, string generation, params string[] currentKeys) =>
        new(SourceGeneration, new Dictionary<long, string> { [sensorId] = generation },
            new Dictionary<long, string>(), currentKeys.ToHashSet(StringComparer.Ordinal));

    private static PrtgDecisionManifest Manifest(DailyAnalysisRecord parent, string oldPrtgFingerprint,
        IReadOnlyList<LogIssueSignature> findings, string outcome)
    {
        var waitReasons = outcome == "partial" ? new List<string> { "silent-presence-proof-waiting" } : new List<string>();
        return new PrtgDecisionManifest
        {
            ParentRecordId = parent.RecordId,
            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
            ParentFindingFingerprint = oldPrtgFingerprint,
            SourceGeneration = SourceGeneration,
            ResourceFingerprint = Hash("resource"), SemanticFingerprint = Hash("semantic"),
            StrategyFingerprint = Hash("strategy"), HostMappingFingerprint = Hash("mapping"),
            RuleFingerprint = Hash("rules"), EvidenceFingerprint = Hash("evidence-v1"),
            FindingFingerprint = PrtgFindingMapper.Fingerprint(findings),
            CompletedAtUtc = DateTime.UtcNow, Outcome = outcome, WaitReasonCodes = waitReasons
        };
    }

    private static string Hash(string text) => HostDayWorkflowFingerprint.HashParts([text]);

    private static LogIssueSignature Signature(string ruleCode, long sensorId, string generation, IssueSeverity severity)
    {
        var rule = Rule(ruleCode, severity);
        return PrtgFindingMapper.ToSignature(Finding(ruleCode, sensorId, generation, rule), Day);
    }

    private static PrtgFinding Finding(string ruleCode, long sensorId, string generation) =>
        Finding(ruleCode, sensorId, generation, Rule(ruleCode,
            ruleCode == PrtgRuleEvaluator.RuleDown ? IssueSeverity.High : IssueSeverity.Medium));

    private static PrtgFinding Finding(string ruleCode, long sensorId, string generation, KnownIssueRule rule) =>
        new(900, sensorId, ruleCode, $"{ruleCode} fixture", 90, rule)
        {
            SourceGeneration = SourceGeneration, ResourceGeneration = generation,
            SensorCategory = ruleCode == PrtgDiskRuleDecision.RuleCode ? PrtgSensorCategories.Disk : PrtgSensorCategories.Availability,
            IncidentStartedAt = DateTimeOffset.UtcNow.AddHours(-3)
        };

    private static KnownIssueRule Rule(string ruleCode, IssueSeverity severity) =>
        KnownIssueSeed.CreateRules().FirstOrDefault(rule => rule.PrtgRuleCode == ruleCode) ?? new KnownIssueRule
        {
            Id = "fixture-" + ruleCode, Platform = "prtg", Enabled = true, PrtgRuleCode = ruleCode,
            PrtgThreshold = 1, Severity = severity, Category = IssueCategory.Service, Description = "fixture"
        };
}
