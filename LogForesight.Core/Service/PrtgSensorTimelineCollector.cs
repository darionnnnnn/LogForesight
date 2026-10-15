using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>以逐 sensor 水位反向讀取；只有完整碰到重疊邊界才原子提交涵蓋及新水位。</summary>
public sealed class PrtgSensorTimelineCollector(StorageBackend backend, PrtgClient client)
{
    public static readonly TimeSpan IncrementalOverlap = TimeSpan.FromDays(1);
    private const int ProductionPageSize = 1000;
    private readonly int _pageSize = ProductionPageSize;
    private const int MaximumPages = PrtgSensorTimelineEvidence.MaximumBootstrapPagesPerSensor;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BatchLeaseDuration = TimeSpan.FromMinutes(10);
    private readonly string _leaseOwner = Guid.NewGuid().ToString("N");

    internal PrtgSensorTimelineCollector(StorageBackend backend, PrtgClient client, int pageSize)
        : this(backend, client)
    {
        if (pageSize is < 1 or > ProductionPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        _pageSize = pageSize;
    }

    public async Task<PrtgSensorTimelineEvidence> CollectAsync(long sensorId, long hostId,
        PrtgMonitoringPolicy policy, CancellationToken ct,
        PrtgSamplingActivity.BackgroundLease? admission = null)
    {
        var store = new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + sensorId));
        var attemptAt = DateTimeOffset.UtcNow;
        if (policy.SensorIds.Count > PrtgSensorTimelineEvidence.MaximumBootstrapSensors)
            return store.Update(e => { e.LastAttemptAt = attemptAt; e.QualityReason = "timeline-bootstrap-capacity-shortfall"; });
        var scope = policy.EffectiveSensorScope(sensorId, hostId);
        var operationRevision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        if (scope.Length == 0)
            return store.Update(e => { e.LastAttemptAt = attemptAt; e.QualityReason = "outside-effective-scope"; });
        var configuredSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var messageTimeFingerprint = policy.MessageTimeFingerprint(configuredSettings.PrtgUrl);
        if (messageTimeFingerprint.Length == 0)
            return store.Update(e => { e.LastAttemptAt = attemptAt; e.QualityReason = "message-date-format-unconfigured"; });

        var leaseVersion = 0L;
        var acquired = false;
        var scanned = new List<PrtgTimedState>();
        var scannedKeys = new List<string>();
        var pendingNextPage = 0;
        var pendingBoundaryHash = "";
        var bootstrapPagesRead = 0;
        var authorityBefore = backend.PrtgStore().GetResourceIdentity(sensorId);
        PrtgSensorTimelineEvidence initial = store.Update(e =>
        {
            InvalidateLegacyMessageStatus(e, attemptAt);
            InvalidateMessageTimeAuthority(e, messageTimeFingerprint, attemptAt);
            InvalidateMessageTimeCursor(e, messageTimeFingerprint);
            if (e.SensorId != 0 && ScopeNeedsReset(e, scope, authorityBefore, policy.SourceGeneration, sensorId, hostId))
            {
                // 僅此 sensor 的有效範圍改變時清除舊證據，不以全域修訂重暖其他資源。
                e.Coverage.Clear(); e.States.Clear(); e.PendingStates.Clear(); e.PendingEventKeys.Clear();
                e.PendingNextPage = 0; e.PendingBoundaryHash = ""; e.LastCompleteThrough = null;
                e.ResourceGeneration = Guid.NewGuid().ToString("N"); e.ValidFrom = attemptAt;
                e.BootstrapStartedAt = attemptAt; e.BootstrapDeadlineAt = null; e.BootstrapStatus = "capacity-unverified";
                e.DiskSemanticFingerprint = ""; e.DiskSemanticValidFrom = null; e.DiskSemanticCheckedAt = null;
                e.DiskIncidentStartedAt = null; e.QualityReason = "effective-scope-warmup";
            }
            e.EffectiveScopeFingerprint = scope;
            acquired = e.TryAcquireLease(_leaseOwner, attemptAt, LeaseDuration, out leaseVersion);
            if (!acquired) e.QualityReason = "another-worker-holds-lease";
        });
        if (!acquired) return initial;

        bool BootstrapScopeStillCurrent()
        {
            var currentIdentity = backend.PrtgStore().GetResourceIdentity(sensorId);
            var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            return currentIdentity.Generation == initial.ResourceGeneration &&
                currentIdentity.Epoch == initial.IdentityEpoch &&
                currentIdentity.ChannelGeneration == initial.ChannelGeneration &&
                currentIdentity.ResourceFingerprint == initial.IdentityFingerprint &&
                currentPolicy.SourceGeneration == policy.SourceGeneration &&
                currentPolicy.MessageTimeFingerprint(currentSettings.PrtgUrl) == messageTimeFingerprint &&
                currentPolicy.EffectiveSensorScope(sensorId, hostId) == scope && currentSettings.PrtgEnabled &&
                currentPolicy.EndpointHint == EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl);
        }

        using var linked = admission == null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, admission.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        var token = linked.Token;
        var preservePrefix = true;
        try
        {
            if (!policy.SensorIds.Contains(sensorId) || !policy.HostIds.Contains(hostId))
                throw new InvalidOperationException("outside-effective-scope");
            TimeZoneInfo sourceZone;
            CultureInfo sourceCulture;
            try
            {
                sourceZone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
                sourceCulture = CultureInfo.GetCultureInfo(policy.SourceCultureName);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw new InvalidOperationException("source-time-zone-or-culture-unverified"); }

            var observed = await ReadIdentity(sensorId, token);
            if (observed.MappingRevision != operationRevision)
                throw new InvalidOperationException("identity-or-scope-changed-during-query");
            var identity = backend.PrtgStore().BindObservedResource(sensorId, hostId, policy.SourceGeneration, observed.Fingerprint);
            attemptAt = DateTimeOffset.UtcNow;
            var fingerprint = observed.Fingerprint;
            initial = store.Update(e =>
            {
                if (!e.OwnsLease(_leaseOwner, leaseVersion, attemptAt)) throw new InvalidOperationException("timeline-lease-lost");
                e.Bind(sensorId, hostId, policy.SourceGeneration, fingerprint, identity.Generation,
                    identity.Epoch, identity.ChannelGeneration, attemptAt);
                e.EffectiveScopeFingerprint = scope;
                e.MappingRevision = operationRevision;
            });

            // 首次身分只從確認時點起算；已收斂 sensor 以一天重疊重新讀取邊界。
            var from = initial.LastCompleteThrough is { } watermark
                ? Max(initial.ValidFrom, watermark - IncrementalOverlap)
                : initial.ValidFrom;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var timestampStatuses = new Dictionary<DateTimeOffset, string>();
            var ended = false;
            DateTimeOffset? previous = null;
            var firstPage = initial.PendingNextPage > 0 ? initial.PendingNextPage - 1 : 0;
            var expectedBoundary = initial.PendingBoundaryHash;
            var lastPageHash = "";
            bootstrapPagesRead = initial.BootstrapPagesRead;
            var pageBudget = initial.LastCompleteThrough == null
                ? MaximumPages - bootstrapPagesRead : MaximumPages;
            if (initial.LastCompleteThrough == null && initial.BootstrapPagesRead >= MaximumPages)
                throw new InvalidOperationException("timeline-bootstrap-capacity-shortfall");
            for (var pageOffset = 0; pageOffset < pageBudget; pageOffset++)
            {
                var page = firstPage + pageOffset;
                token.ThrowIfCancellationRequested();
                if (!MessageTimeAuthorityStillCurrent(policy, messageTimeFingerprint))
                    throw new InvalidOperationException("message-time-authority-changed-before-page-request");
                if (initial.LastCompleteThrough == null) bootstrapPagesRead++;
                using var doc = JsonDocument.Parse(await client.GetJsonAsync(
                    $"api/table.json?content=messages&id={sensorId}&columns=objid,datetime,status,message&filter_drel=12months&sortby=-datetime&start={page * _pageSize}&count={_pageSize}", token));
                if (!doc.RootElement.TryGetProperty("messages", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("messages-array-missing");
                var count = rows.GetArrayLength();
                if (count == 0) { ended = true; break; }
                var boundaryReached = false;
                var pageKeys = new List<string>(count);
                foreach (var row in rows.EnumerateArray())
                {
                    if (Read(row, "objid") != sensorId.ToString(CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("sensor-or-time-unverified");
                    if (!PrtgMessageDateTimeParser.TryParse(Read(row, "datetime"), policy.MessageDateFormat,
                            sourceCulture, sourceZone, out var at, out var parseReason))
                        throw new InvalidOperationException(parseReason);
                    if (at > attemptAt.AddMinutes(2) || previous.HasValue && at > previous.Value)
                        throw new InvalidOperationException("source-clock-or-order-unverified");
                    previous = at;
                    var messageStatus = ReadMessageStatus(row);
                    var status = messageStatus.State;
                    var rawKey = $"{at:O}|{messageStatus.CategoryKey}|{Read(row, "message")}";
                    pageKeys.Add(rawKey);
                    if (!seen.Add(rawKey))
                        throw new InvalidOperationException("messages-repeated-page-or-ambiguous-event");
                    if (status is null) continue;
                    if (timestampStatuses.TryGetValue(at, out var priorStatus) && priorStatus != status)
                        throw new InvalidOperationException("contradictory-status-at-same-time");
                    timestampStatuses[at] = status;
                    if (at < from) { boundaryReached = true; continue; }
                    var eventKey = $"{at:O}|{status}";
                    if (at <= attemptAt && !initial.PendingEventKeys.Contains(eventKey, StringComparer.Ordinal))
                    {
                        scannedKeys.Add(eventKey);
                        scanned.Add(new(sensorId, at, status, policy.SourceGeneration, initial.ResourceGeneration));
                    }
                }
                var currentPageHash = HashPage(pageKeys, messageTimeFingerprint);
                if (pageOffset == 0 && expectedBoundary.Length > 0 && currentPageHash != expectedBoundary)
                    throw new InvalidOperationException("timeline-resume-boundary-shifted");
                lastPageHash = currentPageHash;
                if (boundaryReached || count < _pageSize) { ended = true; break; }
                pendingNextPage = page + 1;
                pendingBoundaryHash = lastPageHash;
            }
            if (!ended) throw new InvalidOperationException(initial.LastCompleteThrough == null &&
                bootstrapPagesRead >= MaximumPages
                ? "timeline-bootstrap-capacity-shortfall" : "messages-truncated");

            var currentStatus = ReadSensorState(observed.Sensor);
            if (string.IsNullOrWhiteSpace(currentStatus)) throw new InvalidOperationException("state-unavailable");
            var previousStatus = scanned.OrderByDescending(state => state.At).Select(state => state.Status).FirstOrDefault()
                ?? initial.PendingStates.OrderByDescending(state => state.At).Select(state => state.Status).FirstOrDefault()
                ?? initial.States.OrderByDescending(state => state.At).Select(state => state.Status).FirstOrDefault();
            if (previousStatus != currentStatus)
                scanned.Add(new(sensorId, attemptAt, currentStatus, policy.SourceGeneration, initial.ResourceGeneration));

            // 重查資源與政策；查詢途中只要來源／資源／有效範圍改變，整批候選都不得進正式證據。
            var finalIdentity = await ReadIdentity(sensorId, token);
            var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != operationRevision ||
                finalIdentity.Fingerprint != fingerprint ||
                !new PrtgResourceIdentityStore(backend.Blob(PrtgResourceIdentityStore.Prefix + sensorId))
                    .IsCurrent(sensorId, policy.SourceGeneration, identity.Generation, identity.DeviceId, hostId, identity.ChannelGeneration) ||
                currentPolicy.EffectiveSensorScope(sensorId, hostId) != scope ||
                currentPolicy.MessageTimeFingerprint(currentSettings.PrtgUrl) != messageTimeFingerprint ||
                currentPolicy.SourceGeneration != policy.SourceGeneration || !currentSettings.PrtgEnabled ||
                currentPolicy.EndpointHint != EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl))
                throw new InvalidOperationException("identity-or-scope-changed-during-query");

            preservePrefix = false;
            return store.Update(e =>
            {
                var now = DateTimeOffset.UtcNow;
                var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                var latestPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                if (!e.OwnsLease(_leaseOwner, leaseVersion, now) || e.ResourceGeneration != identity.Generation ||
                    e.IdentityEpoch != identity.Epoch || e.ChannelGeneration != identity.ChannelGeneration ||
                    e.EffectiveScopeFingerprint != scope || currentPolicy.EffectiveSensorScope(sensorId, hostId) != scope ||
                    latestPolicy.MessageTimeFingerprint(latestSettings.PrtgUrl) != messageTimeFingerprint)
                    throw new InvalidOperationException("identity-or-scope-changed-during-query");
                var known = e.States.Concat(e.PendingStates).GroupBy(s => s.At).ToDictionary(g => g.Key, g => g.Select(s => s.Status).Distinct().ToArray());
                if (scanned.Any(s => known.TryGetValue(s.At, out var statuses) && statuses.Any(status => status != s.Status)))
                    throw new InvalidOperationException("contradictory-status-at-same-time");
                if (from == attemptAt)
                {
                    // Newly observed identity is only a point anchor, never a positive-duration coverage span.
                    e.States = PrtgSensorTimelineEvidence.CompactStates(e.States.Concat(scanned));
                    e.LastCompleteThrough = attemptAt;
                    e.PendingStates.Clear(); e.PendingEventKeys.Clear();
                    e.PendingNextPage = 0; e.PendingBoundaryHash = "";
                    e.PendingMessageTimeFingerprint = "";
                }
                else e.Accept(from, attemptAt, e.PendingStates.Concat(scanned));
                e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, bootstrapPagesRead);
                if (from == attemptAt) e.QualityReason = "identity-warmup";
                e.LastAttemptAt = attemptAt; e.LastServedAt = attemptAt;
                e.NextAttemptAt = attemptAt.AddDays(1);
                e.ConsecutiveAttempts = 0;
                e.LeaseOwner = ""; e.LeaseUntil = null;
            });
        }
        catch (OperationCanceledException) when (admission?.WasPreempted == true)
        {
            var scopeStillCurrent = BootstrapScopeStillCurrent();
            return store.Update(e =>
            {
                if (scopeStillCurrent && e.OwnsLease(_leaseOwner, leaseVersion, DateTimeOffset.UtcNow) &&
                    e.ResourceGeneration == initial.ResourceGeneration && e.IdentityEpoch == initial.IdentityEpoch &&
                    e.ChannelGeneration == initial.ChannelGeneration && e.EffectiveScopeFingerprint == scope)
                {
                    e.LastAttemptAt = attemptAt; e.LastServedAt = attemptAt; e.QualityReason = "yielded-to-sampling";
                    var pending = PrtgSensorTimelineEvidence.CompactStates(e.PendingStates.Concat(scanned)).ToArray();
                    if (pending.Length <= 20000) e.PendingStates = pending.ToList();
                    e.PendingEventKeys = e.PendingEventKeys.Concat(scannedKeys).Distinct(StringComparer.Ordinal).TakeLast(20000).ToList();
                    if (pendingNextPage > 0) { e.PendingNextPage = pendingNextPage; e.PendingBoundaryHash = pendingBoundaryHash;
                        e.PendingMessageTimeFingerprint = messageTimeFingerprint; }
                    if (e.LastCompleteThrough == null)
                        e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, bootstrapPagesRead);
                    e.LeaseOwner = ""; e.LeaseUntil = null;
                }
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return RecordFailure(store, leaseVersion, attemptAt, "query-time-budget-exceeded", scanned, preservePrefix, scannedKeys, pendingNextPage, pendingBoundaryHash, bootstrapPagesRead); }
        catch (OperationCanceledException)
        {
            var scopeStillCurrent = BootstrapScopeStillCurrent();
            store.Update(e =>
            {
                if (scopeStillCurrent && e.OwnsLease(_leaseOwner, leaseVersion, DateTimeOffset.UtcNow) &&
                    e.ResourceGeneration == initial.ResourceGeneration && e.IdentityEpoch == initial.IdentityEpoch &&
                    e.ChannelGeneration == initial.ChannelGeneration && e.EffectiveScopeFingerprint == scope &&
                    e.LastCompleteThrough == null)
                    e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, bootstrapPagesRead);
            });
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or PrtgClientException)
        {
            var reason = ex is InvalidOperationException ? ex.Message : "source-query-failed";
            return RecordFailure(store, leaseVersion, attemptAt, reason, scanned,
                preservePrefix && reason is not "identity-or-scope-changed-during-query" and not "contradictory-status-at-same-time" and not "timeline-resume-boundary-shifted" and not "message-time-authority-changed-before-page-request",
                scannedKeys, pendingNextPage, pendingBoundaryHash, bootstrapPagesRead);
        }
        finally
        {
            // 失敗／取消會保留完整舊水位及已驗證的暫存前導；租約失效時不覆蓋新 owner。
            store.Update(e =>
            {
                if (e.OwnsLease(_leaseOwner, leaseVersion, DateTimeOffset.UtcNow))
                { e.LeaseOwner = ""; e.LeaseUntil = null; }
            });
        }
    }

    /// <summary>以一組最多五十顆 sensor 共用前後身分快照；兩次快照任一無效時整組不提交涵蓋水位。</summary>
    public async Task<IReadOnlyList<PrtgSensorTimelineEvidence>> CollectBatchAsync(
        IReadOnlyList<PrtgTimelineWorkItem> items, PrtgMonitoringPolicy policy, CancellationToken ct,
        PrtgSamplingActivity.BackgroundLease? admission = null, long? expectedMappingRevision = null,
        string? bootstrapCycleId = null, DateTimeOffset? bootstrapAsOfUtc = null)
    {
        if (items.Count == 0) return [];
        if (items.Count > PrtgResourceGuardProbe.MaxBatchSize || items.Select(x => x.SensorId).Distinct().Count() != items.Count)
            throw new ArgumentException("timeline-batch-size-or-identity-invalid", nameof(items));

        var stores = items.ToDictionary(x => x.SensorId,
            x => new PrtgSensorTimelineStore(backend.Blob(PrtgSensorTimelineStore.Prefix + x.SensorId)));
        var configuredSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
        var messageTimeFingerprint = policy.MessageTimeFingerprint(configuredSettings.PrtgUrl);
        if (messageTimeFingerprint.Length == 0)
            return stores.Values.Select(store => store.Update(e =>
                { e.LastAttemptAt = DateTimeOffset.UtcNow; e.QualityReason = "message-date-format-unconfigured"; })).ToArray();
        var candidates = new List<BatchCandidate>();
        using var linked = admission == null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, admission.Token);
        var token = linked.Token;

        try
        {
            ValidateBatchScope(items, policy);
            var before = await ReadIdentityBatch(items.Select(x => x.SensorId).ToArray(), token);
            if (expectedMappingRevision.HasValue && before.MappingRevision != expectedMappingRevision.Value)
                throw new InvalidOperationException("identity-or-scope-changed-during-query");
            var capturedAt = DateTimeOffset.UtcNow;
            foreach (var item in items)
            {
                var scope = policy.EffectiveSensorScope(item.SensorId, item.HostId);
                var observed = before[item.SensorId];
                var identity = backend.PrtgStore().BindObservedResource(item.SensorId, item.HostId,
                    policy.SourceGeneration, observed.Fingerprint);
                var store = stores[item.SensorId];
                var leaseVersion = 0L;
                var acquired = false;
                var initial = store.Update(e =>
                {
                    ResetForIdentityOrScope(e, item, scope, policy.SourceGeneration, observed.Fingerprint, identity,
                        capturedAt);
                    InvalidateLegacyMessageStatus(e, capturedAt);
                    InvalidateMessageTimeAuthority(e, messageTimeFingerprint, capturedAt);
                    InvalidateMessageTimeCursor(e, messageTimeFingerprint);
                    e.EffectiveScopeFingerprint = scope;
                    acquired = e.TryAcquireLease(_leaseOwner, capturedAt, BatchLeaseDuration, out leaseVersion);
                    if (!acquired) e.QualityReason = "another-worker-holds-lease";
                });
                if (!acquired)
                {
                    MarkBatchUncommitted(stores, candidates, "another-worker-holds-lease", preservePrefix: true);
                    return stores.Values.Select(x => x.Get()).ToArray();
                }
                candidates.Add(new BatchCandidate(item, store, initial, new BatchIdentity(observed.Sensor, observed.Fingerprint,
                    observed.MappingRevision, identity), scope, leaseVersion,
                    capturedAt, initial.LastCompleteThrough is { } watermark
                        ? Max(initial.ValidFrom, watermark - IncrementalOverlap) : initial.ValidFrom,
                    messageTimeFingerprint));
            }

            foreach (var candidate in candidates.ToArray())
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    await CollectMessages(candidate, policy, token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    MarkBatchFailure(candidate, "query-time-budget-exceeded", preservePrefix: true);
                    candidates.Remove(candidate);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or PrtgClientException)
                {
                    var reason = ex is InvalidOperationException ? ex.Message : "source-query-failed";
                    MarkBatchFailure(candidate, reason,
                        reason is not "contradictory-status-at-same-time" and not "timeline-resume-boundary-shifted");
                    candidates.Remove(candidate);
                }
            }

            token.ThrowIfCancellationRequested();
            var after = await ReadIdentityBatch(items.Select(x => x.SensorId).ToArray(), token);
            var currentPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
            var currentSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
            if (currentPolicy.MessageTimeFingerprint(currentSettings.PrtgUrl) != messageTimeFingerprint)
            {
                MarkBatchUncommitted(stores, candidates, "message-time-authority-changed-before-page-request", preservePrefix: false);
                return stores.Values.Select(x => x.Get()).ToArray();
            }
            if (before.MappingRevision != after.MappingRevision ||
                backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != before.MappingRevision ||
                currentPolicy.SourceGeneration != policy.SourceGeneration ||
                items.Any(x => currentPolicy.EffectiveSensorScope(x.SensorId, x.HostId) != policy.EffectiveSensorScope(x.SensorId, x.HostId)) ||
                !currentSettings.PrtgEnabled ||
                currentPolicy.EndpointHint != EfPrtgObservationStore.SourceHintFor(currentSettings.PrtgUrl) ||
                items.Any(x => before[x.SensorId].Fingerprint != after[x.SensorId].Fingerprint ||
                    candidates.Where(c => c.Item.SensorId == x.SensorId).Any(c =>
                        !SameAuthority(backend.PrtgStore().GetResourceIdentity(x.SensorId), c.Identity.Authority))))
            {
                MarkBatchUncommitted(stores, candidates, "identity-or-scope-changed-during-query", preservePrefix: false);
                return stores.Values.Select(x => x.Get()).ToArray();
            }

            foreach (var candidate in candidates)
            {
                var now = DateTimeOffset.UtcNow;
                candidate.Store.Update(e =>
                {
                    var latestSettings = new SystemSettingsStore(backend.Blob("system_settings")).Get();
                    var latestPolicy = new PrtgMonitoringPolicyStore(backend.Blob(PrtgMonitoringPolicyStore.BlobKey)).Get();
                    if (!e.OwnsLease(_leaseOwner, candidate.LeaseVersion, now) ||
                        e.ResourceGeneration != candidate.Initial.ResourceGeneration ||
                        e.EffectiveScopeFingerprint != candidate.Scope ||
                        e.IdentityEpoch != candidate.Identity.Authority.Epoch ||
                        e.ResourceGeneration != candidate.Identity.Authority.Generation ||
                        e.ChannelGeneration != candidate.Identity.Authority.ChannelGeneration ||
                        latestPolicy.MessageTimeFingerprint(latestSettings.PrtgUrl) != candidate.MessageTimeFingerprint)
                        throw new InvalidOperationException("identity-or-scope-changed-during-query");
                    var known = e.States.Concat(e.PendingStates).GroupBy(s => s.At)
                        .ToDictionary(g => g.Key, g => g.Select(s => s.Status).Distinct().ToArray());
                    if (candidate.Scanned.Any(s => known.TryGetValue(s.At, out var statuses) && statuses.Any(status => status != s.Status)))
                        throw new InvalidOperationException("contradictory-status-at-same-time");
                    if (candidate.Initial.LastCompleteThrough is null)
                    {
                        e.MessageTimeFingerprint = candidate.MessageTimeFingerprint;
                        e.States = PrtgSensorTimelineEvidence.CompactStates(e.States.Concat(
                            candidate.Scanned.Where(state => state.At == candidate.CapturedAt)));
                        e.LastCompleteThrough = candidate.CapturedAt;
                        e.PendingStates.Clear(); e.PendingEventKeys.Clear(); e.PendingNextPage = 0; e.PendingBoundaryHash = "";
                        e.PendingMessageTimeFingerprint = "";
                    }
                    else
                    {
                        if (candidate.From < e.ValidFrom) e.ValidFrom = candidate.From;
                        e.Accept(candidate.From, candidate.CapturedAt, e.PendingStates.Concat(candidate.Scanned));
                    }
                    e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, candidate.BootstrapPagesRead);
                    if (!string.IsNullOrWhiteSpace(bootstrapCycleId) && bootstrapCycleId.Length <= 64 &&
                        bootstrapAsOfUtc is { } asOf && e.LastCompleteThrough is { } through && through >= asOf &&
                        e.PendingNextPage == 0)
                        e.BootstrapCollectionCycleId = bootstrapCycleId;
                    if (candidate.Initial.LastCompleteThrough == null) e.QualityReason = "identity-warmup";
                    e.LastAttemptAt = candidate.CapturedAt; e.LastServedAt = candidate.CapturedAt;
                    e.NextAttemptAt = candidate.CapturedAt.AddDays(1); e.ConsecutiveAttempts = 0;
                    e.LeaseOwner = ""; e.LeaseUntil = null;
                });
            }
            return stores.Values.Select(x => x.Get()).ToArray();
        }
        catch (OperationCanceledException)
        {
            var reason = admission?.WasPreempted == true ? "yielded-to-sampling" : "batch-cancelled";
            MarkBatchUncommitted(stores, candidates, reason, preservePrefix: true);
            if (admission?.WasPreempted == true)
                return stores.Values.Select(x => x.Get()).ToArray();
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or PrtgClientException)
        {
            var reason = ex is InvalidOperationException ? ex.Message : "batch-identity-unavailable";
            MarkBatchUncommitted(stores, candidates, reason, preservePrefix: reason != "identity-or-scope-changed-during-query");
            return stores.Values.Select(x => x.Get()).ToArray();
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Store.Update(e =>
                {
                    if (e.OwnsLease(_leaseOwner, candidate.LeaseVersion, DateTimeOffset.UtcNow))
                    { e.LeaseOwner = ""; e.LeaseUntil = null; }
                });
        }
    }

    private void ValidateBatchScope(IReadOnlyList<PrtgTimelineWorkItem> items, PrtgMonitoringPolicy policy)
    {
        if (policy.SensorIds.Count > PrtgSensorTimelineEvidence.MaximumBootstrapSensors)
            throw new InvalidOperationException("timeline-bootstrap-capacity-shortfall");
        foreach (var item in items)
            if (policy.EffectiveSensorScope(item.SensorId, item.HostId).Length == 0)
                throw new InvalidOperationException("outside-effective-scope");
    }

    private void ResetForIdentityOrScope(PrtgSensorTimelineEvidence e, PrtgTimelineWorkItem item,
        string scope, string sourceGeneration, string fingerprint, PrtgResourceIdentity authority,
        DateTimeOffset capturedAt)
    {
        var reset = e.SensorId == 0 || ScopeNeedsReset(e, scope, authority, sourceGeneration, item.SensorId, item.HostId) || e.SourceGeneration != sourceGeneration ||
            e.IdentityFingerprint != fingerprint || e.ResourceGeneration != authority.Generation || e.IdentityEpoch != authority.Epoch ||
            e.ChannelGeneration != authority.ChannelGeneration;
        if (e.SensorId != 0 && reset)
        {
            e.Coverage.Clear(); e.States.Clear(); e.PendingStates.Clear(); e.PendingEventKeys.Clear();
            e.PendingNextPage = 0; e.PendingBoundaryHash = ""; e.PendingMessageTimeFingerprint = ""; e.LastCompleteThrough = null;
            e.ResourceGeneration = Guid.NewGuid().ToString("N"); e.ValidFrom = capturedAt;
            e.BootstrapStartedAt = capturedAt; e.BootstrapDeadlineAt = null;
            e.BootstrapStatus = "capacity-unverified"; e.BootstrapPagesRead = 0; e.DiskSemanticFingerprint = ""; e.DiskSemanticValidFrom = null;
            e.BootstrapCollectionCycleId = "";
            e.DiskSemanticCheckedAt = null; e.DiskIncidentStartedAt = null; e.QualityReason = "identity-or-scope-warmup";
        }
        e.Bind(item.SensorId, item.HostId, sourceGeneration, fingerprint, authority.Generation,
            authority.Epoch, authority.ChannelGeneration, capturedAt);
        if (reset) e.ValidFrom = capturedAt;
    }

    private static void InvalidateLegacyMessageStatus(PrtgSensorTimelineEvidence evidence, DateTimeOffset observedAt)
    {
        if (evidence.MessageStatusSemanticVersion == PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent)
            return;

        // Keep resource identity, ValidFrom, and disk semantic evidence; only message-derived timeline data is stale.
        evidence.States.Clear(); evidence.Coverage.Clear(); evidence.PendingStates.Clear(); evidence.PendingEventKeys.Clear();
        evidence.PendingNextPage = 0; evidence.PendingBoundaryHash = "";
        // A parser refresh is a bootstrap, not a completed watermark. Keeping this null also
        // preserves the bounded initial page budget and makes an exhausted scan explicit.
        evidence.LastCompleteThrough = null;
        evidence.BootstrapStartedAt = observedAt; evidence.BootstrapDeadlineAt = null;
        evidence.BootstrapStatus = "capacity-unverified"; evidence.BootstrapPagesRead = 0;
        evidence.BootstrapCollectionCycleId = "";
        evidence.NextAttemptAt = null;
        evidence.QualityReason = "message-status-semantics-warmup";
        evidence.MessageStatusSemanticVersion = PrtgSensorTimelineEvidence.MessageStatusSemanticVersionCurrent;
    }

    private static void InvalidateMessageTimeAuthority(PrtgSensorTimelineEvidence evidence,
        string currentFingerprint, DateTimeOffset observedAt)
    {
        if (evidence.MessageTimeFingerprint == currentFingerprint) return;
        // Message interpretation is its own authority. Retain resource identity and disk proof,
        // while requiring a fresh message bootstrap from this confirmation time.
        evidence.States.Clear(); evidence.Coverage.Clear(); evidence.PendingStates.Clear(); evidence.PendingEventKeys.Clear();
        evidence.PendingNextPage = 0; evidence.PendingBoundaryHash = ""; evidence.PendingMessageTimeFingerprint = "";
        evidence.LastCompleteThrough = null; evidence.ValidFrom = observedAt;
        evidence.BootstrapStartedAt = observedAt; evidence.BootstrapDeadlineAt = null;
        evidence.BootstrapStatus = "capacity-unverified"; evidence.BootstrapPagesRead = 0;
        evidence.BootstrapCollectionCycleId = ""; evidence.NextAttemptAt = null;
        evidence.MessageTimeFingerprint = currentFingerprint;
        evidence.QualityReason = "message-time-authority-warmup";
    }

    private static void InvalidateMessageTimeCursor(PrtgSensorTimelineEvidence evidence, string currentFingerprint)
    {
        if (evidence.PendingNextPage <= 0 || evidence.PendingMessageTimeFingerprint == currentFingerprint) return;
        evidence.PendingStates.Clear(); evidence.PendingEventKeys.Clear();
        evidence.PendingNextPage = 0; evidence.PendingBoundaryHash = ""; evidence.PendingMessageTimeFingerprint = "";
        evidence.QualityReason = "message-time-cursor-authority-mismatch";
    }

    private static bool ScopeNeedsReset(PrtgSensorTimelineEvidence evidence, string scope,
        PrtgResourceIdentity authority, string sourceGeneration, long sensorId, long hostId)
    {
        if (evidence.EffectiveScopeFingerprint == scope) return false;
        // 舊版尚無逐 sensor scope 欄位；只沿用已具有當前權威 epoch 的證據，絕不追認舊鏡像。
        return !string.IsNullOrEmpty(evidence.EffectiveScopeFingerprint) ||
            !PrtgResourceQualification.IsIdentityCurrent(evidence, authority, sourceGeneration, sensorId, authority.DeviceId, hostId) ||
            evidence.ChannelGeneration != authority.ChannelGeneration;
    }

    private async Task CollectMessages(BatchCandidate candidate, PrtgMonitoringPolicy policy, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        TimeZoneInfo zone;
        CultureInfo culture;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(policy.SourceTimeZoneId);
            culture = CultureInfo.GetCultureInfo(policy.SourceCultureName);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new InvalidOperationException("source-time-zone-or-culture-unverified"); }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var timestampStatuses = new Dictionary<DateTimeOffset, string>();
        PrtgTimedState? precedingBoundaryState = null;
        DateTimeOffset? previous = null;
        var firstPage = candidate.Initial.PendingNextPage > 0 ? candidate.Initial.PendingNextPage - 1 : 0;
        var expectedBoundary = candidate.Initial.PendingBoundaryHash;
        var ended = false;
        var pageBudget = candidate.Initial.LastCompleteThrough == null
            ? MaximumPages - candidate.Initial.BootstrapPagesRead : MaximumPages;
        if (candidate.Initial.LastCompleteThrough == null && candidate.Initial.BootstrapPagesRead >= MaximumPages)
            throw new InvalidOperationException("timeline-bootstrap-capacity-shortfall");
        if (pageBudget <= 0) throw new InvalidOperationException("timeline-bootstrap-capacity-shortfall");
        for (var pageOffset = 0; pageOffset < pageBudget; pageOffset++)
        {
            var page = firstPage + pageOffset;
            token.ThrowIfCancellationRequested();
            if (!MessageTimeAuthorityStillCurrent(policy, candidate.MessageTimeFingerprint))
                throw new InvalidOperationException("message-time-authority-changed-before-page-request");
            if (candidate.Initial.LastCompleteThrough == null) candidate.BootstrapPagesRead++;
            using var doc = JsonDocument.Parse(await client.GetJsonAsync(
                $"api/table.json?content=messages&id={candidate.Item.SensorId}&columns=objid,datetime,status,message&filter_drel=12months&sortby=-datetime&start={page * _pageSize}&count={_pageSize}", token));
            if (!doc.RootElement.TryGetProperty("messages", out var rows) || rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("messages-array-missing");
            var count = rows.GetArrayLength();
            if (count == 0) { ended = true; break; }
            var pageKeys = new List<string>(count);
            var boundaryReached = false;
            foreach (var row in rows.EnumerateArray())
            {
                if (Read(row, "objid") != candidate.Item.SensorId.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("sensor-or-time-unverified");
                if (!PrtgMessageDateTimeParser.TryParse(Read(row, "datetime"), policy.MessageDateFormat,
                        culture, zone, out var at, out var parseReason))
                    throw new InvalidOperationException(parseReason);
                if (at > candidate.CapturedAt.AddMinutes(2) || previous.HasValue && at > previous.Value)
                    throw new InvalidOperationException("source-clock-or-order-unverified");
                previous = at;
                var messageStatus = ReadMessageStatus(row);
                var status = messageStatus.State;
                var key = $"{at:O}|{messageStatus.CategoryKey}|{Read(row, "message")}";
                pageKeys.Add(key);
                if (!seen.Add(key)) throw new InvalidOperationException("messages-repeated-page-or-ambiguous-event");
                if (status is null)
                    continue;
                if (timestampStatuses.TryGetValue(at, out var priorStatus) && priorStatus != status)
                    throw new InvalidOperationException("contradictory-status-at-same-time");
                timestampStatuses[at] = status;
                if (at < candidate.From)
                {
                    if (candidate.Initial.LastCompleteThrough is not null)
                        precedingBoundaryState ??= new(candidate.Item.SensorId, at, status,
                            policy.SourceGeneration, candidate.Initial.ResourceGeneration);
                    boundaryReached = true;
                    continue;
                }
                var eventKey = $"{at:O}|{status}";
                if (at <= candidate.CapturedAt && !candidate.Initial.PendingEventKeys.Contains(eventKey, StringComparer.Ordinal))
                {
                    candidate.ScannedKeys.Add(eventKey);
                    candidate.Scanned.Add(new(candidate.Item.SensorId, at, status, policy.SourceGeneration, candidate.Initial.ResourceGeneration));
                }
            }
            var hash = HashPage(pageKeys, candidate.MessageTimeFingerprint);
            if (pageOffset == 0 && expectedBoundary.Length > 0 && hash != expectedBoundary)
                throw new InvalidOperationException("timeline-resume-boundary-shifted");
            if (boundaryReached || count < _pageSize) { ended = true; break; }
            candidate.PendingNextPage = page + 1; candidate.PendingBoundaryHash = hash;
        }
        if (!ended)
            throw new InvalidOperationException(candidate.Initial.LastCompleteThrough == null &&
                candidate.BootstrapPagesRead >= MaximumPages
                ? "timeline-bootstrap-capacity-shortfall" : "messages-truncated");

        if (precedingBoundaryState is { } anchor &&
            !candidate.Initial.PendingEventKeys.Contains($"{anchor.At:O}|{anchor.Status}", StringComparer.Ordinal))
        {
            candidate.Scanned.Add(anchor);
            candidate.ScannedKeys.Add($"{anchor.At:O}|{anchor.Status}");
            candidate.ExtendFrom(anchor.At);
        }

        var startStatus = ReadSensorState(candidate.Identity.Sensor);
        if (string.IsNullOrWhiteSpace(startStatus)) throw new InvalidOperationException("state-unavailable");
        var previousStatus = candidate.Scanned.OrderByDescending(s => s.At).Select(s => s.Status).FirstOrDefault()
            ?? candidate.Initial.PendingStates.OrderByDescending(s => s.At).Select(s => s.Status).FirstOrDefault()
            ?? candidate.Initial.States.OrderByDescending(s => s.At).Select(s => s.Status).FirstOrDefault();
        if (previousStatus != startStatus)
            candidate.Scanned.Add(new(candidate.Item.SensorId, candidate.CapturedAt, startStatus,
                policy.SourceGeneration, candidate.Initial.ResourceGeneration));
    }

    private bool MessageTimeAuthorityStillCurrent(PrtgMonitoringPolicy policy, string fingerprint)
    {
        return PrtgSharedAuthoritySnapshot.TryRead(backend, out var snapshot, out _) && snapshot is not null &&
            snapshot.Settings.PrtgEnabled && snapshot.Policy.SourceGeneration == policy.SourceGeneration &&
            snapshot.Policy.MessageTimeFingerprint(snapshot.Settings.PrtgUrl) == fingerprint;
    }

    private async Task<BatchIdentitySnapshot> ReadIdentityBatch(long[] sensorIds, CancellationToken ct)
    {
        var revision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var query = PrtgResourceGuardProbe.BuildObjidFilter(sensorIds);
        using var doc = JsonDocument.Parse(await client.GetJsonAsync(
            $"/api/table.json?content=sensors&columns=objid,parentid,type,status,status_raw,cumsince{query}", ct));
        if (!doc.RootElement.TryGetProperty("sensors", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("sensor-identity-batch-unavailable");
        var requested = sensorIds.ToHashSet();
        var found = new Dictionary<long, BatchIdentity>();
        foreach (var sensor in rows.EnumerateArray())
        {
            var objidText = Read(sensor, "objid");
            if (!long.TryParse(objidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || !requested.Contains(id))
                throw new InvalidOperationException("sensor-identity-batch-out-of-scope");
            var parent = Read(sensor, "parentid"); var type = Read(sensor, "type");
            var since = Read(sensor, "cumsince_raw"); if (since.Length == 0) since = Read(sensor, "cumsince");
            var status = ReadSensorState(sensor);
            if (parent.Length == 0 || type.Length == 0 || since.Length == 0 || status.Length == 0)
                throw new InvalidOperationException("sensor-identity-batch-schema-invalid");
            if (found.ContainsKey(id)) throw new InvalidOperationException("sensor-identity-batch-duplicate");
            var fingerprint = PrtgTimelineResourceIdentity.BuildResourceFingerprint(parent, type, since, revision);
            found.Add(id, new BatchIdentity(sensor.Clone(), fingerprint, revision));
        }
        if (found.Count != requested.Count || requested.Any(id => !found.ContainsKey(id)))
            throw new InvalidOperationException("sensor-identity-batch-missing");
        if (backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != revision)
            throw new InvalidOperationException("identity-or-scope-changed-during-query");
        return new BatchIdentitySnapshot(found, revision);
    }

    private void MarkBatchFailure(BatchCandidate candidate, string reason, bool preservePrefix)
    {
        candidate.Store.Update(e =>
        {
            if (!e.OwnsLease(_leaseOwner, candidate.LeaseVersion, DateTimeOffset.UtcNow)) return;
            e.LastAttemptAt = candidate.CapturedAt; e.LastServedAt = candidate.CapturedAt; e.QualityReason = reason;
            if (reason is not "yielded-to-sampling" and not "batch-cancelled")
            {
                e.ConsecutiveAttempts = Math.Min(20, e.ConsecutiveAttempts + 1);
                e.NextAttemptAt = candidate.CapturedAt + (reason == "messages-truncated" ? TimeSpan.FromMinutes(1) : RetryDelay(e.ConsecutiveAttempts));
            }
            if (preservePrefix && candidate.Scanned.Count > 0)
            {
                var pending = PrtgSensorTimelineEvidence.CompactStates(e.PendingStates.Concat(candidate.Scanned)).ToArray();
                if (pending.Length <= 20000) e.PendingStates = pending.ToList();
                else e.QualityReason = "timeline-state-capacity-exceeded";
                e.PendingEventKeys = e.PendingEventKeys.Concat(candidate.ScannedKeys).Distinct(StringComparer.Ordinal).TakeLast(20000).ToList();
            }
            if (e.LastCompleteThrough == null)
                e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, candidate.BootstrapPagesRead);
            if (reason == "message-time-authority-changed-before-page-request")
            {
                e.PendingStates.Clear(); e.PendingEventKeys.Clear();
                e.PendingNextPage = 0; e.PendingBoundaryHash = ""; e.PendingMessageTimeFingerprint = "";
            }
            else if (candidate.PendingNextPage > 0 && reason != "timeline-resume-boundary-shifted")
            { e.PendingNextPage = candidate.PendingNextPage; e.PendingBoundaryHash = candidate.PendingBoundaryHash;
                e.PendingMessageTimeFingerprint = candidate.MessageTimeFingerprint; }
            else if (reason == "timeline-resume-boundary-shifted")
            { e.PendingNextPage = 0; e.PendingBoundaryHash = ""; e.PendingMessageTimeFingerprint = ""; }
            if (reason == "timeline-bootstrap-capacity-shortfall") e.BootstrapStatus = "capacity-shortfall";
            e.LeaseOwner = ""; e.LeaseUntil = null;
        });
    }

    private void MarkBatchUncommitted(IReadOnlyDictionary<long, PrtgSensorTimelineStore> stores,
        IEnumerable<BatchCandidate> candidates, string reason, bool preservePrefix)
    {
        var candidatesBySensor = candidates.ToDictionary(x => x.Item.SensorId);
        foreach (var pair in stores)
        {
            if (candidatesBySensor.TryGetValue(pair.Key, out var candidate))
                MarkBatchFailure(candidate, reason, preservePrefix);
            else
                pair.Value.Update(e =>
                {
                    var now = DateTimeOffset.UtcNow;
                    e.LastAttemptAt = now; e.QualityReason = reason;
                    if (reason is not "yielded-to-sampling" and not "batch-cancelled" and not "another-worker-holds-lease")
                    {
                        e.ConsecutiveAttempts = Math.Min(20, e.ConsecutiveAttempts + 1);
                        e.NextAttemptAt = now + RetryDelay(e.ConsecutiveAttempts);
                    }
                });
        }
    }

    private sealed record BatchIdentity(JsonElement Sensor, string Fingerprint, long MappingRevision,
        PrtgResourceIdentity? Authority = null);
    private sealed record BatchIdentitySnapshot(IReadOnlyDictionary<long, BatchIdentity> Sensors, long MappingRevision)
    {
        public BatchIdentity this[long id] => Sensors[id];
    }

    private sealed class BatchCandidate(PrtgTimelineWorkItem item, PrtgSensorTimelineStore store,
        PrtgSensorTimelineEvidence initial, BatchIdentity identity, string scope, long leaseVersion,
        DateTimeOffset capturedAt, DateTimeOffset from, string messageTimeFingerprint)
    {
        public PrtgTimelineWorkItem Item { get; } = item;
        public PrtgSensorTimelineStore Store { get; } = store;
        public PrtgSensorTimelineEvidence Initial { get; } = initial;
        public BatchIdentity Identity { get; } = identity;
        public string Scope { get; } = scope;
        public long LeaseVersion { get; } = leaseVersion;
        public DateTimeOffset CapturedAt { get; } = capturedAt;
        public DateTimeOffset From { get; private set; } = from;
        public string MessageTimeFingerprint { get; } = messageTimeFingerprint;
        public List<PrtgTimedState> Scanned { get; } = [];
        public List<string> ScannedKeys { get; } = [];
        public int PendingNextPage { get; set; }
        public int BootstrapPagesRead { get; set; } = initial.BootstrapPagesRead;
        public string PendingBoundaryHash { get; set; } = "";
        public void ExtendFrom(DateTimeOffset earlier) { if (earlier < From) From = earlier; }
    }

    private PrtgSensorTimelineEvidence RecordFailure(PrtgSensorTimelineStore store, long leaseVersion,
        DateTimeOffset attemptAt, string reason, IReadOnlyCollection<PrtgTimedState> scanned, bool preservePrefix,
        IReadOnlyCollection<string> scannedKeys, int pendingNextPage, string pendingBoundaryHash, int bootstrapPagesRead) =>
        store.Update(e =>
        {
            if (!e.OwnsLease(_leaseOwner, leaseVersion, DateTimeOffset.UtcNow)) return;
            e.LastAttemptAt = attemptAt; e.LastServedAt = attemptAt; e.QualityReason = reason;
            e.ConsecutiveAttempts = Math.Min(20, e.ConsecutiveAttempts + 1);
            e.NextAttemptAt = attemptAt + (reason == "messages-truncated" ? TimeSpan.FromMinutes(1) : RetryDelay(e.ConsecutiveAttempts));
            if (preservePrefix && e.ResourceGeneration.Length > 0 && scanned.Count > 0)
            {
                var pending = PrtgSensorTimelineEvidence.CompactStates(e.PendingStates.Concat(scanned)).ToArray();
                if (pending.Length <= 20000) e.PendingStates = pending.ToList();
                else e.QualityReason = "timeline-state-capacity-exceeded";
                e.PendingEventKeys = e.PendingEventKeys.Concat(scannedKeys).Distinct(StringComparer.Ordinal).TakeLast(20000).ToList();
            }
            if (preservePrefix && pendingNextPage > 0 && reason != "timeline-resume-boundary-shifted")
            { e.PendingNextPage = pendingNextPage; e.PendingBoundaryHash = pendingBoundaryHash;
                e.PendingMessageTimeFingerprint = e.MessageTimeFingerprint; }
            else if (reason == "message-time-authority-changed-before-page-request")
            { e.PendingStates.Clear(); e.PendingEventKeys.Clear(); e.PendingNextPage = 0;
                e.PendingBoundaryHash = ""; e.PendingMessageTimeFingerprint = ""; }
            else if (reason == "timeline-resume-boundary-shifted")
            { e.PendingNextPage = 0; e.PendingBoundaryHash = ""; e.PendingMessageTimeFingerprint = ""; }
            if (e.LastCompleteThrough == null)
                e.BootstrapPagesRead = Math.Max(e.BootstrapPagesRead, bootstrapPagesRead);
            if (reason == "timeline-bootstrap-capacity-shortfall") e.BootstrapStatus = "capacity-shortfall";
            e.LeaseOwner = ""; e.LeaseUntil = null;
        });

    private static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));

    private async Task<(JsonElement Sensor, string Fingerprint, long MappingRevision)> ReadIdentity(long sensorId, CancellationToken ct)
    {
        using var sensorDoc = JsonDocument.Parse(await client.GetJsonAsync(
            $"api/table.json?content=sensors&id={sensorId}&columns=objid,parentid,type,status,status_raw,cumsince&count=2", ct));
        if (!sensorDoc.RootElement.TryGetProperty("sensors", out var sensors) || sensors.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("sensor-identity-unavailable");
        var exact = sensors.EnumerateArray().Where(s => Read(s, "objid") == sensorId.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (exact.Length != 1) throw new InvalidOperationException("sensor-identity-not-unique");
        var sensor = exact[0];
        var parent = Read(sensor, "parentid"); var type = Read(sensor, "type");
        var since = Read(sensor, "cumsince_raw");
        if (string.IsNullOrWhiteSpace(since)) since = Read(sensor, "cumsince");
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(since))
            throw new InvalidOperationException("resource-generation-unverified");
        var revision = backend.Blob(EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var fingerprint = PrtgTimelineResourceIdentity.BuildResourceFingerprint(parent, type, since, revision);
        return (sensor.Clone(), fingerprint, revision);
    }

    private static bool SameAuthority(PrtgResourceIdentity left, PrtgResourceIdentity? right) => right is not null &&
        left.SensorId == right.SensorId && left.Epoch == right.Epoch && left.Generation == right.Generation &&
        left.SourceGeneration == right.SourceGeneration && left.DeviceId == right.DeviceId && left.HostId == right.HostId &&
        left.ChannelGeneration == right.ChannelGeneration && left.Active == right.Active &&
        left.PendingReconciliation == right.PendingReconciliation;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static string HashPage(IEnumerable<string> keys, string messageTimeFingerprint) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(messageTimeFingerprint + "\n" + string.Join("\n", keys))));
    private static string Read(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString() : "";

    private static string ReadSensorState(JsonElement row)
    {
        var raw = Read(row, "status_raw"); var text = Read(row, "status");
        return PrtgSensorStatuses.TryParseSourceStatus(raw, text, out var status) ? status : string.Empty;
    }

    private static (string? State, string CategoryKey) ReadMessageStatus(JsonElement row)
    {
        var raw = Read(row, "status_raw"); var text = Read(row, "status");
        if (raw.Length > 0)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new InvalidOperationException("message-status-code-unrecognized");
            var state = code switch
            {
                601 => "Unknown", 602 => "Resuming", 603 => "Pausing", 604 or 605 or 606 => "Paused",
                607 => "Up", 608 => "Down", 609 => "Warning", 610 => "Unusual", 611 => "OK",
                612 => "Connected", 613 => "Disconnected", 614 => "Info", 615 => "Paused License Limit",
                616 => "No Probe", 617 => "None", 618 => "Active", 619 => "Down (Acknowledged)",
                620 => "Down (Partial)", _ => null
            };
            if (state is not null) return (state, raw);
            if (IsKnownNonStateMessageCategory(code)) return (null, raw);
            throw new InvalidOperationException("message-status-code-unrecognized");
        }

        var fallback = text.Trim();
        var canonical = fallback.ToLowerInvariant() switch
        {
            "unknown" => "Unknown", "resuming" => "Resuming", "pausing" => "Pausing", "paused" => "Paused",
            "up" => "Up", "down" => "Down", "warning" => "Warning", "unusual" => "Unusual", "ok" => "OK",
            "connected" => "Connected", "disconnected" => "Disconnected", "info" => "Info",
            "paused license limit" => "Paused License Limit", "no probe" => "No Probe", "none" => "None",
            "active" => "Active", "down acknowledged" or "down (acknowledged)" => "Down (Acknowledged)",
            "down partial" or "down (partial)" => "Down (Partial)",
            _ => null
        };
        return canonical is null
            ? throw new InvalidOperationException("message-status-text-unrecognized")
            : (canonical, "text:" + canonical);
    }

    private static bool IsKnownNonStateMessageCategory(int code) =>
        code is >= 1 and <= 14 or >= 101 and <= 109 or >= 201 and <= 204 or >= 301 and <= 303 or
            >= 401 and <= 407 or >= 501 and <= 514 or >= 701 and <= 731 or >= 801 and <= 806 or
            >= 851 and <= 859 or >= 901 and <= 902;
}

/// <summary>來源物件的穩定指紋；全域修訂只在 collection operation 前後作取消 fence。</summary>
public static class PrtgTimelineResourceIdentity
{
    public static string BuildResourceFingerprint(string parent, string type, string since, long mappingRevision)
    {
        // Global scope revision is an operation fence only; unrelated map edits cannot invalidate this resource.
        var material = $"{parent}|{type}|{since}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
