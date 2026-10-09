using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using LogForesight.Core.Analysis;
using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services.Mail;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>Resumes durable mode transitions without changing the mode-authority blob.</summary>
public sealed class PrtgResourcePressureModeReplayHostedService(StorageBackend backend,
    ISystemSettingsStore settings, BackgroundWorkGate gate, DataVersionStamp stamp,
    MailNotificationService mail, IWebAiService ai, IssueCaseCoordinator caseCoordinator,
    WorkOrderCoordinator workOrders, IDispatchCandidateSource candidateSource, IHostStore hosts,
    HostDayWorkflowService workflow, IAiService? reportAiService = null) : BackgroundService
{
    private const int HostPageSize = 20;
    private const int DayPageSize = 20;
    private const int JobsPerHostSlice = 8;
    private static readonly TimeSpan SliceBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly NamedMutexGate _lease = new(@"Global\LogForesight.PrtgModeReplay");
    internal Action? BeforeReconciliationForTesting { get; set; }
    internal Action? BeforeCaseHandoffForTesting { get; set; }
    internal Action? BeforeReplayReportForTesting { get; set; }
    private readonly IAiService? _reportAiService = reportAiService;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = 0;
                var notifyMail = false;
                await gate.RunAsync("PRTG 正式模式撤回與重評", () => Task.Run(() =>
                {
                    var acquired = _lease.RunExclusiveAsync(() => Task.Run(() =>
                    {
                        var result = RunOnce(stoppingToken);
                        changed = result.Changed;
                        notifyMail = result.NotifyMail;
                        if (changed > 0) stamp.Bump();
                    }, stoppingToken), TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    if (!acquired) Log.Debug("PRTG mode replay lease is held elsewhere; this slice will retry later");
                }, stoppingToken), stoppingToken);
                if (notifyMail) await mail.NotifyAfterRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* Shared gate yielded to scheduled or manual work. */ }
            catch (Exception ex) { Log.Warn(ex, "PRTG resource-mode replay paused; persisted cursor will resume on the next slice"); }
            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal (int Changed, bool NotifyMail) RunOnce(CancellationToken ct)
    {
        var records = (IAnalysisRecordQuery)backend.RecordStore();
        var changed = 0;
        var notifyMail = false;
        NightlyDispatch? dispatch = null;
        var hostCursorStore = new PrtgResourcePressureModeReplayHostCursorStore(
            backend.Blob("prtg_resource_pressure_replay_host_cursor"));
        string? hostCursor = hostCursorStore.Get();
        var timer = Stopwatch.StartNew();
        var budgetExhausted = false;
        for (var hostPage = 0; hostPage < 4 && !ct.IsCancellationRequested && !budgetExhausted; hostPage++)
        {
            var page = ReadReplayHostsPageIsolated(hostCursor, HostPageSize);
            foreach (var host in page.Hosts)
            {
                ct.ThrowIfCancellationRequested();
                if (timer.Elapsed >= SliceBudget) { budgetExhausted = true; break; }
                var progressStore = new PrtgResourcePressureModeReplayProgressStore(
                    backend.Blob(PrtgResourcePressureModeReplayProgressStore.BlobKey(host.HostId)));
                int jobCount;
                int jobOffset;
                try
                {
                    jobCount = host.ReplayJobs.Count;
                    jobOffset = progressStore.GetNextJobOffset(jobCount);
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    Log.Warn(ex, "PRTG mode replay progress is unreadable for host {HostId}; skipping this host for a fair retry", host.HostId);
                    hostCursor = PrtgResourcePressureModeStore.BlobKey(host.HostId);
                    hostCursorStore.Set(hostCursor);
                    continue;
                }
                var jobs = Enumerable.Range(0, Math.Min(JobsPerHostSlice, jobCount))
                    .Select(index => host.ReplayJobs[(jobOffset + index) % jobCount]).ToArray();
                WebHost? replayWebHost = null;
                if (jobs.Length > 0)
                {
                    try { replayWebHost = hosts.GetAll().FirstOrDefault(item => item.HostId == host.HostId); }
                    catch (Exception ex) { Log.Warn(ex, "Could not load host display context for PRTG replay host {HostId}", host.HostId); }
                }
                var attemptedJobs = 0;
                foreach (var job in jobs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (timer.Elapsed >= SliceBudget) { budgetExhausted = true; break; }
                    attemptedJobs++;
                    var progress = progressStore.Find(job);
                    long faultRecordCursor = progress?.AfterRecordId ?? 0;
                    try
                    {
                    if (progress?.Status == PrtgModeReplayStatus.Completed && progress.TransitionId == job.TransitionId)
                        continue;
                    if (progress?.Status == PrtgModeReplayStatus.Superseded && progress.TransitionId == job.TransitionId)
                        continue;
                    var current = CaptureCurrentJob(job);
                    if (current is null)
                    {
                        progressStore.Update(job, old => old with { Status = PrtgModeReplayStatus.Superseded,
                            LastReason = "mode-transition-superseded", CompletedAtUtc = DateTime.UtcNow });
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(job.SourceGeneration) || string.IsNullOrWhiteSpace(job.ResourceGeneration))
                    {
                        var due = IsOverdue(job.DeadlineArmed ? job.DueAtUtc : progress?.DueAtUtc, DateTime.UtcNow);
                        progressStore.Update(job, old => old with
                        {
                            Status = due ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting,
                            LastReason = "unknown-generation-scope",
                            CompletedAtUtc = null
                        });
                        continue;
                    }
                    if (!Guid.TryParseExact(job.SourceGeneration, "N", out _))
                    {
                        var due = IsOverdue(job.DeadlineArmed ? job.DueAtUtc : progress?.DueAtUtc, DateTime.UtcNow);
                        progressStore.Update(job, old => old with
                        {
                            Status = due ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting,
                            LastReason = "source-generation-unrecognized",
                            CompletedAtUtc = null
                        });
                        continue;
                    }
                    var before = progress?.AfterRecordId ?? 0;
                    var days = records.QueryModeReplayHostPage(job.HostId, DateTime.MinValue, DateTime.MaxValue,
                        before, DayPageSize);
                    if (days.Count == 0)
                    {
                        var unresolved = progress?.UnresolvedHostDays ?? 0;
                        var status = unresolved == 0 && (progress?.AfterRecordId ?? 0) > 0 ? PrtgModeReplayStatus.Completed :
                            IsOverdue(job.DeadlineArmed ? job.DueAtUtc : progress?.DueAtUtc, DateTime.UtcNow)
                                ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting;
                        progressStore.Update(job, old => old with
                        {
                            Status = status,
                            AfterRecordId = 0,
                            UnresolvedHostDays = 0,
                            LastReason = status == PrtgModeReplayStatus.Completed ? null :
                                unresolved > 0 ? $"{unresolved}-host-days-waiting" : "no-persisted-host-days",
                            CompletedAtUtc = status == PrtgModeReplayStatus.Completed ? DateTime.UtcNow : null
                        });
                        continue;
                    }

                    var lastRecordId = before;
                    var waiting = 0;
                    string? waitingReason = null;
                    var applied = 0;
                    var supersededDuringSweep = false;
                    DateTime? evidenceReadyAt = null;
                    foreach (var day in days)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (timer.Elapsed >= SliceBudget) { budgetExhausted = true; break; }
                        var parentPage = records.QueryWorkflowRecoveryPage(day.Date, day.Date,
                            day.RecordId - 1, 1);
                        var record = parentPage.Records.FirstOrDefault(row => row.RecordId == day.RecordId &&
                            row.HostId == job.HostId && row.Date.Date == day.Date.Date);
                        if (record is null || !record.CanSupplementWithPrtg())
                        {
                            waiting++;
                            lastRecordId = day.RecordId; // move past this day; the next sweep restarts at zero
                            continue;
                        }
                        var attempt = CaptureCurrentJob(job);
                        if (attempt is null)
                        {
                            supersededDuringSweep = true;
                            progressStore.Update(job, old => old with
                            {
                                Status = PrtgModeReplayStatus.Superseded,
                                LastReason = "mode-transition-superseded-during-sweep",
                                CompletedAtUtc = DateTime.UtcNow
                            });
                            break;
                        }
                        if (job.Enabled)
                        {
                            var qualified = TryReevaluateEnabled(job, record, out var didChange,
                                out var qualifiedFindingPublished, out var evidenceReadyForDay,
                                out var qualifiedSignatures);
                            evidenceReadyAt ??= evidenceReadyForDay;
                            if (!qualified)
                            {
                                waiting++;
                                lastRecordId = day.RecordId;
                                continue;
                            }
                            if (didChange) applied++;
                            if (qualifiedFindingPublished)
                            {
                                if (!ai.Available)
                                {
                                    var replayedParent = backend.RecordStore(new HostKey
                                        { HostId = job.HostId, HostName = record.Host })
                                        .ReadRecent(record.Date, 1)
                                        .FirstOrDefault(item => item.RecordId == record.RecordId);
                                    if (replayedParent is null || replayWebHost is null ||
                                        !FinalizeReplayReport(replayedParent, replayWebHost, ct, resourceModeDisabled: false))
                                    {
                                        waiting++;
                                        waitingReason = "mode-replay-report-finalization-pending";
                                        break; // Keep the durable cursor before this day so report generation retries.
                                    }
                                }
                                dispatch ??= CreateCaseDispatch();
                                BeforeCaseHandoffForTesting?.Invoke();
                                if (replayWebHost is null || !HostDayPostProcessor.AttachCase(caseCoordinator, dispatch,
                                        replayWebHost.HostName, record.Date, qualifiedSignatures.ToList(),
                                        "[PRTG mode replay] ", workflow, job.HostId, record.RecordId))
                                {
                                    waiting++;
                                    lastRecordId = day.RecordId;
                                    continue;
                                }
                                notifyMail = true;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(job.SourceGeneration) || string.IsNullOrWhiteSpace(job.ResourceGeneration))
                            {
                                waiting++;
                                lastRecordId = day.RecordId;
                                continue;
                            }
                            var batch = new PrtgStateReconciliationBatch(job.SourceGeneration,
                                new Dictionary<long, string>(), new Dictionary<long, string>(), new HashSet<string>(),
                                [new PrtgResourceGenerationFence(job.SensorObjid, job.SourceGeneration, job.ResourceGeneration)],
                                attempt.Value.Version, resourceModeFenceRequired: true,
                                expectedParentRecordId: record.RecordId,
                                expectedParentFingerprint: HostDayWorkflowFingerprint.ForParentRecord(record),
                                expectedParentFindingFingerprint: PrtgFindingMapper.Fingerprint(
                                    record.TopIssues.Where(PrtgFindingMapper.IsPrtg)));
                            var committed = backend.RecordStore().AttachPrtgModeRevocationsWithReconciliation(
                                job.HostId, day.Date, [], new HashSet<string>(), out _, aiConfigured: ai.Available,
                                reconciliation: batch);
                            if (!committed)
                            {
                                waiting++;
                                lastRecordId = day.RecordId;
                                continue;
                            }
                            var reconciledParent = backend.RecordStore(new HostKey
                                { HostId = job.HostId, HostName = record.Host })
                                .ReadRecent(day.Date, 1)
                                .FirstOrDefault(item => item.RecordId == record.RecordId);
                            if (reconciledParent is null)
                            {
                                waiting++;
                                waitingReason = "mode-replay-parent-unavailable-after-revocation";
                                break;
                            }
                            if (!ai.Available && RiskLevels.IsActionable(reconciledParent.RiskLevel))
                            {
                                var currentReportInput = HostDayWorkflowFingerprint.PrtgInputFingerprint(reconciledParent);
                                var prtgAfter = PrtgFindingMapper.Fingerprint(
                                    reconciledParent.TopIssues.Where(PrtgFindingMapper.IsPrtg));
                                var prtgBefore = PrtgFindingMapper.Fingerprint(
                                    record.TopIssues.Where(PrtgFindingMapper.IsPrtg));
                                var needsReport = !StringComparer.Ordinal.Equals(prtgBefore, prtgAfter) ||
                                    reconciledParent.ReportFile is null ||
                                    !StringComparer.Ordinal.Equals(reconciledParent.PrtgReportEvidenceFingerprint,
                                        currentReportInput);
                                if (needsReport && (replayWebHost is null ||
                                    !FinalizeReplayReport(reconciledParent, replayWebHost, ct,
                                        resourceModeDisabled: true)))
                                {
                                    waiting++;
                                    waitingReason = "mode-replay-report-finalization-pending";
                                    break;
                                }
                            }
                            applied++;
                        }
                        lastRecordId = day.RecordId;
                        faultRecordCursor = day.RecordId;
                    }

                    if (supersededDuringSweep) continue;

                    changed += applied;
                    var now = DateTime.UtcNow;
                    var pageFull = days.Count == DayPageSize;
                    var unresolvedAfterSweep = Math.Min(100_000,
                        (progressStore.Find(job)?.UnresolvedHostDays ?? 0) + waiting);
                    var unresolvedAtEnd = !pageFull && unresolvedAfterSweep > 0;
                    var effectiveDueAt = job.DeadlineArmed ? job.DueAtUtc :
                        progress?.DueAtUtc ?? evidenceReadyAt?.AddMinutes(15);
                    var overdue = IsOverdue(effectiveDueAt, now);
                    var nextStatus = budgetExhausted ? PrtgModeReplayStatus.Running : unresolvedAtEnd
                        ? overdue ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting
                        : waiting > 0
                            ? (overdue ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting)
                            : pageFull ? PrtgModeReplayStatus.Running : PrtgModeReplayStatus.Completed;
                    progressStore.Update(job, old => old with
                    {
                        Status = nextStatus,
                        AfterRecordId = pageFull || budgetExhausted ? lastRecordId : 0,
                        UnresolvedHostDays = pageFull || budgetExhausted
                            ? Math.Min(100_000, old.UnresolvedHostDays + waiting) : 0,
                        EvidenceReadyAtUtc = old.EvidenceReadyAtUtc ?? evidenceReadyAt,
                        DueAtUtc = old.DueAtUtc ?? (job.DeadlineArmed ? job.DueAtUtc :
                            (old.EvidenceReadyAtUtc ?? evidenceReadyAt)?.AddMinutes(15)),
                        LastReason = waiting > 0 ? waitingReason ?? (job.Enabled ? $"{waiting}-qualified-parent-or-closed-day-pending" :
                            $"{waiting}-parent-row-not-reconcilable") : unresolvedAtEnd
                                ? $"{unresolvedAfterSweep}-host-days-waiting" : null,
                        StartedAtUtc = old.StartedAtUtc ?? now,
                        CompletedAtUtc = nextStatus == PrtgModeReplayStatus.Completed ? now : null
                    });
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "PRTG mode replay job failed for host {HostId}, sensor {SensorObjid}; isolating it for retry",
                            job.HostId, job.SensorObjid);
                        try
                        {
                            progressStore.Update(job, old => old with
                            {
                                Status = IsOverdue(job.DeadlineArmed ? job.DueAtUtc : old.DueAtUtc, DateTime.UtcNow)
                                    ? PrtgModeReplayStatus.Overdue : PrtgModeReplayStatus.Waiting,
                                AfterRecordId = faultRecordCursor,
                                LastReason = "replay-job-failed-see-log",
                                CompletedAtUtc = null
                            });
                        }
                        catch (Exception progressEx)
                        {
                            Log.Warn(progressEx, "Could not persist isolated PRTG mode replay failure for host {HostId}", job.HostId);
                        }
                    }
                }
                if (jobCount > 0)
                {
                    try { progressStore.SetNextJobOffset((jobOffset + Math.Max(1, attemptedJobs)) % jobCount); }
                    catch (Exception ex) { Log.Warn(ex, "Could not persist PRTG replay round-robin cursor for host {HostId}", host.HostId); }
                }
                hostCursor = PrtgResourcePressureModeStore.BlobKey(host.HostId);
                hostCursorStore.Set(hostCursor);
                if (budgetExhausted) break;
            }
            if (budgetExhausted) break;
            hostCursor = page.RawCount < HostPageSize ? null : page.NextCursor;
            hostCursorStore.Set(hostCursor);
            if (page.RawCount < HostPageSize) break;
        }
        return (changed, notifyMail);
    }

    private PrtgResourcePressureModeHostPage ReadReplayHostsPageIsolated(string? afterKey, int limit)
    {
        const string prefix = "prtg_resource_pressure_modes_";
        using var ctx = backend.CreateContext();
        var keys = ctx.Blobs.AsNoTracking().Where(row => row.BlobKey.StartsWith(prefix) &&
                (afterKey == null || string.Compare(row.BlobKey, afterKey) > 0))
            .OrderBy(row => row.BlobKey).Select(row => row.BlobKey).Take(limit).ToArray();
        var hosts = new List<PrtgResourcePressureModeHostSnapshot>(keys.Length);
        foreach (var key in keys)
        {
            if (!long.TryParse(key.AsSpan(prefix.Length), out var hostId) || hostId <= 0) continue;
            try
            {
                hosts.Add(new PrtgResourcePressureModeStore(backend.Blob(key)).ReadHostSnapshot(hostId));
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "PRTG mode authority is unreadable for host {HostId}; continuing to later hosts", hostId);
            }
        }
        return new(hosts, keys.LastOrDefault(), keys.Length);
    }

    private static bool IsOverdue(DateTime? dueAtUtc, DateTime nowUtc) =>
        dueAtUtc.HasValue && dueAtUtc.Value.Kind == DateTimeKind.Utc && nowUtc >= dueAtUtc.Value;

    private (long Version, PrtgResourcePressureModeHostSnapshot Snapshot)? CaptureCurrentJob(
        PrtgResourcePressureModeReplayJob job)
    {
        var snapshot = new PrtgResourcePressureModeStore(backend.Blob(
            PrtgResourcePressureModeStore.BlobKey(job.HostId))).ReadHostSnapshot(job.HostId);
        var exact = job.Enabled
            ? snapshot.Grants.Any(grant => grant.SensorObjid == job.SensorObjid && grant.Family == job.Family &&
                grant.FormalEnabled && grant.SourceGeneration == job.SourceGeneration &&
                grant.ResourceGeneration == job.ResourceGeneration && grant.ModeTransitionId == job.TransitionId)
            : snapshot.Revocations.Any(marker => marker.SensorObjid == job.SensorObjid && marker.Family == job.Family &&
                marker.SourceGeneration == job.SourceGeneration && marker.ResourceGeneration == job.ResourceGeneration &&
                marker.ModeTransitionId == job.TransitionId);
        return exact ? (snapshot.BlobVersion, snapshot) : null;
    }

    private bool TryReevaluateEnabled(PrtgResourcePressureModeReplayJob job,
        DailyAnalysisRecord parent, out bool changed, out bool qualifiedFindingPublished,
        out DateTime? evidenceReadyAtUtc, out IReadOnlyList<LogIssueSignature> qualifiedSignatures)
    {
        changed = false;
        qualifiedFindingPublished = false;
        evidenceReadyAtUtc = null;
        qualifiedSignatures = Array.Empty<LogIssueSignature>();
        if (string.IsNullOrWhiteSpace(job.SourceGeneration) || string.IsNullOrWhiteSpace(job.ResourceGeneration)) return false;
        try
        {
            var system = settings.Get();
            var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            if (!system.PrtgEnabled || string.IsNullOrWhiteSpace(system.PrtgUrl) ||
                !policy.Ready(system.PrtgUrl) || !policy.HostIds.Contains(job.HostId) ||
                !policy.SensorIds.Contains(job.SensorObjid) || policy.SourceGeneration != job.SourceGeneration)
                return false;
            var consumption = new PrtgResourcePeriodConsumer(backend, settings).EvaluateClosedHostDayBatch(
                [job.SensorObjid], parent.Date.Date, DateTime.UtcNow, evaluationHostIds: [job.HostId]);
            var closed = consumption.ClosedDay;
            if (closed is null || closed.ExceededBatchRowBound || closed.RejectedSensorObjids.Contains(job.SensorObjid) ||
                !closed.ExpectedWindowCountsBySensor.TryGetValue(job.SensorObjid, out var expected) || expected <= 0 ||
                !closed.EvaluatedWindowCountsBySensor.TryGetValue(job.SensorObjid, out var evaluated) || evaluated != expected ||
                !closed.SelectedSensorObjids.Contains(job.SensorObjid)) return false;
            var currentMode = CaptureCurrentJob(job);
            if (currentMode is null) return false;
            var modeVersion = currentMode.Value.Version;
            var expectedRuleCode = job.Family == PrtgResourceFamily.Cpu
                ? "resource_cpu_sustained_pressure" : "resource_memory_sustained_pressure";
            var assessment = consumption.Assessments.FirstOrDefault(item => item.HostId == job.HostId &&
                item.SensorObjid == job.SensorObjid && item.Family == job.Family && item.IsComplete);
            if (assessment is null) return false;
            evidenceReadyAtUtc = DateTime.UtcNow;

            var qualified = consumption.QualifiedFormalFindings.Where(item => item.EvidenceDay.Date == parent.Date.Date &&
                item.Finding.SensorObjid == job.SensorObjid && item.Finding.SourceGeneration == job.SourceGeneration &&
                item.Finding.ResourceGeneration == job.ResourceGeneration &&
                item.Finding.RuleCode == expectedRuleCode)
                .Select(item => item.Finding).ToArray();
            var signatures = qualified.Select(item => PrtgFindingMapper.ToSignature(item, parent.Date, job.HostId)).ToArray();
            var currentKeys = signatures.Select(item => item.EventKey).Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            var batch = new PrtgStateReconciliationBatch(job.SourceGeneration,
                new Dictionary<long, string>(), new Dictionary<long, string>(), currentKeys,
                [new PrtgResourceGenerationFence(job.SensorObjid, job.SourceGeneration, job.ResourceGeneration)],
                modeVersion, resourceModeFenceRequired: true,
                expectedParentRecordId: parent.RecordId,
                expectedParentFingerprint: HostDayWorkflowFingerprint.ForParentRecord(parent),
                expectedParentFindingFingerprint: PrtgFindingMapper.Fingerprint(parent.TopIssues.Where(PrtgFindingMapper.IsPrtg)));

            PrtgDecisionManifest? manifest = null;
            if (signatures.Length > 0)
            {
                var prtgStore = backend.PrtgStore();
                prtgStore.EnsureResourceAuthorityRevisions([job.HostId]);
                var authorityRevision = prtgStore.ReadResourceAuthorityRevision(job.HostId);
                if (authorityRevision <= 0) return false;
                var hostMap = prtgStore.GetLatestHostMapAuthorityWithDate(
                    PrtgTriggeredValueFetcher.HostMapLookbackDays, parent.Date).Rows
                    .Where(row => row.HostId == job.HostId).GroupBy(row => row.DeviceObjid)
                    .Select(group => group.Last()).OrderBy(row => row.DeviceObjid).ToArray();
                if (hostMap.Length == 0) return false;
                var mappingFingerprint = HostDayWorkflowFingerprint.HashParts(hostMap.Select(row =>
                    PrtgSilentPresenceMappingFingerprint.Compute(row.DeviceObjid, job.HostId)));
                var strategyProfile = PrtgFetchStrategy.Profile(system.PrtgFetchStrategy);
                var strategyFingerprint = HostDayWorkflowFingerprint.HashParts([
                    system.PrtgFetchStrategy, policy.SourceTimeZoneId, policy.SourceCultureName,
                    (!strategyProfile.NightlyExactValues).ToString(),
                    policy.SourceAuthorityFingerprint(system.PrtgUrl), mappingFingerprint]);
                var prtgRules = KnownIssueCatalog.Rules
                    .Where(rule => string.Equals(rule.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && rule.Enabled)
                    .ToList();
                var ruleFingerprint = HostDayWorkflowFingerprint.HashParts(prtgRules
                    .OrderBy(rule => rule.Id, StringComparer.Ordinal)
                    .Select(rule => System.Text.Json.JsonSerializer.Serialize(rule)));
                var parentFingerprint = HostDayWorkflowFingerprint.ForParentRecord(parent);
                var resourceFingerprint = HostDayWorkflowFingerprint.HashParts([
                    job.SourceGeneration, job.ResourceGeneration, assessment.ProfileFingerprint,
                    assessment.EvidenceFingerprint, closed.SelectionEpoch]);
                var semanticFingerprint = HostDayWorkflowFingerprint.HashParts([
                    assessment.CurrentRuleFingerprint, assessment.RuleAdmissionFingerprint]);
                var evidenceFingerprint = HostDayWorkflowFingerprint.HashParts([
                    assessment.EvidenceFingerprint, closed.SelectionEpoch,
                    expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    evaluated.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                manifest = new PrtgDecisionManifest
                {
                    ParentRecordId = parent.RecordId,
                    ParentFingerprint = parentFingerprint,
                    ParentFindingFingerprint = batch.ExpectedParentFindingFingerprint,
                    PolicyRevision = policy.Revision,
                    SourceGeneration = policy.SourceGeneration,
                    ResourceAuthorityRevision = authorityRevision,
                    ResourceModeBlobVersion = modeVersion,
                    ResourceModeFenceRequired = true,
                    ResourceFingerprint = resourceFingerprint,
                    SemanticFingerprint = semanticFingerprint,
                    StrategyFingerprint = strategyFingerprint,
                    HostMappingFingerprint = mappingFingerprint,
                    RuleFingerprint = ruleFingerprint,
                    EvidenceFingerprint = evidenceFingerprint,
                    FindingFingerprint = PrtgFindingMapper.Fingerprint(signatures),
                    CompletedAtUtc = DateTime.UtcNow,
                    Outcome = "partial",
                    WaitReasonCodes = ["mode-replay-resource-family-only"]
                };
            }
            BeforeReconciliationForTesting?.Invoke();
            if (signatures.Length > 0)
                changed = backend.RecordStore().AttachPrtgFindingsWithReconciliation(job.HostId, parent.Date,
                    signatures, new HashSet<string>(), out _, aiConfigured: ai.Available,
                    manifest: manifest!, reconciliation: batch);
            else
            {
                // No-hit evaluation may reconcile stale same-generation findings, but cannot publish a manifest.
                changed = backend.RecordStore().AttachPrtgModeRevocationsWithReconciliation(job.HostId,
                    parent.Date, [], new HashSet<string>(), out _, aiConfigured: false,
                    reconciliation: batch);
            }
            if (!changed)
            {
                if (signatures.Length == 0) return false;
                var existingParent = backend.RecordStore(new HostKey { HostId = job.HostId, HostName = parent.Host })
                    .ReadRecent(parent.Date, 1).FirstOrDefault(record => record.RecordId == parent.RecordId);
                var expectedFindingKeys = signatures.Select(signature => signature.EventKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key)).ToHashSet(StringComparer.Ordinal);
                if (existingParent is null || !existingParent.CanSupplementWithPrtg() ||
                    !HostDayWorkflowFingerprint.HasValidPrtgManifest(existingParent) ||
                    !expectedFindingKeys.IsSubsetOf(existingParent.TopIssues
                        .Where(PrtgFindingMapper.IsPrtg).Select(signature => signature.EventKey)
                        .ToHashSet(StringComparer.Ordinal))) return false;
            }
            if (signatures.Length > 0)
            {
                var observationPairs = qualified.Zip(signatures,
                    (item, signature) => (Finding: item, Signature: signature)).ToArray();
                try
                {
                    backend.PrtgObservationStore().Capture(job.HostId, parent.Date.Date, system.Revision,
                        observationPairs, system.PrtgUrl, TransitionRunId(job.TransitionId), job.TransitionId);
                }
                catch (Exception observationError) when (observationError is not OperationCanceledException)
                {
                    Log.Warn(observationError,
                        "Mode replay PRTG observation capture failed for host {HostId}, day {Day}; retry stays pending",
                        job.HostId, parent.Date);
                    changed = false;
                    return false;
                }
                var currentParent = backend.RecordStore(new HostKey { HostId = job.HostId, HostName = parent.Host })
                    .ReadRecent(parent.Date, 1).FirstOrDefault(record => record.RecordId == parent.RecordId);
                if (currentParent is null || !currentParent.CanSupplementWithPrtg()) return false;
                workflow.RestoreFromRecord(currentParent, aiEnabled: ai.Available, prtgEnabled: true);
                if (HostDayWorkflowFingerprint.HasValidPrtgManifest(currentParent))
                    workflow.SetPrtg(job.HostId, parent.Date,
                        currentParent.PrtgManifest!.Outcome == "complete" ? WorkflowLegState.Succeeded : WorkflowLegState.Degraded,
                        evidenceReady: currentParent.PrtgManifest.Outcome == "complete",
                        currentParent.PrtgManifest.EvidenceFingerprint,
                        failure: currentParent.PrtgManifest.Outcome == "partial"
                            ? string.Join(",", currentParent.PrtgManifest.WaitReasonCodes) : null);
                qualifiedFindingPublished = true;
            }
            qualifiedSignatures = signatures;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Log.Debug(ex, "Qualified closed-day replay is waiting for host {HostId}, day {Day}", job.HostId, parent.Date);
            return false;
        }
    }

    private NightlyDispatch CreateCaseDispatch()
    {
        var currentSettings = settings.Get();
        var issueCases = backend.IssueCaseStore();
        DispatchContext dispatchContext;
        try
        {
            dispatchContext = DispatchContext.Build(candidateSource.Build(),
                new IssueOwnerStore(backend.Blob("issue_owners")), backend.WorkOrderStore(), issueCases,
                new NoiseMarkStore(backend.Blob("noise_marks")), currentSettings, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "PRTG mode replay dispatch context unavailable; formal case start will still be retried safely");
            dispatchContext = DispatchContext.CreateUnavailable(issueCases);
        }
        return new NightlyDispatch(workOrders, dispatchContext, hosts);
    }

    private bool FinalizeReplayReport(DailyAnalysisRecord record, WebHost host, CancellationToken ct,
        bool resourceModeDisabled)
    {
        BeforeReplayReportForTesting?.Invoke();

        var aiService = _reportAiService;
        if (aiService is null)
        {
            var appSettings = new AppSettings();
            RuntimeSettingsResolver.ApplySystemSettingsOverrides(appSettings, settings);
            aiService = new AIService(appSettings.Ai, usageMeter: new AiUsageStore(
                backend.Blob(AiUsageStore.BlobKey)));
        }
        var recordStore = backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
        var suppressionStore = new MuteAwareSuppressionStore(
            new SuppressionStore(backend.Blob("suppressions")),
            new IssueOwnerStore(backend.Blob("issue_owners")));
        var reportService = new RiskReportService(aiService, backend.ReportStore());
        var analysis = new LogAnalysisService(new EventLogService(), aiService, recordStore, suppressionStore,
            reportService: reportService, host: host.HostName, hostId: host.HostId,
            riskyEventStore: backend.RiskyEventStore(), hostGroupIds: host.GroupIds);
        return analysis.FinalizeModeReplayReportAsync(record, ct, resourceModeDisabled).GetAwaiter().GetResult();
    }

    private static long TransitionRunId(string transitionId)
    {
        var value = ulong.Parse(transitionId.AsSpan(0, 16),
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        var runId = (long)(value & long.MaxValue);
        return runId == 0 ? 1 : runId;
    }
}
