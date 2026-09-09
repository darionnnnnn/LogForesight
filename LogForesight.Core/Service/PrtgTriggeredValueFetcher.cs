using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>觸發式數值取數的結果統計</summary>
public sealed record PrtgTriggeredFetchResult(
    int TriggerHosts, int TargetSensors, int ValuesWritten, int FailedSensors);

/// <summary>
/// 觸發式 PRTG 數值取數：預設只對「**目標日**風險為高或中」的主機所對應的 device 上、
/// 且 type 命中白名單的 sensor 擷取 hourly 數值，並與分析流程並行進行。
/// 目標日就是這趟批次處理的那一天（`day` 參數），不是固定的昨天——歷史回填也走這裡。
/// 取數範圍可由 `PrtgValueFetchScope` 放寬，見 docs/PRTG-SPEC.md §3a。
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

        // all-mapped 的候選集合是「該日全部 ok 對應的主機」，與分析結果無關——首輪就會全部取完，
        // 之後的輪詢只是空等到分析結束（畫面上 PRTG 軌長時間停在執行中）。因此這個模式單輪收工。
        var singlePass = effectiveScope == PrtgValueFetchScope.AllMapped;

        if (singlePass && hostToDevices.Count == 0)
        {
            // 管理者指定了「全部已對應主機」卻一台都沒有，多半是鏡像還沒同步過或對應全部落空。
            // 靜默收工會讓畫面顯示「已完成」而什麼都沒抓到，看不出原因。
            // triggered 模式沒有觸發主機是常態（當天沒人出問題），不在這裡出聲。
            _console.WriteLine("  ⚠ 取數範圍是「全部已對應主機」，但這一天沒有任何已對應的 PRTG 主機，" +
                               "本次沒有取到任何數值。請先執行「同步結構與對應」，" +
                               "或到 PRTG 維護頁的鏡像狀態檢查主機對應結果。");
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
            if (singlePass || analysisCompleted())
            {
                break;
            }
            progress?.Invoke(RunPhases.PrtgTriggered, totalTargetSensors, 0);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(pollSeconds, 1)), ct);
        }

        // 迴圈結束後再執行一次「掃描並取數」作為收尾掃描：統計段先寫入紀錄、AI 段可能事後上調風險，
        // 只靠過程中的輪詢會漏掉這些主機。all-mapped 的候選與分析無關，首輪已取完，不需要這一次。
        if (!singlePass)
        {
            await ScanAndFetchAsync();
        }

        return new PrtgTriggeredFetchResult(
            fetchedHosts.Count,
            totalTargetSensors,
            totalValuesWritten,
            totalFailedSensors);
    }
}
