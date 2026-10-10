using System.Reflection;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Filters;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogForesight.Web.Controllers.Api;

[ApiController, Route("api/prtg/acceptance"), Permission(Capability.Maintain)]
public sealed class PrtgAcceptanceController(StorageBackend backend, IVisibilityService visibility,
    ICurrentUser user, IAuditService audit) : ControllerBase
{
    private const string LabelPrefix = "prtg_acceptance_labels_";
    // Supports 1,000 rows at the largest accepted field lengths, including four-byte UTF-8 text.
    private const int MaximumLabelCharacters = 40 * 1024 * 1024;
    private const int MaximumLabelUtf8Bytes = 40 * 1024 * 1024;
    private const int MaximumExportLabels = 1000;
    // Matches the existing bounded analysis-record payload contract. SQL reads at most cap+1 characters per row.
    private const int MaximumRecordContentCharacters = WorkflowRecoveryPage.MaximumPayloadBytes;
    private const int RecordReadBatchSize = 16;
    private const int MaximumRecordFactBytes = 8 * 1024 * 1024;
    private const int MaximumExportJsonBytes = 64 * 1024 * 1024;
    private const int MaximumMailStateCharacters = MailNotifyStateStore.MaxMutationCharacters;
    private sealed record RecordRead(IReadOnlyList<object> Items, bool Complete, IReadOnlyList<string> Reasons, int OutputBytes);
    private sealed record IntentRead(IReadOnlyList<object> Items, int? Total, bool Complete, string? Reason);
    private sealed record LabelHostRead(IReadOnlyList<PrtgAcceptanceIncident> Items, bool Complete, string? Reason);
    private sealed record LabelRead(IReadOnlyList<PrtgAcceptanceIncident> Items, int? Total, bool Complete, string? Reason);
    private sealed record TimelineRead(IReadOnlyList<object> Items, int? SelectedTotal, int? UnknownTotal, bool Complete, bool UnattributedUnknown);
    private string CurrentSegment()
    {
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        var identities = new List<object>();
        var ownedSensors = new Dictionary<long, HashSet<long>>();
        var bindingStore = new PrtgTrustedSamplingBindingStore(backend);
        foreach (var page in policy.SensorIds.Distinct().Order().Chunk(100))
        {
            var current = backend.PrtgStore().GetResourceIdentities(page);
            var timelinePage = PrtgSensorTimelineStore.ReadManyBoundedForSilentRule(backend.CreateContext, page);
            var timelines = page.Select(id => (SensorId: id, Timeline: timelinePage.GetValueOrDefault(id))).ToArray();
            // Current authority wins over a stale timeline's host assignment. Read bindings
            // only after this visibility fence, in bounded batches even for a full fleet.
            var scoped = timelines.Where(p => current.TryGetValue(p.SensorId, out var identity)
                ? visible.Contains(identity.HostId) : p.Timeline is not null && visible.Contains(p.Timeline.HostId)).ToArray();
            var bindings = bindingStore.GetMany(scoped.Select(p => p.SensorId));
            foreach (var item in scoped)
            {
                var p = item.Timeline;
                current.TryGetValue(item.SensorId, out var identity);
                bindings.TryGetValue(item.SensorId, out var binding);
                var hostId = identity?.HostId ?? p!.HostId;
                if (!ownedSensors.TryGetValue(hostId, out var sensors)) ownedSensors[hostId] = sensors = [];
                sensors.Add(item.SensorId);
                identities.Add(new
                {
                    SensorId = item.SensorId,
                    Timeline = p is null ? null : new { p.SourceGeneration, p.ResourceGeneration, p.MappingRevision,
                        p.DiskSemanticFingerprint, p.DiskSemanticValidFrom },
                    Current = identity is null ? null : new { identity.Epoch, identity.Generation,
                        identity.SourceGeneration, identity.HostId, identity.ChannelGeneration,
                        identity.Active, identity.PendingReconciliation },
                    Sampling = binding is null ? null : new { binding.BindingRevision, binding.SemanticFingerprint,
                        binding.AuthorityContextFingerprint, binding.SourceGeneration, binding.ResourceGeneration,
                        binding.IdentityEpoch, binding.ChannelGeneration }
                });
            }
        }
        var modes = ownedSensors.OrderBy(pair => pair.Key).SelectMany(pair =>
            new PrtgResourcePressureModeStore(backend.Blob(PrtgResourcePressureModeStore.BlobKey(pair.Key)))
                .ReadHostSnapshot(pair.Key).Grants.Where(g => g.FormalEnabled && pair.Value.Contains(g.SensorObjid))
                .OrderBy(g => g.SensorObjid).ThenBy(g => g.Family)
                .Select(g => new { g.HostId, g.SensorObjid, g.Family, g.ProfileFingerprint, g.RulesVersion,
                    g.SourceGeneration, g.ResourceGeneration, g.ChannelGeneration, g.ResourceEpoch,
                    g.SemanticVersion, g.StrategyVersion, g.RuleId, g.RuleFingerprint, g.MaintainAuthorized,
                    g.ModeTransitionId })).ToArray();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { identities, modes })))).ToLowerInvariant();
        var build = typeof(PrtgAcceptanceController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return $"{settings.Revision}|{policy.Revision}|{backend.Blob("rules").ReadVersion()}|{PrtgDiskAssessmentService.ParserSemanticVersion}|{build}|{hash}";
    }
    [HttpGet("export")]
    public IActionResult Export([FromQuery] DateTime from, [FromQuery] DateTime through)
    {
        if (from == default || through == default || from.Date > through.Date || (through.Date - from.Date).Days > 365)
            return BadRequest(ApiResponse.Fail("validation_failed", "請選擇最多 366 天的驗收期間。"));
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToArray();
        var policy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
        var selected = policy.HostIds.Where(visible.Contains).ToArray();
        using var db = backend.CreateContext();
        var recordsQuery = db.DailyRecords.AsNoTracking().Where(r => selected.Contains(r.HostId) && r.RecordDate >= from.Date && r.RecordDate <= through.Date);
        var recordsTotal = recordsQuery.Count();
        var recordRead = ReadRecordFacts(recordsQuery, selected, recordsTotal);
        var observationsQuery = db.PrtgObservations.AsNoTracking().Where(r => selected.Contains(r.HostId) && r.RecordDate >= from.Date && r.RecordDate <= through.Date);
        var observationsTotal = observationsQuery.Count();
        var observations = observationsQuery.OrderBy(r => r.SnapshotId).Take(10000).Select(r => new
        { r.SnapshotId, r.RunId, r.DecisionKey, r.ActiveKey, r.HostId, r.SensorObjid, r.RecordDate, r.SourceGeneration,
            r.ResourceGeneration, r.QualityReason, r.SupplementStatus, r.RecordedAtUtc, r.SupplementAttemptAtUtc, r.SupplementParentRecordId }).ToArray();
        var allPolicyHostsVisible = policy.HostIds.Distinct().Any() && policy.HostIds.Distinct().All(selected.Contains);
        var timelineRead = ReadTimelines(policy.SensorIds, selected, allPolicyHostsVisible);
        // Filter each complete host document by the requested period before applying the global output cap.
        var labelRead = ReadLabels(selected, from.Date, through.Date, MaximumExportLabels);
        var labels = labelRead.Items;
        var intentRead = ReadIntents(selected, from.Date, through.Date);
        var settings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var recordsScopeComplete = recordRead.Complete;
        var observationsScopeComplete = observations.Length == observationsTotal;
        var intentsScopeComplete = intentRead.Complete;
        var scopeComplete = selected.Length > 0 && recordsScopeComplete && observationsScopeComplete && intentsScopeComplete && labelRead.Complete && timelineRead.Complete;
        var comparisonsAvailable = scopeComplete && labelRead.Complete;
        var scopeReasons = new List<string>();
        if (selected.Length == 0) scopeReasons.Add("selected-host-scope-empty");
        scopeReasons.AddRange(recordRead.Reasons);
        if (!observationsScopeComplete) scopeReasons.Add("observation-output-cap");
        if (!intentsScopeComplete && intentRead.Reason is not null) scopeReasons.Add(intentRead.Reason);
        if (!labelRead.Complete && labelRead.Reason is not null) scopeReasons.Add(labelRead.Reason);
        if (!timelineRead.Complete) scopeReasons.Add(timelineRead.UnattributedUnknown
            ? "timeline-scope-unattributed" : "timeline-missing-oversized-malformed-or-cross-host");
        var data = new
        {
            FormatVersion = 2, TimelineRepresentation = "bounded-summary-v1", Purpose = "manual-site-acceptance", ExportedAtUtc = DateTime.UtcNow,
            Build = typeof(PrtgAcceptanceController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            from, through, SettingsRevision = settings.Revision, PolicyRevision = policy.Revision,
            policy.SourceGeneration, policy.CoreSystemId, policy.ValidFrom,
            RulesRevision = backend.Blob("rules").ReadVersion(), HostIds = selected,
            ScopeComplete = scopeComplete,
            ScopeStatus = scopeReasons.Distinct(StringComparer.Ordinal).ToArray(),
            NotificationIntentsTotal = intentRead.Total, NotificationIntentsScopeComplete = intentRead.Complete,
            NotificationIntents = intentRead.Items,
            TimingSemantics = "RecordedAtUtc 是保存判定時間，成功 applied 的 SupplementAttemptAtUtc 是補掛可見時間，SmtpAcceptedAtUtc 只證明 SMTP 接受；未知時間不以 UpdatedAtUtc 倒推，信箱實收到達須人工核對。",
            RecordsTotal = recordsTotal, RecordsOutputCount = recordRead.Items.Count,
            RecordsScopeComplete = recordsScopeComplete, RecordsOutputBytes = recordRead.OutputBytes,
            ObservationsTotal = observationsTotal, Records = recordRead.Items, Observations = observations,
            TimelineSelectedTotal = timelineRead.SelectedTotal, TimelineUnknownTotal = timelineRead.UnknownTotal,
            TimelineScopeUnknown = timelineRead.UnattributedUnknown, Timelines = timelineRead.Items,
            LabelsTotal = labelRead.Total, LabelsScopeComplete = labelRead.Complete, Labels = labels,
            TimingSummary = comparisonsAvailable ? PrtgAcceptanceComparison.TimingSummary(labels) : null,
            Timings = comparisonsAvailable ? PrtgAcceptanceComparison.Timings(labels) : null,
            Comparison = comparisonsAvailable ? PrtgAcceptanceComparison.Evaluate(labels) : null,
            ComparisonUnavailableReason = comparisonsAvailable ? null : "匯出範圍不完整或超過標籤上限；不以部分範圍計算全體比較。",
            Segments = comparisonsAvailable ? labels.GroupBy(i => i.Segment).Select(g => new { Segment = g.Key, Comparison = PrtgAcceptanceComparison.Evaluate(g.ToArray()), TimingSummary = PrtgAcceptanceComparison.TimingSummary(g.ToArray()) }) : null,
            Limitations = "僅含可見試點；無站台效益門檻或獨立事故標籤時不能宣稱實用。請先遮蔽 Core 身分與資源識別再對外提供。鏡像數量與已取得資料不等於全站完整。",
            LabelTemplate = new PrtgAcceptanceIncident { IncidentId = "現場獨立事故識別", EvidenceReference = "工單／事故報告／原生 PRTG 告警對照", Segment = CurrentSegment() }
        };
        audit.Record("prtg_acceptance_export", "匯出可見 NetIQ 試點的取數、判定與人工事故比對證據。", "prtg", null);
        try
        {
            using var stream = new CappedMemoryStream(MaximumExportJsonBytes);
            JsonSerializer.Serialize(stream, data, new JsonSerializerOptions { WriteIndented = true });
            return File(stream.ToArray(), "application/json", $"prtg-acceptance-{from:yyyyMMdd}-{through:yyyyMMdd}.json");
        }
        catch (Exception ex) when (IsExportByteLimitException(ex))
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                ApiResponse.Fail("export_output_byte_cap", "驗收匯出超過 64 MiB 輸出上限；未產生部分檔案。請縮小期間或分批匯出。"));
        }
    }

    private RecordRead ReadRecordFacts(IQueryable<DailyRecordRow> query, long[] selectedHosts, int recordsTotal)
    {
        var selected = selectedHosts.ToHashSet();
        var items = new List<object>(Math.Min(recordsTotal, 10000));
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var outputBytes = 0;
        var processed = 0;
        var cursor = long.MinValue;
        var target = Math.Min(recordsTotal, 10000);
        while (processed < target)
        {
            var page = query.Where(r => r.RecordId > cursor).OrderBy(r => r.RecordId).Take(RecordReadBatchSize)
                .Select(r => new { r.RecordId, r.HostId, r.HostName, r.RecordDate, r.CreatedAt, r.RiskLevel,
                    r.RiskReviewStatus, r.DetailPruned,
                    ContentPrefix = r.ContentJson == null ? string.Empty : r.ContentJson.Substring(0, MaximumRecordContentCharacters + 1),
                    ContentCharacters = r.ContentJson == null ? -1 : r.ContentJson.Length }).ToArray();
            if (page.Length == 0) break;
            foreach (var row in page)
            {
                cursor = row.RecordId;
                processed++;
                if (!selected.Contains(row.HostId))
                {
                    // The SQL predicate is the primary ACL fence. This second check prevents a malformed query result
                    // from exposing a private host's metadata or content-derived identifiers.
                    reasons.Add("record-host-ownership-mismatch");
                    continue;
                }
                object? contentFact = null;
                string? contentReason = null;
                if (row.DetailPruned) contentReason = "detail-pruned";
                else if (row.ContentCharacters < 0) contentReason = "content-missing";
                else if (row.ContentCharacters > MaximumRecordContentCharacters) contentReason = "content-prefix-cap";
                else if (Encoding.UTF8.GetByteCount(row.ContentPrefix) > MaximumRecordContentCharacters) contentReason = "content-utf8-cap";
                else
                {
                    try
                    {
                        var content = JsonSerializer.Deserialize<LogForesight.Core.Models.DailyAnalysisRecord>(row.ContentPrefix);
                        if (content?.TopIssues is null || content.TopIssues.Any(issue => issue is null)) contentReason = "content-root-or-top-issues-null";
                        else if (content.HostId != 0 && content.HostId != row.HostId) contentReason = "content-parent-host-mismatch";
                        else if (content.HostId == 0 && (string.IsNullOrWhiteSpace(content.Host) || string.IsNullOrWhiteSpace(row.HostName) ||
                            !string.Equals(content.Host.Trim(), row.HostName.Trim(), StringComparison.OrdinalIgnoreCase)))
                            contentReason = "content-parent-host-name-mismatch";
                        else if (content.Date.Date != row.RecordDate.Date) contentReason = "content-parent-day-mismatch";
                        else contentFact = content;
                    }
                    catch (JsonException) { contentReason = "content-malformed"; }
                }
                if (contentReason is not null) reasons.Add("record-content-unknown");
                var contentModel = contentFact as LogForesight.Core.Models.DailyAnalysisRecord;
                var fact = new
                {
                    row.HostId, row.RecordDate, row.CreatedAt, row.RiskLevel, row.RiskReviewStatus, row.DetailPruned,
                    ContentStatus = contentReason is null ? "known" : "unknown",
                    ContentUnknownReason = contentReason,
                    LogSource = contentModel?.LogSource.ToString(), LatestNetiqAttemptStatus = contentModel?.LatestNetiqAttemptStatus,
                    NetiqQualified = contentModel is null ? (bool?)null : contentModel.CanSupplementWithPrtg(),
                    NetiqEvents = contentModel?.TopIssues.Count(i => !PrtgFindingMapper.IsPrtg(i)),
                    Prtg = contentModel?.TopIssues.Where(PrtgFindingMapper.IsPrtg).Select(i => new
                    { i.EventKey, i.PrtgSourceGeneration, i.PrtgResourceGeneration, i.PrtgIncidentStartedAt, i.Suppressed,
                        i.ElevatesDayRisk, Severity = i.Severity.ToString() }).ToArray()
                };
                var factBytes = JsonSerializer.SerializeToUtf8Bytes(fact).Length;
                if (factBytes > MaximumRecordFactBytes - outputBytes)
                {
                    reasons.Add("record-output-byte-cap");
                    return new(items, false, reasons.Order(StringComparer.Ordinal).ToArray(), outputBytes);
                }
                outputBytes += factBytes;
                items.Add(fact);
                if (processed >= target) break;
            }
        }
        if (recordsTotal > 10000 || processed != recordsTotal || items.Count != recordsTotal)
            reasons.Add(recordsTotal > 10000 ? "record-row-cap" : "record-scope-count-mismatch");
        var currentTotal = query.Count();
        if (currentTotal != recordsTotal) reasons.Add("record-scope-changed-during-export");
        return new(items, reasons.Count == 0, reasons.Order(StringComparer.Ordinal).ToArray(), outputBytes);
    }

    private IntentRead ReadIntents(long[] selectedHosts, DateTime from, DateTime through)
    {
        var (json, _, reportedLength) = backend.Blob("mail_notify_state").ReadBoundedWithVersion(MaximumMailStateCharacters);
        if (json is null) return new([], null, false, "notification-state-missing");
        if (reportedLength > MaximumMailStateCharacters || json.Length > MaximumMailStateCharacters ||
            Encoding.UTF8.GetByteCount(json) > MaximumMailStateCharacters)
            return new([], null, false, "notification-state-oversized");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new([], null, false, "notification-state-malformed");
            var outboxProperties = 0;
            var outboxKind = JsonValueKind.Undefined;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "UrgentOutbox", StringComparison.OrdinalIgnoreCase)) continue;
                outboxProperties++;
                outboxKind = property.Value.ValueKind;
            }
            if (outboxProperties != 1 || outboxKind != JsonValueKind.Object)
                return new([], null, false, "notification-state-outbox-missing-or-invalid");
        }
        catch (JsonException) { return new([], null, false, "notification-state-malformed"); }
        LogForesight.Core.Models.MailNotifyState? state;
        try { state = JsonSerializer.Deserialize<LogForesight.Core.Models.MailNotifyState>(json, LfJsonOptions.Pretty); }
        catch (JsonException) { return new([], null, false, "notification-state-malformed"); }
        if (state?.UrgentOutbox is null || state.UrgentOutbox.Any(pair => pair.Value is null ||
            string.IsNullOrWhiteSpace(pair.Key) || !string.Equals(pair.Key, pair.Value.Key, StringComparison.Ordinal) ||
            pair.Value.HostId <= 0 || pair.Value.RecordDate == default || string.IsNullOrWhiteSpace(pair.Value.SettingsRevision) ||
            string.IsNullOrWhiteSpace(pair.Value.Status) || pair.Value.ProblemKeys is null || pair.Value.ProblemKeys.Any(key => key is null) ||
            pair.Value.SmtpAcceptedAtUtc is null || pair.Value.Recipients is null ||
            pair.Value.Recipients.Any(recipient => string.IsNullOrWhiteSpace(recipient.Key) || string.IsNullOrWhiteSpace(recipient.Value))))
            return new([], null, false, "notification-state-invalid-outbox");

        var selected = selectedHosts.ToHashSet();
        var matching = state.UrgentOutbox.Values.Where(i => selected.Contains(i.HostId) && i.RecordDate.Date >= from && i.RecordDate.Date <= through)
            .OrderBy(i => i.Key, StringComparer.Ordinal).ToArray();
        var projected = matching.Take(1000).Select(i => (object)new
        {
            i.Key, i.HostId, i.RecordDate, i.SettingsRevision, i.Status, i.UpdatedAtUtc,
            ProblemKeys = i.ProblemKeys ?? [],
            SmtpAcceptedAtUtc = (i.SmtpAcceptedAtUtc ?? []).Values.Order().ToArray(),
            RecipientStatuses = (i.Recipients ?? []).Values.GroupBy(status => status).Select(g => new { Status = g.Key, Count = g.Count() }).ToArray()
        }).ToArray();
        var complete = matching.Length <= 1000;
        return new(projected, matching.Length, complete, complete ? null : "notification-intent-output-cap");
    }

    private sealed class ExportByteLimitException : Exception { }

    private static bool IsExportByteLimitException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is ExportByteLimitException) return true;
        return false;
    }

    private sealed class CappedMemoryStream(long maximumBytes) : MemoryStream
    {
        private void Check(long count)
        {
            if (count < 0 || Position > maximumBytes - count) throw new ExportByteLimitException();
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void SetLength(long value)
        {
            if (value > maximumBytes) throw new ExportByteLimitException();
            base.SetLength(value);
        }
    }
    [HttpGet("incidents")]
    public IActionResult Incidents([FromQuery] long? hostId = null)
    {
        var visible = visibility.GetVisibleHostIds().Where(id => !visibility.IsCaseGrantOnly(id)).ToHashSet();
        if (hostId.HasValue && !visible.Contains(hostId.Value)) return Forbid();
        long[] ids = hostId.HasValue ? [hostId.Value] : visible.Order().ToArray();
        var read = ReadLabels(ids, maximum: hostId.HasValue ? 1000 : MaximumExportLabels);
        var labels = read.Items;
        return Ok(ApiResponse<object>.Ok(new
        {
            Items = labels, ScopeComplete = read.Complete, ScopeStatus = ScopeStatus(read.Complete, read.Reason),
            ItemsTotal = read.Total,
            TimingSummary = read.Complete ? PrtgAcceptanceComparison.TimingSummary(labels) : null,
            Timings = read.Complete ? PrtgAcceptanceComparison.Timings(labels) : null,
            Comparison = read.Complete ? PrtgAcceptanceComparison.Evaluate(labels) : null,
            ComparisonUnavailableReason = read.Complete ? null : "標籤範圍不完整或超過 1,000 筆；不以部分標籤計算比較。"
        }));
    }

    [HttpPut("incidents")]
    public IActionResult Save([FromBody] PrtgAcceptanceIncident incident)
    {
        if (!visibility.GetVisibleHostIds().Contains(incident.HostId) || visibility.IsCaseGrantOnly(incident.HostId)) return Forbid();
        if (string.IsNullOrWhiteSpace(incident.IncidentId) || incident.IncidentId.Length > 128 || (incident.Outcome == "occurred" && incident.OccurredAt == null) ||
            incident.OccurredAt > DateTimeOffset.Now || incident.EvidenceReference == null || incident.EvidenceReference.Length > 1000 ||
            incident.Reason == null || incident.Reason.Length > 1000 || incident.Segment == null || incident.Segment.Length > 512)
            return BadRequest(ApiResponse.Fail("validation_failed", "事故識別、主機必填；實際發生案例須填發生時間；證據與理由最多 1000 字。"));
        if (incident.Outcome is not ("occurred" or "prevented" or "unknown") ||
            incident.ActionDetails == null || incident.ActionDetails.Length > 1000 ||
            incident.BeforeMeasurement == null || incident.BeforeMeasurement.Length > 1000 ||
            incident.AfterMeasurement == null || incident.AfterMeasurement.Length > 1000 ||
            (incident.Outcome != "occurred" && incident.OccurredAt != null) ||
            (incident.Outcome == "prevented" && (!incident.DispositionAt.HasValue ||
                string.IsNullOrWhiteSpace(incident.ActionDetails) || string.IsNullOrWhiteSpace(incident.BeforeMeasurement) ||
                string.IsNullOrWhiteSpace(incident.AfterMeasurement) || string.IsNullOrWhiteSpace(incident.EvidenceReference))))
            return BadRequest(ApiResponse.Fail("validation_failed", "預防案例須填介入時間、具體動作、前後量測與證據；未發生的事故時間請留空。欄位最多 1000 字。"));
        if (incident.CombinedActionableAt.HasValue && (!incident.CombinedEvidenceAvailableAt.HasValue ||
            incident.CombinedEvidenceAvailableAt > incident.CombinedActionableAt || string.IsNullOrWhiteSpace(incident.EvidenceReference)))
            return BadRequest(ApiResponse.Fail("validation_failed", "可行動時間須有當時可取得的證據；不得把事後取得的證據算作提前預警。"));
        if (new[] { incident.NetiqVerificationMinutes, incident.NativePrtgVerificationMinutes,
            incident.SimpleUnionVerificationMinutes, incident.CombinedVerificationMinutes }.Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 0 || v > 1000000)))
            return BadRequest(ApiResponse.Fail("validation_failed", "查證耗時須為非負分鐘數；未知請留空。"));
        var now = DateTimeOffset.Now;
        if (new[] { incident.NetiqActionableAt, incident.NativePrtgActionableAt, incident.SimpleUnionActionableAt,
            incident.CombinedActionableAt, incident.CombinedEvidenceAvailableAt, incident.DispositionAt }.Any(t => t > now))
            return BadRequest(ApiResponse.Fail("validation_failed", "驗收時間不能在未來；未知請留空。"));
        if (string.IsNullOrWhiteSpace(incident.Segment))
            incident.Segment = CurrentSegment();
        incident.ReviewedBy = user.UserId.ToString(); incident.ReviewedAt = DateTimeOffset.UtcNow;
        try
        {
            backend.Blob(LabelPrefix + incident.HostId).MutateWithContext((_, json) =>
            {
                List<PrtgAcceptanceIncident?>? rows;
                try { rows = json is null ? [] : JsonSerializer.Deserialize<List<PrtgAcceptanceIncident?>>(json); }
                catch (JsonException) { throw DomainException.Conflict("既有查證文件格式錯誤；已拒絕覆寫，請先修復文件。"); }
                if (rows is null || rows.Count > 1000 || rows.Any(row => row is null || row.HostId != incident.HostId))
                    throw DomainException.Conflict("既有查證文件含無效、跨主機或超過 1000 筆的資料；已拒絕覆寫。");
                var list = rows.Select(row => row!).ToList();
                var old = list.FindIndex(i => i.IncidentId == incident.IncidentId);
                if (old >= 0) list[old] = incident;
                else { if (list.Count >= 1000) throw DomainException.Validation("每台主機最多 1000 筆查證事故。"); list.Add(incident); }
                var serialized = JsonSerializer.Serialize(list);
                if (serialized.Length > MaximumLabelCharacters || Encoding.UTF8.GetByteCount(serialized) > MaximumLabelUtf8Bytes)
                    throw DomainException.Validation("查證標籤文件超過有界儲存上限。");
                return (serialized, true);
            }, maxCurrentCharacters: MaximumLabelCharacters);
        }
        catch (InvalidDataException)
        {
            return Conflict(ApiResponse.Fail(ApiErrorCodes.Conflict, "既有查證文件超過有界儲存上限；已拒絕覆寫。"));
        }
        audit.Record("prtg_acceptance_label", "保存人工事故查證與各基準可行動時間，不改正式風險、交辦或通知。", "prtg", incident.IncidentId);
        return Incidents(incident.HostId);
    }

    private static string[] ScopeStatus(bool complete, params string?[] reasons) => complete
        ? [] : reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal).ToArray();

    private LabelRead ReadLabels(IEnumerable<long> ids, DateTime? from = null, DateTime? through = null, int maximum = MaximumExportLabels)
    {
        var items = new List<PrtgAcceptanceIncident>(Math.Min(maximum, MaximumExportLabels));
        var reasons = new List<string>();
        var total = 0;
        foreach (var hostId in ids.Distinct().Order())
        {
            var host = ReadLabelHost(hostId);
            if (!host.Complete && host.Reason is not null) reasons.Add(host.Reason);
            if (!host.Complete) continue;
            // Period filtering precedes the global cap, so old out-of-period rows do not crowd out evidence.
            foreach (var item in host.Items.Where(item =>
                (!from.HasValue || LabelDate(item) >= from.Value) && (!through.HasValue || LabelDate(item) <= through.Value)))
            {
                total++;
                if (items.Count < maximum) items.Add(item);
                else { reasons.Add("label-output-cap"); break; }
            }
            if (reasons.Contains("label-output-cap")) break;
        }
        var complete = reasons.Count == 0;
        return new(items, complete ? total : null, complete, reasons.Count == 0 ? null : string.Join(",", reasons.Distinct(StringComparer.Ordinal)));
    }

    private static DateTime LabelDate(PrtgAcceptanceIncident item) =>
        TimeZoneInfo.ConvertTime(item.OccurredAt ?? item.DispositionAt ?? item.ReviewedAt, TimeZoneInfo.Local).Date;

    private LabelHostRead ReadLabelHost(long hostId)
    {
        var (prefix, _, reportedLength) = backend.Blob(LabelPrefix + hostId).ReadBoundedWithVersion(MaximumLabelCharacters);
        if (prefix is null) return new([], false, "label-document-missing");
        if (reportedLength > MaximumLabelCharacters || Encoding.UTF8.GetByteCount(prefix) > MaximumLabelUtf8Bytes)
            return new([], false, "label-document-oversized");
        List<PrtgAcceptanceIncident?>? rows;
        try { rows = JsonSerializer.Deserialize<List<PrtgAcceptanceIncident?>>(prefix); }
        catch (JsonException) { return new([], false, "label-document-malformed"); }
        if (rows is null || rows.Count > 1000 || rows.Any(row => row is null))
            return new([], false, "label-document-invalid-or-host-cap-exceeded");
        var valid = rows.Where(row => row!.HostId == hostId).Select(row => row!).ToArray();
        var forged = valid.Length != rows.Count;
        return new(valid, !forged, forged ? "label-embedded-host-mismatch" : null);
    }

    private TimelineRead ReadTimelines(IEnumerable<long> sensorIds, long[] selectedHosts, bool fullPolicyVisibility)
    {
        var visibleSelected = selectedHosts.ToHashSet();
        var ids = sensorIds.Where(id => id > 0).Distinct().Order().ToArray();
        var items = new List<object>(ids.Length);
        var unknown = 0;
        var visibleAssigned = 0;
        var unattributed = false;
        foreach (var page in ids.Chunk(100))
        {
            var current = backend.PrtgStore().GetResourceIdentities(page);
            // In a partial grant, fetch timeline blobs only for current identities already proven in the visible policy scope.
            var readableIds = page.Where(id => current.TryGetValue(id, out var identity)
                ? visibleSelected.Contains(identity.HostId) : fullPolicyVisibility).ToArray();
            var evidence = PrtgSensorTimelineStore.ReadManyBoundedForSilentRule(backend.CreateContext, readableIds);
            foreach (var id in page)
            {
                var hasIdentity = current.TryGetValue(id, out var identity);
                var assigned = hasIdentity && visibleSelected.Contains(identity!.HostId);
                if (!assigned && !fullPolicyVisibility)
                {
                    if (!hasIdentity) unattributed = true;
                    // Known out-of-scope hosts are skipped. An unassigned sensor in a partial grant is not countable or enumerable.
                    continue;
                }
                if (!assigned && fullPolicyVisibility && hasIdentity)
                {
                    // An anomalous current mapping can point outside the selected policy scope; retain incompleteness without its ID.
                    unattributed = true;
                    continue;
                }
                if (assigned) visibleAssigned++;
                evidence.TryGetValue(id, out var timeline);
                var matches = hasIdentity && timeline is not null && timeline.SensorId == id && timeline.HostId == identity!.HostId;
                if (!matches)
                {
                    unknown++;
                    // IDs/host IDs are included only when current ownership proves this selected visible row (or the complete policy is visible).
                    items.Add(new { SensorId = assigned || fullPolicyVisibility ? id : (long?)null,
                        HostId = assigned ? identity!.HostId : (long?)null, Status = "unknown",
                        Reason = !hasIdentity ? "current-identity-missing" : timeline is null ? "missing-oversized-or-malformed" : "identity-or-host-mismatch" });
                    continue;
                }
                items.Add(new { SensorId = id, HostId = identity!.HostId, Status = "known", timeline.SourceGeneration, timeline.ResourceGeneration,
                    timeline.IdentityEpoch, timeline.ChannelGeneration, timeline.MappingRevision, timeline.DiskSemanticFingerprint,
                    timeline.DiskSemanticValidFrom, timeline.DiskSemanticCheckedAt, timeline.DiskIncidentStartedAt,
                    timeline.ValidFrom, timeline.LastAttemptAt, timeline.LastCompleteThrough, timeline.BootstrapStatus,
                    timeline.BootstrapPagesRead, timeline.NextAttemptAt, timeline.QualityReason,
                    CoveragePeriods = timeline.Coverage.Count, StateSamples = timeline.States.Count });
            }
        }
        int? selectedTotal = unattributed ? null : fullPolicyVisibility ? ids.Length : visibleAssigned;
        int? unknownTotal = unattributed ? null : unknown;
        return new(items, selectedTotal, unknownTotal, unknown == 0 && !unattributed, unattributed);
    }
}
