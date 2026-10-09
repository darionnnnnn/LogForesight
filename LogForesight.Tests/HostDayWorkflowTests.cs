using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public sealed class HostDayWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lf-workflow-" + Guid.NewGuid().ToString("N"));
    private StorageBackend Backend() => new(new StorageSettings { Type = "Sqlite" }, _root);
    private HostDayWorkflowService Service() => new(new HostDayWorkflowStore(Backend()));
    private readonly DateTime _day = DateTime.Today.AddDays(-1);

    [Fact]
    public void HostDayAndDateAreIndependent_CompletionOrderAndRepeatedReplayConverge()
    {
        var service = Service();
        var pairs = new[] { (1L, _day), (1L, _day.AddDays(-1)), (2L, _day), (2L, _day.AddDays(-1)) };
        foreach (var (host, day) in pairs)
            service.ParentSucceeded(host, "h" + host, day, "parent-1", 0, "decision-" + host + day.Day,
                prtgEnabled: true, aiEnabled: true);

        // Two completion orders: PRTG first for A, parent first for B. Duplicate replay must be idempotent.
        service.SetPrtg(1, _day, WorkflowLegState.Succeeded, true, "prtg-a");
        service.ParentSucceeded(2, "h2", _day, "parent-1", 0, "decision-2" + _day.Day,
            prtgEnabled: true, aiEnabled: true);
        service.SetPrtg(2, _day, WorkflowLegState.Succeeded, true, "prtg-b");
        service.SetPrtg(1, _day, WorkflowLegState.Succeeded, true, "prtg-a");

        Assert.Equal(WorkflowLegState.Succeeded, service.Get(1, _day)!.Parent);
        Assert.Equal(WorkflowLegState.Succeeded, service.Get(1, _day)!.Prtg);
        Assert.Equal(WorkflowLegState.Succeeded, service.Get(2, _day)!.Parent);
        Assert.Equal(WorkflowLegState.Succeeded, service.Get(2, _day)!.Prtg);
        Assert.Equal(WorkflowLegState.Waiting, service.Get(1, _day.AddDays(-1))!.Prtg);
        Assert.Equal(WorkflowLegState.Waiting, service.Get(2, _day.AddDays(-1))!.Prtg);
    }

    [Fact]
    public void LatePrtgChangeBumpsDecisionVersion_RejectsOldAiWrite_AndRestartRestoresPending()
    {
        var first = Service();
        first.ParentSucceeded(11, "late-host", _day, "p1", 2, "parent-v1", true, true);
        var oldAiVersion = first.BeginAi(11, _day);
        first.SetPrtg(11, _day, WorkflowLegState.Succeeded, true, "evidence-v1");
        Assert.False(first.CompleteAi(11, _day, oldAiVersion, true));

        var currentAiVersion = first.BeginAi(11, _day);
        Assert.True(first.CompleteAi(11, _day, currentAiVersion, true));
        var beforeNoopReplay = first.Get(11, _day)!.DecisionVersion;
        first.SetPrtg(11, _day, WorkflowLegState.Succeeded, true, "evidence-v1");
        Assert.Equal(beforeNoopReplay, first.Get(11, _day)!.DecisionVersion);
        first.SetPrtg(11, _day, WorkflowLegState.Degraded, true, "evidence-v2");

        var restarted = Service();
        var restored = restarted.Get(11, _day)!;
        Assert.Equal(beforeNoopReplay + 1, restored.DecisionVersion);
        Assert.Equal(WorkflowLegState.Waiting, restored.Ai);
        Assert.Equal(WorkflowLegState.Degraded, restored.Prtg);
        Assert.False(restarted.CompleteAi(11, _day, currentAiVersion, true));
    }

    [Fact]
    public void ParentFailureZeroEventsDisabledTimeoutAndDegradedRemainDistinct()
    {
        var service = Service();
        service.ParentFailed(20, "failed", _day, "p-failed");
        var lateEvidenceAt = DateTime.Now.AddMinutes(-16);
        service.ParentSucceeded(21, "zero", _day, "p-zero", 0, "zero", true, false, lateEvidenceAt);
        service.SetPrtg(21, _day, WorkflowLegState.Deferred, true, now: lateEvidenceAt);
        service.ParentSucceeded(22, "disabled", _day, "p-disabled", 4, "disabled", false, false);
        service.ParentSucceeded(23, "timeout", _day, "p-timeout", 4, "timeout", true, false);
        service.SetPrtg(23, _day, WorkflowLegState.Degraded, true);

        Assert.Equal(WorkflowLegState.Failed, service.Get(20, _day)!.Parent);
        Assert.Equal("failed", service.Get(20, _day)!.WorkflowOutcome);
        Assert.Equal(0, service.Get(21, _day)!.ParentEventCount);
        Assert.Equal(WorkflowLegState.Deferred, service.Get(21, _day)!.Prtg);
        service.SetPrtg(21, _day, WorkflowLegState.Deferred, true, now: DateTime.Now);
        Assert.Equal(WorkflowLegState.Overdue, service.Get(21, _day)!.Prtg);
        Assert.Equal(WorkflowLegState.Disabled, service.Get(22, _day)!.Prtg);
        Assert.Equal(WorkflowLegState.Degraded, service.Get(23, _day)!.Prtg);
    }

    [Fact]
    public void LegacyRecoveryWithoutCurrentAuthorityRejectsZeroFindingManifestAndChangedParent()
    {
        var workflow = Service();
        var record = new DailyAnalysisRecord
        {
            RecordId = 781, HostId = 78, Host = "manifest-zero", Date = _day,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-20), AuditEventCount = 0,
            RiskLevel = "低", TopIssues = []
        };
        record.PrtgManifest = new PrtgDecisionManifest
        {
            Version = 1, ParentRecordId = record.RecordId,
            ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(record),
            ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(record.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
            SourceGeneration = "source",
            StrategyFingerprint = Hash("strategy"), HostMappingFingerprint = Hash("mapping"),
            RuleFingerprint = Hash("rules"), EvidenceFingerprint = Hash("evidence-v1"),
            ResourceFingerprint = Hash("resource"), SemanticFingerprint = Hash("semantic"),
            FindingFingerprint = PrtgFindingMapper.Fingerprint(record.TopIssues),
            CompletedAtUtc = DateTime.UtcNow, Outcome = "complete"
        };
        workflow.ReconcileAuthority([record], _day, _day, prtgEnabled: true, aiEnabled: true);
        Assert.Equal(WorkflowLegState.Succeeded, workflow.Get(record.HostId, _day)!.Parent);
        Assert.Equal(WorkflowLegState.Waiting, workflow.Get(record.HostId, _day)!.Prtg);
        Assert.Equal("whole-evidence-current-authority-unavailable",
            workflow.Get(record.HostId, _day)!.PrtgFailure);
        Assert.Equal(0, workflow.Get(record.HostId, _day)!.ParentEventCount);

        record.ErrorCount = 1;
        workflow.ReconcileAuthority([record], _day, _day, prtgEnabled: true, aiEnabled: true);
        Assert.Equal(WorkflowLegState.Waiting, workflow.Get(record.HostId, _day)!.Prtg);
        Assert.Empty(workflow.ParentHostIdsForDay(_day)); // recovery must not retain the live-run touched map
    }

    [Fact]
    public void ReplayingSameParentPreservesSupplementDeadline_RecoveryUsesRecordCompletionTime()
    {
        var workflow = Service();
        var completedAt = DateTime.Today.AddDays(-1).AddHours(21);
        workflow.ParentSucceeded(81, "deadline", _day, "run-1", 0, "same-parent", true, false, completedAt);
        workflow.SetPrtg(81, _day, WorkflowLegState.Deferred, evidenceReady: true, now: completedAt);
        var original = workflow.Get(81, _day)!;
        Assert.Equal(completedAt, original.ParentCompletedAt);
        Assert.Equal(completedAt.AddMinutes(15), original.SupplementDueAt);
        workflow.ReleaseTouchedDays([_day]);

        workflow.ParentSucceeded(81, "deadline", _day, "run-2", 0, "same-parent", true, false,
            now: DateTime.Now.AddHours(2), trackTouched: false, preserveMissingCompletionTime: true);
        var replayed = workflow.Get(81, _day)!;
        Assert.Equal(original.ParentCompletedAt, replayed.ParentCompletedAt);
        Assert.Equal(original.SupplementDueAt, replayed.SupplementDueAt);

        var record = new DailyAnalysisRecord
        {
            RecordId = 811, HostId = 82, Host = "recover-deadline", Date = _day,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = completedAt.ToUniversalTime(), AuditEventCount = 0, RiskLevel = "低"
        };
        workflow.ReconcileAuthority([record], _day, _day, prtgEnabled: true, aiEnabled: false);
        Assert.Equal(completedAt, workflow.Get(82, _day)!.ParentCompletedAt);
        Assert.Empty(workflow.ParentHostIdsForDay(_day));
    }

    [Fact]
    public void TerminalDegradedAndFailedLegsAreClassifiedAsPartialCompletion()
    {
        var workflow = Service();
        workflow.ParentSucceeded(83, "partial", _day, "run", 1, "fp", true, true);
        workflow.SetPrtg(83, _day, WorkflowLegState.Degraded, evidenceReady: true, evidenceFingerprint: "evidence");
        var state = workflow.Get(83, _day)!;
        Assert.False(state.IsComplete); // AI and current sensor coverage are still waiting.
        Assert.Equal("partial", state.WorkflowOutcome);

        workflow.ParentSucceeded(84, "ai-failed", _day, "run", 1, "fp", false, true);
        var version = workflow.BeginAi(84, _day);
        workflow.CompleteAi(84, _day, version, success: false, failure: "ai unavailable");
        Assert.Equal("partial", workflow.Get(84, _day)!.WorkflowOutcome);
    }

    [Fact]
    public void RecoveryKeysetCursorAdvancesPastMalformedPage()
    {
        var backend = Backend();
        backend.Blob("workflow_000_malformed").Mutate(_ => ("{}", true));
        var store = new HostDayWorkflowStore(backend);
        store.Update(1, _day, _ => { });

        var first = store.GetExistingHostDaysPage(afterKey: null, limit: 1);
        Assert.Equal(1, first.RawCount);
        Assert.Empty(first.Items);
        Assert.Equal("workflow_000_malformed", first.NextCursor);

        var second = store.GetExistingHostDaysPage(first.NextCursor, limit: 1);
        Assert.Equal(1, second.RawCount);
        Assert.Single(second.Items);
        Assert.True(string.CompareOrdinal(second.NextCursor, first.NextCursor) > 0);
    }

    [Fact]
    public async Task MailPartialsAndConcurrentUpdatesAreDurableAndSerialized()
    {
        var service = Service();
        service.ParentSucceeded(30, "mail", _day, "p1", 1, "fp", false, false);
        service.RecordCases(30, _day, ["case:alpha"], delivered: true);
        service.RecordMail(30, _day, ["notice:v1"],
            new Dictionary<string, IReadOnlyCollection<string>> { ["notice:v1"] = ["part:high", "part:daily"] },
            ["part:high"], ["part:daily"]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            service.RecordMail(30, _day, [$"intent:{i}"],
                new Dictionary<string, IReadOnlyCollection<string>> { [$"intent:{i}"] = [$"delivered:{i}"] },
                [$"delivered:{i}"], []))));

        var restarted = Service().Get(30, _day)!;
        Assert.Contains("part:high", restarted.MailDeliveredParts);
        Assert.Contains("part:daily", restarted.MailFailedParts);
        Assert.Equal(4, restarted.MailIntents.Length);
        Assert.Single(restarted.MailDeliveredParts);
        Assert.True(restarted.MailTrackingIncomplete); // overflow stays explicit instead of exceeding the durable byte cap
        Assert.Equal(WorkflowLegState.Succeeded, restarted.CaseState);
        Assert.False(restarted.MailIsComplete); // 1 success cannot hide the still-failed expected recipient
    }

    [Fact]
    public void MailPlanUsesExactDecisionVersionAndCurrentRecipientParts()
    {
        var service = Service();
        service.ParentSucceeded(31, "mail-version", _day, "p1", 1, "decision-v1", false, false, parentRecordId: 3101);
        var version1 = service.CaptureDeliveryVersion(31, _day, 3101)!;
        service.CloseMailPlan(31, _day, version1, ["summary"], "recipient-plan-evaluated");
        Assert.False(service.Get(31, _day)!.MailPlanClosed); // required lane without an eligible recipient waits.

        const string intent = "summary:decision-v1-content-a";
        var firstPlan = new Dictionary<string, IReadOnlyCollection<string>>
        { [intent] = ["recipient-a", "recipient-b"] };
        service.ReplaceMailPlan(31, _day, "summary", firstPlan, version1);
        service.RecordMail(31, _day, [intent], firstPlan, ["recipient-a"], ["recipient-b"], version1);
        service.CloseMailPlan(31, _day, version1, ["summary"], "recipient-plan-evaluated");
        Assert.False(service.Get(31, _day)!.MailIsComplete);
        Assert.True(service.HasAcceptedMailPart(31, _day, version1, intent, "recipient-a"));

        var changedRoute = new Dictionary<string, IReadOnlyCollection<string>>
        { [intent] = ["recipient-a", "recipient-c"] };
        service.ReplaceMailPlan(31, _day, "summary", changedRoute, version1);
        Assert.True(service.HasAcceptedMailPart(31, _day, version1, intent, "recipient-a"));
        Assert.False(service.HasAcceptedMailPart(31, _day, version1, intent, "recipient-b"));
        Assert.False(service.HasAcceptedMailPart(31, _day, version1, intent, "recipient-c"));
        service.RecordMail(31, _day, [intent], changedRoute, ["recipient-c"], [], version1);
        service.CloseMailPlan(31, _day, version1, ["summary"], "recipient-plan-evaluated");
        Assert.True(service.Get(31, _day)!.MailIsComplete);

        service.ParentSucceeded(31, "mail-version", _day, "p2", 1, "decision-v2", false, false, parentRecordId: 3102);
        var version2 = service.CaptureDeliveryVersion(31, _day, 3102)!;
        Assert.NotEqual(version1, version2);
        Assert.False(service.Get(31, _day)!.MailIsComplete);
        Assert.False(service.HasAcceptedMailPart(31, _day, version2, intent, "recipient-a"));
        service.CloseMailPlan(31, _day, version2, ["summary"], "recipient-plan-evaluated");
        Assert.False(service.Get(31, _day)!.MailPlanClosed);
    }

    [Fact]
    public void RecordingChangedMailIntentReplacesOldLaneReceipt()
    {
        var service = Service();
        service.ParentSucceeded(32, "mail-replace", _day, "p1", 1, "decision-v1", false, false, parentRecordId: 3201);
        var version = service.CaptureDeliveryVersion(32, _day, 3201)!;
        var oldPlan = new Dictionary<string, IReadOnlyCollection<string>> { ["summary:content-a"] = ["part-a"] };
        service.ReplaceMailPlan(32, _day, "summary", oldPlan, version);
        service.RecordMail(32, _day, ["summary:content-a"], oldPlan, ["part-a"], [], version);

        var newPlan = new Dictionary<string, IReadOnlyCollection<string>> { ["summary:content-b"] = ["part-b"] };
        service.RecordMail(32, _day, ["summary:content-b"], newPlan, [], ["part-b"], version);

        var state = service.Get(32, _day)!;
        Assert.Equal(new[] { "summary:content-b" }, state.MailIntents);
        Assert.DoesNotContain("summary:content-a", state.MailExpectedPartsByIntent.Keys);
        Assert.Empty(state.MailDeliveredPartsByIntent.GetValueOrDefault("summary:content-b") ?? []);
        Assert.Equal(new[] { "part-b" }, state.MailFailedPartsByIntent["summary:content-b"]);
        Assert.False(state.MailIsComplete);
    }

    [Fact]
    public void RecoveryKeysetPagesAreCappedAt500AndOversizedMetadataIsRejectedBeforeParse()
    {
        var backend = Backend();
        var service = new HostDayWorkflowService(new HostDayWorkflowStore(backend));
        for (var i = 1; i <= 501; i++)
            service.ParentFailed(100_000 + i, "page-host-" + i, _day, "failed");

        var workflowStore = new HostDayWorkflowStore(backend);
        var first = workflowStore.GetExistingHostDaysPage(null, 500);
        var second = workflowStore.GetExistingHostDaysPage(first.NextCursor, 500);
        Assert.Equal(500, first.RawCount);
        Assert.Equal(500, first.Items.Count);
        Assert.Single(second.Items);
        Assert.NotEqual(first.NextCursor, second.Items[0].BlobKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => workflowStore.GetExistingHostDaysPage(null, 501));

        backend.Blob("workflow_999999_20261001").Mutate(_ => (new string('x', 70_000), true));
        Assert.Throws<InvalidDataException>(() => workflowStore.Get(999_999, new DateTime(2026, 10, 1)));
        Assert.Throws<InvalidDataException>(() => service.ParentFailed(999_999, "oversized", new DateTime(2026, 10, 1), "failed"));
    }

    [Fact]
    public void PrtgReadinessRequiresClosedCurrentSelectionAndEveryExpectedFamily()
    {
        var workflow = Service();
        workflow.ParentSucceeded(901, "readiness", _day, "run", 0, "parent", true, false,
            parentRecordId: 901);
        workflow.SetPrtg(901, _day, WorkflowLegState.Succeeded, evidenceReady: true, evidenceFingerprint: Hash("manifest"));
        var initial = workflow.Get(901, _day)!;
        Assert.False(initial.IsComplete);

        var epoch = Hash("epoch-11");
        var selected = Hash("sensor-set-11");
        workflow.BeginPrtgReadiness(901, _day, epoch, selected,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.ClosePrtgSelectedSensors(901, _day, Hash("epoch-10"), selected);
        workflow.PublishPrtgResourceAssessment(901, _day, Hash("epoch-10"), selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        Assert.False(workflow.Get(901, _day)!.PrtgReadinessComplete);

        workflow.ClosePrtgSelectedSensors(901, _day, epoch, selected);
        workflow.PublishPrtgResourceAssessment(901, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.PublishPrtgResourceAssessment(901, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Disk, PrtgWorkflowAssessment.Insufficient);
        Assert.False(workflow.Get(901, _day)!.PrtgReadinessComplete);
        Assert.Equal(WorkflowLegState.Waiting, workflow.Get(901, _day)!.Prtg);

        workflow.BeginPrtgReadiness(901, _day, epoch, selected,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.PublishPrtgResourceAssessment(901, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.PublishPrtgResourceAssessment(901, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Disk, PrtgWorkflowAssessment.Recovery);
        workflow.ClosePrtgSelectedSensors(901, _day, epoch, selected);
        Assert.True(workflow.Get(901, _day)!.PrtgReadinessComplete);
        workflow.SetPrtg(901, _day, WorkflowLegState.Succeeded, evidenceReady: true, evidenceFingerprint: Hash("manifest"));
        Assert.Equal(WorkflowLegState.Succeeded, workflow.Get(901, _day)!.Prtg);
    }

    [Fact]
    public void PrtgReadinessWaitsUntilEveryExpectedSensorWasAssessed()
    {
        var workflow = Service();
        workflow.ParentSucceeded(904, "sensor-count", _day, "run", 0, "parent", true, false,
            parentRecordId: 904);
        var epoch = Hash("epoch-sensor-count");
        var selected = Hash("selected-sensor-count");
        workflow.BeginPrtgReadiness(904, _day, epoch, selected,
            [PrtgWorkflowResourceFamily.Cpu], new Dictionary<PrtgWorkflowResourceFamily, int>
            { [PrtgWorkflowResourceFamily.Cpu] = 2 });
        workflow.PublishPrtgResourceAssessment(904, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.ClosePrtgSelectedSensors(904, _day, epoch, selected);
        Assert.False(workflow.Get(904, _day)!.PrtgReadinessComplete);

        workflow.PublishPrtgResourceAssessment(904, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.Recovery);
        Assert.True(workflow.Get(904, _day)!.PrtgReadinessComplete);
    }

    [Fact]
    public void CompletedResourceReadinessAloneDoesNotStartWholeEvidenceSupplementDeadline()
    {
        var workflow = Service();
        var parentAt = DateTime.Now.AddMinutes(-30);
        workflow.ParentSucceeded(905, "readiness-deadline", _day, "run", 0, "parent-v1", true, false,
            now: parentAt, parentRecordId: 905);
        var epoch = Hash("deadline-epoch-1");
        var selected = Hash("deadline-selection-1");
        PublishCompleteReadiness(workflow, 905, epoch, selected);

        var ready = workflow.Get(905, _day)!;
        Assert.True(ready.PrtgReadinessComplete);
        Assert.Null(ready.PrtgEvidenceReadyAt);
        Assert.Null(ready.SupplementDueAt);
        Assert.Equal(WorkflowLegState.Running, ready.Prtg);
        var restarted = Service();
        PublishCompleteReadiness(restarted, 905, epoch, selected);
        var replayed = restarted.Get(905, _day)!;

        Assert.True(replayed.PrtgReadinessComplete);
        Assert.Null(replayed.PrtgEvidenceReadyAt);
        Assert.Null(replayed.SupplementDueAt);
    }

    [Fact]
    public void ResourcePassSelectionEpochAndParentChangesNeverInventAWholeEvidenceDeadline()
    {
        var workflow = Service();
        workflow.ParentSucceeded(906, "readiness-fence", _day, "run", 0, "parent-v1", true, false,
            now: DateTime.Now.AddMinutes(-30), parentRecordId: 906);
        var epoch = Hash("fenced-epoch-1");
        var selected = Hash("fenced-selection-1");
        PublishCompleteReadiness(workflow, 906, epoch, selected);
        var original = workflow.Get(906, _day)!;
        Assert.Null(original.PrtgEvidenceReadyAt);
        Assert.Null(original.SupplementDueAt);

        var changedSelection = Hash("fenced-selection-2");
        workflow.BeginPrtgReadiness(906, _day, epoch, changedSelection,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.PublishPrtgResourceAssessment(906, _day, epoch, changedSelection,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.ClosePrtgSelectedSensors(906, _day, epoch, changedSelection);

        var incomplete = workflow.Get(906, _day)!;
        Assert.False(incomplete.PrtgReadinessComplete);
        Assert.Null(incomplete.PrtgEvidenceReadyAt);
        Assert.Null(incomplete.SupplementDueAt);

        PublishCompleteReadiness(workflow, 906, epoch, changedSelection);
        Assert.Null(workflow.Get(906, _day)!.SupplementDueAt);

        var changedEpoch = Hash("fenced-epoch-2");
        workflow.BeginPrtgReadiness(906, _day, changedEpoch, changedSelection,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.PublishPrtgResourceAssessment(906, _day, changedEpoch, changedSelection,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.ClosePrtgSelectedSensors(906, _day, changedEpoch, changedSelection);
        var changedSource = workflow.Get(906, _day)!;
        Assert.False(changedSource.PrtgReadinessComplete);
        Assert.Null(changedSource.PrtgEvidenceReadyAt);
        Assert.Null(changedSource.SupplementDueAt);

        PublishCompleteReadiness(workflow, 906, changedEpoch, changedSelection);
        Assert.Null(workflow.Get(906, _day)!.SupplementDueAt);
        workflow.ParentSucceeded(906, "readiness-fence", _day, "new-run", 0, "parent-v2", true, false,
            now: DateTime.Now, parentRecordId: 907);
        var changedParent = workflow.Get(906, _day)!;
        Assert.Equal(907, changedParent.ParentRecordId);
        Assert.Empty(changedParent.PrtgReadinessEpoch);
        Assert.Null(changedParent.PrtgEvidenceReadyAt);
        Assert.Null(changedParent.SupplementDueAt);
    }

    [Fact]
    public void SuccessfulOrDisabledPrtgIsNotReclassifiedAsOverdue()
    {
        var workflow = Service();
        var elapsed = DateTime.Now.AddMinutes(-30);
        workflow.ParentSucceeded(908, "already-successful", _day, "run", 0, "parent-success", true, false,
            now: elapsed, parentRecordId: 908);
        workflow.SetPrtg(908, _day, WorkflowLegState.Succeeded, evidenceReady: true,
            evidenceFingerprint: Hash("already-successful-evidence"), now: elapsed);
        var success = workflow.Get(908, _day)!;
        Assert.Equal(WorkflowLegState.Succeeded, success.Prtg);
        Assert.Equal(elapsed.AddMinutes(15), success.SupplementDueAt);

        workflow.ParentSucceeded(909, "not-required", _day, "run", 0, "parent-disabled", false, false,
            now: elapsed, parentRecordId: 909);
        var disabled = workflow.Get(909, _day)!;
        Assert.Equal(WorkflowLegState.Disabled, disabled.Prtg);
        Assert.Null(disabled.SupplementDueAt);
    }

    [Fact]
    public void ParentRecordIdentityChangeInvalidatesRunningAiAndParentFingerprintIgnoresPrtgFindings()
    {
        var workflow = Service();
        workflow.ParentSucceeded(902, "parent-id", _day, "run-1", 0, "same-decision", true, true,
            parentRecordId: 1001);
        var version = workflow.BeginAi(902, _day);
        workflow.ParentSucceeded(902, "parent-id", _day, "run-2", 0, "same-decision", true, true,
            parentRecordId: 1002);
        Assert.False(workflow.CompleteAi(902, _day, version, success: true));
        Assert.Equal(1002, workflow.Get(902, _day)!.ParentRecordId);

        var record = new DailyAnalysisRecord
        {
            RecordId = 1002, HostId = 902, Host = "parent-id", Date = _day,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow, TopIssues = []
        };
        var parentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(record);
        record.TopIssues.Add(new LogIssueSignature { LogName = PrtgFindingMapper.PrtgLogName, EventKey = "PRTG:disk:1" });
        Assert.Equal(parentFingerprint, HostDayWorkflowFingerprint.ForParentRecord(record));
        Assert.NotEqual(HostDayWorkflowFingerprint.ForRecord(record), HostDayWorkflowFingerprint.ForRecord(
            new DailyAnalysisRecord { RecordId = 1002, HostId = 902, Host = "parent-id", Date = _day,
                LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
                LatestNetiqAttemptAtUtc = record.LatestNetiqAttemptAtUtc, TopIssues = [] }));
    }

    [Fact]
    public void CaseStateStoresOnlySha256TargetsAndNeedsAllExpectedTargetsDelivered()
    {
        var workflow = Service();
        workflow.ParentSucceeded(903, "cases", _day, "run", 0, "decision", false, false);
        workflow.RecordCaseResults(903, _day, ["event-a", "event-b"], ["event-a"], []);
        var partial = workflow.Get(903, _day)!;
        Assert.Equal(2, partial.CaseIntents.Length);
        Assert.All(partial.CaseIntents, id => Assert.Matches("^[A-F0-9]{64}$", id));
        Assert.False(partial.CaseIsComplete);
        workflow.RecordCaseResults(903, _day, ["event-a", "event-b"], ["event-b"], []);
        Assert.True(workflow.Get(903, _day)!.CaseIsComplete);
    }

    [Fact]
    public void PrtgCorroborationDoesNotInvalidateNetiqParentButNetiqCorrelationDoes()
    {
        var record = new DailyAnalysisRecord { RecordId = 51, HostId = 5, Host = "parent", Date = _day,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success" };
        var original = HostDayWorkflowFingerprint.ForParentRecord(record);
        record.CorrelationAlerts.Add("【儲存異常同日訊號】PRTG evidence");
        record.SuppressedCorrelationAlerts.Add("【容量異常同日訊號】PRTG evidence");
        Assert.Equal(original, HostDayWorkflowFingerprint.ForParentRecord(record));
        record.CorrelationAlerts.Add("NetIQ independent event correlation");
        Assert.NotEqual(original, HostDayWorkflowFingerprint.ForParentRecord(record));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    private void PublishCompleteReadiness(HostDayWorkflowService workflow, long hostId,
        string epoch, string selected)
    {
        workflow.BeginPrtgReadiness(hostId, _day, epoch, selected,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.PublishPrtgResourceAssessment(hostId, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.PublishPrtgResourceAssessment(hostId, _day, epoch, selected,
            PrtgWorkflowResourceFamily.Disk, PrtgWorkflowAssessment.Recovery);
        workflow.ClosePrtgSelectedSensors(hostId, _day, epoch, selected);
    }
}
