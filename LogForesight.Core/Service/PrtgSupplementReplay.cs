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
    public int RunBatch(int limit = 100, CancellationToken cancellationToken = default)
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
            o.SensorObjid.HasValue && policy.SensorIds.Contains(o.SensorObjid.Value) &&
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
                try
                {
                    using var document = JsonDocument.Parse(row.ContentJson);
                    var finding = document.RootElement.GetProperty("Decision").Deserialize<LogIssueSignature>();
                    if (finding == null || finding.EventKey != row.EventKey || finding.PrtgSourceGeneration != policy.SourceGeneration ||
                        finding.PrtgResourceGeneration != row.ResourceGeneration)
                    { Mark(row, "invalid", null); continue; }
                    var proof = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + row.SensorObjid)).Get();
                    if (proof.HostId != row.HostId || proof.SourceGeneration != row.SourceGeneration || proof.ResourceGeneration != row.ResourceGeneration ||
                        proof.MappingRevision != backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion())
                    { Mark(row, "scope-paused", null); continue; }
                    var currentRule = currentRules?.FirstOrDefault(r => r.Id == finding.RuleId && r.Enabled && r.Platform == "prtg");
                    var capturedRule = document.RootElement.GetProperty("Finding").GetProperty("Rule").Deserialize<KnownIssueRule>();
                    if (currentRule == null || JsonSerializer.Serialize(currentRule) != JsonSerializer.Serialize(capturedRule))
                    { Mark(row, "rules-changed", null); continue; }
                    var mutes = new MuteAwareSuppressionStore(new SuppressionStore(backend.Blob("suppressions")),
                        new IssueOwnerStore(backend.Blob("issue_owners"))).LoadAll();
                    var active = SuppressionFilter.ActiveForHost(mutes, host.HostName, host.GroupIds, DateTime.Now);
                    SuppressionFilter.MarkSuppressed([finding], active, SuppressionFilter.MutesOf(mutes), row.RecordDate);
                    operation.Checkpoint();
                    var added = store.AttachPrtgFindings(host.HostId, row.RecordDate, [finding], SuppressionFilter.ToCorrelationPatternIdSet(active), out _);
                    var current = store.ReadRecent(row.RecordDate, 1).FirstOrDefault(r => r.Date == row.RecordDate);
                    if (current == null || !current.CanSupplementWithPrtg() || !current.TopIssues.Any(i => i.EventKey == finding.EventKey))
                    { Mark(row, "waiting-netiq", null); continue; }
                    operation.Checkpoint();
                    var attach = cases.AttachNewDay(host.HostName, row.RecordDate, [finding], DateTime.Now);
                    dispatch.DispatchDay(host.HostName, row.RecordDate, attach.Unassigned, DateTime.Now);
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
                catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "PRTG 補追加失敗 host={HostId} snapshot={SnapshotId}", row.HostId, row.SnapshotId); Mark(row, "retry", null); }
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
