using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public sealed partial class PrtgDiskFormalFlowTests
{
    [Fact]
    public void ModeReplayCapturesTransitionObservationAndExistingSupplementReplayCreatesOneSameFindingCase()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);
        var parentBeforeReceiver = Parent();
        var manifest = Assert.IsType<PrtgDecisionManifest>(parentBeforeReceiver.PrtgManifest);
        var finding = Assert.Single(parentBeforeReceiver.TopIssues.Where(issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure));
        PrtgObservationRow observation;
        using (var db = _backend.CreateContext())
            observation = Assert.Single(db.PrtgObservations.Where(row => row.ActiveKey != null).ToArray());
        using (var json = System.Text.Json.JsonDocument.Parse(observation.ContentJson))
        {
            Assert.Equal(setup.Job.TransitionId, json.RootElement.GetProperty("TransitionId").GetString());
            Assert.Equal(setup.Settings.Get().Revision, json.RootElement.GetProperty("SettingsRevision").GetString());
        }
        Assert.Equal(EfPrtgObservationStore.SourceHintFor(setup.Settings.Get().PrtgUrl), observation.SourceHint);
        Assert.True(observation.RunId > 0);
        Assert.Equal("pending", observation.SupplementStatus);
        var issueKey = IssueSignatureKey.For(finding);
        var replayStartedCase = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Equal(issueKey, replayStartedCase.IssueKey);
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);

        var replay = SupplementReplayForMode();
        var applied = replay.RunBatch(workflow: WorkflowForMode());
        Assert.True(applied == 1, SupplementReplayDiagnostics(setup.Job, finding, applied));
        var caseRecord = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Equal(issueKey, caseRecord.IssueKey);
        Assert.Equal(finding.EventKey, caseRecord.PrtgEvidence?.EventKey);
        using (var db = _backend.CreateContext())
        {
            Assert.Equal("applied", Assert.Single(db.PrtgObservations).SupplementStatus);
            Assert.Single(db.IssueCases);
        }
        var parentAfterReceiver = Parent();
        Assert.Equal(manifest.Outcome, parentAfterReceiver.PrtgManifest!.Outcome);
        Assert.Equal(manifest.ResourceModeBlobVersion, parentAfterReceiver.PrtgManifest.ResourceModeBlobVersion);

        PrtgFinding capturedFinding;
        using (var json = System.Text.Json.JsonDocument.Parse(observation.ContentJson))
            capturedFinding = Assert.IsType<PrtgFinding>(json.RootElement.GetProperty("Finding")
                .Deserialize<PrtgFinding>());
        _backend.PrtgObservationStore().Capture(HostId, _completedDay, setup.Settings.Get().Revision,
            [(capturedFinding, finding)], setup.Settings.Get().PrtgUrl, observation.RunId,
            setup.Job.TransitionId);
        Assert.Equal(0, replay.RunBatch(workflow: WorkflowForMode()));
        using var final = _backend.CreateContext();
        Assert.Single(final.PrtgObservations);
        Assert.Single(final.IssueCases);
    }

    [Fact]
    public void ModeReplayRetriesDurableCaseHandoffAfterParentCommitAndQueuesAi()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var firstWorker = ReplayWorker(setup.Settings, new FakeWebAi { Available = true });
        firstWorker.BeforeCaseHandoffForTesting = () => throw new TimeoutException("simulated process loss after parent commit");

        firstWorker.RunOnce(CancellationToken.None);

        var committed = Parent();
        Assert.Contains(committed.TopIssues, issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure);
        Assert.True(committed.AiPending);
        Assert.False(committed.AiAnalyzed);
        Assert.Empty(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Equal(PrtgModeReplayStatus.Waiting, ReplayProgress().Find(setup.Job)?.Status);

        // A fresh worker re-evaluates the same finding, retries the durable start claim, and completes.
        ReplayWorker(setup.Settings, new FakeWebAi { Available = true }).RunOnce(CancellationToken.None);

        var completed = Assert.IsType<PrtgResourcePressureModeReplayProgress>(ReplayProgress().Find(setup.Job));
        Assert.Equal(PrtgModeReplayStatus.Completed, completed.Status);
        var finding = Assert.Single(Parent().TopIssues.Where(issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure));
        var caseRecord = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Equal(IssueSignatureKey.For(finding), caseRecord.IssueKey);
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);

        ReplayWorker(setup.Settings, new FakeWebAi { Available = true }).RunOnce(CancellationToken.None);
        Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);
    }

    [Fact]
    public void ModeReplayHandsOffAlreadyPersistedQualifiedFindingBeforeCompletingProgress()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var existing = CpuPressureSignature(_completedDay, setup.Profile);
        Assert.True(AttachCpuFinding(setup, existing));

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        Assert.Contains(Parent().TopIssues, issue => issue.EventKey == existing.EventKey);
        Assert.Equal(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
        var caseRecord = Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST"));
        Assert.Equal(IssueSignatureKey.For(existing), caseRecord.IssueKey);
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);
    }

    [Fact]
    public void ModeReplayRetriesUnavailableDispatchContextAfterRestartWithoutDuplicateCaseOrOrder()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var candidateSource = new FailFirstModeReplayCandidateSource(HostId);

        ReplayWorker(setup.Settings, candidateSource: candidateSource).RunOnce(CancellationToken.None);

        Assert.Equal(1, candidateSource.BuildCalls);
        Assert.Equal(PrtgModeReplayStatus.Waiting, ReplayProgress().Find(setup.Job)?.Status);
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);

        // A fresh worker can rebuild dispatch context; the same committed finding is handed off once.
        ReplayWorker(setup.Settings, candidateSource: candidateSource).RunOnce(CancellationToken.None);

        Assert.Equal(2, candidateSource.BuildCalls);
        Assert.Equal(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
        Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);

        ReplayWorker(setup.Settings, candidateSource: candidateSource).RunOnce(CancellationToken.None);
        Assert.Single(_backend.WorkOrderStore().GetAllActive());
        Assert.Single(WorkflowForMode().Get(HostId, _completedDay)!.FormalCaseStartClaims);
    }

    [Fact]
    public void MissingPressureClaimStillAttachesIndependentIssueAndKeepsReplayRetryable()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var pressure = CpuPressureSignature(_completedDay, setup.Profile);
        var independent = new LogIssueSignature
        {
            LogName = "NetIQ",
            Source = "Synthetic NetIQ issue",
            EventId = 53053,
            EntryType = System.Diagnostics.EventLogEntryType.Error
        };
        var independentKey = IssueSignatureKey.For(independent);
        var existingCaseId = Guid.NewGuid().ToString("N");
        _backend.IssueCaseStore().Save(new IssueCase
        {
            CaseId = existingCaseId,
            HostName = "DISK-HOST",
            IssueKey = independentKey,
            IssueLabel = independent.SourceEventLabel,
            Status = IssueHandlingStatuses.InProgress,
            HandlerId = 99001,
            FirstLinkedDate = _completedDay.AddDays(-1),
            LastLinkedDate = _completedDay.AddDays(-1),
            CreatedAt = DateTime.Now,
            CreatedByAccount = "fixture",
            UpdatedAt = DateTime.Now
        });
        var owners = new IssueOwnerStore(_backend.Blob("issue_owners"));
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, owners);
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        var dispatch = new NightlyDispatch(orders, DispatchContext.CreateUnavailable(_backend.IssueCaseStore()), _hosts);

        var attached = HostDayPostProcessor.AttachCase(cases, dispatch, "DISK-HOST", _completedDay,
            [pressure, independent], workflow: WorkflowForMode(), hostId: HostId,
            parentRecordId: Parent().RecordId + 1);

        Assert.False(attached);
        Assert.Contains(_backend.IssueHandlingStore().GetForDay("DISK-HOST", _completedDay), item =>
            item.IssueKey == independentKey && item.CaseId == existingCaseId);
        Assert.DoesNotContain(_backend.IssueHandlingStore().GetForDay("DISK-HOST", _completedDay), item =>
            item.IssueKey == IssueSignatureKey.For(pressure));
        Assert.Equal(existingCaseId, Assert.Single(_backend.IssueCaseStore().GetOpenForHost("DISK-HOST")).CaseId);
        Assert.Null(WorkflowForMode().Get(HostId, _completedDay));
        Assert.Empty(_backend.WorkOrderStore().GetAllActive());
    }

    [Fact]
    public void ModeReplayWithoutAiWritesBoundedCacheReportForPersistedPrtgFinding()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var reportAi = new FakeAiService();
        var service = ReplayWorker(setup.Settings, aiAvailable: false, reportAi);
        DailyAnalysisRecord? parentBeforeReport = null;
        service.BeforeReplayReportForTesting = () => parentBeforeReport = Parent();

        service.RunOnce(CancellationToken.None);

        var parent = Parent();
        var before = Assert.IsType<DailyAnalysisRecord>(parentBeforeReport);
        Assert.Equal(HostDayWorkflowFingerprint.ForRecord(before), HostDayWorkflowFingerprint.ForRecord(parent));
        Assert.Equal(before.UncoveredChecks, parent.UncoveredChecks);
        Assert.Equal(before.ErrorCount, parent.ErrorCount);
        Assert.Equal(before.WarningCount, parent.WarningCount);
        var finding = Assert.Single(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg));
        Assert.Equal(RiskLevels.Medium, parent.RiskLevel);
        var report = Assert.IsType<ReportContent>(_backend.ReportStore().Read(
            new HostKey { HostId = HostId, HostName = "DISK-HOST" }, _completedDay, ReportKinds.DailyRisk));
        Assert.Contains(Assert.Single(finding.SampleMessages), report.Content);
        Assert.Contains($"sha256:{HostDayWorkflowFingerprint.PrtgInputFingerprint(parent)}", report.Content);
        Assert.Contains("未重新查詢 NetIQ", report.Content);
        Assert.True(report.Content.Contains("最多 500 筆的每主機日快取子集", StringComparison.Ordinal) ||
            report.Content.Contains("NetIQ 原始事件快取為空", StringComparison.Ordinal), report.Content);
        Assert.True(report.Content.Contains("不代表完整原始日誌", StringComparison.Ordinal) ||
            report.Content.Contains("不列為完整原始日誌", StringComparison.Ordinal), report.Content);
        Assert.Equal(0, reportAi.Calls);
        Assert.Equal(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
    }

    [Fact]
    public void ModeReplayWithAiAvailableLeavesReportToExistingAiPendingConsumer()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var reportAi = new FakeAiService();

        ReplayWorker(setup.Settings, aiAvailable: true, reportAi).RunOnce(CancellationToken.None);

        Assert.True(Parent().AiPending);
        Assert.Null(Parent().ReportFile);
        Assert.Null(_backend.ReportStore().Read(new HostKey { HostId = HostId, HostName = "DISK-HOST" },
            _completedDay, ReportKinds.DailyRisk));
        Assert.Equal(0, reportAi.Calls);
    }

    [Fact]
    public void ModeOffReplayKeepsHighNetiqBaselineAndRefreshesNoAiReport()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true, highNetiqBaseline: true);
        ReplayWorker(setup.Settings, aiAvailable: false).RunOnce(CancellationToken.None);
        var previousReport = Assert.IsType<ReportContent>(_backend.ReportStore().Read(
            new HostKey { HostId = HostId, HostName = "DISK-HOST" }, _completedDay, ReportKinds.DailyRisk));
        Assert.Contains(Assert.Single(Parent().TopIssues.Single(PrtgFindingMapper.IsPrtg).SampleMessages),
            previousReport.Content);
        Assert.True(setup.Mode.DisableFormalMode(HostId, ModeReplayCpuPressureSensorId, DateTime.UtcNow));

        ReplayWorker(setup.Settings, aiAvailable: false).RunOnce(CancellationToken.None);

        var parent = Parent();
        Assert.DoesNotContain(parent.TopIssues, PrtgFindingMapper.IsPrtg);
        Assert.Equal(RiskLevels.High, parent.RiskLevel);
        Assert.Equal(RiskLevels.High, parent.PrtgBaselineRiskLevel);
        Assert.NotNull(parent.ReportFile);
        var refreshed = Assert.IsType<ReportContent>(_backend.ReportStore().Read(
            new HostKey { HostId = HostId, HostName = "DISK-HOST" }, _completedDay, ReportKinds.DailyRisk));
        Assert.DoesNotContain(Assert.Single(previousReport.Content.Split("\n")
            .Where(line => line.Contains("PRTG量值／期間說明", StringComparison.Ordinal))), refreshed.Content);
        Assert.Contains($"sha256:{HostDayWorkflowFingerprint.PrtgInputFingerprint(parent)}", refreshed.Content);
        var offJob = Assert.Single(setup.Mode.ReadHostSnapshot(HostId).ReplayJobs.Where(job => !job.Enabled));
        Assert.Equal(PrtgModeReplayStatus.Completed, ReplayProgress().Find(offJob)?.Status);
    }

    [Fact]
    public void ModeOffReplayWithAiAvailableQueuesBaselineRefreshAndClearsStaleReportMarker()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true, highNetiqBaseline: true);
        ReplayWorker(setup.Settings, aiAvailable: false).RunOnce(CancellationToken.None);
        Assert.NotNull(Parent().ReportFile);
        Assert.True(setup.Mode.DisableFormalMode(HostId, ModeReplayCpuPressureSensorId, DateTime.UtcNow));

        var reportAi = new FakeAiService();
        ReplayWorker(setup.Settings, aiAvailable: true, reportAi).RunOnce(CancellationToken.None);

        var parent = Parent();
        Assert.DoesNotContain(parent.TopIssues, PrtgFindingMapper.IsPrtg);
        Assert.Equal(RiskLevels.High, parent.RiskLevel);
        Assert.Equal(RiskLevels.High, parent.PrtgBaselineRiskLevel);
        Assert.True(parent.AiPending);
        Assert.NotNull(parent.ReportFile);
        Assert.NotEqual(parent.PrtgReportEvidenceFingerprint,
            HostDayWorkflowFingerprint.PrtgInputFingerprint(parent));
        Assert.Equal(0, reportAi.Calls);
    }

    [Fact]
    public void ModeOffReplayDoesNotCreateReportWhenRetractionReturnsDayToLowRisk()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        ReplayWorker(setup.Settings, aiAvailable: false).RunOnce(CancellationToken.None);
        var prior = Parent();
        Assert.NotNull(prior.ReportFile);
        var priorReport = _backend.ReportStore().Read(new HostKey { HostId = HostId, HostName = "DISK-HOST" },
            _completedDay, ReportKinds.DailyRisk)?.Content;
        Assert.True(setup.Mode.DisableFormalMode(HostId, ModeReplayCpuPressureSensorId, DateTime.UtcNow));

        ReplayWorker(setup.Settings, aiAvailable: false).RunOnce(CancellationToken.None);

        var parent = Parent();
        Assert.Equal(RiskLevels.Low, parent.RiskLevel);
        Assert.NotNull(parent.ReportFile);
        Assert.NotEqual(parent.PrtgReportEvidenceFingerprint,
            HostDayWorkflowFingerprint.PrtgInputFingerprint(parent));
        Assert.Equal(priorReport, _backend.ReportStore().Read(new HostKey
            { HostId = HostId, HostName = "DISK-HOST" }, _completedDay, ReportKinds.DailyRisk)?.Content);
    }

    [Fact]
    public void ModeReplayReportAttachFailureLeavesDayRetryableAndRestartRegeneratesReport()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var failedWriter = ReplayWorker(setup.Settings, aiAvailable: false, new FakeAiService());
        failedWriter.BeforeReplayReportForTesting = () =>
        {
            using var db = _backend.CreateContext();
            db.Database.ExecuteSqlRaw("CREATE TRIGGER fail_replay_report_parent_update BEFORE UPDATE ON lf_daily_records BEGIN SELECT RAISE(ABORT, 'fixture report parent update failure'); END;");
        };

        failedWriter.RunOnce(CancellationToken.None);

        var waiting = Assert.IsType<PrtgResourcePressureModeReplayProgress>(ReplayProgress().Find(setup.Job));
        Assert.Equal(PrtgModeReplayStatus.Waiting, waiting.Status);
        Assert.Equal("mode-replay-report-finalization-pending", waiting.LastReason);
        Assert.Equal(0, waiting.AfterRecordId);
        Assert.Null(Parent().ReportFile);
        using (var db = _backend.CreateContext())
            db.Database.ExecuteSqlRaw("DROP TRIGGER fail_replay_report_parent_update;");

        ReplayWorker(setup.Settings, aiAvailable: false, new FakeAiService()).RunOnce(CancellationToken.None);

        Assert.NotNull(Parent().ReportFile);
        Assert.Equal(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
    }

    [Fact]
    public void EnableObservationCaptureFailureRetriesAfterRestartWithoutMovingEvidenceDeadline()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        using (var db = _backend.CreateContext())
            db.Database.ExecuteSqlRaw("CREATE TRIGGER fail_mode_observation_capture BEFORE INSERT ON lf_prtg_observations BEGIN SELECT RAISE(ABORT, 'fixture capture failure'); END;");

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        var afterFailedCapture = Parent();
        Assert.Contains(afterFailedCapture.TopIssues, issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure);
        Assert.NotNull(afterFailedCapture.PrtgManifest);
        var progressStore = ReplayProgress();
        var waiting = Assert.IsType<PrtgResourcePressureModeReplayProgress>(progressStore.Find(setup.Job));
        Assert.Equal(PrtgModeReplayStatus.Waiting, waiting.Status);
        Assert.NotNull(waiting.EvidenceReadyAtUtc);
        Assert.Equal(waiting.EvidenceReadyAtUtc!.Value.AddMinutes(15), waiting.DueAtUtc);
        using (var db = _backend.CreateContext()) Assert.Empty(db.PrtgObservations);

        using (var db = _backend.CreateContext())
            db.Database.ExecuteSqlRaw("DROP TRIGGER fail_mode_observation_capture;");

        // A newly constructed worker resumes the durable job and captures into the existing receiver seam.
        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);
        var completed = Assert.IsType<PrtgResourcePressureModeReplayProgress>(progressStore.Find(setup.Job));
        Assert.Equal(PrtgModeReplayStatus.Completed, completed.Status);
        Assert.Equal(waiting.EvidenceReadyAtUtc, completed.EvidenceReadyAtUtc);
        Assert.Equal(waiting.DueAtUtc, completed.DueAtUtc);
        using (var db = _backend.CreateContext())
            Assert.Equal("pending", Assert.Single(db.PrtgObservations).SupplementStatus);

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);
        var replay = SupplementReplayForMode();
        var applied = replay.RunBatch(workflow: WorkflowForMode());
        Assert.True(applied == 1, SupplementReplayDiagnostics(setup.Job, Parent().TopIssues.Single(issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure), applied));
        Assert.Equal(0, replay.RunBatch(workflow: WorkflowForMode()));
        using var final = _backend.CreateContext();
        Assert.Equal("applied", Assert.Single(final.PrtgObservations).SupplementStatus);
        Assert.Single(final.IssueCases);
    }

    [Fact]
    public void EnableRunOncePublishesFreshQualifiedCpuManifestAsPartialResourceOnlyDecision()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var modeVersion = setup.Mode.ReadHostSnapshot(HostId).BlobVersion;
        var consumer = new PrtgResourcePeriodConsumer(_backend, setup.Settings)
            .EvaluateClosedHostDayBatch([ModeReplayCpuPressureSensorId], _completedDay, DateTime.UtcNow,
                evaluationHostIds: [HostId]);
        var closed = Assert.IsType<PrtgResourceClosedDayMetadata>(consumer.ClosedDay);
        Assert.Contains(ModeReplayCpuPressureSensorId, closed.SelectedSensorObjids);
        Assert.DoesNotContain(ModeReplayCpuPressureSensorId, closed.RejectedSensorObjids);
        Assert.Equal(closed.ExpectedWindowCountsBySensor[ModeReplayCpuPressureSensorId],
            closed.EvaluatedWindowCountsBySensor[ModeReplayCpuPressureSensorId]);
        Assert.True(closed.ExpectedWindowCountsBySensor[ModeReplayCpuPressureSensorId] >= 20);

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        var parent = Assert.Single(_backend.RecordStore(new HostKey { HostId = HostId, HostName = "DISK-HOST" })
            .ReadRecent(_completedDay, 1));
        var finding = Assert.Single(parent.TopIssues.Where(issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure));
        Assert.Equal(ResourceGeneration(setup.Profile), finding.PrtgResourceGeneration);
        Assert.NotNull(parent.PrtgManifest);
        Assert.Equal("partial", parent.PrtgManifest!.Outcome);
        Assert.Contains("mode-replay-resource-family-only", parent.PrtgManifest.WaitReasonCodes);
        Assert.True(parent.PrtgManifest.ResourceModeFenceRequired);
        Assert.Equal(modeVersion, parent.PrtgManifest.ResourceModeBlobVersion);
        Assert.Equal(modeVersion, setup.Mode.ReadHostSnapshot(HostId).BlobVersion);
        var progress = ReplayProgress().Find(setup.Job);
        Assert.Equal(PrtgModeReplayStatus.Completed, progress?.Status);
        Assert.NotNull(progress?.EvidenceReadyAtUtc);
        Assert.Equal(progress!.EvidenceReadyAtUtc!.Value.AddMinutes(15), progress.DueAtUtc);
    }

    [Fact]
    public void EnableRunOnceLeavesOldFindingAndManifestUntouchedWhenClosedDayEvidenceIsMissing()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: false);
        var stale = CpuPressureSignature(_completedDay, setup.Profile);
        Assert.True(AttachCpuFinding(setup, stale));
        var before = Parent();
        var beforeManifest = System.Text.Json.JsonSerializer.Serialize(before.PrtgManifest);

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        var after = Parent();
        Assert.Contains(after.TopIssues, issue => issue.EventKey == stale.EventKey);
        Assert.Equal(beforeManifest, System.Text.Json.JsonSerializer.Serialize(after.PrtgManifest));
        var progress = ReplayProgress().Find(setup.Job);
        Assert.Equal(PrtgModeReplayStatus.Waiting, progress?.Status);
        Assert.NotNull(progress?.LastReason);
    }

    [Fact]
    public void EnableRunOnceRejectsOldResourceGenerationWithoutPublishingManifest()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var stale = CpuPressureSignature(_completedDay, setup.Profile);
        Assert.True(AttachCpuFinding(setup, stale));
        var identity = _backend.PrtgStore().GetResourceIdentity(ModeReplayCpuPressureSensorId);
        _backend.PrtgStore().BindObservedResource(ModeReplayCpuPressureSensorId, HostId,
            identity.SourceGeneration, "rotated-resource-fingerprint");

        ReplayWorker(setup.Settings).RunOnce(CancellationToken.None);

        var after = Parent();
        Assert.Contains(after.TopIssues, issue => issue.EventKey == stale.EventKey);
        Assert.Null(after.PrtgManifest);
        Assert.NotEqual(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
    }

    [Fact]
    public void EnableRunOnceRejectsModeRevisionRaceBeforePublishingFindingOrManifest()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var worker = ReplayWorker(setup.Settings);
        var raced = false;
        worker.BeforeReconciliationForTesting = () =>
        {
            if (raced) return;
            raced = true;
            Assert.True(setup.Mode.DisableFormalMode(HostId, ModeReplayCpuPressureSensorId, DateTime.UtcNow));
        };

        worker.RunOnce(CancellationToken.None);

        Assert.True(raced);
        var after = Parent();
        Assert.DoesNotContain(after.TopIssues, issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure);
        Assert.Null(after.PrtgManifest);
        Assert.NotEqual(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
    }

    [Fact]
    public void EnableRunOnceRejectsParentFingerprintRaceBeforePublishingFindingOrManifest()
    {
        var setup = PrepareFormalCpuReplay(includeClosedDayEvidence: true);
        var worker = ReplayWorker(setup.Settings);
        var raced = false;
        worker.BeforeReconciliationForTesting = () =>
        {
            if (raced) return;
            raced = true;
            var parent = Parent();
            var unrelated = new LogIssueSignature
            {
                LogName = "System", Source = "cas-test", EventId = 1,
                EventKey = "cas-test:preserve", Severity = IssueSeverity.Medium, Count = 1
            };
            var current = setup.Mode.ReadHostSnapshot(HostId);
            var fence = new PrtgResourceGenerationFence(ModeReplayCpuPressureSensorId,
                setup.Job.SourceGeneration, setup.Job.ResourceGeneration);
            var batch = new PrtgStateReconciliationBatch(setup.Job.SourceGeneration,
                new Dictionary<long, string>(), new Dictionary<long, string>(), new HashSet<string>(),
                [fence], current.BlobVersion, resourceModeFenceRequired: true);
            Assert.True(_backend.RecordStore().AttachPrtgModeRevocationsWithReconciliation(
                HostId, _completedDay, [unrelated], new HashSet<string>(), out _, aiConfigured: false,
                reconciliation: batch));
            Assert.NotEqual(parent.RecordId, 0);
        };

        worker.RunOnce(CancellationToken.None);

        Assert.True(raced);
        var after = Parent();
        Assert.Contains(after.TopIssues, issue => issue.EventKey == "cas-test:preserve");
        Assert.DoesNotContain(after.TopIssues, issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure);
        Assert.Null(after.PrtgManifest);
        Assert.NotEqual(PrtgModeReplayStatus.Completed, ReplayProgress().Find(setup.Job)?.Status);
    }

    [Fact]
    public void RunOnceUsesHostPagesAndContinuesPastWaitingFirstPage()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = false; value.PrtgUrl = string.Empty; });
        const long firstHost = 900_000;
        const int waitingHosts = 20;
        for (var index = 0; index <= waitingHosts; index++)
        {
            var hostId = firstHost + index;
            var sensorId = 910_000 + index;
            SeedDisabledReplay(hostId, sensorId, addParent: index == waitingHosts);
        }

        ReplayWorker(settings).RunOnce(CancellationToken.None);

        var lastHostProgress = new PrtgResourcePressureModeReplayProgressStore(_backend.Blob(
            PrtgResourcePressureModeReplayProgressStore.BlobKey(firstHost + waitingHosts))).Get();
        Assert.Contains(lastHostProgress.Items, item => item.Status == PrtgModeReplayStatus.Completed);
        for (var index = 0; index < waitingHosts; index++)
        {
            var progress = new PrtgResourcePressureModeReplayProgressStore(_backend.Blob(
                PrtgResourcePressureModeReplayProgressStore.BlobKey(firstHost + index))).Get();
            Assert.Contains(progress.Items, item => item.Status == PrtgModeReplayStatus.Waiting);
        }
    }

    [Fact]
    public void RunOnceRoundRobinsEightJobsAndResumesTheNinthAfterRestartWithoutMovingDeadlines()
    {
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        settings.Update(value => { value.PrtgEnabled = false; value.PrtgUrl = string.Empty; });
        const long hostId = 920_001;
        const int jobCount = 9;
        var now = DateTime.UtcNow;
        var mode = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(hostId)));
        mode.Update(document =>
        {
            for (var index = 0; index < jobCount; index++)
            {
                var sensorId = 920_100 + index;
                var transition = Guid.NewGuid().ToString("N");
                document.Revocations.Add(new(hostId, sensorId, PrtgResourceFamily.Cpu,
                    SourceGenerationForTest, "resource-generation-" + index, now, transition));
                document.ReplayJobs.Add(new(hostId, sensorId, PrtgResourceFamily.Cpu,
                    SourceGenerationForTest, "resource-generation-" + index, false,
                    transition, now, now.AddMinutes(15)));
            }
        });
        var jobs = mode.ReadHostSnapshot(hostId).ReplayJobs;
        var authorityVersion = mode.ReadHostSnapshot(hostId).BlobVersion;

        ReplayWorker(settings).RunOnce(CancellationToken.None);
        var progressStore = new PrtgResourcePressureModeReplayProgressStore(_backend.Blob(
            PrtgResourcePressureModeReplayProgressStore.BlobKey(hostId)));
        Assert.Equal(8, progressStore.Get().Items.Count);
        Assert.Equal(8, progressStore.GetNextJobOffset(jobCount));
        Assert.All(jobs.Take(8), job =>
        {
            var progress = Assert.IsType<PrtgResourcePressureModeReplayProgress>(progressStore.Find(job));
            Assert.Equal(job.DueAtUtc, progress.DueAtUtc);
        });

        // Each slice is capped at eight jobs. The second slice starts at job nine, wraps, and
        // advances eight more positions: (8 + 8) % 9 == 7.
        ReplayWorker(settings).RunOnce(CancellationToken.None);
        Assert.Equal(jobCount, progressStore.Get().Items.Count);
        Assert.Equal(7, progressStore.GetNextJobOffset(jobCount));
        Assert.Equal(authorityVersion, mode.ReadHostSnapshot(hostId).BlobVersion);
        Assert.All(jobs, job => Assert.Equal(job.DueAtUtc, progressStore.Find(job)?.DueAtUtc));
    }

    private (PrtgTrustedSamplingProfile Profile, ISystemSettingsStore Settings,
        PrtgResourcePressureModeStore Mode, PrtgResourcePressureModeReplayJob Job)
        PrepareFormalCpuReplay(bool includeClosedDayEvidence, bool highNetiqBaseline = false)
    {
        SeedDiskHistory(with28Days: true, descending: false, excludeOutsideParentDay: true,
            highNetiqBaseline: highNetiqBaseline);
        ModeReplayEnableResourceConsumerSettings();
        var rules = KnownIssueSeed.CreateRules().Select(rule =>
            rule.Id == "builtin-prtg-resource-cpu-pressure" ? rule.CloneForSeedOverwrite(true) : rule).ToList();
        new KnownIssueRuleStore(_backend.Blob("rules")).Save(new RuleFileContent
        { SeedVersion = KnownIssueSeed.Version, Rules = rules });
        KnownIssueCatalog.Initialize(rules);
        var profile = ModeReplaySeedPressureSensor(ModeReplayCpuPressureSensorId, "CPU Usage",
            PrtgSensorCategories.Cpu, PrtgTrustedQuantitySemantic.CpuLoadPercent);
        var pressureSignature = CpuPressureSignature(_completedDay, profile);
        new IssueOwnerStore(_backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = pressureSignature.Source,
            EventId = pressureSignature.EventId,
            OwnerUserIds = [99_001]
        });
        var store = _backend.PrtgStore();
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        var currentCutoff = ModeReplayFloorUtcHour(DateTime.UtcNow);
        store.MergeSampledValues(new[] { currentCutoff.AddHours(-2), currentCutoff.AddHours(-1) }
            .Select(hour => PrtgResourceFixture.TrustedDiskHour(ModeReplayCpuPressureSensorId, hour, profile, 95)).ToArray());
        var assessment = Assert.Single(new PrtgResourcePeriodConsumer(_backend, settings)
            .EvaluateBatch([ModeReplayCpuPressureSensorId], currentCutoff, DateTime.UtcNow,
                diskReasonObservations: null, evaluationHostIds: [HostId]).Assessments);
        Assert.True(assessment.Decision.Kind != PrtgResourceDecisionKind.Insufficient,
            $"CPU fixture evidence must qualify before trial issuance; reason={assessment.Decision.ReasonCode}.");
        Assert.Equal(PrtgResourceDecisionKind.Hit, assessment.Decision.Kind);
        Assert.Equal(2, assessment.Decision.Window.Count);
        Assert.All(assessment.Decision.Window, hour =>
        {
            Assert.Equal(4, hour.GoodSlots);
            Assert.Equal(4, hour.RequiredSlots);
            Assert.Equal(95, hour.AveragePercent);
        });
        var authorization = new PrtgResourcePressureAuthorizationService(_backend, settings);
        var trial = authorization.IssueSuccessfulTrial(assessment, maintainAuthorized: true, DateTime.UtcNow);
        Assert.True(authorization.SetFormalMode(assessment, trial.TrialResultId,
            enabled: true, maintainAuthorized: true, DateTime.UtcNow));

        if (includeClosedDayEvidence)
        {
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(_completedDay.Date, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            // 26 hourly readings produce 25 adjacent windows; the closed-day consumer admits
            // only the windows whose two endpoints remain within this parent host-day boundary.
            var rows = Enumerable.Range(0, 26).Select(index => startUtc.AddHours(index - 1))
                .Select(hour => PrtgResourceFixture.TrustedDiskHour(ModeReplayCpuPressureSensorId, hour, profile, 95)).ToArray();
            store.MergeSampledValues(rows);
        }

        var mode = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(HostId)));
        return (profile, settings, mode, Assert.Single(mode.ReadHostSnapshot(HostId).ReplayJobs));
    }

    private PrtgResourcePressureModeReplayHostedService ReplayWorker(ISystemSettingsStore settings,
        bool aiAvailable = false, FakeAiService? reportAiService = null,
        IDispatchCandidateSource? candidateSource = null, IWebAiService? ai = null)
    {
        var ownerStore = new IssueOwnerStore(_backend.Blob("issue_owners"));
        ownerStore.Upsert(new IssueProfile
        {
            SourceName = "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure,
            EventId = 0,
            OwnerUserIds = [99_001]
        });
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts, ownerStore);
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        return new(_backend, settings, new BackgroundWorkGate(new SchedulerRunState(), TimeSpan.Zero),
            new DataVersionStamp(), null!, ai ?? new FakeWebAi { Available = aiAvailable }, cases, orders,
            candidateSource ?? new ModeReplayCandidateSource(HostId), _hosts, WorkflowForMode(), reportAiService ?? new FakeAiService());
    }

    private PrtgResourcePressureModeReplayHostedService ReplayWorker(ISystemSettingsStore settings,
        IWebAiService ai, IDispatchCandidateSource? candidateSource = null) =>
        ReplayWorker(settings, aiAvailable: ai.Available, candidateSource: candidateSource, ai: ai);

    private PrtgResourcePressureModeReplayProgressStore ReplayProgress() => new(_backend.Blob(
        PrtgResourcePressureModeReplayProgressStore.BlobKey(HostId)));

    private PrtgSupplementReplay SupplementReplayForMode()
    {
        var finding = Parent().TopIssues.Single(issue =>
            issue.Source == "PRTG:" + PrtgRuleEvaluator.RuleResourceCpuPressure);
        new IssueOwnerStore(_backend.Blob("issue_owners")).Upsert(new IssueProfile
        {
            SourceName = finding.Source,
            EventId = finding.EventId,
            OwnerUserIds = [99_001]
        });
        var cases = new IssueCaseCoordinator(_backend.IssueCaseStore(), _backend.IssueHandlingStore(),
            _backend.RecordHandlingStore(), _backend.RecordStore(), _hosts,
            new IssueOwnerStore(_backend.Blob("issue_owners")));
        var orders = new WorkOrderCoordinator(_backend.WorkOrderStore(), _backend.IssueCaseStore(),
            _backend.IssueHandlingStore(), cases, _backend.RecordHandlingStore(), _hosts);
        return new(_backend, _hosts, cases, orders, new ModeReplayCandidateSource(HostId));
    }

    private HostDayWorkflowService WorkflowForMode() => new(new HostDayWorkflowStore(_backend));

    private string SupplementReplayDiagnostics(PrtgResourcePressureModeReplayJob job,
        LogIssueSignature expectedFinding, int applied)
    {
        PrtgObservationRow? observation;
        using (var db = _backend.CreateContext())
            observation = db.PrtgObservations.AsNoTracking().Where(row => row.ActiveKey != null &&
                row.HostId == HostId && row.SensorObjid == ModeReplayCpuPressureSensorId &&
                row.RecordDate == _completedDay).OrderByDescending(row => row.RecordedAtUtc).FirstOrDefault();
        var caseExists = _backend.IssueCaseStore().GetOpenForHost("DISK-HOST")
            .Any(item => item.IssueKey == IssueSignatureKey.For(expectedFinding));
        var claimCount = WorkflowForMode().Get(HostId, _completedDay)?.FormalCaseStartClaims.Count ?? 0;
        return $"SupplementReplay applied={applied}; status:{observation?.SupplementStatus ?? "missing"}; " +
            $"parentCanSupplement:{Parent().CanSupplementWithPrtg()}; " +
            $"formalModeFence:{Parent().PrtgManifest?.ResourceModeFenceRequired}; " +
            $"caseExists:{caseExists}; durableCaseClaims:{claimCount}; transition:{job.TransitionId}";
    }

    private sealed class ModeReplayCandidateSource(long hostId) : IDispatchCandidateSource
    {
        public DispatchCandidatePool Build() => new()
        {
            PoolMemberCount = 1,
            ActivePoolMemberCount = 1,
            ByUserId = new Dictionary<long, DispatchCandidate>
            {
                [99001] = new()
                {
                    UserId = 99001,
                    Account = "mode-replay-handler",
                    InPool = true,
                    VisibleHostIds = new HashSet<long> { hostId }
                }
            }
        };
    }

    private sealed class FailFirstModeReplayCandidateSource(long hostId) : IDispatchCandidateSource
    {
        private readonly ModeReplayCandidateSource _inner = new(hostId);
        public int BuildCalls { get; private set; }

        public DispatchCandidatePool Build()
        {
            BuildCalls++;
            if (BuildCalls == 1) throw new InvalidOperationException("simulated dispatch receiver unavailable");
            return _inner.Build();
        }
    }

    private DailyAnalysisRecord Parent() => Assert.Single(_backend.RecordStore(
        new HostKey { HostId = HostId, HostName = "DISK-HOST" }).ReadRecent(_completedDay, 1));

    private bool AttachCpuFinding((PrtgTrustedSamplingProfile Profile, ISystemSettingsStore Settings,
        PrtgResourcePressureModeStore Mode, PrtgResourcePressureModeReplayJob Job) setup,
        LogIssueSignature finding)
    {
        var snapshot = setup.Mode.ReadHostSnapshot(HostId);
        var batch = new PrtgStateReconciliationBatch(setup.Job.SourceGeneration,
            new Dictionary<long, string>(), new Dictionary<long, string>(), new HashSet<string> { finding.EventKey },
            [new PrtgResourceGenerationFence(ModeReplayCpuPressureSensorId,
                setup.Job.SourceGeneration, setup.Job.ResourceGeneration)], snapshot.BlobVersion,
            resourceModeFenceRequired: true);
        return _backend.RecordStore().AttachPrtgModeRevocationsWithReconciliation(HostId,
            _completedDay, [finding], new HashSet<string>(), out _, aiConfigured: false, reconciliation: batch);
    }

    private LogIssueSignature CpuPressureSignature(DateTime day, PrtgTrustedSamplingProfile profile)
    {
        var rule = new KnownIssueRuleStore(_backend.Blob("rules")).Load().Content!.Rules.Single(item =>
            item.PrtgRuleCode == PrtgRuleEvaluator.RuleResourceCpuPressure);
        var finding = new PrtgFinding(93_001, ModeReplayCpuPressureSensorId,
            PrtgRuleEvaluator.RuleResourceCpuPressure, "CPU pressure retained history", 95, rule)
        {
            SourceGeneration = profile.SourceGeneration,
            ResourceGeneration = profile.ResourceGeneration,
            SensorCategory = PrtgSensorCategories.Cpu
        };
        return PrtgFindingMapper.ToSignature(finding, day, HostId);
    }

    private static string ResourceGeneration(PrtgTrustedSamplingProfile profile) => profile.ResourceGeneration;

    private void ModeReplayEnableResourceConsumerSettings()
    {
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(value =>
        {
            value.PrtgEnabled = true;
            value.PrtgUrl = _url;
            value.AutoDispatchEnabled = true;
        });
        new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Update(policy =>
        {
            policy.HostIds = policy.HostIds.Append(HostId).Distinct().ToList();
            policy.SensorIds = policy.SensorIds.Append(ModeReplayCpuPressureSensorId).Distinct().ToList();
        });
    }

    private PrtgTrustedSamplingProfile ModeReplaySeedPressureSensor(long sensorId, string name,
        string category, PrtgTrustedQuantitySemantic quantity)
    {
        var now = DateTime.UtcNow;
        var prtg = _backend.PrtgStore();
        prtg.UpsertSensors([new PrtgSensorRow
        {
            Objid = sensorId, DeviceObjid = DeviceId, Name = name, SensorType = "SNMP CPU Load",
            Category = category, Status = "Up"
        }], now);
        prtg.ReplaceHostMapForDate(_completedDay, [new PrtgHostMapRow
        {
            DeviceObjid = DeviceId, MapDate = _completedDay, HostId = HostId,
            HostName = "DISK-HOST", MapStatus = PrtgMapStatus.Ok, CreatedAt = now
        }]);
        var settings = new SystemSettingsStore(_backend.Blob("system_settings"));
        var currentSettings = settings.Get();
        var policyStore = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policyStore.Update(policy =>
        {
            policy.Revision = "mode-replay-fixture";
            policy.CoreSystemId = "mode-replay-consumer-fixture";
            if (string.IsNullOrWhiteSpace(policy.SourceGeneration))
                policy.SourceGeneration = "mode-replay-source-generation";
            policy.EndpointHint = EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl);
            if (policy.ValidFrom == default)
                policy.ValidFrom = new DateTimeOffset(_completedDay.AddDays(-31));
            policy.HostIds = policy.HostIds.Append(HostId).Distinct().ToList();
            policy.SensorIds = policy.SensorIds.Append(sensorId).Distinct().ToList();
            policy.SourceTimeZoneId = "UTC";
            policy.SourceCultureName = "en-US";
            policy.RawTimestampTimeZoneId = "UTC";
            policy.AnalysisTimeZoneId = "UTC";
            policy.TimeBasisEvidenceReference = "mode-replay-fixture-time-basis";
        });
        var policy = policyStore.Get();
        var identity = prtg.BindObservedResource(sensorId, HostId, policy.SourceGeneration,
            $"mode-replay-{sensorId}");
        identity = prtg.SetObservedChannel(sensorId, policy.SourceGeneration,
            "load|CPU Usage|%|1|direct|resource-period-v1", identity.Generation);
        var strategyDefinition = PrtgFetchStrategy.Profile(
            new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgFetchStrategy);
        var strategy = new PrtgTrustedSamplingStrategyStateStore(
            _backend.Blob(PrtgTrustedSamplingStrategyStateStore.BlobKey)).GetCurrent(policy,
            PrtgFetchStrategy.Normalize(new SystemSettingsStore(_backend.Blob("system_settings")).Get().PrtgFetchStrategy),
            strategyDefinition.SnapshotIntervalMinutes, DateTime.UtcNow.AddDays(-32));
        var observedOa = now.ToOADate();
        var sourceProfile = PrtgTrustedSamplingProfile.FromProbe(sensorId, identity, "SNMP CPU Load", "load",
            name, quantity, "%", 1, "direct", PrtgDiskAssessmentService.ParserSemanticVersion,
            strategy.StrategyFingerprint, strategy.StrategyMinutes, strategy.EffectiveFromHourUtc,
            TimeSpan.FromMinutes(strategy.StrategyMinutes), "seconds", "UTC", "UTC", "UTC",
            new DateTimeOffset(now), "mode-replay-source-metadata", "mode-replay-physical-sample", true,
            95, 95, observedOa, observedOa);
        var profile = PrtgConsumerProfileFixtureClosure.Publish(_backend, sourceProfile);
        return profile;
    }

    private static DateTime ModeReplayFloorUtcHour(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    private void SeedDisabledReplay(long hostId, long sensorId, bool addParent)
    {
        var now = DateTime.UtcNow;
        var transition = Guid.NewGuid().ToString("N");
        var mode = new PrtgResourcePressureModeStore(_backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(hostId)));
        mode.Update(document =>
        {
            document.Revocations.Add(new(hostId, sensorId, PrtgResourceFamily.Cpu,
                SourceGenerationForTest, "resource-generation-page", now, transition));
            document.ReplayJobs.Add(new(hostId, sensorId, PrtgResourceFamily.Cpu,
                SourceGenerationForTest, "resource-generation-page", false, transition,
                now, now.AddMinutes(15)));
        });
        if (!addParent) return;
        var rule = KnownIssueSeed.CreateRules().Single(item =>
            item.PrtgRuleCode == PrtgRuleEvaluator.RuleResourceCpuPressure);
        var finding = new PrtgFinding(93_101, sensorId, PrtgRuleEvaluator.RuleResourceCpuPressure,
            "CPU pressure page fixture", 95, rule)
        {
            SourceGeneration = SourceGenerationForTest,
            ResourceGeneration = "resource-generation-page",
            SensorCategory = PrtgSensorCategories.Cpu
        };
        _backend.RecordStore(new HostKey { HostId = hostId, HostName = "page-host" }).Append(
            new DailyAnalysisRecord
            {
                HostId = hostId, Host = "page-host", Date = _completedDay,
                LogSource = AnalysisLogSource.Netiq, RiskLevel = RiskLevels.High, RiskBasis = "PRTG",
                PrtgBaselineRiskLevel = RiskLevels.Low, PrtgBaselineRiskBasis = "NetIQ baseline",
                TopIssues = [PrtgFindingMapper.ToSignature(finding, _completedDay, hostId)]
            });
    }

    private const string SourceGenerationForTest = "1234567890abcdef1234567890abcdef";
    private const long ModeReplayCpuPressureSensorId = 93_002;
}
