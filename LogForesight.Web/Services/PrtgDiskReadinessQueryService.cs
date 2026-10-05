using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using LogForesight.Web.Auth;
using LogForesight.Web.Models;

namespace LogForesight.Web.Services;

/// <summary>從既有 PRTG 鏡像唯讀重算逐顆磁碟 readiness。</summary>
public sealed class PrtgDiskReadinessQueryService
{
    private const int SummaryCandidateCap = 100;
    private readonly EfPrtgStore _store;
    private readonly IHostStore? _hosts;
    private readonly ISystemSettingsStore _systemSettings;
    private readonly PrtgDiskSemanticEvidenceStore _evidence;
    private readonly PrtgDiskVerificationResultStore _verificationResults;
    private readonly IVisibilityService? _visibility;
    private readonly ICurrentUser? _currentUser;

    public PrtgDiskReadinessQueryService(EfPrtgStore store, IHostStore? hosts, ISystemSettingsStore systemSettings,
        PrtgDiskSemanticEvidenceStore evidence, PrtgDiskVerificationResultStore verificationResults,
        IVisibilityService? visibility = null, ICurrentUser? currentUser = null)
    {
        _store = store;
        _hosts = hosts;
        _systemSettings = systemSettings;
        _evidence = evidence;
        _verificationResults = verificationResults;
        _visibility = visibility;
        _currentUser = currentUser;
    }

    public PrtgDiskReadinessPage Get(int page, int pageSize, DateTime? now = null)
    {
        var hostSnapshot = RequireFullHostVisibility();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var asOf = (now ?? DateTime.Now).Date;
        var start = asOf.AddDays(-PrtgValueReadiness.WindowDays);
        var mapLookbackStart = asOf.AddDays(-30);
        var completedDay = DateOnly.FromDateTime(asOf.AddDays(-1));
        // The UI's candidate contract is current-as-of mapping (including today's conflict row),
        // while its readiness values stop at yesterday. Core binds both dates in one operation.
        var assessor = new PrtgDiskAssessmentService(_store, _hosts!, _systemSettings, _evidence, _verificationResults);
        var operation = assessor.BeginAssessment(completedDay, asOf, null, PrtgDiskDecisionMode.Preview,
            capturedHostSnapshot: hostSnapshot.Snapshot);
        var total = operation.CandidateCount;
        var inventory = _store.GetReadinessInventoryCounts(hostSnapshot.ActiveHosts.Keys.ToArray(),
            operation.SensorTypeWhitelist, asOf, mapLookbackStart);
        // Page 1's first 100 rows are both the visible page prefix and the existing summary sample.
        // Reuse the same candidate and metadata snapshots instead of creating more Core operations.
        var summaryBatch = page == 1 ? assessor.AssessPage(operation, 0, SummaryCandidateCap) : null;
        var pageBatch = summaryBatch ?? assessor.AssessPage(operation, offset, pageSize);
        var pageAssessmentRows = page == 1 ? pageBatch.Rows.Take(pageSize).ToArray() : pageBatch.Rows;
        var detailIds = pageAssessmentRows.Select(row => row.SensorObjid).ToArray();
        var sensorDetails = _store.GetReadinessSensorDetailsByIds(detailIds);
        if (sensorDetails.Count != detailIds.Length || sensorDetails.Select(row => row.Objid).Distinct().Count() != detailIds.Length)
            throw new InvalidOperationException("PRTG 準備度候選明細與 Core 快照不一致；拒絕回傳部分頁面。");
        var detailsBySensor = sensorDetails.ToDictionary(row => row.Objid);
        var sensors = pageAssessmentRows.Select(row => detailsBySensor.TryGetValue(row.SensorObjid, out var detail)
            ? detail : throw new InvalidOperationException("PRTG 準備度候選明細缺列；拒絕回傳部分頁面。"))
            .ToArray();
        if (sensors.Where((sensor, index) => sensor.DeviceObjid != pageAssessmentRows[index].DeviceObjid).Any())
            throw new InvalidOperationException("PRTG 準備度候選裝置與 Core 快照不一致；拒絕回傳部分頁面。");
        var assessedBySensor = pageAssessmentRows.ToDictionary(x => x.SensorObjid);
        var hosts = hostSnapshot.ActiveHosts;
        // Candidate validation needs today's map row even though historical value assessment ends yesterday.
        var pageMaps = _store.GetReadinessMaps(sensors.Select(s => s.DeviceObjid).Distinct().ToArray(), mapLookbackStart, asOf.AddDays(1));
        var dailyMap = pageMaps.GroupBy(m => (m.DeviceObjid, Day: m.MapDate.Date)).ToDictionary(g => g.Key, g => g.First());
        var latestMapByDevice = pageMaps.GroupBy(m => m.DeviceObjid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MapDate).First());
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
            var assessed = assessedBySensor.TryGetValue(sensor.Objid, out var capturedAssessment) ? capturedAssessment
                : throw new InvalidOperationException("PRTG 準備度候選缺少 Core 投影；拒絕回傳不一致頁面。");
            var readiness = assessed.Readiness;
            var semanticVerified = assessed?.EvidenceValidity is { IsValid: true };
            var invalidReason = assessed?.EvidenceValidity is { IsValid: false } validity ? validity.InvalidReason : null;
            // Core currently omits EvidenceValidity when the newest host mapping no longer matches
            // the stored host id. Preserve the evidence lifecycle state in the card for that case.
            invalidReason ??= operation.GetEvidenceIdentityMismatchReason(sensor.Objid);
            var semanticReady = readiness.Status == PrtgValueReadinessStatus.Ready && semanticVerified;
            var missingHourMasks = assessed.MissingHourMasks;
            var semanticLabel = invalidReason is not null ? "已失效" : readiness.Status == PrtgValueReadinessStatus.Ready
                ? semanticReady ? "可試算" : "語意未確認"
                : "資料累積中";
            return new PrtgDiskReadinessRow(sensor.Objid, sensor.DeviceObjid, sensor.Name, sensor.SensorType ?? string.Empty,
                readiness.Status.ToString(), readiness.UsableDays, readiness.RequiredDays, readiness.UsableHours,
                readiness.RequiredHoursPerDay, readiness.LatestUsableHour, semanticVerified, semanticReady, semanticLabel,
                invalidReason ?? readiness.Reason, assessed.MissingHourWindowStart, missingHourMasks, mappedNames.OrderBy(x => x).ToArray());
        }).ToArray();
        // Aggregate readiness in bounded Core-assessor batches. Never load all hourly rows at once;
        // if the mirror exceeds the cap, the response labels these numbers as a prefix sample.
        var summaryComputed = page == 1;
        var summaryCount = summaryComputed ? Math.Min(total, SummaryCandidateCap) : 0;
        var readyCount = summaryBatch?.Rows.Count(x => x.Readiness.Status == PrtgValueReadinessStatus.Ready) ?? 0;
        var semanticCount = summaryBatch?.Rows.Count(x => x.EvidenceValidity is { IsValid: true }) ?? 0;
        var previewCount = summaryBatch?.Rows.Count(x => x.Readiness.Status == PrtgValueReadinessStatus.Ready && x.Readiness.SemanticReady
            && x.EvidenceValidity is { IsValid: true }) ?? 0;
        var response = new PrtgDiskReadinessPage(total, page, pageSize, (total + pageSize - 1) / pageSize,
            results.Count(x => x.Status == PrtgValueReadinessStatus.Ready.ToString()),
            results.Count(x => x.SemanticVerified), results.GroupBy(x => x.Reason)
                .ToDictionary(g => g.Key, g => g.Count()),
            total == 0 ? "沒有符合磁碟分類且目前映射至啟用主機的候選感測器；請檢查主機對應與磁碟分類。" : null,
            results, inventory.CandidateSensors, inventory.MappedActiveSensors, inventory.WhitelistedSensors,
            inventory.PausedSensors, inventory.ConflictSensors, inventory.UnmappedSensors, inventory.DisabledHostSensors,
            summaryCount, total > summaryCount, summaryComputed,
            readyCount, semanticCount, previewCount);
        assessor.CompleteAssessment(operation);
        EnsureFullHostVisibility(hostSnapshot.Snapshot);
        return response;
    }

    private ReadinessHostSnapshot RequireFullHostVisibility()
    {
        var hostStore = _hosts;
        if (_visibility is null || hostStore is null || _currentUser is null)
            throw DomainException.Forbidden("無法確認全站 PRTG 主機可見範圍，拒絕讀取全站準備度。");
        var snapshot = hostStore.CapturePrtgSnapshot();
        EnsureFullHostVisibility(snapshot);
        if (hostStore.DataVersion != snapshot.Version)
            throw new InvalidOperationException("PRTG 主機授權範圍於準備度頁面開始時改變；請重試。");
        var activeHosts = snapshot.Hosts.Where(host => host.Active && host.MergedInto == null)
            .ToDictionary(host => host.HostId);
        return new ReadinessHostSnapshot(snapshot, activeHosts);
    }

    private void EnsureFullHostVisibility(PrtgHostSnapshot snapshot)
    {
        var visibility = _visibility;
        var currentUser = _currentUser;
        var hostStore = _hosts;
        if (visibility is null || currentUser is null || hostStore is null)
            throw DomainException.Forbidden("無法確認全站 PRTG 主機可見範圍，拒絕讀取全站準備度。");

        // Recompute against the same immutable host image just before return. Group and ownership
        // grants can change independently of the host blob version while the SQL page is assembled.
        var visible = visibility.GetVisibleHostIds(snapshot);
        if (!currentUser.Has(Capability.ViewAll) &&
            (visible.Count == 0 || snapshot.Hosts.Count == 0 || snapshot.Hosts.Any(host =>
                !visible.Contains(host.HostId) || visibility.IsCaseGrantOnly(host.HostId))))
            throw DomainException.Forbidden("PRTG 磁碟準備度摘要包含全站資料，僅可由可見全部主機的管理者查詢。");
        if (hostStore.DataVersion != snapshot.Version)
            throw new InvalidOperationException("PRTG 主機授權範圍於準備度頁面計算期間改變；請重試。");
    }

    private sealed record ReadinessHostSnapshot(PrtgHostSnapshot Snapshot,
        IReadOnlyDictionary<long, PrtgHostSnapshotEntry> ActiveHosts);
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
