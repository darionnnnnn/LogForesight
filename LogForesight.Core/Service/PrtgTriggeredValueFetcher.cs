using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>觸發式數值取數的結果統計</summary>
public sealed record PrtgTriggeredFetchResult(
    int TriggerHosts, int TargetSensors, int ValuesWritten, int FailedSensors);

/// <summary>
/// 觸發式 PRTG 數值取數：只對「昨日風險為高或中」的主機所對應的 device 上、
/// 且 type 命中白名單的 sensor 擷取 hourly 數值，並與分析流程並行進行。
/// </summary>
public sealed class PrtgTriggeredValueFetcher
{
    private readonly PrtgFetchService _fetchService;
    private readonly EfPrtgStore _store;
    private readonly IAnalysisRecordQuery _records;
    private readonly IRunConsole _console;

    public PrtgTriggeredValueFetcher(
        PrtgFetchService fetchService,
        EfPrtgStore store,
        IAnalysisRecordQuery records,
        IRunConsole console)
    {
        _fetchService = fetchService;
        _store = store;
        _records = records;
        _console = console;
    }

    public async Task<PrtgTriggeredFetchResult> RunAsync(
        DateTime day,
        IReadOnlyCollection<string>? whitelist,
        int concurrency,
        Func<bool> analysisCompleted,
        CancellationToken ct,
        int pollSeconds = 30,
        IReadOnlyCollection<long>? extraTriggerHosts = null,
        Action<string, int, int>? progress = null,
        string? scope = null,
        IReadOnlyCollection<long>? extraScopeHosts = null)
    {
        // 白名單為空時 all-mapped 會退回 triggered（第二道防線，見 PrtgValueFetchScope）
        var whitelistEmpty = whitelist == null || whitelist.Count == 0;
        var effectiveScope = PrtgValueFetchScope.EffectiveScope(scope, whitelistEmpty);

        if (PrtgValueFetchScope.ShouldWarnUnsafeAllMapped(scope, whitelistEmpty))
        {
            _console.WriteLine("  ⚠ 取數範圍設為「全部已對應主機」但 sensor type 白名單為空，" +
                               "等於對全部 sensor 取數——本次退回「只抓觸發主機」。請先設定白名單。");
        }
        var hostMapRows = _store.GetHostMapForDate(day);
        var hostToDevices = new Dictionary<long, List<long>>();
        foreach (var row in hostMapRows)
        {
            if (row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue)
            {
                var hostId = row.HostId.Value;
                if (!hostToDevices.TryGetValue(hostId, out var devList))
                {
                    devList = new List<long>();
                    hostToDevices[hostId] = devList;
                }
                devList.Add(row.DeviceObjid);
            }
        }

        var fetchedHosts = new HashSet<long>();
        var totalTargetSensors = 0;
        var totalValuesWritten = 0;
        var totalFailedSensors = 0;
        var isFirstScan = true;

        async Task ScanAndFetchAsync()
        {
            var filter = new RecordQueryFilter
            {
                From = day,
                To = day,
                RiskLevels = new[] { "高", "中" },
                Hosts = null
            };
            var records = _records.QueryLightweight(filter);
            var triggered = records.Select(r => r.HostId).AsEnumerable();
            if (isFirstScan && extraTriggerHosts is { Count: > 0 })
            {
                triggered = triggered.Concat(extraTriggerHosts);
            }

            // 取數範圍（docs/PRTG-SPEC.md §3a）：三種模式共用同一個下游收斂，差別只在候選主機怎麼來。
            // all-mapped 的候選是「全部有 ok 對應的主機」，與輪詢無關——它在首輪就全部取完，
            // 之後的輪詢因去重集合自然不再產生新主機。
            var candidateHosts = PrtgValueFetchScope.SelectHosts(
                effectiveScope, triggered, hostToDevices.Keys,
                isFirstScan ? (extraScopeHosts ?? Array.Empty<long>()) : Array.Empty<long>());

            isFirstScan = false;

            var newHosts = candidateHosts
                .Distinct()
                .Where(id => !fetchedHosts.Contains(id))
                .ToList();

            if (newHosts.Count == 0) return;

            var deviceObjids = new List<long>();
            foreach (var h in newHosts)
            {
                fetchedHosts.Add(h);
                if (hostToDevices.TryGetValue(h, out var devs))
                {
                    deviceObjids.AddRange(devs);
                }
            }

            var targets = _store.GetValueFetchTargets(whitelist, deviceObjids);
            if (targets.Count == 0) return;

            var (written, failedSensors) = await _fetchService.FetchValuesForSensorsAsync(day, targets, concurrency, ct, progress);
            totalTargetSensors += targets.Count;
            totalValuesWritten += written;
            totalFailedSensors += failedSensors;

            _console.WriteLine($"觸發式取數：新增問題主機 {newHosts.Count} 台、目標 sensor {targets.Count} 個，寫入 {written} 筆數值。");
        }

        while (true)
        {
            await ScanAndFetchAsync();
            if (analysisCompleted())
            {
                break;
            }
            progress?.Invoke(RunPhases.PrtgTriggered, totalTargetSensors, 0);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(pollSeconds, 1)), ct);
        }

        // 迴圈結束後再執行一次「掃描並取數」作為收尾掃描
        await ScanAndFetchAsync();

        return new PrtgTriggeredFetchResult(
            fetchedHosts.Count,
            totalTargetSensors,
            totalValuesWritten,
            totalFailedSensors);
    }
}
