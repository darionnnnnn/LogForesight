using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Bounded, read-only assessment of persisted disk history. It never contacts PRTG or publishes findings.</summary>
public sealed class PrtgDiskAssessmentService
{
    public const int MaximumBatchSize = 100;
    public const int MaximumCandidateSnapshotSize = 15_000;
    public const int MaximumExpectedHistoricalPointsPerBatch = 100_000;
    public const int CandidateMappingLookbackDays = 30;
    public const string ParserSemanticVersion = "disk-semantic-v1";
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hosts;
    private readonly ISystemSettingsStore _settings;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore? _verificationResults;
    private readonly object _operationOwner = new();

    private PrtgDiskMetadataSnapshot CaptureMetadataSnapshot()
    {
        return PrtgDiskMetadataSnapshot.Capture(_evidence, _verificationResults, _store, () =>
        {
            var settings = _settings.Get();
            var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
            var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
            return _store.GetTrustedSamplingPolicyContext(strategyName, strategyMinutes);
        });
    }

    public PrtgDiskAssessmentService(EfPrtgStore store, IHostStore hosts, ISystemSettingsStore settings,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore? verificationResults = null)
    { _store = store; _hosts = hosts; _settings = settings; _evidence = evidence; _verificationResults = verificationResults; }

    public PrtgDiskAssessmentBatch Assess(DateOnly completedDay, KnownIssueRule? rule,
        PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Preview, int limit = MaximumBatchSize, int offset = 0,
        IReadOnlyCollection<long>? selectedHostIds = null, IReadOnlyCollection<long>? selectedSensorObjids = null) =>
        AssessCandidatePage(completedDay, rule, mode, limit, offset, selectedHostIds, selectedSensorObjids,
            candidateSnapshot: null, candidateMappingRevision: null, candidatePolicyVersion: null);

    /// <summary>
    /// Opens one bounded assessment operation. A caller may pass the immutable host snapshot already
    /// captured by its HTTP request so authorization, names, and candidate scope share one version.
    /// Candidate count is captured here and is never accepted from the caller.
    /// </summary>
    public PrtgDiskAssessmentOperation BeginAssessment(DateOnly completedDay, DateTime candidateMappingThrough,
        KnownIssueRule? rule = null, PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Preview,
        IReadOnlyCollection<long>? selectedHostIds = null, IReadOnlyCollection<long>? selectedSensorObjids = null,
        PrtgHostSnapshot? capturedHostSnapshot = null)
    {
        if (completedDay.ToDateTime(TimeOnly.MinValue).Date >= DateTime.Today)
            throw new ArgumentOutOfRangeException(nameof(completedDay), "僅允許評估已完成日期。");
        var completed = completedDay.ToDateTime(TimeOnly.MinValue).Date;
        var mappingThrough = candidateMappingThrough.Date;
        if (mappingThrough < completed || mappingThrough > DateTime.Today)
            throw new ArgumentOutOfRangeException(nameof(candidateMappingThrough), "候選映射日必須介於完成日與今天之間。");

        var hostSnapshot = capturedHostSnapshot ?? _hosts.CapturePrtgSnapshot();
        var hostVersion = hostSnapshot.Version;
        if (_hosts.DataVersion != hostVersion)
            throw new InvalidOperationException("PRTG 主機清單版本已變更；請重新開始評估。");
        var selectedHosts = selectedHostIds?.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        var selected = selectedHosts?.ToHashSet();
        var activeHostIds = hostSnapshot.Hosts.Where(h => h.Active && h.MergedInto == null &&
                (selected is null || selected.Contains(h.HostId)))
            .Select(h => h.HostId).OrderBy(id => id).ToArray();
        var selectedSensors = selectedSensorObjids?.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        var settings = _settings.Get();
        var whitelist = (settings.PrtgSensorTypeWhitelist ?? new List<string>()).ToArray();
        var settingsRevision = settings.Revision ?? string.Empty;
        var settingsFingerprint = Fingerprint(string.Join("\n", whitelist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        var ruleFingerprint = Fingerprint(rule is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(rule));
        var mappingFrom = mappingThrough.AddDays(-CandidateMappingLookbackDays);
        var hostMapRevision = _store.ReadHostMapDataRevision();
        var catalogueRevision = _store.ReadCatalogueDataRevision();
        var captured = activeHostIds.Length == 0 || selectedSensors is { Length: 0 }
            ? (0, new List<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)>())
            : _store.GetLatestMappedReadinessSensorSnapshot(activeHostIds, mappingThrough, mappingFrom,
                MaximumCandidateSnapshotSize, selectedSensors);
        if (hostMapRevision != _store.ReadHostMapDataRevision() || catalogueRevision != _store.ReadCatalogueDataRevision())
            throw new InvalidOperationException("PRTG 主機映射或感測器目錄於候選快照擷取期間改變；請重新開始評估。");

        var candidateSnapshot = new PrtgDiskCandidateSnapshot(completedDay, mappingThrough, activeHostIds,
            selectedSensorObjids is null, selectedSensors, null, ruleFingerprint, hostMapRevision,
            captured.Item1, captured.Item2, hostSnapshot, catalogueRevision);
        var metadataSnapshot = CaptureMetadataSnapshot();
        if (_hosts.DataVersion != hostVersion)
            throw new InvalidOperationException("PRTG 主機清單版本於候選快照擷取期間改變；請重新開始評估。");

        return new PrtgDiskAssessmentOperation(_operationOwner, completedDay, mappingThrough, rule, mode,
            selectedHosts, selectedSensors, hostSnapshot, activeHostIds, hostVersion, settingsRevision,
            settingsFingerprint, whitelist, ruleFingerprint, candidateSnapshot, metadataSnapshot);
    }

    /// <summary>Assess one page using the private immutable candidate and metadata snapshots captured by BeginAssessment.</summary>
    public PrtgDiskAssessmentBatch AssessPage(PrtgDiskAssessmentOperation operation, int offset, int limit)
    {
        ValidateOperationOwner(operation);
        if (operation.IsCompleted) throw new InvalidOperationException("PRTG 評估作業已完成；不能再讀取頁面。");
        return AssessCandidatePage(operation.CompletedDay, operation.Rule, operation.Mode, limit, offset,
            operation.SelectedHostIds, operation.SelectedSensorObjids, operation.CandidateSnapshot,
            candidatePolicyVersion: operation.RuleFingerprint, metadataSnapshot: operation.MetadataSnapshot,
            deferMetadataFence: true, capturedHostSnapshot: operation.HostSnapshot,
            capturedWhitelist: operation.CapturedWhitelist, candidateMappingThrough: operation.CandidateMappingThrough);
    }

    /// <summary>Final fence for the complete Web response; call only after every page/detail has been mapped.</summary>
    public void CompleteAssessment(PrtgDiskAssessmentOperation operation)
    {
        ValidateOperationOwner(operation);
        if (operation.IsCompleted) throw new InvalidOperationException("PRTG 評估作業已完成。");
        if (_store.ReadHostMapDataRevision() != operation.CandidateSnapshot.HostMapDataRevision ||
            _store.ReadCatalogueDataRevision() != operation.CandidateSnapshot.CatalogueDataRevision)
            throw new InvalidOperationException("PRTG 主機映射或感測器目錄於準備度頁面計算期間改變；已拒絕回傳整頁，請重試。");
        ValidateMetadataSnapshot(operation.MetadataSnapshot);
        var currentSettings = _settings.Get();
        var currentSettingsFingerprint = Fingerprint(string.Join("\n", (currentSettings.PrtgSensorTypeWhitelist ?? new List<string>())
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        if (!string.Equals(currentSettings.Revision ?? string.Empty, operation.SettingsRevision, StringComparison.Ordinal) ||
            !string.Equals(currentSettingsFingerprint, operation.SettingsFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("系統準備度設定於頁面計算期間改變；已拒絕回傳整頁，請重試。");
        if (_hosts.DataVersion != operation.HostVersion)
            throw new InvalidOperationException("PRTG 主機授權或顯示名稱於頁面計算期間改變；已拒絕回傳整頁，請重試。");
        if (!string.Equals(Fingerprint(operation.Rule is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(operation.Rule)),
                operation.RuleFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("PRTG 評估規則於頁面計算期間改變；已拒絕回傳整頁，請重試。");
        operation.MarkCompleted();
    }

    /// <summary>Captures one immutable set of range-wide candidates' denominators and metadata fences.</summary>
    public PrtgDiskAssessmentRangeOperation BeginRangeAssessment(DateOnly fromDate, DateOnly throughDate,
        KnownIssueRule? rule, PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Preview,
        PrtgHostSnapshot? capturedHostSnapshot = null, CancellationToken cancellationToken = default)
    {
        var dayCount = throughDate.DayNumber - fromDate.DayNumber + 1;
        if (dayCount is < 1 or > 730 || throughDate.ToDateTime(TimeOnly.MinValue).Date >= DateTime.Today)
            throw new ArgumentOutOfRangeException(nameof(throughDate), "僅允許 1 到 730 個已完成日期的範圍評估。");
        cancellationToken.ThrowIfCancellationRequested();

        var hostSnapshot = capturedHostSnapshot ?? _hosts.CapturePrtgSnapshot();
        if (_hosts.DataVersion != hostSnapshot.Version)
            throw new InvalidOperationException("PRTG 主機清單版本已變更；請重新開始範圍評估。");
        var activeHostIds = hostSnapshot.Hosts.Where(h => h.Active && h.MergedInto == null)
            .Select(h => h.HostId).Distinct().OrderBy(id => id).ToArray();
        var activeHostSet = activeHostIds.ToHashSet();
        var settings = _settings.Get();
        var whitelist = (settings.PrtgSensorTypeWhitelist ?? new List<string>()).ToArray();
        var settingsRevision = settings.Revision ?? string.Empty;
        var settingsFingerprint = Fingerprint(string.Join("\n", whitelist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        var ruleFingerprint = Fingerprint(rule is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(rule));
        var hostMapRevision = _store.ReadHostMapDataRevision();
        var catalogueRevision = _store.ReadCatalogueDataRevision();
        var counts = _store.GetReadinessRangeCandidateCounts(fromDate, throughDate, activeHostSet, cancellationToken);
        if (_store.ReadHostMapDataRevision() != hostMapRevision || _store.ReadCatalogueDataRevision() != catalogueRevision)
            throw new InvalidOperationException("PRTG 主機映射或感測器目錄於候選範圍計數期間改變；請重新開始範圍評估。");
        var metadataSnapshot = CaptureMetadataSnapshot();
        if (_hosts.DataVersion != hostSnapshot.Version)
            throw new InvalidOperationException("PRTG 主機清單於候選範圍計數期間改變；請重新開始範圍評估。");

        return new PrtgDiskAssessmentRangeOperation(_operationOwner, fromDate, throughDate, rule, mode,
            hostSnapshot, activeHostIds, settingsRevision, settingsFingerprint, whitelist, ruleFingerprint,
            counts, hostMapRevision, catalogueRevision, metadataSnapshot);
    }

    /// <summary>Assesses one flat page; all date slices are located by one bounded candidate SELECT.</summary>
    public PrtgDiskAssessmentRangePage AssessRangePage(PrtgDiskAssessmentRangeOperation operation,
        long offset, int limit, CancellationToken cancellationToken = default)
    {
        ValidateRangeOperationOwner(operation);
        if (operation.IsCompleted) throw new InvalidOperationException("PRTG 磁碟範圍評估作業已完成；不能再讀取頁面。");
        limit = Math.Clamp(limit, 1, MaximumBatchSize);
        var historyDays = Math.Max(28, operation.Rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0);
        // The SQL wall-time superset includes two conservative boundary days on either side;
        // exact UTC/local filtering below keeps only the requested local host-day window.
        var historyPointsPerSensor = checked((historyDays + 4) * 24);
        if (historyPointsPerSensor > MaximumExpectedHistoricalPointsPerBatch)
            throw new InvalidOperationException("PRTG 磁碟範圍每顆感測器歷史點數已超過頁面上限；拒絕預覽。");
        // 單一 HTTP 頁可能橫跨多日；在此限制整頁列數乘歷史窗口，不只限制各日內部批次。
        limit = Math.Min(limit, MaximumExpectedHistoricalPointsPerBatch / historyPointsPerSensor);
        cancellationToken.ThrowIfCancellationRequested();
        var slices = operation.CreateSlices(offset, limit);
        if (slices.Count == 0)
            return new(operation.CandidateCount, offset, Array.Empty<PrtgDiskAssessmentRangeRow>());

        var located = _store.GetReadinessRangeCandidateRows(operation.ActiveHostIds, slices, cancellationToken);
        var rows = new List<PrtgDiskAssessmentRangeRow>(located.Count);
        var batchSize = EffectiveBatchSize(operation.Rule);
        foreach (var slice in slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = located.Where(row => row.CompletedDay == slice.CompletedDay)
                .Select(row => (row.Objid, row.DeviceObjid, row.HostId, row.MappingDate, row.Name, row.SensorType,
                    row.Category, row.Unit, row.Paused, row.DevicePaused)).ToArray();
            if (candidates.Length != slice.Take)
                throw new InvalidOperationException("PRTG 磁碟範圍候選切片筆數與私有計數不同；已拒絕整頁。");
            for (var relativeOffset = 0; relativeOffset < candidates.Length; relativeOffset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = Math.Min(batchSize, candidates.Length - relativeOffset);
                var batchCandidates = candidates.Skip(relativeOffset).Take(take).ToArray();
                var batch = AssessCapturedCandidates(slice.CompletedDay, operation.Rule, operation.Mode,
                    slice.Total, checked(slice.Offset + relativeOffset), batchCandidates, operation.HostSnapshot,
                    operation.CapturedWhitelist, operation.MetadataSnapshot, deferMetadataFence: true,
                    candidateMappingThrough: slice.CompletedDay.ToDateTime(TimeOnly.MinValue),
                    cancellationToken: cancellationToken);
                rows.AddRange(batch.Rows.Select(assessment =>
                    new PrtgDiskAssessmentRangeRow(slice.CompletedDay, assessment)));
            }
        }
        if (rows.Count != located.Count || rows.Count > limit)
            throw new InvalidOperationException("PRTG 磁碟範圍評估頁輸出筆數與已定位候選不一致。");
        return new(operation.CandidateCount, offset, rows.AsReadOnly());
    }

    /// <summary>Final fence for the entire RuleAdmin response, after mapping and suppression estimates.</summary>
    public void CompleteRangeAssessment(PrtgDiskAssessmentRangeOperation operation)
    {
        ValidateRangeOperationOwner(operation);
        if (operation.IsCompleted) throw new InvalidOperationException("PRTG 磁碟範圍評估作業已完成。");
        if (_store.ReadHostMapDataRevision() != operation.HostMapDataRevision ||
            _store.ReadCatalogueDataRevision() != operation.CatalogueDataRevision)
            throw new InvalidOperationException("PRTG 主機映射或感測器目錄於磁碟範圍預覽期間改變；已拒絕回傳整頁，請重試。");
        ValidateMetadataSnapshot(operation.MetadataSnapshot);
        var currentSettings = _settings.Get();
        var currentFingerprint = Fingerprint(string.Join("\n", (currentSettings.PrtgSensorTypeWhitelist ?? new List<string>())
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        if (!string.Equals(currentSettings.Revision ?? string.Empty, operation.SettingsRevision, StringComparison.Ordinal) ||
            !string.Equals(currentFingerprint, operation.SettingsFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("系統準備度設定於磁碟範圍預覽期間改變；已拒絕回傳整頁，請重試。");
        if (_hosts.DataVersion != operation.HostVersion)
            throw new InvalidOperationException("PRTG 主機授權或顯示名稱於磁碟範圍預覽期間改變；已拒絕回傳整頁，請重試。");
        if (!string.Equals(Fingerprint(operation.Rule is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(operation.Rule)),
                operation.RuleFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("PRTG 評估規則於磁碟範圍預覽期間改變；已拒絕回傳整頁，請重試。");
        operation.MarkCompleted();
    }

    private void ValidateRangeOperationOwner(PrtgDiskAssessmentRangeOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(operation.Owner, _operationOwner))
            throw new InvalidOperationException("PRTG 磁碟範圍評估作業屬於不同的服務執行個體。");
    }

    private void ValidateOperationOwner(PrtgDiskAssessmentOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!ReferenceEquals(operation.Owner, _operationOwner))
            throw new InvalidOperationException("PRTG 評估作業屬於不同的服務執行個體。");
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    internal PrtgDiskAssessmentBatch AssessCandidatePage(DateOnly completedDay, KnownIssueRule? rule,
        PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Preview, int limit = MaximumBatchSize, int offset = 0,
        IReadOnlyCollection<long>? selectedHostIds = null, IReadOnlyCollection<long>? selectedSensorObjids = null,
        PrtgDiskCandidateSnapshot? candidateSnapshot = null, long? candidateMappingRevision = null,
        string? candidatePolicyVersion = null, PrtgDiskMetadataSnapshot? metadataSnapshot = null,
        bool deferMetadataFence = false, PrtgHostSnapshot? capturedHostSnapshot = null,
        IReadOnlyCollection<string>? capturedWhitelist = null, DateTime? candidateMappingThrough = null)
    {
        limit = Math.Min(Math.Clamp(limit, 1, MaximumBatchSize), EffectiveBatchSize(rule));
        offset = Math.Max(0, offset);
        var day = completedDay.ToDateTime(TimeOnly.MinValue);
        if (day.Date >= DateTime.Today) throw new ArgumentOutOfRangeException(nameof(completedDay), "僅允許評估已完成日期。");
        var selected = selectedHostIds?.ToHashSet();
        var hostSnapshot = candidateSnapshot?.HostSnapshot ?? capturedHostSnapshot ?? _hosts.CapturePrtgSnapshot();
        if (_hosts.DataVersion != hostSnapshot.Version)
            throw new InvalidOperationException("PRTG 主機授權範圍於磁碟候選頁擷取期間改變；請重新開始評估。");
        var activeHostIds = hostSnapshot.Hosts.Where(h => h.Active && h.MergedInto == null &&
                (selected is null || selected.Contains(h.HostId)))
            .Select(h => h.HostId).OrderBy(id => id).ToArray();
        var activeHostSet = activeHostIds.ToHashSet();
        // 一般歷史試算只看到目標完成日；HTTP readiness operation 可明確傳入分開的
        // current-as-of candidate day，同時仍把歷史數值評估停在最近完成日。
        var mappingThrough = candidateMappingThrough?.Date ?? day;
        var activeHostScope = activeHostIds;
        var selectedScope = selectedSensorObjids?.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        var mappingFrom = mappingThrough.AddDays(-CandidateMappingLookbackDays);
        if (candidateSnapshot is null)
        {
            var hostMapDataRevision = _store.ReadHostMapDataRevision();
            var captured = activeHostScope.Length == 0 || selectedScope is { Length: 0 }
                ? (0, new List<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)>())
                : _store.GetLatestMappedReadinessSensorSnapshot(activeHostScope, mappingThrough, mappingFrom,
                    MaximumCandidateSnapshotSize, selectedScope);
            if (hostMapDataRevision != _store.ReadHostMapDataRevision())
                throw new InvalidOperationException("PRTG 日映射於磁碟候選快照擷取期間改變；請重新開始評估。");
            candidateSnapshot = new PrtgDiskCandidateSnapshot(completedDay, mappingThrough, activeHostScope,
                selectedSensorObjids is null, selectedScope, candidateMappingRevision, candidatePolicyVersion,
                hostMapDataRevision, captured.Item1, captured.Item2, hostSnapshot);
        }
        else if (!candidateSnapshot.Matches(completedDay, mappingThrough, activeHostScope, selectedSensorObjids is null, selectedScope,
                     candidateMappingRevision, candidatePolicyVersion))
        {
            throw new InvalidOperationException("PRTG 磁碟候選範圍已改變；請重新開始評估，不能沿用舊候選總數。");
        }

        var total = candidateSnapshot.Total;
        var candidates = candidateSnapshot.GetPage(offset, limit);
        metadataSnapshot ??= CaptureMetadataSnapshot();
        return AssessCapturedCandidates(completedDay, rule, mode, total, offset, candidates, hostSnapshot,
            capturedWhitelist, metadataSnapshot, deferMetadataFence, mappingThrough, candidateSnapshot);
    }

    private PrtgDiskAssessmentBatch AssessCapturedCandidates(DateOnly completedDay, KnownIssueRule? rule,
        PrtgDiskDecisionMode mode, int total, int offset,
        IReadOnlyList<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType,
            string Category, string? Unit, bool Paused, bool DevicePaused)> candidates,
        PrtgHostSnapshot hostSnapshot, IReadOnlyCollection<string>? capturedWhitelist,
        PrtgDiskMetadataSnapshot metadataSnapshot, bool deferMetadataFence, DateTime candidateMappingThrough,
        PrtgDiskCandidateSnapshot? candidateSnapshot = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var historyDays = Math.Max(PrtgDiskTrendThresholds.Provisional.RecentWindowDays,
            rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0);
        var projectionSensors = (int)Math.Clamp((12L * 1024 * 1024) / ((historyDays + 4) * 24L * 512), 1, MaximumBatchSize);
        if (candidates.Count > projectionSensors)
        {
            var combined = new List<PrtgDiskAssessmentRow>(candidates.Count);
            foreach (var chunk in candidates.Chunk(projectionSensors))
                combined.AddRange(AssessCapturedCandidates(completedDay, rule, mode, total, offset + combined.Count,
                    chunk, hostSnapshot, capturedWhitelist, metadataSnapshot, true, candidateMappingThrough,
                    candidateSnapshot, cancellationToken).Rows);
            if (!deferMetadataFence) ValidateMetadataSnapshot(metadataSnapshot);
            return new(total, offset, combined.Count, (long)offset + combined.Count < total,
                combined.GroupBy(row => row.Decision.Exclusion.ToString()).ToDictionary(group => group.Key, group => group.Count()),
                combined) { CandidateSnapshot = candidateSnapshot, MetadataSnapshot = metadataSnapshot };
        }
        var day = completedDay.ToDateTime(TimeOnly.MinValue);
        if (day.Date >= DateTime.Today) throw new ArgumentOutOfRangeException(nameof(completedDay), "僅允許評估已完成日期。");
        var activeHostIds = hostSnapshot.Hosts.Where(h => h.Active && h.MergedInto == null)
            .Select(h => h.HostId).OrderBy(id => id).ToArray();
        var activeHostSet = activeHostIds.ToHashSet();
        var mappingThrough = candidateMappingThrough.Date;
        var mappingFrom = mappingThrough.AddDays(-CandidateMappingLookbackDays);
        if (candidates.Any(sensor => !activeHostSet.Contains(sensor.HostId)))
            throw new InvalidOperationException("PRTG 磁碟範圍候選包含不在捕獲主機快照內的主機。");
        var hasMore = (long)offset + candidates.Count < total;
        var ids = candidates.Select(x => x.Objid).ToArray();
        var start = day.AddDays(-PrtgValueReadiness.WindowDays + 1);
        var recentStart = day.AddDays(-Math.Max(PrtgDiskTrendThresholds.Provisional.RecentWindowDays, rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0) + 1);
        var valuesStart = recentStart < start ? recentStart : start;
        var localTimeZone = TimeZoneInfo.Local;
        var authorityAnalysisTimeZoneId = metadataSnapshot.Authority.Strategy.AnalysisTimeZoneId;
        var analysisTimeZone = TryFindTimeZone(authorityAnalysisTimeZoneId);
        var timeBasisReason = !metadataSnapshot.Authority.Strategy.Ready ||
            !string.Equals(metadataSnapshot.Authority.Policy.AnalysisTimeZoneId, authorityAnalysisTimeZoneId, StringComparison.Ordinal) ||
            analysisTimeZone is null
            ? "Unknown：可信採樣的 AnalysisTimeZoneId 缺失或無效，無法定位磁碟歷史主機日。"
            : null;
        DateTime valuesStartUtc = default;
        DateTime valuesEndUtc = default;
        DateTime rawPeriodStart = default;
        DateTime rawPeriodEnd = default;
        if (timeBasisReason is null)
        {
            if (!TryConvertLocalBoundaryToUtc(valuesStart, localTimeZone, out valuesStartUtc) ||
                !TryConvertLocalBoundaryToUtc(day.AddDays(1), localTimeZone, out valuesEndUtc))
                timeBasisReason = "Unknown：Local host-day 邊界落在無效或模糊 DST 時段，無法安全定位磁碟歷史。";
            else if (!TryBuildAnalysisWallBounds(valuesStartUtc, valuesEndUtc, analysisTimeZone,
                         out rawPeriodStart, out rawPeriodEnd))
                timeBasisReason = "Unknown：無法把 Local host-day 窗口轉成可信 AnalysisTimeZone wall 範圍。";
        }
        var pageProfiles = metadataSnapshot.CaptureProfiles(_store, ids);
        var profileResolutions = new Dictionary<long, PrtgTrustedSamplingProfileResolution>();
        var semanticValidities = new Dictionary<long, PrtgDiskSemanticEvidenceValidity?>();
        foreach (var sensor in candidates)
        {
            var stored = metadataSnapshot.GetEvidence(sensor.Objid);
            var probe = metadataSnapshot.GetVerificationResult(sensor.Objid);
            var identity = metadataSnapshot.GetResourceIdentity(sensor.Objid);
            PrtgDiskSemanticEvidenceValidity? validity = null;
            if (stored is not null && stored.DeviceObjid == sensor.DeviceObjid && stored.HostId == sensor.HostId &&
                string.Equals(stored.SensorType, sensor.SensorType, StringComparison.OrdinalIgnoreCase))
            {
                var context = new PrtgDiskSemanticContext(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.SensorType,
                    stored.MainChannelIdentifier, stored.MainChannelName, stored.Unit, stored.Scale, stored.Direction);
                validity = _evidence.CheckValidity(stored, sensor.Objid, context, ParserSemanticVersion);
                if (!PrtgResourceQualification.IsChannelCurrent(stored, probe, identity) || !validity.IsValid ||
                    !MatchesVerifiedPercent(probe, stored, (sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.Name, sensor.SensorType, sensor.Category, sensor.Unit, sensor.Paused, sensor.DevicePaused)))
                    validity = new() { IsValid = false, Evidence = stored,
                        InvalidReason = validity.InvalidReason ?? "缺少與目前對應一致的 typed 探測證據，或無法證明可用百分比語意。" };
            }
            semanticValidities[sensor.Objid] = validity;
            var authority = metadataSnapshot.Authority;
            var resolved = PrtgTrustedSamplingProfileResolver.Resolve(pageProfiles.GetValueOrDefault(sensor.Objid), identity,
                authority.Policy, sensor.Objid, sensor.SensorType, authority.Strategy, DateTime.UtcNow, DateTime.UtcNow);
            var profile = pageProfiles.GetValueOrDefault(sensor.Objid);
            if (profile is null || profile.Quantity != PrtgTrustedQuantitySemantic.DiskFreePercent ||
                profile.Unit != "%" || profile.Scale != 1 || profile.Direction != "direct" ||
                !IsAvailableCapacityChannel(profile.PrimaryChannelCaption))
                resolved = new(null, resolved.MissingFacts, resolved.RejectionReason ?? "disk_semantic_profile_mismatch");
            profileResolutions[sensor.Objid] = resolved;
        }
        List<PrtgDiskReadinessValue> values;
        string? projectionCapacityReason = null;
        try
        {
            values = timeBasisReason is null
                ? _store.GetTrustedReadinessValues(ids, rawPeriodStart, rawPeriodEnd,
                    MaximumExpectedHistoricalPointsPerBatch, projected =>
                        profileResolutions.TryGetValue(projected.SensorObjid, out var resolution) &&
                        PrtgDiskTrustedProofValidator.IsTrusted(projected, resolution, valuesEndUtc))
                : new();
        }
        catch (PrtgReadinessCapacityException ex)
        {
            // No partial projection can imply complete history. The caller gets an explicit readiness reason.
            values = new();
            projectionCapacityReason = ex.Reason;
        }
        var hostHoursBySensor = new Dictionary<long, List<DiskHistoryHour>>();
        var timeBasisUnknownSensors = new HashSet<long>();
        if (timeBasisReason is null && analysisTimeZone is not null)
        {
            foreach (var value in values)
            {
                if (!TryMapAnalysisHourToLocalWindow(value.PeriodStart, analysisTimeZone, localTimeZone,
                        valuesStartUtc, valuesEndUtc, out var utcStart, out var localPeriodStart, out var ambiguous))
                {
                    if (ambiguous) timeBasisUnknownSensors.Add(value.SensorObjid);
                    continue;
                }
                if (!hostHoursBySensor.TryGetValue(value.SensorObjid, out var sensorHours))
                    hostHoursBySensor[value.SensorObjid] = sensorHours = new();
                sensorHours.Add(new(value, localPeriodStart, utcStart));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var mapSnapshotStart = valuesStart < mappingFrom ? valuesStart : mappingFrom;
        var mapReadThrough = mappingThrough > day ? mappingThrough : day;
        var maps = _store.GetReadinessMaps(candidates.Select(x => x.DeviceObjid).Distinct().ToArray(), mapSnapshotStart, mapReadThrough.AddDays(1));
        cancellationToken.ThrowIfCancellationRequested();
        var latestCandidateMaps = maps.Where(m => m.MapDate >= mappingFrom && m.MapDate <= mappingThrough)
            .GroupBy(m => m.DeviceObjid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MapDate).First());
        foreach (var sensor in candidates)
        {
            if (!latestCandidateMaps.TryGetValue(sensor.DeviceObjid, out var latestMap) ||
                latestMap.MapDate != sensor.MappingDate || latestMap.MapStatus != PrtgMapStatus.Ok || latestMap.HostId != sensor.HostId)
                throw new InvalidOperationException("PRTG 日映射於候選快照建立後改變；已拒絕使用新映射重算本頁，請重新開始評估。");
        }
        var mapByDay = maps.GroupBy(x => (x.DeviceObjid, Day: x.MapDate.Date)).ToDictionary(g => g.Key, g => g.First());
        var whitelist = (capturedWhitelist ?? _settings.Get().PrtgSensorTypeWhitelist).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PrtgDiskAssessmentRow>(candidates.Count);
        foreach (var sensor in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayMaps = new Dictionary<DateTime, long?>();
            for (var d = start; d <= day; d = d.AddDays(1))
                if (mapByDay.TryGetValue((sensor.DeviceObjid, d), out var map) && map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue && activeHostSet.Contains(map.HostId.Value))
                    dayMaps[d] = map.HostId;
            var ownValues = hostHoursBySensor.GetValueOrDefault(sensor.Objid) ?? new();
            var hours = ownValues.Select(v => new PrtgReadinessHour(v.LocalPeriodStart, v.Value.Quality, v.Value.Coverage, v.Value.Trusted)).ToArray();
            var stored = metadataSnapshot.GetEvidence(sensor.Objid);
            var validity = semanticValidities.GetValueOrDefault(sensor.Objid);
            var readiness = PrtgValueReadiness.Evaluate(new(sensor.Objid, sensor.DeviceObjid, sensor.Category,
                activeHostSet.Contains(sensor.HostId), activeHostSet.Contains(sensor.HostId), sensor.Paused, sensor.DevicePaused,
                (whitelist.Count == 0 || whitelist.Contains(sensor.SensorType)), stored?.Unit, stored?.MainChannelName, validity is { IsValid: true }, hours, dayMaps), day.AddDays(1));
            readiness = readiness with
            {
                Status = timeBasisReason is not null || timeBasisUnknownSensors.Contains(sensor.Objid)
                    ? PrtgValueReadinessStatus.Unknown : readiness.Status,
                Reason = (timeBasisReason ?? (timeBasisUnknownSensors.Contains(sensor.Objid)
                    ? "Unknown：可信歷史包含無效或 ambiguous DST analysis wall hour，無法映射到 Local host day。" : readiness.Reason)) +
                    (projectionCapacityReason is null ? string.Empty : " 容量限制，歷史投影未完整讀取：" + projectionCapacityReason) +
                    (hours.Any(h => !h.Trusted && (string.Equals(h.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(h.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase)))
                        ? " 部分歷史列缺少符合目前來源／身分／策略的完整可信 proof。" : string.Empty) +
                    (string.IsNullOrWhiteSpace(sensor.Unit)
                    ? " 鏡像未提供目前通道／單位欄位；通道漂移須待下次 typed probe 才能確認。"
                    : " 鏡像未提供目前通道欄位；通道漂移須待下次 typed probe 才能確認。")
            };
            var dataDays = hours.Where(h => h.PeriodStart >= start && h.PeriodStart < day.AddDays(1) && PrtgValueReadiness.IsUsable(h))
                .Select(h => h.PeriodStart.Date).Distinct();
            if (dataDays.Any(d => !dayMaps.TryGetValue(d, out var mappedHost) || mappedHost != sensor.HostId))
                readiness = readiness with { Status = PrtgValueReadinessStatus.Unknown,
                    Reason = readiness.Reason + " 歷史資料日主機對應與目前候選主機不一致。" };

            var ruleWindow = rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? PrtgDiskTrendThresholds.Provisional.RecentWindowDays;
            var eligibleDaily = ownValues.Where(v => v.Value.AvgValue.HasValue &&
                    PrtgValueReadiness.IsUsable(new(v.LocalPeriodStart, v.Value.Quality, v.Value.Coverage, v.Value.Trusted)) &&
                    v.LocalPeriodStart.Date >= day.AddDays(-ruleWindow + 1))
                .GroupBy(v => v.LocalPeriodStart.Date).Select(g => new { Day = g.Key, Hours = g.Select(x => x.LocalPeriodStart).Distinct().Count(), Average = g.Average(x => x.Value.AvgValue!.Value) })
                .Where(x => x.Hours >= PrtgValueReadiness.MinDailyUsableHours)
                .ToArray();
            var trendMappingValid = eligibleDaily.All(x => mapByDay.TryGetValue((sensor.DeviceObjid, x.Day), out var mapped)
                && mapped.MapStatus == PrtgMapStatus.Ok && mapped.HostId == sensor.HostId);
            var daily = trendMappingValid
                ? eligibleDaily.Select(x => new PrtgDiskTrendDay(DateOnly.FromDateTime(x.Day), validity is { IsValid: true, Evidence: not null } ? x.Average : (double?)null)).ToArray()
                : Array.Empty<PrtgDiskTrendDay>();
            var decision = PrtgDiskRuleDecision.Evaluate(new(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.Category,
                readiness, validity, daily, completedDay, rule, mode));
            rows.Add(new(sensor.Objid, sensor.DeviceObjid, sensor.HostId, readiness, validity, decision)
            {
                CompletedDate = completedDay,
                EvidenceFingerprint = Fingerprint(System.Text.Json.JsonSerializer.Serialize(new
                {
                    sensor.Objid, sensor.DeviceObjid, sensor.HostId, CompletedDay = completedDay,
                    Identity = metadataSnapshot.GetResourceIdentity(sensor.Objid),
                    SemanticEvidence = stored, ProfileDigest = pageProfiles.GetValueOrDefault(sensor.Objid)?.MetadataDigest,
                    RuleFingerprint = rule is null ? string.Empty : Fingerprint(System.Text.Json.JsonSerializer.Serialize(rule)),
                    Hours = ownValues.OrderBy(value => value.LocalPeriodStart).ThenBy(value => value.Value.Id)
                        .Select(value => new { AnalysisPeriodStart = value.Value.PeriodStart,
                            AnalysisHourUtcStart = value.UtcStart, HostPeriodStart = value.LocalPeriodStart,
                            value.Value.Trusted, value.Value.EvidenceFingerprint }).ToArray(),
                    DayMappings = dayMaps.OrderBy(pair => pair.Key).Select(pair => new { Day = pair.Key, HostId = pair.Value }).ToArray()
                })),
                MissingHourWindowStart = DateOnly.FromDateTime(start),
                MissingHourMasks = BuildMissingHourMasks(hours, start)
            });
        }
        var exclusions = rows.GroupBy(x => x.Decision.Exclusion.ToString()).ToDictionary(g => g.Key, g => g.Count());
        if (!deferMetadataFence) ValidateMetadataSnapshot(metadataSnapshot);
        return new(total, offset, rows.Count, hasMore, exclusions, rows)
        { CandidateSnapshot = candidateSnapshot, MetadataSnapshot = metadataSnapshot };
    }

    /// <summary>消費暫存結果前核對正式日映射資料版本；不重算候選總數或全載映射。</summary>
    internal void ValidateCandidateSnapshotDataRevision(PrtgDiskCandidateSnapshot? candidateSnapshot)
    {
        if (candidateSnapshot is null)
            throw new InvalidOperationException("PRTG 磁碟候選快照不存在；拒絕提交未核對日映射的結果。");
        if (_hosts.DataVersion != candidateSnapshot.HostVersion)
            throw new InvalidOperationException("PRTG 主機授權或顯示名稱於磁碟候選快照處理期間改變；已拒絕提交所有暫存結果，請重啟新範圍。");
        if (candidateSnapshot.HostMapDataRevision != _store.ReadHostMapDataRevision())
            throw new InvalidOperationException("PRTG 日映射版本於磁碟候選快照處理期間改變；已拒絕提交所有暫存結果，請重啟新範圍。");
    }

    /// <summary>跨頁正式評估提交前，同時核對日映射與兩份語意 metadata。</summary>
    internal void ValidateAssessmentSnapshot(PrtgDiskCandidateSnapshot? candidateSnapshot,
        PrtgDiskMetadataSnapshot? metadataSnapshot)
    {
        ValidateCandidateSnapshotDataRevision(candidateSnapshot);
        if (metadataSnapshot is null)
            throw new InvalidOperationException("PRTG 磁碟語意快照不存在；拒絕提交未核對 metadata 的結果。");
        ValidateMetadataSnapshot(metadataSnapshot);
    }

    /// <summary>
    /// Efficient enable guard: semantic evidence is a necessary precondition, so avoid walking the full
    /// mirrored sensor inventory when no evidence exists. Each candidate is then resolved by ID and passed
    /// through the same readiness, evidence validity, and rule decision path used by preview/formal assessment.
    /// </summary>
    public bool HasAnyReadySemanticCandidate(DateOnly completedDay, KnownIssueRule? rule)
    {
        var metadataSnapshot = CaptureMetadataSnapshot();
        var evidenceIds = metadataSnapshot.GetEvidenceIds();
        if (evidenceIds.Length == 0)
        {
            ValidateMetadataSnapshot(metadataSnapshot);
            return false;
        }
        var batchSize = EffectiveBatchSize(rule);
        foreach (var chunk in evidenceIds.Chunk(batchSize))
        {
            var batch = AssessCandidatePage(completedDay, rule, PrtgDiskDecisionMode.Preview, batchSize, 0,
                selectedSensorObjids: chunk, metadataSnapshot: metadataSnapshot, deferMetadataFence: true);
            if (batch.Rows.Any(r => r.Readiness.Status == PrtgValueReadinessStatus.Ready
                                    && r.Readiness.SemanticReady
                                    && r.EvidenceValidity is { IsValid: true }))
            {
                ValidateMetadataSnapshot(metadataSnapshot);
                return true;
            }
        }
        ValidateMetadataSnapshot(metadataSnapshot);
        return false;
    }

    private void ValidateMetadataSnapshot(PrtgDiskMetadataSnapshot snapshot)
    {
        var verificationCurrent = _verificationResults is null
            ? snapshot.Verification is null
            : snapshot.Verification is not null && _verificationResults.IsSnapshotCurrent(snapshot.Verification);
        var ids = snapshot.GetEvidenceIds().Concat(snapshot.GetProfileFenceSnapshot().Keys).Distinct().ToArray();
        var identities = _store.GetResourceIdentities(ids);
        if (ids.Any(id => !snapshot.TryGetResourceIdentity(id, out var captured) ||
            !IdentitiesMatch(captured, identities.GetValueOrDefault(id) ?? new PrtgResourceIdentity { SensorId = id })))
            throw new InvalidOperationException("PRTG 資源世代於磁碟候選評估期間改變；已拒絕整批結果，請重新開始評估。");
        var profileFences = snapshot.GetProfileFenceSnapshot();
        if (profileFences.Count > 0)
        {
            foreach (var page in profileFences.Chunk(MaximumBatchSize))
            {
                var currentProfiles = _store.GetTrustedSamplingProfiles(page.Select(item => item.Key));
                if (page.Any(item => !string.Equals(item.Value,
                        currentProfiles.TryGetValue(item.Key, out var profile) ? profile.MetadataDigest : "<missing>",
                        StringComparison.Ordinal)))
                    throw new InvalidOperationException("PRTG sensor sampling profile 於磁碟準備度評估期間改變；已拒絕整批結果，請重新開始評估。");
            }
        }
        var settings = _settings.Get();
        var strategyName = PrtgFetchStrategy.Normalize(settings.PrtgFetchStrategy);
        var strategyMinutes = PrtgFetchStrategy.Profile(strategyName).SnapshotIntervalMinutes;
        var currentAuthority = _store.GetTrustedSamplingPolicyContext(strategyName, strategyMinutes);
        if (!snapshot.AuthorityMatches(currentAuthority))
            throw new InvalidOperationException("PRTG source、strategy 或 time-basis 於磁碟準備度評估期間改變；已拒絕整批結果，請重新開始評估。");
        if (!_evidence.IsSnapshotCurrent(snapshot.Evidence) || !verificationCurrent)
            throw new InvalidOperationException("PRTG 語意 metadata 於候選評估期間改變；已拒絕整批結果，請重新開始評估。");
    }

    private static bool IdentitiesMatch(PrtgResourceIdentity left, PrtgResourceIdentity right) =>
        left.SensorId == right.SensorId && left.Epoch == right.Epoch && left.Generation == right.Generation &&
        left.SourceGeneration == right.SourceGeneration && left.DeviceId == right.DeviceId && left.HostId == right.HostId &&
        left.ResourceFingerprint == right.ResourceFingerprint && left.InventoryFingerprint == right.InventoryFingerprint &&
        left.ChannelFingerprint == right.ChannelFingerprint && left.ChannelGeneration == right.ChannelGeneration &&
        left.Active == right.Active && left.PendingReconciliation == right.PendingReconciliation;

    public static int EffectiveBatchSize(KnownIssueRule? rule)
    {
        var windowDays = Math.Max(28, rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0);
        var historyHours = (long)(windowDays + 4) * 24;
        return (int)Math.Clamp(MaximumExpectedHistoricalPointsPerBatch / historyHours, 1, MaximumBatchSize);
    }

    private static TimeZoneInfo? TryFindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { return null; }
    }

    private static bool TryConvertLocalBoundaryToUtc(DateTime localWall, TimeZoneInfo localZone, out DateTime utc)
    {
        utc = default;
        var wall = DateTime.SpecifyKind(localWall, DateTimeKind.Unspecified);
        if (localZone.IsInvalidTime(wall) || localZone.IsAmbiguousTime(wall)) return false;
        try
        {
            utc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, localZone), DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool TryBuildAnalysisWallBounds(DateTime fromUtc, DateTime toUtc,
        TimeZoneInfo analysisZone, out DateTime wallFrom, out DateTime wallTo)
    {
        wallFrom = default;
        wallTo = default;
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc) return false;
        try
        {
            var minimum = DateTime.MaxValue;
            var maximum = DateTime.MinValue;
            for (var instant = fromUtc; instant < toUtc; instant = instant.AddHours(6))
            {
                var wall = TimeZoneInfo.ConvertTimeFromUtc(instant, analysisZone);
                if (wall < minimum) minimum = wall;
                if (wall > maximum) maximum = wall;
            }
            var endWall = TimeZoneInfo.ConvertTimeFromUtc(toUtc, analysisZone);
            if (endWall < minimum) minimum = endWall;
            if (endWall > maximum) maximum = endWall;
            // Keep the raw SQL predicate broad across an offset transition; the bounded
            // rows are converted back to UTC and clipped to the exact Local interval.
            wallFrom = DateTime.SpecifyKind(minimum.AddDays(-1), DateTimeKind.Unspecified);
            wallTo = DateTime.SpecifyKind(maximum.AddDays(1), DateTimeKind.Unspecified);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException)
        { return false; }
    }

    private static bool TryMapAnalysisHourToLocalWindow(DateTime analysisWallHour,
        TimeZoneInfo analysisZone, TimeZoneInfo localZone, DateTime windowStartUtc, DateTime windowEndUtc,
        out DateTime utcStart, out DateTime localWallHour, out bool ambiguous)
    {
        utcStart = default;
        localWallHour = default;
        ambiguous = false;
        var wall = DateTime.SpecifyKind(analysisWallHour, DateTimeKind.Unspecified);
        if (wall.Minute != 0 || wall.Second != 0 || wall.Millisecond != 0 || wall.Ticks % TimeSpan.TicksPerSecond != 0 ||
            analysisZone.IsInvalidTime(wall) || analysisZone.IsAmbiguousTime(wall))
        {
            ambiguous = true;
            return false;
        }
        try { utcStart = TimeZoneInfo.ConvertTimeToUtc(wall, analysisZone); }
        catch (ArgumentException) { ambiguous = true; return false; }
        var utcEnd = utcStart.AddHours(1);
        if (utcStart < windowStartUtc || utcEnd > windowEndUtc) return false;
        localWallHour = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utcStart, localZone), DateTimeKind.Unspecified);
        return true;
    }

    internal static bool MatchesVerifiedPercent(PrtgDiskVerificationResult? probe, PrtgDiskSemanticEvidence evidence,
        (long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused) sensor)
    {
        if (probe is null || probe.Cancelled || probe.ValuesMatch != true || probe.ComparedPointCount <= 0 ||
            probe.SensorObjid != sensor.Objid || probe.DeviceObjid != sensor.DeviceObjid || probe.HostId != sensor.HostId ||
            !string.Equals(probe.SensorType, sensor.SensorType, StringComparison.OrdinalIgnoreCase) ||
            (Reported(sensor.Unit) && !Same(sensor.Unit, evidence.Unit)) ||
            !string.Equals(probe.ParserSemanticVersion, ParserSemanticVersion, StringComparison.Ordinal) ||
            (Reported(probe.ChannelIdentifier) && !Same(probe.ChannelIdentifier, evidence.MainChannelIdentifier)) ||
            (Reported(probe.ChannelName) && !Same(probe.ChannelName, evidence.MainChannelName)) ||
            (Reported(probe.Unit) && !Same(probe.Unit, evidence.Unit)) ||
            (probe.Scale.HasValue && probe.Scale.Value != evidence.Scale) ||
            (Reported(probe.Direction) && !Same(probe.Direction, evidence.Direction))) return false;
        var unit = evidence.Unit?.Trim();
        var name = evidence.MainChannelName?.Trim();
        var freeChannel = IsAvailableCapacityChannel(name);
        var percentUnit = unit is not null && (unit.Equals("%", StringComparison.Ordinal) || unit.Equals("percent", StringComparison.OrdinalIgnoreCase) || unit.Equals("percentage", StringComparison.OrdinalIgnoreCase));
        return freeChannel && percentUnit && Same(evidence.Direction, "descending-danger") && evidence.Scale == 1;
    }

    private sealed record DiskHistoryHour(PrtgDiskReadinessValue Value, DateTime LocalPeriodStart, DateTime UtcStart);

    private static IReadOnlyList<uint> BuildMissingHourMasks(IReadOnlyList<PrtgReadinessHour> hours, DateTime windowStart)
    {
        var usableTimes = hours.Where(PrtgValueReadiness.IsUsable).Select(hour => hour.PeriodStart).ToHashSet();
        var masks = new uint[PrtgValueReadiness.WindowDays];
        for (var dayIndex = 0; dayIndex < PrtgValueReadiness.WindowDays; dayIndex++)
        {
            var date = windowStart.Date.AddDays(dayIndex);
            for (var hourIndex = 0; hourIndex < 24; hourIndex++)
                if (!usableTimes.Contains(date.AddHours(hourIndex)))
                    masks[dayIndex] |= 1u << hourIndex;
        }
        return Array.AsReadOnly(masks);
    }

    private static bool Reported(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool IsAvailableCapacityChannel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = string.Join(' ', name.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Contains("used") || normalized.Contains("total") || normalized.Contains("utiliz") || normalized.Contains("consumed"))
            return false;
        return normalized is "free" or "free space" or "free capacity"
            or "available" or "available space" or "available capacity";
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(string quality, double? coverage) =>
        string.Equals(quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase) && coverage >= PrtgValueUsability.SampledMinCoverage;
}

public sealed record PrtgDiskAssessmentBatch(int CandidateCount, int Offset, int AssessedCount, bool HasMore,
    IReadOnlyDictionary<string, int> ExclusionCounts, IReadOnlyList<PrtgDiskAssessmentRow> Rows)
{
    internal PrtgDiskCandidateSnapshot? CandidateSnapshot { get; init; }
    internal PrtgDiskMetadataSnapshot? MetadataSnapshot { get; init; }
}

/// <summary>單次操作共用的私有 typed dictionary；不把 mutable dictionary 交給 consumer。</summary>
internal sealed class PrtgDiskMetadataSnapshot
{
    private readonly Dictionary<long, PrtgResourceIdentity> _identities;
    private readonly object _profileFenceLock = new();
    private readonly Dictionary<long, string> _profileDigests = new();
    private PrtgDiskMetadataSnapshot(PrtgDiskSemanticEvidenceSnapshot evidence,
        PrtgDiskVerificationResultSnapshot? verification,
        IReadOnlyDictionary<long, PrtgResourceIdentity> identities, PrtgTrustedSamplingPolicyContext authority)
    { Evidence = evidence; Verification = verification; _identities = identities.ToDictionary(pair => pair.Key, pair => pair.Value); Authority = authority; }

    internal PrtgDiskSemanticEvidenceSnapshot Evidence { get; }
    internal PrtgDiskVerificationResultSnapshot? Verification { get; }
    internal PrtgTrustedSamplingPolicyContext Authority { get; }
    internal int EvidenceDeserializeCount => Evidence.DeserializeCount;
    internal int VerificationDeserializeCount => Verification?.DeserializeCount ?? 0;
    internal PrtgDiskSemanticEvidence? GetEvidence(long sensorObjid) =>
        Evidence.TryGetValue(sensorObjid, out var item) ? item : null;
    internal PrtgDiskVerificationResult? GetVerificationResult(long sensorObjid) =>
        Verification?.Get(sensorObjid);
    internal long[] GetEvidenceIds() => Evidence.GetSensorIds();
    internal PrtgResourceIdentity? GetResourceIdentity(long sensorObjid) => _identities.GetValueOrDefault(sensorObjid);
    internal bool TryGetResourceIdentity(long sensorObjid, out PrtgResourceIdentity identity) =>
        _identities.TryGetValue(sensorObjid, out identity!);

    internal IReadOnlyDictionary<long, PrtgTrustedSamplingProfile> CaptureProfiles(EfPrtgStore store,
        IReadOnlyCollection<long> sensorIds)
    {
        var profiles = store.GetTrustedSamplingProfiles(sensorIds);
        var currentIdentities = store.GetResourceIdentities(sensorIds);
        lock (_profileFenceLock)
            foreach (var sensorId in sensorIds.Distinct())
            {
                var digest = profiles.TryGetValue(sensorId, out var profile) ? profile.MetadataDigest : "<missing>";
                if (_profileDigests.TryGetValue(sensorId, out var captured) && captured != digest)
                    throw new InvalidOperationException("可信 profile 在同一評估作業期間變更；拒絕混合不同版本。");
                _profileDigests[sensorId] = digest;
                if (!_identities.ContainsKey(sensorId))
                    _identities[sensorId] = currentIdentities.GetValueOrDefault(sensorId) ?? new PrtgResourceIdentity { SensorId = sensorId };
            }
        return profiles;
    }

    internal IReadOnlyDictionary<long, string> GetProfileFenceSnapshot()
    {
        lock (_profileFenceLock) return new Dictionary<long, string>(_profileDigests);
    }

    internal bool AuthorityMatches(PrtgTrustedSamplingPolicyContext current) =>
        string.Equals(AuthorityFingerprint(Authority), AuthorityFingerprint(current), StringComparison.Ordinal);

    private static string AuthorityFingerprint(PrtgTrustedSamplingPolicyContext context) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { context.Policy.SourceGeneration, context.Policy.Revision,
                context.Policy.RawTimestampTimeZoneId, context.Policy.SourceTimeZoneId, context.Policy.AnalysisTimeZoneId,
                context.Policy.TimeBasisEvidenceReference, context.Strategy.StrategyName, context.Strategy.StrategyMinutes,
                context.Strategy.StrategyFingerprint, context.Strategy.EffectiveFromHourUtc,
                StrategyRawTimestampTimeZoneId = context.Strategy.RawTimestampTimeZoneId,
                StrategyAnalysisTimeZoneId = context.Strategy.AnalysisTimeZoneId,
                Missing = context.Strategy.MissingFacts }))));

    internal static PrtgDiskMetadataSnapshot Capture(PrtgDiskSemanticEvidenceStore evidence,
        PrtgDiskVerificationResultStore? verificationResults, EfPrtgStore store,
        Func<PrtgTrustedSamplingPolicyContext> captureAuthority)
    {
        var evidenceSnapshot = evidence.CaptureSnapshot();
        var verificationSnapshot = verificationResults?.CaptureSnapshot();
        var authority = captureAuthority();
        var sensorIds = evidenceSnapshot.GetSensorIds();
        var identities = store.GetResourceIdentities(sensorIds).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var sensorId in sensorIds)
            identities.TryAdd(sensorId, new PrtgResourceIdentity { SensorId = sensorId });
        return new(evidenceSnapshot, verificationSnapshot, identities, authority);
    }
}

/// <summary>只在單次評估作業中共用的有界候選集合與範圍柵欄。</summary>
internal sealed class PrtgDiskCandidateSnapshot
{
    private readonly long[] _activeHostIds;
    private readonly bool _allSensors;
    private readonly long[]? _selectedSensorObjids;
    private readonly long? _mappingRevision;
    private readonly long _hostMapDataRevision;
    private readonly string? _policyVersion;
    private readonly (long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category,
        string? Unit, bool Paused, bool DevicePaused)[] _rows;

    public PrtgDiskCandidateSnapshot(DateOnly completedDay, DateTime candidateMappingThrough, long[] activeHostIds, bool allSensors,
        long[]? selectedSensorObjids, long? mappingRevision, string? policyVersion, long hostMapDataRevision, int total,
        IReadOnlyList<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category,
            string? Unit, bool Paused, bool DevicePaused)> rows, PrtgHostSnapshot hostSnapshot,
        long? catalogueDataRevision = null)
    {
        CompletedDay = completedDay;
        CandidateMappingThrough = candidateMappingThrough.Date;
        _activeHostIds = activeHostIds.ToArray();
        _allSensors = allSensors;
        _selectedSensorObjids = selectedSensorObjids?.ToArray();
        _mappingRevision = mappingRevision;
        _hostMapDataRevision = hostMapDataRevision;
        CatalogueDataRevision = catalogueDataRevision;
        _policyVersion = policyVersion;
        Total = total;
        _rows = rows.ToArray();
        HostSnapshot = hostSnapshot;
        HostVersion = hostSnapshot.Version;
        if (Total != _rows.Length || Total > PrtgDiskAssessmentService.MaximumCandidateSnapshotSize)
            throw new InvalidOperationException("PRTG 磁碟候選快照超出界線或筆數不一致。");
    }

    public DateOnly CompletedDay { get; }
    public DateTime CandidateMappingThrough { get; }
    public int Total { get; }
    internal long HostMapDataRevision => _hostMapDataRevision;
    internal PrtgHostSnapshot HostSnapshot { get; }
    internal long HostVersion { get; }
    internal long? CatalogueDataRevision { get; }
    public List<(long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType, string Category,
        string? Unit, bool Paused, bool DevicePaused)> GetPage(int offset, int limit) =>
        _rows.Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).ToList();

    public bool Matches(DateOnly completedDay, DateTime candidateMappingThrough, long[] activeHostIds, bool allSensors, long[]? selectedSensorObjids,
        long? mappingRevision, string? policyVersion) =>
        CompletedDay == completedDay && CandidateMappingThrough == candidateMappingThrough.Date && _allSensors == allSensors &&
        _activeHostIds.SequenceEqual(activeHostIds) &&
        _mappingRevision == mappingRevision &&
        string.Equals(_policyVersion, policyVersion, StringComparison.Ordinal) &&
        (_selectedSensorObjids is null ? selectedSensorObjids is null :
            selectedSensorObjids is not null && _selectedSensorObjids.SequenceEqual(selectedSensorObjids));

    internal (long Objid, long DeviceObjid, long HostId, DateTime MappingDate, string Name, string SensorType,
        string Category, string? Unit, bool Paused, bool DevicePaused)? FindCandidate(long sensorObjid) =>
        _rows.FirstOrDefault(row => row.Objid == sensorObjid) is var candidate && candidate.Objid == sensorObjid
            ? candidate : null;
}
public sealed record PrtgDiskAssessmentRow(long SensorObjid, long DeviceObjid, long CurrentHostId,
    PrtgValueReadinessResult Readiness, PrtgDiskSemanticEvidenceValidity? EvidenceValidity,
    PrtgDiskRuleDecisionResult Decision)
{
    public DateOnly CompletedDate { get; internal init; }
    /// <summary>Exact qualified history/identity/rule digest retained for the formal day manifest.</summary>
    public string EvidenceFingerprint { get; internal init; } = string.Empty;
    /// <summary>Compact coverage projection derived from the same bounded value rows as Readiness.</summary>
    public DateOnly MissingHourWindowStart { get; internal init; }
    public IReadOnlyList<uint> MissingHourMasks { get; internal init; } = Array.Empty<uint>();
}
