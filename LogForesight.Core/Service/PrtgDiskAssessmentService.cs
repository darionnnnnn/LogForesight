using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>Bounded, read-only assessment of persisted disk history. It never contacts PRTG or publishes findings.</summary>
public sealed class PrtgDiskAssessmentService
{
    public const int MaximumBatchSize = 100;
    public const int MaximumExpectedHistoricalPointsPerBatch = 100_000;
    public const int CandidateMappingLookbackDays = 30;
    public const string ParserSemanticVersion = "disk-semantic-v1";
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hosts;
    private readonly ISystemSettingsStore _settings;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore? _verificationResults;

    public PrtgDiskAssessmentService(EfPrtgStore store, IHostStore hosts, ISystemSettingsStore settings,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore? verificationResults = null)
    { _store = store; _hosts = hosts; _settings = settings; _evidence = evidence; _verificationResults = verificationResults; }

    public PrtgDiskAssessmentBatch Assess(DateOnly completedDay, KnownIssueRule? rule,
        PrtgDiskDecisionMode mode = PrtgDiskDecisionMode.Preview, int limit = MaximumBatchSize, int offset = 0,
        IReadOnlyCollection<long>? selectedHostIds = null, IReadOnlyCollection<long>? selectedSensorObjids = null)
    {
        limit = Math.Min(Math.Clamp(limit, 1, MaximumBatchSize), EffectiveBatchSize(rule));
        offset = Math.Max(0, offset);
        var day = completedDay.ToDateTime(TimeOnly.MinValue);
        if (day.Date >= DateTime.Today) throw new ArgumentOutOfRangeException(nameof(completedDay), "僅允許評估已完成日期。");
        var selected = selectedHostIds?.ToHashSet();
        var hosts = _hosts.GetAll().Where(h => h.Active && h.MergedInto == null && (selected is null || selected.Contains(h.HostId)))
            .ToDictionary(h => h.HostId);
        int total;
        List<(long Objid, long DeviceObjid, long HostId, string Name, string SensorType, string Category, string? Unit, bool Paused, bool DevicePaused)> candidates;
        if (selectedSensorObjids is null)
        {
            (total, candidates) = _store.GetLatestMappedReadinessSensors(hosts.Keys.ToArray(), DateTime.Today,
                DateTime.Today.AddDays(-CandidateMappingLookbackDays), limit, offset);
        }
        else
        {
            var scoped = selectedSensorObjids.Distinct().Where(id => id > 0)
                .Select(id => _store.GetCurrentReadinessSensorById(id, hosts.Keys.ToArray(), DateTime.Today,
                    DateTime.Today.AddDays(-CandidateMappingLookbackDays)))
                .Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x.Objid).ToList();
            total = scoped.Count;
            candidates = scoped.Skip(offset).Take(limit).ToList();
        }
        var hasMore = offset + candidates.Count < total;
        var ids = candidates.Select(x => x.Objid).ToArray();
        var start = day.AddDays(-PrtgValueReadiness.WindowDays + 1);
        var recentStart = day.AddDays(-Math.Max(PrtgDiskTrendThresholds.Provisional.RecentWindowDays, rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0) + 1);
        var valuesStart = recentStart < start ? recentStart : start;
        var values = _store.GetReadinessValues(ids, valuesStart, day.AddDays(1));
        var maps = _store.GetReadinessMaps(candidates.Select(x => x.DeviceObjid).Distinct().ToArray(), valuesStart, day.AddDays(1));
        var mapByDay = maps.GroupBy(x => (x.DeviceObjid, Day: x.MapDate.Date)).ToDictionary(g => g.Key, g => g.First());
        var whitelist = _settings.Get().PrtgSensorTypeWhitelist.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PrtgDiskAssessmentRow>(candidates.Count);
        foreach (var sensor in candidates)
        {
            var dayMaps = new Dictionary<DateTime, long?>();
            for (var d = start; d <= day; d = d.AddDays(1))
                if (mapByDay.TryGetValue((sensor.DeviceObjid, d), out var map) && map.MapStatus == PrtgMapStatus.Ok && map.HostId.HasValue && hosts.ContainsKey(map.HostId.Value))
                    dayMaps[d] = map.HostId;
            var ownValues = values.Where(v => v.SensorObjid == sensor.Objid).ToArray();
            var hours = ownValues.Select(v => new PrtgReadinessHour(v.PeriodStart, v.Quality, v.Coverage)).ToArray();
            var stored = _evidence.Get(sensor.Objid);
            PrtgDiskSemanticEvidenceValidity? validity = null;
            if (stored is not null && stored.DeviceObjid == sensor.DeviceObjid && stored.HostId == sensor.HostId &&
                string.Equals(stored.SensorType, sensor.SensorType, StringComparison.OrdinalIgnoreCase))
            {
                // Current mirror has no channel/unit fields. Require a matching successful typed probe result;
                // comparing evidence with itself cannot establish current channel semantics.
                var context = new PrtgDiskSemanticContext(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.SensorType,
                    stored.MainChannelIdentifier, stored.MainChannelName, stored.Unit, stored.Scale, stored.Direction);
                validity = _evidence.CheckValidity(sensor.Objid, context, ParserSemanticVersion);
                var probe = _verificationResults?.Get(sensor.Objid);
                if (!validity.IsValid || !MatchesVerifiedPercent(probe, stored, sensor))
                    validity = new() { IsValid = false, Evidence = stored,
                        InvalidReason = validity.InvalidReason ?? "缺少與目前對應一致的 typed 探測證據，或無法證明可用百分比語意。" };
            }
            var readiness = PrtgValueReadiness.Evaluate(new(sensor.Objid, sensor.DeviceObjid, sensor.Category,
                hosts.ContainsKey(sensor.HostId), hosts.ContainsKey(sensor.HostId), sensor.Paused, sensor.DevicePaused,
                whitelist.Contains(sensor.SensorType), stored?.Unit, stored?.MainChannelName, validity is { IsValid: true }, hours, dayMaps), day.AddDays(1));
            readiness = readiness with
            {
                Reason = readiness.Reason + (string.IsNullOrWhiteSpace(sensor.Unit)
                    ? " 鏡像未提供目前通道／單位欄位；通道漂移須待下次 typed probe 才能確認。"
                    : " 鏡像未提供目前通道欄位；通道漂移須待下次 typed probe 才能確認。")
            };
            var dataDays = hours.Where(h => h.PeriodStart >= start && h.PeriodStart < day.AddDays(1) &&
                    (string.Equals(h.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(h.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase) && h.Coverage >= PrtgValueUsability.SampledMinCoverage))
                .Select(h => h.PeriodStart.Date).Distinct();
            if (dataDays.Any(d => !dayMaps.TryGetValue(d, out var mappedHost) || mappedHost != sensor.HostId))
                readiness = readiness with { Status = PrtgValueReadinessStatus.Unknown, Reason = "歷史資料日主機對應與目前候選主機不一致。" };

            var ruleWindow = rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? PrtgDiskTrendThresholds.Provisional.RecentWindowDays;
            var eligibleDaily = ownValues.Where(v => v.AvgValue.HasValue && IsUsable(v.Quality, v.Coverage) && v.PeriodStart.Date >= day.AddDays(-ruleWindow + 1))
                .GroupBy(v => v.PeriodStart.Date).Select(g => new { Day = g.Key, Hours = g.Select(x => x.PeriodStart).Distinct().Count(), Average = g.Average(x => x.AvgValue!.Value) })
                .Where(x => x.Hours >= PrtgValueReadiness.MinDailyUsableHours)
                .ToArray();
            var trendMappingValid = eligibleDaily.All(x => mapByDay.TryGetValue((sensor.DeviceObjid, x.Day), out var mapped)
                && mapped.MapStatus == PrtgMapStatus.Ok && mapped.HostId == sensor.HostId);
            var daily = trendMappingValid
                ? eligibleDaily.Select(x => new PrtgDiskTrendDay(DateOnly.FromDateTime(x.Day), validity is { IsValid: true, Evidence: not null } ? x.Average : (double?)null)).ToArray()
                : Array.Empty<PrtgDiskTrendDay>();
            var decision = PrtgDiskRuleDecision.Evaluate(new(sensor.Objid, sensor.DeviceObjid, sensor.HostId, sensor.Category,
                readiness, validity, daily, completedDay, rule, mode));
            rows.Add(new(sensor.Objid, sensor.DeviceObjid, sensor.HostId, readiness, validity, decision));
        }
        var exclusions = rows.GroupBy(x => x.Decision.Exclusion.ToString()).ToDictionary(g => g.Key, g => g.Count());
        return new(total, offset, rows.Count, hasMore, exclusions, rows);
    }

    /// <summary>
    /// Efficient enable guard: semantic evidence is a necessary precondition, so avoid walking the full
    /// mirrored sensor inventory when no evidence exists. Each candidate is then resolved by ID and passed
    /// through the same readiness, evidence validity, and rule decision path used by preview/formal assessment.
    /// </summary>
    public bool HasAnyReadySemanticCandidate(DateOnly completedDay, KnownIssueRule? rule)
    {
        var evidenceIds = _evidence.GetAll().Select(e => e.SensorObjid).Distinct().OrderBy(id => id).ToArray();
        if (evidenceIds.Length == 0) return false;
        var batchSize = EffectiveBatchSize(rule);
        foreach (var chunk in evidenceIds.Chunk(batchSize))
        {
            var batch = Assess(completedDay, rule, PrtgDiskDecisionMode.Preview, batchSize, 0,
                selectedSensorObjids: chunk);
            if (batch.Rows.Any(r => r.Readiness.Status == PrtgValueReadinessStatus.Ready
                                    && r.Readiness.SemanticReady
                                    && r.EvidenceValidity is { IsValid: true })) return true;
        }
        return false;
    }

    public static int EffectiveBatchSize(KnownIssueRule? rule)
    {
        var windowDays = Math.Max(28, rule?.PrtgDiskTrendThresholds?.RecentWindowDays ?? 0);
        return Math.Clamp(MaximumExpectedHistoricalPointsPerBatch / (windowDays * 24), 1, MaximumBatchSize);
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
    IReadOnlyDictionary<string, int> ExclusionCounts, IReadOnlyList<PrtgDiskAssessmentRow> Rows);
public sealed record PrtgDiskAssessmentRow(long SensorObjid, long DeviceObjid, long CurrentHostId,
    PrtgValueReadinessResult Readiness, PrtgDiskSemanticEvidenceValidity? EvidenceValidity,
    PrtgDiskRuleDecisionResult Decision);
