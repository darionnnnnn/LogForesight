using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 以一台已對應主機、一顆感測器、一天的真實資料驗證正式數值擷取路徑。
/// 不執行結構同步或清除；唯一寫入是該 sensor 當日 hourly 數值的冪等 upsert。
/// </summary>
public static class PrtgProbeDataFlowRunner
{
    public static async Task<bool> RunAsync(PrtgClient client, StorageBackend backend, IHostStore hosts,
        SystemSettings settings, IRunConsole console, CancellationToken ct = default, DateTime? targetDay = null)
    {
        console.WriteLine("══════════ PRTG 小範圍資料流驗證 ══════════");
        console.WriteLine("最多抽樣 20 台已對應主機，實際只向 PRTG 讀取 1 顆感測器的 1 天 historicdata；會將該日真實數值寫入資料庫，不會清除資料。");
        var day = (targetDay ?? DateTime.Today.AddDays(-1)).Date;
        var store = backend.PrtgStore();
        var (mapDate, mapRows) = store.GetLatestHostMapWithDate();
        var activeHosts = hosts.GetAll().Where(h => h.Active && h.MergedInto == null)
            .ToDictionary(h => h.HostId);
        var mapped = mapRows.Where(m => m.MapStatus == PrtgMapStatus.Ok &&
                                      m.HostId.HasValue && activeHosts.ContainsKey(m.HostId.Value))
            .OrderBy(m => m.DeviceObjid).Take(20).ToList();
        if (mapped.Count == 0)
        {
            console.WriteLine("✗ 找不到已對應且啟用中的主機。請先到「鏡像狀態」同步結構與對應，再重試。");
            return false;
        }

        var sensors = store.GetSensorsForDevices(mapped.Select(m => m.DeviceObjid).ToArray());
        var whitelist = settings.PrtgSensorTypeWhitelist ?? new List<string>();
        var allowedTypes = whitelist.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = mapped.SelectMany(m => sensors.Where(s => s.DeviceObjid == m.DeviceObjid &&
                         !s.Paused && (allowedTypes.Count == 0 || allowedTypes.Contains(s.SensorType)))
                    .Select(s => (Map: m, Sensor: s)))
            .FirstOrDefault();
        if (candidate.Sensor == null)
        {
            console.WriteLine($"✗ 前 {mapped.Count} 台已對應主機中沒有未暫停且符合 sensor type 白名單的感測器；未對 PRTG 發出取值請求。請檢查鏡像及白名單。");
            return false;
        }

        var map = candidate.Map;
        var sensor = candidate.Sensor;
        console.WriteLine($"[1/3] 目前主機對應 ✓ 主機「{activeHosts[map.HostId!.Value].HostName}」(HostId={map.HostId}) → 裝置 {map.DeviceObjid} → sensor {sensor.Objid}「{sensor.Name}」({sensor.SensorType})；對應日期 {mapDate:yyyy-MM-dd}。");
        console.WriteLine($"      此裝置鏡像共有 {sensors.Count(s => s.DeviceObjid == map.DeviceObjid)} 顆 sensor，本次只驗 1 顆；不代表每台只會抓 1 顆。");

        var startedAt = DateTime.Now;
        var fetch = new PrtgFetchService(client, store,
            new PrtgFreshnessStore(backend.Blob(PrtgFreshnessStore.BlobKey)), console,
            new Dictionary<string, string>());
        var (written, failed) = await fetch.FetchValuesForSensorsAsync(day, new[] { sensor.Objid }, 1, ct);
        if (failed != 0 || written == 0)
        {
            console.WriteLine($"[2/3] 取值／解析／寫入 ✗ {day:yyyy-MM-dd} 寫入 {written} 筆、失敗 sensor {failed} 顆；不能據此判定數值路徑可用。");
            return false;
        }
        console.WriteLine($"[2/3] 取值／解析／寫入 ✓ {day:yyyy-MM-dd}，寫入 {written} 筆 hourly 紀錄。");

        var rows = store.GetValuesForSensor(sensor.Objid, day, day.AddDays(1))
            .Where(v => v.CreatedAt >= startedAt).ToList();
        var numeric = rows.Where(v => v.Quality == PrtgDataQuality.Ok && v.AvgValue.HasValue).ToList();
        if (numeric.Count == 0)
        {
            console.WriteLine($"[3/3] 資料庫讀回 ✗ 本次寫入可讀回 {rows.Count} 筆，但沒有可用的數值；請檢查 PRTG 回應的 value_raw、coverage 與日期格式。");
            return false;
        }
        console.WriteLine($"[3/3] 資料庫讀回 ✓ 本次寫入 {rows.Count} 筆，其中有效數值 {numeric.Count} 筆；例如 {numeric[0].PeriodStart:HH:mm} = {numeric[0].AvgValue:G}。");
        console.WriteLine("結論：此樣本的目前主機對應、PRTG historicdata、正式解析與資料庫寫入／讀回可用。昨天的歷史歸戶是否與目前相同，以及定時快照、狀態變更、規則命中、全站覆蓋率與效能，未由此樣本驗證。");
        return true;
    }
}
