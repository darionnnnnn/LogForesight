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

    private static AiOutcome Outcome(string fingerprint, List<string>? uncoveredChecksAddendum = null) => new(
        "new headline", "new summary", "trend", "action", RiskLevels.High, "test", true, 0,
        new List<string>(), null, new List<CategoryDeepDive>(), uncoveredChecksAddendum, fingerprint);

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    public void Dispose() => _fixture.Dispose();
}
