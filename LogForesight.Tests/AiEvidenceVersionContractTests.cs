using System.Diagnostics;
using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class AiEvidenceVersionContractTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private IAnalysisRecordStore Store(long hostId, string host) => new EfAnalysisRecordStore(
        _fixture.NewContext, "sqlite-in-memory", new HostKey { HostId = hostId, HostName = host });
    private static readonly IReadOnlySet<string> NoPatterns = new HashSet<string>();

    [Fact]
    public void SameRiskPrtgDiffRequeuesAi_NoDiffDoesNot_AndLowRiskDoesNotExpandAiScope()
    {
        var day = DateTime.Today.AddDays(-2);
        const long hostId = 8501;
        var store = Store(hostId, "AI-VERSION-HOST");
        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = hostId, Host = "AI-VERSION-HOST", Date = day,
            RiskLevel = RiskLevels.High, Headline = "old AI headline", Summary = "old AI summary",
            AiAnalyzed = true, TopIssues = new List<LogIssueSignature>()
        });
        var originalParent = store.ReadRecent(day, 1).Single();
        var originalFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(originalParent);
        var originalDecision = HostDayWorkflowFingerprint.ForRecord(originalParent);
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-warning-hardware");
        var changedEvidence = PrtgFindingMapper.ToSignature(
            new PrtgFinding(11, 1101, "warning", "same risk, new evidence", 100, rule), day);

        Assert.True(store.AttachPrtgFindings(hostId, day, [changedEvidence], NoPatterns, out _, aiConfigured: true));
        var pending = store.ReadRecent(day, 1).Single();
        Assert.Equal(RiskLevels.High, pending.RiskLevel);
        Assert.False(pending.AiAnalyzed);
        Assert.True(pending.AiPending);
        Assert.Equal("old AI summary", pending.Summary); // old text remains visible as stale evidence context
        Assert.False(store.TryAttachAiResult(day, Outcome(originalFingerprint), originalParent.RecordId,
            originalDecision, originalFingerprint));

        var currentFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(pending);
        Assert.True(store.TryAttachAiResult(day, Outcome(currentFingerprint), pending.RecordId,
            HostDayWorkflowFingerprint.ForRecord(pending), currentFingerprint));
        Assert.False(store.AttachPrtgFindings(hostId, day, [changedEvidence], NoPatterns, out _, aiConfigured: true));
        var settled = store.ReadRecent(day, 1).Single();
        Assert.True(settled.AiAnalyzed);
        Assert.False(settled.AiPending);

        const long lowHostId = 8502;
        var lowDay = day.AddDays(-1);
        var lowStore = Store(lowHostId, "AI-LOW-RISK-HOST");
        lowStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = lowHostId, Host = "AI-LOW-RISK-HOST", Date = lowDay,
            RiskLevel = RiskLevels.Low, AiAnalyzed = true,
            TopIssues = new List<LogIssueSignature>()
        });
        var informational = new LogIssueSignature
        {
            LogName = PrtgFindingMapper.PrtgLogName, Source = "PRTG:informational", EventKey = "prtg:informational:22",
            EventId = 0, EntryType = EventLogEntryType.Information, Severity = IssueSeverity.Low,
            ElevatesDayRisk = false, Count = 1
        };
        Assert.True(lowStore.AttachPrtgFindings(lowHostId, lowDay, [informational], NoPatterns, out _, aiConfigured: true));
        var low = lowStore.ReadRecent(lowDay, 1).Single();
        Assert.Equal(RiskLevels.Low, low.RiskLevel);
        Assert.True(low.AiAnalyzed);
        Assert.False(low.AiPending);
    }

    [Fact]
    public void CompetingAiAndPrtgWriters_StaleAiCannotWinEitherCommitOrder()
    {
        var day = DateTime.Today.AddDays(-3);
        var firstStore = Store(8601, "RACE-A");
        var secondStore = Store(8602, "RACE-B");
        var rule = KnownIssueSeed.CreateRules().Single(r => r.Id == "builtin-prtg-warning-hardware");
        var evidence = PrtgFindingMapper.ToSignature(new PrtgFinding(12, 1201, "warning", "new", 100, rule), day);

        // PRTG commits first: conditional AI sees the new authoritative fingerprint and loses.
        firstStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = 8601, Host = "RACE-A", Date = day,
            RiskLevel = RiskLevels.High, AiAnalyzed = true, Summary = "old"
        });
        var staleParent = firstStore.ReadRecent(day, 1).Single();
        var staleFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(staleParent);
        var staleDecision = HostDayWorkflowFingerprint.ForRecord(staleParent);
        Assert.True(firstStore.AttachPrtgFindings(8601, day, [evidence], NoPatterns, out _, aiConfigured: true));
        Assert.False(firstStore.TryAttachAiResult(day, Outcome(staleFingerprint), staleParent.RecordId,
            staleDecision, staleFingerprint));
        Assert.True(firstStore.ReadRecent(day, 1).Single().AiPending);

        // AI commits first: the later PRTG transaction invalidates that result in the same row update.
        var secondDay = day.AddDays(-1);
        secondStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = 8602, Host = "RACE-B", Date = secondDay,
            RiskLevel = RiskLevels.High, AiAnalyzed = true, Summary = "old"
        });
        var secondEvidence = PrtgFindingMapper.ToSignature(
            new PrtgFinding(13, 1301, "warning", "new", 100, rule), secondDay);
        var currentRecord = secondStore.ReadRecent(secondDay, 1).Single();
        var current = HostDayWorkflowFingerprint.PrtgInputFingerprint(currentRecord);
        Assert.True(secondStore.TryAttachAiResult(secondDay, Outcome(current), currentRecord.RecordId,
            HostDayWorkflowFingerprint.ForRecord(currentRecord), current));
        Assert.True(secondStore.AttachPrtgFindings(8602, secondDay, [secondEvidence], NoPatterns, out _, aiConfigured: true));
        var final = secondStore.ReadRecent(secondDay, 1).Single();
        Assert.False(final.AiAnalyzed);
        Assert.True(final.AiPending);

        // A replacement NetIQ parent may carry identical PRTG evidence, but has a new decision identity.
        // The immutable SQL row identity still rejects work computed against the deleted parent.
        var replacementStore = Store(8603, "RACE-REPLACED-PARENT");
        var thirdDay = day.AddDays(-2);
        replacementStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = 8603, Host = "RACE-REPLACED-PARENT", Date = thirdDay,
            RiskLevel = RiskLevels.High, AiAnalyzed = true, TopIssues = [evidence]
        });
        var oldParent = replacementStore.ReadRecent(thirdDay, 1).Single();
        var oldPrtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(oldParent);
        var oldDecision = HostDayWorkflowFingerprint.ForRecord(oldParent);
        replacementStore.DeleteDays([thirdDay]);
        replacementStore.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = 8603, Host = "RACE-REPLACED-PARENT", Date = thirdDay,
            RiskLevel = RiskLevels.High, AiAnalyzed = true, TopIssues = [evidence]
        });
        var newParent = replacementStore.ReadRecent(thirdDay, 1).Single();
        Assert.Equal(oldPrtg, HostDayWorkflowFingerprint.PrtgInputFingerprint(newParent));
        Assert.NotEqual(oldDecision, HostDayWorkflowFingerprint.ForRecord(newParent));
        Assert.NotEqual(oldParent.RecordId, newParent.RecordId);
        Assert.False(replacementStore.TryAttachAiResult(thirdDay, Outcome(oldPrtg), oldParent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(newParent), oldPrtg));
    }

    [Fact]
    public void ChangedFormalPrtgEvidenceRequeuesAiEvenWhenFindingsDoNotChange()
    {
        var day = DateTime.Today.AddDays(-4);
        const long hostId = 8503;
        var store = Store(hostId, "AI-MANIFEST-HOST");
        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, HostId = hostId, Host = "AI-MANIFEST-HOST", Date = day,
            RiskLevel = RiskLevels.High, AiAnalyzed = true, Summary = "previously complete"
        });
        var parent = store.ReadRecent(day, 1).Single();

        PrtgDecisionManifest Manifest(string evidence) => new()
        {
            ParentRecordId = parent.RecordId,
            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
            ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
            SourceGeneration = "source-v1",
            ResourceFingerprint = Hash("resource"),
            SemanticFingerprint = Hash("semantic"),
            StrategyFingerprint = Hash("strategy"),
            HostMappingFingerprint = Hash("mapping"),
            RuleFingerprint = Hash("rules"),
            EvidenceFingerprint = Hash(evidence),
            FindingFingerprint = PrtgFindingMapper.Fingerprint([]),
            CompletedAtUtc = DateTime.UtcNow,
            Outcome = "complete"
        };

        Assert.False(store.AttachPrtgFindings(hostId, day, [], NoPatterns, out _, aiConfigured: true,
            manifest: Manifest("evidence-v1")));
        var first = store.ReadRecent(day, 1).Single();
        Assert.True(first.AiPending);
        Assert.False(first.AiAnalyzed);
        var firstDecision = HostDayWorkflowFingerprint.ForRecord(first);
        var firstPrtg = HostDayWorkflowFingerprint.PrtgInputFingerprint(first);

        Assert.False(store.AttachPrtgFindings(hostId, day, [], NoPatterns, out _, aiConfigured: true,
            manifest: Manifest("evidence-v2")));
        var second = store.ReadRecent(day, 1).Single();
        Assert.NotEqual(firstPrtg, HostDayWorkflowFingerprint.PrtgInputFingerprint(second));
        Assert.False(store.TryAttachAiResult(day, Outcome(firstPrtg), first.RecordId, firstDecision, firstPrtg));
        Assert.True(second.AiPending);
        Assert.False(second.AiAnalyzed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuardedAiAddendumCarriesOnlyAStillValidFormalPrtgParentManifest(bool resourcePressure)
    {
        var day = DateTime.Today.AddDays(-6);
        const long hostId = 8504;
        const long staleHostId = 8505;
        const long modeVersion = 1;
        var store = (EfAnalysisRecordStore)Store(hostId, "AI-FORMAL-ADDENDUM");
        var staleStore = (EfAnalysisRecordStore)Store(staleHostId, "AI-FORMAL-STALE-ADDENDUM");
        var addendum = new List<string> { "Synthetic report evidence remains unavailable." };

        var parent = SeedFormalParent(store, hostId, day, modeVersion, resourcePressure);
        var prtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(parent);
        Assert.True(store.TryAttachAiResult(day, Outcome(prtgFingerprint, addendum), parent.RecordId,
            HostDayWorkflowFingerprint.ForRecord(parent), prtgFingerprint));

        var updated = store.ReadRecent(day, 1).Single();
        Assert.Contains(addendum.Single(), updated.UncoveredChecks);
        Assert.Equal(HostDayWorkflowFingerprint.ForParentRecord(updated), updated.PrtgManifest!.ParentFingerprint);
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(updated));
        var carriedParentFingerprint = updated.PrtgManifest.ParentFingerprint;
        var currentPrtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(updated);
        for (var replay = 0; replay < 5; replay++)
        {
            var current = store.ReadRecent(day, 1).Single();
            Assert.True(store.TryAttachAiResult(day, Outcome(currentPrtgFingerprint, addendum), current.RecordId,
                HostDayWorkflowFingerprint.ForRecord(current), currentPrtgFingerprint));
        }
        var replayed = store.ReadRecent(day, 1).Single();
        Assert.Single(replayed.UncoveredChecks.Where(note => note == addendum.Single()));
        Assert.Equal(carriedParentFingerprint, replayed.PrtgManifest!.ParentFingerprint);
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(replayed));

        var staleDay = day.AddDays(-1);
        var staleParent = SeedFormalParent(staleStore, staleHostId, staleDay, modeVersion, resourcePressure);
        using (var context = _fixture.NewContext())
        {
            var row = context.DailyRecords.Single(item => item.RecordId == staleParent.RecordId);
            var changed = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson)!;
            changed.UncoveredChecks.Add("Independent parent evidence changed after manifest capture.");
            row.ContentJson = JsonSerializer.Serialize(changed);
            context.SaveChanges();
        }

        var staleCurrent = staleStore.ReadRecent(staleDay, 1).Single();
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgManifest(staleCurrent));
        var stalePrtgFingerprint = HostDayWorkflowFingerprint.PrtgInputFingerprint(staleCurrent);
        var accepted = staleStore.TryAttachAiResult(staleDay, Outcome(stalePrtgFingerprint, addendum),
            staleCurrent.RecordId, HostDayWorkflowFingerprint.ForRecord(staleCurrent), stalePrtgFingerprint);
        Assert.Equal(!resourcePressure, accepted);
        var stillStale = staleStore.ReadRecent(staleDay, 1).Single();
        if (resourcePressure) Assert.DoesNotContain(addendum.Single(), stillStale.UncoveredChecks);
        Assert.False(HostDayWorkflowFingerprint.HasValidPrtgManifest(stillStale));
    }

    private DailyAnalysisRecord SeedFormalParent(EfAnalysisRecordStore store, long hostId, DateTime day,
        long modeVersion, bool resourcePressure = true)
    {
        _fixture.Blob(PrtgResourcePressureModeStore.BlobKey(hostId)).Mutate(current =>
            (current ?? "{}", true));
        store.Append(new DailyAnalysisRecord
        {
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            HostId = hostId, Host = $"FORMAL-{hostId}", Date = day, RiskLevel = RiskLevels.Medium
        });
        var parent = store.ReadRecent(day, 1).Single();
        var issue = resourcePressure ? FormalCpuIssue(hostId) : PrtgFindingMapper.ToSignature(
            new PrtgFinding(11, 1101, "warning", "synthetic hardware warning", 100,
                KnownIssueSeed.CreateRules().Single(rule => rule.Id == "builtin-prtg-warning-hardware")), day);
        var manifest = new PrtgDecisionManifest
        {
            ParentRecordId = parent.RecordId,
            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent),
            ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
            PolicyRevision = "synthetic-ai-addendum-policy-v1",
            SourceGeneration = "synthetic-ai-addendum-source-v1",
            ResourceAuthorityRevision = 1,
            ResourceModeBlobVersion = modeVersion,
            ResourceModeFenceRequired = resourcePressure,
            ResourceFingerprint = Hash("ai-addendum-resource"),
            SemanticFingerprint = Hash("ai-addendum-semantic"),
            StrategyFingerprint = Hash("ai-addendum-strategy"),
            HostMappingFingerprint = Hash("ai-addendum-mapping"),
            RuleFingerprint = Hash("ai-addendum-rule"),
            EvidenceFingerprint = Hash("ai-addendum-evidence"),
            FindingFingerprint = PrtgFindingMapper.Fingerprint([issue]),
            CompletedAtUtc = DateTime.UtcNow,
            Outcome = "complete"
        };

        Assert.True(store.AttachPrtgFindings(hostId, day, [issue], NoPatterns, out _, aiConfigured: true,
            manifest: manifest));
        var manifested = store.ReadRecent(day, 1).Single();
        Assert.True(HostDayWorkflowFingerprint.HasValidPrtgManifest(manifested));
        return manifested;
    }

    private static LogIssueSignature FormalCpuIssue(long hostId) => new()
    {
        LogName = PrtgFindingMapper.PrtgLogName,
        Source = "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure,
        EventId = 0,
        EventKey = $"prtg:{PrtgRuleEvaluator.RuleResourceCpuPressure}:53002:synthetic-source-{hostId}:synthetic-resource-{hostId}",
        EntryType = System.Diagnostics.EventLogEntryType.Warning,
        Category = IssueCategory.Service,
        Severity = IssueSeverity.Medium,
        RuleId = "builtin-prtg-resource-cpu-pressure",
        Count = 1,
        SampleMessages = ["Synthetic formal CPU pressure evidence."],
        PrtgSourceGeneration = $"synthetic-source-{hostId}",
        PrtgResourceGeneration = $"synthetic-resource-{hostId}",
        PrtgChannelGeneration = $"synthetic-channel-{hostId}",
        PrtgRuleAdmissionFingerprint = Hash("synthetic-formal-cpu-admission")
    };

    private static AiOutcome Outcome(string fingerprint, List<string>? uncoveredChecksAddendum = null) => new(
        "new headline", "new summary", "trend", "action", RiskLevels.High, "test", true, 0,
        new List<string>(), null, new List<CategoryDeepDive>(), uncoveredChecksAddendum, fingerprint);

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    public void Dispose() => _fixture.Dispose();
}
