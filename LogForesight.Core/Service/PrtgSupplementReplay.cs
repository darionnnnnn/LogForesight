using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Core.Service;

/// <summary>持久補追加／派工意圖。每次重新核對父列、範圍及來源，所有副作用可冪等重試。</summary>
public sealed class PrtgSupplementReplay(StorageBackend backend, IHostStore hosts,
    IssueCaseCoordinator cases, WorkOrderCoordinator orders, IDispatchCandidateSource candidates)
{
    public int RunBatch(int limit = 100, CancellationToken cancellationToken = default,
        HostDayWorkflowService? workflow = null, bool aiConfigured = false)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        if (!settings.PrtgEnabled || !policy.Ready(settings.PrtgUrl)) return 0;
        using var operation = new PrtgOperationScope(settings,
            () => new SystemSettingsStore(backend.Blob("system_settings")).Get(), cancellationToken,
            new PrtgScopeRevisionReader(backend, hosts).Read, "補追加與派工");
        operation.Checkpoint();
        using var ctx = backend.CreateContext();
        var rows = ctx.PrtgObservations.AsNoTracking().Where(o => o.ActiveKey != null &&
            o.QualityReason == EfPrtgObservationStore.CoveredQuality && o.SupplementStatus != "invalid" &&
            o.SourceGeneration == policy.SourceGeneration && policy.HostIds.Contains(o.HostId) &&
            ((o.SensorObjid.HasValue && policy.SensorIds.Contains(o.SensorObjid.Value)) ||
             (!o.SensorObjid.HasValue && o.EventKey.StartsWith("prtg:silent:"))) &&
            (o.SupplementStatus != "applied" || !ctx.DailyRecords.Any(r => r.RecordId == o.SupplementParentRecordId)))
            .OrderBy(o => o.SupplementAttemptAtUtc).ThenBy(o => o.RecordedAtUtc).Take(limit).ToArray();
        var dispatchContext = DispatchContext.Build(candidates.Build(), new IssueOwnerStore(backend.Blob("issue_owners")),
            backend.WorkOrderStore(), backend.IssueCaseStore(), new NoiseMarkStore(backend.Blob("noise_marks")), settings, DateTime.Now);
        var dispatch = new NightlyDispatch(orders, dispatchContext, hosts);
        var rules = new KnownIssueRuleStore(backend.Blob("rules"));
        var currentRules = rules.Exists ? rules.Load().Content?.Rules : null;
        var applied = 0;
        foreach (var row in rows)
        {
            operation.Checkpoint();
            var host = hosts.GetAll().FirstOrDefault(h => h.HostId == row.HostId);
            if (host is not { Active: true, MergedInto: null } || host.Source != "netiq") { Mark(row, "scope-paused", null); continue; }
            var store = backend.RecordStore(new HostKey { HostId = host.HostId, HostName = host.HostName });
            lock (EfAnalysisRecordStore.LockFor(host.HostId, row.RecordDate))
            {
                var parent = store.ReadRecent(row.RecordDate, 1).FirstOrDefault(r => r.Date == row.RecordDate);
                if (parent == null || !parent.CanSupplementWithPrtg()) { Mark(row, "waiting-netiq", null); continue; }
                var caseIntentStarted = false;
                LogIssueSignature? finding = null;
                HostDayWorkflowVersion? caseVersion = null;
                try
                {
                    using var document = JsonDocument.Parse(row.ContentJson);
                    finding = document.RootElement.GetProperty("Decision").Deserialize<LogIssueSignature>();
                    if (finding == null || finding.EventKey != row.EventKey || finding.PrtgSourceGeneration != policy.SourceGeneration ||
                        finding.PrtgResourceGeneration != row.ResourceGeneration)
                    { Mark(row, "invalid", null); continue; }
                    var currentRule = currentRules?.FirstOrDefault(r => r.Id == finding.RuleId && r.Enabled && r.Platform == "prtg");
                    var capturedRule = document.RootElement.GetProperty("Finding").GetProperty("Rule").Deserialize<KnownIssueRule>();
                    if (currentRule == null || JsonSerializer.Serialize(currentRule) != JsonSerializer.Serialize(capturedRule))
                    { Mark(row, "rules-changed", null); continue; }
                    if (row.SensorObjid is null && finding.EventKey.Split(':') is { Length: >= 2 } keyParts &&
                        keyParts[1] == PrtgRuleEvaluator.RuleSilent)
                    {
                        if (!PrtgSilentDeviceProofRevalidator.IsCurrent(backend, row.HostId, row.RecordDate,
                                finding, capturedRule, settings.PrtgUrl))
                        { Mark(row, "scope-paused", null); continue; }
                    }
                    else if (row.SensorObjid.HasValue)
                    {
                    var identity = backend.PrtgStore().GetResourceIdentity(row.SensorObjid.Value);
                    var valueRule = PrtgResourceProfileQualification.IsCpuOrMemoryPressure(finding) ||
                        finding.RuleId == "builtin-prtg-resource-disk-pressure";
                    bool resourceCurrent;
                    if (valueRule)
                        resourceCurrent = finding.RuleId == "builtin-prtg-resource-disk-pressure"
                            ? PrtgResourceProfileQualification.IsCurrentDiskProfile(backend, row.HostId, row.SensorObjid.Value,
                                DateTime.UtcNow, finding.PrtgSourceGeneration, finding.PrtgResourceGeneration, finding.PrtgChannelGeneration)
                            : PrtgResourceProfileQualification.IsCurrentFormalPressure(backend, row.HostId,
                                row.SensorObjid.Value, finding, DateTime.UtcNow);
                    else
                    {
                        var proof = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + row.SensorObjid)).Get();
                        resourceCurrent = proof.HostId == row.HostId && proof.SourceGeneration == row.SourceGeneration &&
                            proof.ResourceGeneration == row.ResourceGeneration &&
                            PrtgResourceQualification.IsCurrent(proof, identity, policy.SourceGeneration,
                                row.SensorObjid.Value, identity.DeviceId, row.HostId);
                    }
                    if (resourceCurrent && finding.RuleId is "disk_free_trend" or "builtin-prtg-disk-free-trend")
                        resourceCurrent = PrtgResourceQualification.IsChannelCurrent(
                            new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Get(row.SensorObjid.Value),
                            new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Get(row.SensorObjid.Value),
                            identity);
                    if (!resourceCurrent)
                    { Mark(row, "scope-paused", null); continue; }
                    if (finding.RuleId == "builtin-prtg-resource-disk-pressure")
                    {
                        var resourceRules = PrtgResourceCurrentRuleCatalog.Load(backend);
                        if (!PrtgResourceProfileQualification.IsCurrentDiskProfile(backend, row.HostId,
                                row.SensorObjid.Value, DateTime.UtcNow, finding.PrtgSourceGeneration,
                                finding.PrtgResourceGeneration, finding.PrtgChannelGeneration))
                        { Mark(row, "scope-paused", null); continue; }
                        if (finding.PrtgRuleAdmissionFingerprint !=
                            resourceRules.AdmissionFingerprintFor(PrtgResourceFamily.Disk))
                        { Mark(row, "rules-changed", null); continue; }
                        if (finding.PrtgResourceReasonCodes is not { Count: > 0 } reasons ||
                            reasons.Any(code => code is not ("disk-two-hour-low-water" or "disk-seven-day-low-water-trend")))
                        { Mark(row, "invalid", null); continue; }
                        if (reasons.Contains("disk-seven-day-low-water-trend") &&
                            (!resourceRules.DiskTrendEnabled || finding.PrtgTrendSourceRuleId != resourceRules.DiskTrendRuleId ||
                             finding.PrtgTrendSourceRuleFingerprint != resourceRules.DiskTrendRuleFingerprint ||
                             !PrtgResourceQualification.IsChannelCurrent(
                                new PrtgDiskSemanticEvidenceStore(backend.Blob(PrtgDiskSemanticEvidenceStore.BlobKey)).Get(row.SensorObjid.Value),
                                new PrtgDiskVerificationResultStore(backend.Blob(PrtgDiskVerificationResultStore.BlobKey)).Get(row.SensorObjid.Value),
                                identity)))
                        { Mark(row, "rules-changed", null); continue; }
                    }
                    }
                    else { Mark(row, "invalid", null); continue; }
                    var mutes = new MuteAwareSuppressionStore(new SuppressionStore(backend.Blob("suppressions")),
                        new IssueOwnerStore(backend.Blob("issue_owners"))).LoadAll();
                    var active = SuppressionFilter.ActiveForHost(mutes, host.HostName, host.GroupIds, DateTime.Now);
                    SuppressionFilter.MarkSuppressed([finding], active, SuppressionFilter.MutesOf(mutes), row.RecordDate);
                    operation.Checkpoint();
                    var added = store.AttachPrtgFindings(host.HostId, row.RecordDate, [finding],
                        SuppressionFilter.ToCorrelationPatternIdSet(active), out _, aiConfigured);
                    var current = store.ReadRecent(row.RecordDate, 1).FirstOrDefault(r => r.Date == row.RecordDate);
                    if (current == null || !current.CanSupplementWithPrtg() || !current.TopIssues.Any(i => i.EventKey == finding.EventKey))
                    { Mark(row, "waiting-netiq", null); continue; }
                    try
                    {
                        workflow?.RestoreFromRecord(current, aiEnabled: aiConfigured);
                        if (workflow != null)
                        {
                            if (HostDayWorkflowFingerprint.HasValidPrtgManifest(current))
                                workflow.SetPrtg(host.HostId, row.RecordDate, current.PrtgManifest!.Outcome == "complete" ? WorkflowLegState.Succeeded : WorkflowLegState.Degraded,
                                    evidenceReady: current.PrtgManifest.Outcome == "complete",
                                    current.PrtgManifest.EvidenceFingerprint,
                                    failure: current.PrtgManifest.Outcome == "partial" ? string.Join(",", current.PrtgManifest.WaitReasonCodes) : null);
                            else
                                workflow.SetPrtg(host.HostId, row.RecordDate, WorkflowLegState.Waiting, evidenceReady: false,
                                    failure: "formal-manifest-missing-or-stale");
                        }
                    }
                    catch (Exception workflowError)
                    {
                        NLog.LogManager.GetCurrentClassLogger().Warn(workflowError,
                            "PRTG supplement workflow tracking failed host={HostId} date={Date}", host.HostId, row.RecordDate);
                    }
                    operation.Checkpoint();
                    caseVersion = workflow?.CaptureDeliveryVersion(host.HostId, row.RecordDate, current.RecordId);
                    if (PrtgResourceFormalDeliveryFence.IsTargetPressureIssue(finding))
                    {
                        var signatureKey = IssueSignatureKey.For(finding);
                        if (caseVersion is null || workflow is null ||
                            !workflow.ClaimFormalPressureCaseStarts(host.HostId, row.RecordDate, caseVersion, [finding])
                                .Contains(signatureKey))
                        {
                            // This receiver must use the same durable mode/grant fence as the Daily
                            // case-start path. Keep the observation retryable until a current claim
                            // can be committed; never let an unfenced finding reach case or mail work.
                            Mark(row, "retry", null);
                            continue;
                        }
                    }
                    try { if (caseVersion != null) workflow?.RecordCaseIntents(host.HostId, row.RecordDate, [IssueSignatureKey.For(finding)], caseVersion); }
                    catch (Exception workflowError)
                    { NLog.LogManager.GetCurrentClassLogger().Warn(workflowError, "PRTG supplement case intent workflow tracking failed"); }
                    caseIntentStarted = true;
                    var attach = cases.AttachNewDay(host.HostName, row.RecordDate, [finding], DateTime.Now);
                    try
                    {
                        var signatureKey = IssueSignatureKey.For(finding);
                        var attached = attach.Unassigned.All(issue => !IssueSignatureKeyComparer.Instance.Equals(
                            IssueSignatureKey.For(issue), signatureKey));
                        if (caseVersion != null) workflow?.RecordCaseResults(host.HostId, row.RecordDate, [signatureKey],
                            attached ? [signatureKey] : [], [], caseVersion);
                    }
                    catch (Exception workflowError) { NLog.LogManager.GetCurrentClassLogger().Warn(workflowError, "PRTG supplement case workflow tracking failed"); }
                    dispatch.DispatchDay(host.HostName, row.RecordDate, attach.Unassigned, DateTime.Now,
                        workflow is null || caseVersion is null ? null : outcome => workflow.RecordDispatchOutcome(host.HostId, row.RecordDate, outcome, caseVersion));
                    dispatch.FlushRun(DateTime.Now);
                    var parentId = ctx.DailyRecords.AsNoTracking().Where(r => r.HostId == host.HostId && r.RecordDate == row.RecordDate)
                        .Select(r => (long?)r.RecordId).SingleOrDefault();
                    var issueKey = IssueSignatureKey.For(finding.LogName, finding.Source, finding.EventId, finding.EntryType, finding.EventKey);
                    var hasCase = backend.IssueCaseStore().GetMany([host.HostName]).Any(c => c.IssueKey == issueKey &&
                        c.FirstLinkedDate <= row.RecordDate && c.LastLinkedDate >= row.RecordDate);
                    Mark(row, hasCase || finding.Suppressed ? "applied" : "unassigned", parentId);
                    if (added || attach.AttachedCount > 0 || hasCase && row.SupplementStatus != "applied") applied++;
                }
                catch (JsonException) { Mark(row, "invalid", null); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (caseIntentStarted && finding is not null)
                        try
                        {
                            var signatureKey = IssueSignatureKey.For(finding);
                            if (caseVersion != null) workflow?.RecordCaseResults(host.HostId, row.RecordDate, [signatureKey], [], [signatureKey], caseVersion);
                        }
                        catch (Exception workflowError) { NLog.LogManager.GetCurrentClassLogger().Warn(workflowError, "PRTG supplement case failure workflow tracking failed"); }
                    NLog.LogManager.GetCurrentClassLogger().Warn(ex, "PRTG 補追加失敗 host={HostId} snapshot={SnapshotId}", row.HostId, row.SnapshotId);
                    Mark(row, "retry", null);
                }
            }
        }
        operation.CompletedStage("本批補追加／派工核對已返回");
        return applied;
    }
    private void Mark(PrtgObservationRow row, string status, long? parentId)
    {
        using var ctx = backend.CreateContext();
        ctx.PrtgObservations.Where(o => o.SnapshotId == row.SnapshotId && o.ActiveKey == row.ActiveKey)
            .ExecuteUpdate(update => update.SetProperty(o => o.SupplementStatus, status)
                .SetProperty(o => o.SupplementAttemptAtUtc, DateTime.UtcNow).SetProperty(o => o.SupplementParentRecordId, parentId));
    }
}
