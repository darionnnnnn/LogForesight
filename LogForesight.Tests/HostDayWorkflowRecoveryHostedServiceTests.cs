using LogForesight.Core;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

public sealed class HostDayWorkflowRecoveryHostedServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lf-workflow-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly StorageBackend _backend;

    public HostDayWorkflowRecoveryHostedServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _backend = new StorageBackend(new StorageSettings { Type = "Sqlite" }, _directory);
    }

    [Fact]
    public async Task RunSlice_usesBoundedHotFirstPagesAndResumesHotCursorAfterRestart()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value => { value.RetentionDays = 10; value.PrtgEnabled = false; });
        var requests = new List<(DateTime From, DateTime To, long After, int Take)>();
        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (from, to, after, take) =>
            {
                requests.Add((from.Date, to.Date, after, take));
                var id = after + 1;
                return new WorkflowRecoveryPage([], [new WorkflowRecoveryWaitingHostDay(id, 42, from.Date,
                    "recovery-payload-over-limit", WorkflowRecoveryPage.MaximumPayloadPrefixCharacters,
                    0, DateTime.UtcNow)],
                    1, id, WorkflowRecoveryPage.MaximumPayloadBytes);
            }
        };
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var today = new DateTime(2026, 10, 6);
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 2, 1, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var first = await CreateService(query, settings, workflow, budget).RunSliceAsync(CancellationToken.None, today);

        Assert.Equal(2, first.HotRows);
        Assert.Equal(1, first.DeepRows);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request => Assert.Equal(10, request.Take));
        Assert.Equal((today.AddDays(-3), today.AddDays(-1)), (requests[0].From, requests[0].To));
        Assert.Equal((today.AddDays(-3), today.AddDays(-1)), (requests[1].From, requests[1].To));
        Assert.Equal((today.AddDays(-10), today.AddDays(-4)), (requests[2].From, requests[2].To));
        Assert.Equal(3, first.WaitingRows);

        var restartedBudget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        await CreateService(query, settings, workflow, restartedBudget).RunSliceAsync(CancellationToken.None, today);

        Assert.Equal(2, requests[3].After); // the next process resumes, rather than rescanning the hot prefix
        Assert.Equal(10, query.WorkflowTakeRequests[0]);
        Assert.All(query.WorkflowTakeRequests, take => Assert.InRange(take, 1, WorkflowRecoveryPage.MaximumRows));
    }

    [Fact]
    public async Task RunSlice_rechecksHotWindowOnEveryScheduledCycleAfterAnEarlyPassCompletes()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value => { value.RetentionDays = 3; value.PrtgEnabled = false; });
        var calls = 0;
        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (_, _, _, _) =>
            {
                calls++;
                return new WorkflowRecoveryPage([], [], 0, null, 0, new Dictionary<long, long>());
            }
        };
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(100, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var service = CreateService(query, settings, workflow, budget);
        var today = new DateTime(2026, 10, 6);

        await service.RunSliceAsync(CancellationToken.None, today);
        await service.RunSliceAsync(CancellationToken.None, today);

        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("recovery-payload-over-limit")]
    [InlineData("recovery-payload-invalid")]
    public async Task RunSlice_marksUnreadableParentWaitingAndBlocksMailWithoutDroppingAiInFlight(string reason)
    {
        var day = new DateTime(2026, 10, 5);
        var record = AppendParent(63, day, "waiting-visible");
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, "parent", 0,
            HostDayWorkflowFingerprint.ForParentRecord(record), prtgEnabled: true, aiEnabled: true,
            parentRecordId: record.RecordId);
        var aiInput = workflow.BeginAi(record.HostId, day);
        var revision = CapturedRevision(record.RecordId);
        var capturedAtUtc = DateTime.UtcNow;
        var query = WaitingQuery(record, revision, reason, capturedAtUtc);
        var settings = RecoverySettings();
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        await CreateService(query, settings, workflow, budget)
            .RunSliceAsync(CancellationToken.None, day.AddDays(1));

        var waiting = workflow.Get(record.HostId, day)!;
        Assert.True(waiting.IsRecoveryWaiting);
        Assert.Equal(record.RecordId, waiting.RecoveryWaitingRecordId);
        Assert.Equal(revision, waiting.RecoveryWaitingWriteRevision);
        Assert.Equal(reason, waiting.RecoveryWaitingReason);
        Assert.Equal(WorkflowLegState.Waiting, waiting.Prtg);
        Assert.Equal(WorkflowLegState.Running, waiting.Ai);
        Assert.Equal(aiInput, waiting.AiInputVersion);
        Assert.Equal("pending", waiting.WorkflowOutcome);
        Assert.Null(workflow.CaptureDeliveryVersion(record.HostId, day, record.RecordId));
        Assert.True(workflow.IsRecoveryWaiting(record.HostId, day, record.RecordId));
    }

    [Fact]
    public async Task RunSlice_rejectsWaitingSnapshotWhenSqlRevisionChangesAfterQuery()
    {
        var day = new DateTime(2026, 10, 5);
        var record = AppendParent(64, day, "revision-race");
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, "parent", 0,
            HostDayWorkflowFingerprint.ForParentRecord(record), prtgEnabled: true, aiEnabled: false,
            parentRecordId: record.RecordId);
        var before = workflow.Get(record.HostId, day)!;
        var revision = CapturedRevision(record.RecordId);
        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (_, from, after, take) =>
            {
                if (after >= record.RecordId) return EmptyPage();
                var capturedAtUtc = DateTime.UtcNow;
                using (var context = _backend.CreateContext())
                {
                    var row = context.DailyRecords.Single(value => value.RecordId == record.RecordId);
                    row.HostName = "changed-after-capture";
                    context.SaveChanges();
                }
                var waiting = new WorkflowRecoveryWaitingHostDay(record.RecordId, record.HostId,
                    from.Date, "recovery-payload-over-limit", 140_000, revision, capturedAtUtc);
                return new WorkflowRecoveryPage([], [waiting], 1, record.RecordId, 1024);
            }
        };
        var settings = RecoverySettings();
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        await CreateService(query, settings, workflow, budget)
            .RunSliceAsync(CancellationToken.None, day.AddDays(1));

        var state = workflow.Get(record.HostId, day)!;
        Assert.False(state.IsRecoveryWaiting);
        Assert.Equal(before.Parent, state.Parent);
        Assert.Equal(before.ParentRecordId, state.ParentRecordId);
        Assert.Equal(before.DecisionVersion, state.DecisionVersion);
        Assert.Equal(before.DecisionFingerprint, state.DecisionFingerprint);
        Assert.Equal(before.Prtg, state.Prtg);
        Assert.Equal(before.PrtgFingerprint, state.PrtgFingerprint);
        Assert.Equal(before.PrtgEvidenceVersion, state.PrtgEvidenceVersion);
        Assert.Equal(before.Ai, state.Ai);
        Assert.Equal(before.AiInputVersion, state.AiInputVersion);
        Assert.Equal(before.AiResultVersion, state.AiResultVersion);
    }

    [Fact]
    public async Task RunSlice_clearsWaitingAfterQualifiedRecoveryForCurrentRevision()
    {
        var day = new DateTime(2026, 10, 5);
        var host = new HostStore(_backend.Blob("hosts")).Upsert(new WebHost
        {
            HostName = "waiting-recovered",
            Source = "netiq",
            Active = true,
            IpAddress = "192.0.2.65"
        });
        var record = AppendParent(host.HostId, day, host.HostName);
        var currentAuthority = ConfigureCurrentAuthority(record);
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);
        var authoritative = Assert.Single(page.Records);
        authoritative.PrtgManifest = CompleteManifest(authoritative, currentAuthority);
        authoritative.PrtgManifest.PolicyRevision = currentAuthority.PolicyRevision;
        authoritative.PrtgManifest.SourceGeneration = currentAuthority.SourceGeneration;
        authoritative.PrtgManifest.ResourceAuthorityRevision = currentAuthority.ResourceAuthorityRevision;
        authoritative.PrtgManifest.StrategyFingerprint = currentAuthority.StrategyFingerprint;
        authoritative.PrtgManifest.HostMappingFingerprint = currentAuthority.HostMappingFingerprint;
        authoritative.PrtgManifest.RuleFingerprint = currentAuthority.RuleFingerprint;
        Assert.True(currentAuthority.IsValid);
        var revision = page.CapturedWriteRevisions![authoritative.RecordId];
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(authoritative.HostId, authoritative.Host, day, "parent", 0,
            HostDayWorkflowFingerprint.ForParentRecord(authoritative), prtgEnabled: true, aiEnabled: false,
            parentRecordId: authoritative.RecordId);
        var waitCapturedAt = DateTime.UtcNow;
        var query = WaitingQuery(authoritative, revision, "recovery-payload-invalid", waitCapturedAt);
        var settings = RecoverySettings();
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var service = CreateService(query, settings, workflow, budget);

        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1));
        Assert.True(workflow.Get(authoritative.HostId, day)!.IsRecoveryWaiting);

        query.WorkflowRecoveryPageOverride = (_, _, after, _) => after < authoritative.RecordId
            ? new WorkflowRecoveryPage([authoritative], [], 1, authoritative.RecordId,
                1024, new Dictionary<long, long> { [authoritative.RecordId] = revision })
            : EmptyPage();
        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1)); // complete the page pass
        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1)); // next hot pass reads valid JSON

        var recovered = workflow.Get(authoritative.HostId, day)!;
        Assert.False(recovered.IsRecoveryWaiting);
        Assert.Null(recovered.RecoveryWaitingReason);
        Assert.Equal(WorkflowLegState.Succeeded, recovered.Prtg);
    }

    [Fact]
    public async Task RunSlice_doesNotMarkResourcePassOverdueWhenWholeDailyEvidenceAndManifestAreMissing()
    {
        var day = DateTime.Today.AddDays(-1);
        const long hostId = 67;
        var parentAtUtc = DateTime.UtcNow.AddHours(-1);
        _backend.RecordStore().Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = "readiness-overdue", Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = parentAtUtc, AuditEventCount = 0, AiAnalyzed = false
        });
        var page = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10);
        var record = Assert.Single(page.Records);
        var revision = page.CapturedWriteRevisions![record.RecordId];
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(hostId, record.Host, day, record.LatestNetiqAttemptAtUtc!.Value.ToString("O"),
            0, HostDayWorkflowFingerprint.ForParentRecord(record), prtgEnabled: true, aiEnabled: false,
            now: parentAtUtc.ToLocalTime(), parentRecordId: record.RecordId);
        var epoch = Hash("overdue-current-source-epoch");
        var selected = Hash("overdue-current-selected-sensors");
        workflow.BeginPrtgReadiness(hostId, day, epoch, selected,
            [PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowResourceFamily.Disk]);
        workflow.PublishPrtgResourceAssessment(hostId, day, epoch, selected,
            PrtgWorkflowResourceFamily.Cpu, PrtgWorkflowAssessment.NoHit);
        workflow.PublishPrtgResourceAssessment(hostId, day, epoch, selected,
            PrtgWorkflowResourceFamily.Disk, PrtgWorkflowAssessment.Recovery);
        workflow.ClosePrtgSelectedSensors(hostId, day, epoch, selected);

        var ready = workflow.Get(hostId, day)!;
        Assert.True(ready.PrtgReadinessComplete);
        Assert.Null(ready.PrtgEvidenceReadyAt);
        Assert.Null(ready.SupplementDueAt);
        var deliveryVersion = workflow.CaptureDeliveryVersion(hostId, day, record.RecordId)!;
        workflow.RecordCaseIntents(hostId, day, ["incident-67"], deliveryVersion);
        workflow.RecordMail(hostId, day, ["summary:overdue"],
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            { ["summary:overdue"] = ["recipient-part"] }, [], [], deliveryVersion);
        var beforeRecovery = workflow.Get(hostId, day)!;
        var expectedDecisionVersion = beforeRecovery.DecisionVersion;
        var expectedAi = beforeRecovery.Ai;
        var expectedCaseIntents = beforeRecovery.CaseIntents.ToArray();
        var expectedMailIntents = beforeRecovery.MailIntents.ToArray();

        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (from, to, after, _) =>
                from.Date <= day && to.Date >= day && after < record.RecordId
                    ? new WorkflowRecoveryPage([record], [], 1, record.RecordId, 1024,
                        new Dictionary<long, long> { [record.RecordId] = revision })
                    : EmptyPage()
        };
        var settings = RecoverySettings();
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        await CreateService(query, settings, workflow, budget)
            .RunSliceAsync(CancellationToken.None, day.AddDays(1));

        var waiting = workflow.Get(hostId, day)!;
        Assert.Equal(WorkflowLegState.Running, waiting.Prtg);
        Assert.NotEqual(WorkflowLegState.Overdue, waiting.Prtg);
        Assert.Null(waiting.PrtgEvidenceReadyAt);
        Assert.Null(waiting.SupplementDueAt);
        Assert.Equal(expectedDecisionVersion, waiting.DecisionVersion);
        Assert.Equal(expectedAi, waiting.Ai);
        Assert.Equal(expectedCaseIntents, waiting.CaseIntents);
        Assert.Equal(expectedMailIntents, waiting.MailIntents);
    }

    [Fact]
    public async Task RunSlice_repeatedWaitingSnapshotDoesNotRewriteWorkflowBlob()
    {
        var day = new DateTime(2026, 10, 5);
        var record = AppendParent(66, day, "waiting-repeat");
        var revision = CapturedRevision(record.RecordId);
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        workflow.ParentSucceeded(record.HostId, record.Host, day, "parent", 0,
            HostDayWorkflowFingerprint.ForParentRecord(record), prtgEnabled: true, aiEnabled: false,
            parentRecordId: record.RecordId);
        var query = new FakeAnalysisRecordQuery
        {
            WorkflowRecoveryPageOverride = (_, from, after, _) => after >= record.RecordId ? EmptyPage() :
                new WorkflowRecoveryPage([], [new WorkflowRecoveryWaitingHostDay(record.RecordId, record.HostId,
                    from.Date, "recovery-payload-invalid", 140_000, revision, DateTime.UtcNow)],
                    1, record.RecordId, 1024)
        };
        var settings = RecoverySettings();
        var budget = new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var service = CreateService(query, settings, workflow, budget);

        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1));
        var key = $"workflow_{record.HostId}_{day:yyyyMMdd}";
        var versionAfterFirstMark = _backend.Blob(key).ReadVersion();
        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1)); // finish pass
        await service.RunSliceAsync(CancellationToken.None, day.AddDays(1)); // same waiting row, same SQL revision

        Assert.Equal(versionAfterFirstMark, _backend.Blob(key).ReadVersion());
    }

    [Fact]
    public void PendingReport_isRetriedByRecoveryConsumerFromPersistedParentWithoutAi()
    {
        var day = new DateTime(2026, 10, 5);
        var pending = new DailyAnalysisRecord
        {
            HostId = 67,
            Host = "report-retry-recovery",
            Date = day,
            RiskLevel = RiskLevels.High,
            LogSource = AnalysisLogSource.Netiq,
            LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10),
            RiskReportPending = true,
            AiPending = true,
            Headline = "統計風險待補報告",
            Summary = "已保存的主機日需補上正式風險報告。",
            Action = "檢查受影響資源。"
        };
        _backend.RecordStore().Append(pending);
        var saved = Assert.Single(_backend.RecordStore()
            .QueryWorkflowRecoveryPage(day, day, 0, 10).Records);
        Assert.True(saved.RiskReportPending);
        Assert.Null(saved.ReportFile);

        var ai = new RecoveryFakeAiService();
        var reportAi = new FakeAiService();
        var workflow = new HostDayWorkflowService(new HostDayWorkflowStore(_backend));
        var worker = new HostDayWorkflowRecoveryHostedService(
            (IAnalysisRecordQuery)_backend.RecordStore(), _backend, RecoverySettings(), ai, workflow,
            new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)), reportAiService: reportAi);

        Assert.Equal(1, worker.RetryPendingReports([saved], CancellationToken.None));

        var completed = Assert.Single(_backend.RecordStore()
            .QueryWorkflowRecoveryPage(day, day, 0, 10).Records);
        Assert.False(completed.RiskReportPending);
        Assert.True(completed.AiPending); // report retry neither consumes nor clears the separate AI work marker
        Assert.NotNull(completed.ReportFile);
        Assert.Equal(HostDayWorkflowFingerprint.PrtgInputFingerprint(completed),
            completed.PrtgReportEvidenceFingerprint);
        Assert.NotNull(_backend.ReportStore().Read(
            new HostKey { HostId = completed.HostId, HostName = completed.Host },
            day, ReportKinds.DailyRisk));
        Assert.False(ai.Available);
        Assert.Equal(0, reportAi.Calls);
    }

    [Fact]
    public void PendingReport_waitsForAiConsumerWhenProviderIsAvailable()
    {
        var webAi = new RecoveryFakeAiService { Available = true };
        var reportAi = new FakeAiService();
        var worker = new HostDayWorkflowRecoveryHostedService(
            new FakeAnalysisRecordQuery(), _backend, RecoverySettings(), webAi,
            new HostDayWorkflowService(new HostDayWorkflowStore(_backend)),
            new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)), reportAiService: reportAi);
        var pending = new DailyAnalysisRecord { RecordId = 1000, RiskReportPending = true, AiPending = true };

        Assert.Equal(0, worker.RetryPendingReports([pending], CancellationToken.None));

        Assert.True(pending.RiskReportPending);
        Assert.True(pending.AiPending);
        Assert.Equal(0, webAi.Calls);
        Assert.Equal(0, reportAi.Calls);
    }

    [Fact]
    public void PendingReport_retryRotatesPastRepeatedFailuresAndResumesDeterministicallyAfterRestart()
    {
        var day = new DateTime(2026, 10, 5);
        var persisted = new List<DailyAnalysisRecord>();
        for (var index = 0; index < 6; index++)
        {
            var record = new DailyAnalysisRecord
            {
                HostId = 700 + index,
                Host = $"report-retry-fairness-{index}",
                Date = day,
                RiskLevel = RiskLevels.High,
                LogSource = AnalysisLogSource.Netiq,
                LatestNetiqAttemptStatus = "success",
                LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10),
                RiskReportPending = true,
                AiPending = true,
                Headline = "統計風險待補報告",
                Summary = "已保存的主機日需補上正式風險報告。",
                Action = "檢查受影響資源。"
            };
            _backend.RecordStore().Append(record);
            persisted.Add(record);
        }

        var firstPass = HostDayWorkflowRecoveryHostedService.SelectPendingReports(persisted, 0, aiAvailable: false);
        var restartedPass = HostDayWorkflowRecoveryHostedService.SelectPendingReports(persisted.AsEnumerable().Reverse().ToArray(), 1,
            aiAvailable: false);
        Assert.Equal(persisted.Take(4).Select(record => record.RecordId),
            firstPass.Select(record => record.RecordId));
        Assert.Equal(persisted.Skip(4).Concat(persisted.Take(2)).Select(record => record.RecordId),
            restartedPass.Select(record => record.RecordId));
        Assert.Equal(restartedPass.Select(record => record.RecordId),
            HostDayWorkflowRecoveryHostedService.SelectPendingReports(persisted, 1, aiAvailable: false)
                .Select(record => record.RecordId));

        using (var context = _backend.CreateContext())
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRaw(context.Database, $"""
                CREATE TRIGGER fail_first_four_report_parents BEFORE UPDATE ON lf_daily_records
                WHEN OLD.record_id IN ({string.Join(",", persisted.Take(4).Select(record => record.RecordId))})
                BEGIN SELECT RAISE(ABORT, 'fixture keeps first report rows failing'); END;
                """);

        HostDayWorkflowRecoveryHostedService CreateWorker() => new(
            (IAnalysisRecordQuery)_backend.RecordStore(), _backend, RecoverySettings(),
            new RecoveryFakeAiService(), new HostDayWorkflowService(new HostDayWorkflowStore(_backend)),
            new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)), reportAiService: new FakeAiService());

        var firstWorker = CreateWorker();
        Assert.Equal(0, firstWorker.RetryPendingReports(persisted, CancellationToken.None, passNumber: 0));
        Assert.All(persisted, record => Assert.True(record.RiskReportPending));

        // A newly created consumer uses the durable next pass ordinal. The first four continue
        // failing, while the rotated window still finalizes records five and six.
        var restartedWorker = CreateWorker();
        Assert.Equal(2, restartedWorker.RetryPendingReports(persisted, CancellationToken.None, passNumber: 1));

        var saved = _backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10).Records
            .OrderBy(record => record.HostId).ToArray();
        Assert.Equal(persisted.Take(4).Select(record => record.HostId),
            saved.Take(4).Select(record => record.HostId));
        Assert.All(saved.Take(4), record =>
        {
            Assert.True(record.RiskReportPending);
            Assert.Null(record.ReportFile);
        });
        Assert.All(saved.Skip(4), record =>
        {
            Assert.False(record.RiskReportPending);
            Assert.NotNull(record.ReportFile);
        });
    }

    private DailyAnalysisRecord AppendParent(long hostId, DateTime day, string host)
    {
        _backend.RecordStore().Append(new DailyAnalysisRecord
        {
            HostId = hostId, Host = host, Date = day, RiskLevel = RiskLevels.Low,
            LogSource = AnalysisLogSource.Netiq, LatestNetiqAttemptStatus = "success",
            LatestNetiqAttemptAtUtc = DateTime.UtcNow.AddMinutes(-10), AuditEventCount = 0
        });
        return Assert.Single(_backend.RecordStore().QueryWorkflowRecoveryPage(day, day, 0, 10).Records);
    }

    private long CapturedRevision(long recordId) => _backend.RecordStore()
        .QueryWorkflowRecoveryPage(new DateTime(2026, 10, 5), new DateTime(2026, 10, 5), 0, 10)
        .CapturedWriteRevisions![recordId];

    private static FakeSystemSettingsStore RecoverySettings()
    {
        var settings = new FakeSystemSettingsStore();
        settings.Update(value => { value.RetentionDays = 10; value.PrtgEnabled = true; });
        return settings;
    }

    private static FakeAnalysisRecordQuery WaitingQuery(DailyAnalysisRecord record, long revision,
        string reason, DateTime capturedAtUtc) => new()
    {
        WorkflowRecoveryPageOverride = (_, from, after, _) => after >= record.RecordId ? EmptyPage() :
            new WorkflowRecoveryPage([], [new WorkflowRecoveryWaitingHostDay(record.RecordId, record.HostId,
                from.Date, reason, 140_000, revision, capturedAtUtc)], 1, record.RecordId, 1024)
    };

    private static WorkflowRecoveryPage EmptyPage() => new([], [], 0, null, 0,
        new Dictionary<long, long>());

    private static PrtgDecisionManifest CompleteManifest(DailyAnalysisRecord record,
        PrtgWholeEvidenceAuthority? authority = null) => new()
    {
        Version = 1, ParentRecordId = record.RecordId,
        ParentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(record),
        ParentFindingFingerprint = PrtgFindingMapper.Fingerprint(record.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
        SourceGeneration = "recovery-test", HostMappingFingerprint = Hash("host-map"),
        ResourceFingerprint = Hash("resource"), SemanticFingerprint = Hash("semantic"),
        StrategyFingerprint = Hash("strategy"), RuleFingerprint = Hash("rules"),
        EvidenceFingerprint = Hash("evidence"),
        ResourceModeBlobVersion = authority?.ResourceModeBlobVersion ?? -1,
        ResourceModeFenceRequired = authority?.ResourceModeFenceRequired ?? false,
        FindingFingerprint = PrtgFindingMapper.Fingerprint(record.TopIssues.Where(PrtgFindingMapper.IsPrtg)),
        CompletedAtUtc = DateTime.UtcNow, Outcome = "complete"
    };

    private PrtgWholeEvidenceAuthority ConfigureCurrentAuthority(DailyAnalysisRecord record)
    {
        const string url = "https://prtg-recovery.example.invalid/";
        const long deviceId = 6501;
        const long sensorId = 6502;
        new SystemSettingsStore(_backend.Blob("system_settings")).Update(settings =>
        {
            settings.PrtgEnabled = true;
            settings.PrtgUrl = url;
        });
        var policy = new PrtgMonitoringPolicyStore(_backend.Blob(PrtgMonitoringPolicyStore.BlobKey));
        policy.Update(value =>
        {
            value.Revision = "recovery-policy-v1";
            value.CoreSystemId = "recovery-core";
            value.SourceGeneration = "recovery-test";
            value.EndpointHint = LogForesight.Core.Persistence.Sql.EfPrtgObservationStore.SourceHintFor(url);
            value.ValidFrom = new DateTimeOffset(record.Date.Date.AddDays(-30));
            value.HostIds = [record.HostId];
            value.SensorIds = [sensorId];
            value.SourceTimeZoneId = "UTC";
            value.SourceCultureName = "en-US";
            value.RawTimestampTimeZoneId = "UTC";
            value.AnalysisTimeZoneId = "UTC";
            value.TimeBasisEvidenceReference = "recovery-authority-fixture";
        });
        var prtgStore = _backend.PrtgStore();
        var now = DateTime.Now;
        prtgStore.UpsertDevices([new LogForesight.Core.Persistence.Sql.PrtgDeviceRow
        {
            Objid = deviceId, Name = record.Host, Ip = "192.0.2.65"
        }], now);
        prtgStore.UpsertSensors([new LogForesight.Core.Persistence.Sql.PrtgSensorRow
        {
            Objid = sensorId, DeviceObjid = deviceId, Name = "recovery-ping", SensorType = "ping",
            Category = PrtgSensorCategories.Availability
        }], now);
        prtgStore.ReplaceHostMapForDate(record.Date,
            [new LogForesight.Core.Persistence.Sql.PrtgHostMapRow
            {
                DeviceObjid = deviceId, MapDate = record.Date.Date, HostId = record.HostId,
                MapStatus = PrtgMapStatus.Ok
            }]);
        prtgStore.EnsureResourceAuthorityRevisions([record.HostId]);

        var currentService = new HostDayWorkflowRecoveryHostedService(
            (IAnalysisRecordQuery)_backend.RecordStore(), _backend, RecoverySettings(),
            new RecoveryFakeAiService(), new HostDayWorkflowService(new HostDayWorkflowStore(_backend)),
            new HostDayWorkflowRecoveryHostedService.RecoveryBudget(10, 1, 0, 0,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));
        var candidate = CompleteManifest(record);
        record.PrtgManifest = candidate;
        var authority = currentService.CaptureCurrentWholeEvidenceAuthorities([record])[record.RecordId];
        return authority ?? throw new InvalidOperationException("Test fixture did not establish current PRTG whole-evidence authority.");
    }

    private static string Hash(string value) => HostDayWorkflowFingerprint.HashParts([value]);

    private HostDayWorkflowRecoveryHostedService CreateService(IAnalysisRecordQuery query,
        ISystemSettingsStore settings, HostDayWorkflowService workflow,
        HostDayWorkflowRecoveryHostedService.RecoveryBudget budget) =>
        new(query, _backend, settings, new RecoveryFakeAiService(), workflow, budget);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class RecoveryFakeAiService : IWebAiService
    {
        public bool Available { get; set; }
        public int Calls { get; private set; }
        public Task<T?> GenerateAsync<T>(string cacheKey, string systemPrompt, string userPrompt) where T : class
        { Calls++; return Task.FromResult<T?>(null); }
        public Task<string?> ChatOnceAsync(string systemPrompt, string userPrompt)
        { Calls++; return Task.FromResult<string?>(null); }
    }
}
