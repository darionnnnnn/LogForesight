using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;

namespace LogForesight.Web.Services;

/// <summary>從既有 PRTG 鏡像唯讀重算逐顆磁碟 readiness。</summary>
public sealed class PrtgDiskReadinessQueryService
{
    private const int SummaryCandidateCap = 100;
    private readonly EfPrtgStore _store;
    private readonly IHostStore _hosts;
    private readonly ISystemSettingsStore _systemSettings;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore _verificationResults;

    public PrtgDiskReadinessQueryService(EfPrtgStore store, IHostStore hosts, ISystemSettingsStore systemSettings,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verificationResults)
    { _store = store; _hosts = hosts; _systemSettings = systemSettings; _evidence = evidence; _verificationResults = verificationResults; }

    public PrtgDiskReadinessPage Get(int page, int pageSize, DateTime? now = null)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var asOf = (now ?? DateTime.Now).Date;
        var start = asOf.AddDays(-PrtgValueReadiness.WindowDays);
        var mapLookbackStart = asOf.AddDays(-30);
        var hosts = _hosts.GetAll().Where(h => h.Active && h.MergedInto == null).ToDictionary(h => h.HostId);
        var whitelist = _systemSettings.Get().PrtgSensorTypeWhitelist;
        var inventory = _store.GetReadinessInventoryCounts(hosts.Keys.ToArray(), whitelist, asOf, mapLookbackStart);
        // Resolve each device's latest bounded mapping in SQL before pagination; stale Ok rows cannot outlive a newer conflict.
        var (total, sensors) = _store.GetLatestMappedReadinessSensors(hosts.Keys.ToArray(), asOf, mapLookbackStart, pageSize, offset);
        // Use the shared Core assessor for semantic validity so the card follows the exact same
        // evidence predicate as rule preview. Its page and history reads are bounded to 100 sensors.
        var assessment = new PrtgDiskAssessmentService(_store, _hosts, _systemSettings, _evidence, _verificationResults)
            .Assess(DateOnly.FromDateTime(asOf.AddDays(-1)), null, PrtgDiskDecisionMode.Preview, pageSize, offset);
        var assessedBySensor = assessment.Rows.ToDictionary(x => x.SensorObjid);
        var storedValues = _store.GetReadinessValues(sensors.Select(s => s.Objid).ToArray(), start, asOf);
        var valuesBySensor = storedValues.GroupBy(v => v.SensorObjid).ToDictionary(g => g.Key,
            g => g.Select(v => new PrtgReadinessHour(v.PeriodStart, v.Quality, v.Coverage)).ToArray());
        var pageMaps = _store.GetReadinessMaps(sensors.Select(s => s.DeviceObjid).Distinct().ToArray(), mapLookbackStart, asOf);
        var dailyMap = pageMaps.GroupBy(m => (m.DeviceObjid, Day: m.MapDate.Date)).ToDictionary(g => g.Key, g => g.First());
        var latestMapByDevice = pageMaps.GroupBy(m => m.DeviceObjid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MapDate).First());
        var evidenceBySensor = _evidence.GetAll().ToDictionary(x => x.SensorObjid);
        var results = sensors.Select(sensor =>
        {
            var mappedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (latestMapByDevice.TryGetValue(sensor.DeviceObjid, out var latestMap)
                && latestMap.MapStatus == PrtgMapStatus.Ok && latestMap.HostId.HasValue
                && hosts.TryGetValue(latestMap.HostId.Value, out var latestHost))
                mappedNames.Add(latestHost.DisplayName ?? latestHost.HostName);
            for (var day = start; day < asOf; day = day.AddDays(1))
                if (dailyMap.TryGetValue((sensor.DeviceObjid, day), out var m) && m.MapStatus == PrtgMapStatus.Ok &&
                    m.HostId.HasValue && hosts.TryGetValue(m.HostId.Value, out var host))
                    mappedNames.Add(host.DisplayName ?? host.HostName);
            assessedBySensor.TryGetValue(sensor.Objid, out var assessed);
            var readiness = assessed?.Readiness ?? new PrtgValueReadinessResult(sensor.Objid,
                PrtgValueReadinessStatus.Unknown, 0, PrtgValueReadiness.WindowDays, 0,
                PrtgValueReadiness.MinDailyUsableHours, null, false, "無法取得共用評估結果");
            var semanticVerified = assessed?.EvidenceValidity is { IsValid: true };
            var invalidReason = assessed?.EvidenceValidity is { IsValid: false } validity ? validity.InvalidReason : null;
            // Core currently omits EvidenceValidity when the newest host mapping no longer matches
            // the stored host id. Preserve the evidence lifecycle state in the card for that case.
            if (invalidReason is null && evidenceBySensor.TryGetValue(sensor.Objid, out var evidence))
            {
                if (evidence.DeviceObjid != sensor.DeviceObjid || evidence.HostId != sensor.HostId)
                    invalidReason = "主機或裝置對應已變更。";
                else if (!string.Equals(evidence.SensorType?.Trim(), sensor.SensorType?.Trim(), StringComparison.OrdinalIgnoreCase))
                    invalidReason = "sensor 類型已變更。";
            }
            var semanticReady = readiness.Status == PrtgValueReadinessStatus.Ready && semanticVerified;
            valuesBySensor.TryGetValue(sensor.Objid, out var sensorHours);
            var usableTimes = (sensorHours ?? Array.Empty<PrtgReadinessHour>())
                .Where(PrtgValueReadiness.IsUsable).Select(h => h.PeriodStart).ToHashSet();
            var missingHourMasks = new uint[PrtgValueReadiness.WindowDays];
            for (var dayIndex = 0; dayIndex < PrtgValueReadiness.WindowDays; dayIndex++)
                for (var hourIndex = 0; hourIndex < 24; hourIndex++)
                    if (!usableTimes.Contains(start.AddDays(dayIndex).AddHours(hourIndex)))
                        missingHourMasks[dayIndex] |= 1u << hourIndex;
            var semanticLabel = invalidReason is not null ? "已失效" : readiness.Status == PrtgValueReadinessStatus.Ready
                ? semanticReady ? "可試算" : "語意未確認"
                : "資料累積中";
            return new PrtgDiskReadinessRow(sensor.Objid, sensor.DeviceObjid, sensor.Name, sensor.SensorType ?? string.Empty,
                readiness.Status.ToString(), readiness.UsableDays, readiness.RequiredDays, readiness.UsableHours,
                readiness.RequiredHoursPerDay, readiness.LatestUsableHour, semanticVerified, semanticReady, semanticLabel,
                invalidReason ?? readiness.Reason, DateOnly.FromDateTime(start), missingHourMasks, mappedNames.OrderBy(x => x).ToArray());
        }).ToArray();
        // Aggregate readiness in bounded Core-assessor batches. Never load all hourly rows at once;
        // if the mirror exceeds the cap, the response labels these numbers as a prefix sample.
        var summaryComputed = page == 1;
        var summaryCount = summaryComputed ? Math.Min(total, SummaryCandidateCap) : 0;
        var readyCount = 0; var semanticCount = 0; var previewCount = 0;
        for (var batchOffset = 0; batchOffset < summaryCount; batchOffset += PrtgDiskAssessmentService.MaximumBatchSize)
        {
            var batch = new PrtgDiskAssessmentService(_store, _hosts, _systemSettings, _evidence, _verificationResults)
                .Assess(DateOnly.FromDateTime(asOf.AddDays(-1)), null, PrtgDiskDecisionMode.Preview,
                    PrtgDiskAssessmentService.MaximumBatchSize, batchOffset);
            readyCount += batch.Rows.Count(x => x.Readiness.Status == PrtgValueReadinessStatus.Ready);
            semanticCount += batch.Rows.Count(x => x.EvidenceValidity is { IsValid: true });
            previewCount += batch.Rows.Count(x => x.Readiness.Status == PrtgValueReadinessStatus.Ready && x.Readiness.SemanticReady
                && x.EvidenceValidity is { IsValid: true });
        }
        return new(total, page, pageSize, (total + pageSize - 1) / pageSize,
            results.Count(x => x.Status == PrtgValueReadinessStatus.Ready.ToString()),
            results.Count(x => x.SemanticVerified), results.GroupBy(x => x.Reason)
                .ToDictionary(g => g.Key, g => g.Count()),
            total == 0 ? "沒有符合磁碟分類且目前映射至啟用主機的候選感測器；請檢查主機對應與磁碟分類。" : null,
            results, inventory.CandidateSensors, inventory.MappedActiveSensors, inventory.WhitelistedSensors,
            inventory.PausedSensors, inventory.ConflictSensors, inventory.UnmappedSensors, inventory.DisabledHostSensors,
            summaryCount, total > summaryCount, summaryComputed,
            readyCount, semanticCount, previewCount);
    }
}

public sealed record PrtgDiskReadinessPage(int CandidateSensors, int Page, int PageSize, int PageCount,
    int DataReadyOnPage, int SemanticVerifiedOnPage, IReadOnlyDictionary<string, int> ReasonCountsOnPage,
    string? EmptyState, IReadOnlyList<PrtgDiskReadinessRow> Rows, int GlobalMirrorCandidates, int GloballyMappedActive,
    int GloballyWhitelisted, int GloballyPaused, int GloballyConflicted, int GloballyUnmapped, int GloballyDisabledHost,
    int ReadinessSummaryCandidateCount, bool ReadinessSummaryCapped, bool ReadinessSummaryComputed, int DataReadyCount,
    int SemanticVerifiedCount, int PreviewReadyCount);
public sealed record PrtgDiskReadinessRow(long SensorObjid, long DeviceObjid, string SensorName, string SensorType,
    string Status, int UsableDays, int RequiredDays, int UsableHours, int RequiredHoursPerDay,
    DateTime? LatestUsableHour, bool SemanticVerified, bool SemanticReady, string SemanticLabel, string Reason,
    DateOnly MissingHourWindowStart, IReadOnlyList<uint> MissingHourMasks, IReadOnlyList<string> MappedHosts);
