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
        int pollSeconds = 30)
    {
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
            var newHosts = records
                .Select(r => r.HostId)
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

            var (written, failedSensors) = await _fetchService.FetchValuesForSensorsAsync(day, targets, concurrency, ct);
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
