using System.Net;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 資源守門受監看目標結果。
/// </summary>
public sealed record PrtgResourceGuardTargetResult(
    IReadOnlyList<long> SensorObjids,
    IReadOnlyDictionary<long, string> SensorCategories)
;

/// <summary>
/// 批次執行期間 PRTG 資源守門的監看感測器目標自動偵測與覆寫解析服務。
/// </summary>
public static class PrtgResourceGuardTargets
{
    public const string CategoryCoreHealth = "corehealth";
    public const string CategoryUnknown = "unknown";

    /// <summary>
    /// 解析本趟批次執行應監看的 PRTG 感測器清單。
    /// </summary>
    /// <param name="prtgStore">PRTG 鏡像資料存放區</param>
    /// <param name="settings">系統設定（含資源守門參數與 PRTG 連線設定）</param>
    /// <param name="sentinels">Sentinel 連線設定清單</param>
    /// <param name="console">執行輸出終端機（供輸出警告訊息）</param>
    /// <returns>包含受監看感測器 objid 清單與分類的結果物件</returns>
    public static PrtgResourceGuardTargetResult Resolve(
        EfPrtgStore prtgStore,
        SystemSettings settings,
        IReadOnlyList<Sentinel> sentinels,
        IRunConsole console)
    {
        // 1. 覆寫優先：PrtgResourceGuardSensorObjids 非空時，直接用它（逐項 long.TryParse，parse 失敗略過並警告）。
        // 依規格：設定了 objid 清單時，回傳的就是那些 objid，且完全不查 device（即使鏡像表為空也回得出清單）。
        if (settings.PrtgResourceGuardSensorObjids != null && settings.PrtgResourceGuardSensorObjids.Count > 0)
        {
            var objids = new List<long>();
            foreach (var item in settings.PrtgResourceGuardSensorObjids)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (long.TryParse(item.Trim(), out var parsedId))
                {
                    if (!objids.Contains(parsedId))
                        objids.Add(parsedId);
                }
                else
                {
                    console.WriteLine($"[PRTG資源守門] 略過非數字的感測器 objid 設定「{item}」。");
                }
            }

            var categories = new Dictionary<long, string>();
            try
            {
                var sensors = prtgStore.GetAllSensors();
                var sensorMap = sensors.ToDictionary(s => s.Objid);
                foreach (var id in objids)
                {
                    if (sensorMap.TryGetValue(id, out var s))
                    {
                        if (IsCoreHealthSensor(s))
                            categories[id] = CategoryCoreHealth;
                        else if (string.Equals(s.Category, PrtgSensorCategories.Cpu, StringComparison.OrdinalIgnoreCase))
                            categories[id] = PrtgSensorCategories.Cpu;
                        else if (string.Equals(s.Category, PrtgSensorCategories.Memory, StringComparison.OrdinalIgnoreCase))
                            categories[id] = PrtgSensorCategories.Memory;
                        else
                            categories[id] = s.Category?.ToLowerInvariant() ?? CategoryUnknown;
                    }
                    else
                    {
                        categories[id] = CategoryUnknown;
                    }
                }
            }
            catch
            {
                foreach (var id in objids)
                    categories.TryAdd(id, CategoryUnknown);
            }

            return new PrtgResourceGuardTargetResult(objids, categories);
        }

        // 2. 自動偵測：組出「要監看的主機位址集合」
        var sentinelHosts = new List<string>();
        foreach (var s in sentinels)
        {
            if (string.IsNullOrWhiteSpace(s.BaseUrl)) continue;
            if (Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                if (!sentinelHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                    sentinelHosts.Add(uri.Host);
            }
            else
            {
                console.WriteLine($"[PRTG資源守門] Sentinel「{s.Name}」的網址「{s.BaseUrl}」無法解析 host，已略過。");
            }
        }

        string? prtgHost = null;
        if (!string.IsNullOrWhiteSpace(settings.PrtgUrl))
        {
            if (Uri.TryCreate(settings.PrtgUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                prtgHost = uri.Host;
            }
            else
            {
                console.WriteLine($"[PRTG資源守門] PRTG 連線網址「{settings.PrtgUrl}」無法解析 host。");
            }
        }

        var allDevices = prtgStore.GetAllDevices();
        var allSensors = prtgStore.GetAllSensors();

        // 3. 對每個位址比對 device
        var sentinelDeviceObjids = new HashSet<long>();
        foreach (var sHost in sentinelHosts)
        {
            var matchedDevs = FindDevicesForHost(sHost, allDevices);
            if (matchedDevs.Count == 0)
            {
                console.WriteLine($"[PRTG資源守門] 找不到主機「{sHost}」對應的 PRTG 裝置。");
            }
            else
            {
                foreach (var d in matchedDevs)
                    sentinelDeviceObjids.Add(d.Objid);
            }
        }

        var prtgDeviceObjids = new HashSet<long>();
        bool prtgMatched = false;
        if (prtgHost != null)
        {
            var prtgDevs = FindDevicesForHost(prtgHost, allDevices);
            if (prtgDevs.Count > 0)
            {
                prtgMatched = true;
                foreach (var d in prtgDevs)
                    prtgDeviceObjids.Add(d.Objid);
            }
        }

        // 4. PRTG 主機的 fallback：若 PRTG 位址仍對不到 device，改找「底下有 SensorType 含 corehealth（不分大小寫）的 sensor」的那台 device
        // PRTG 的 Core Health sensor 只掛在 core server 自己身上。
        if (!prtgMatched)
        {
            var coreHealthSensor = allSensors.FirstOrDefault(s => IsCoreHealthSensor(s));
            if (coreHealthSensor != null)
            {
                var dev = allDevices.FirstOrDefault(d => d.Objid == coreHealthSensor.DeviceObjid);
                if (dev != null)
                {
                    prtgMatched = true;
                    prtgDeviceObjids.Add(dev.Objid);
                }
                else
                {
                    prtgMatched = true;
                    prtgDeviceObjids.Add(coreHealthSensor.DeviceObjid);
                }
            }
        }

        if (!prtgMatched && prtgHost != null)
        {
            console.WriteLine($"[PRTG資源守門] 找不到 PRTG 主機「{prtgHost}」對應的 PRTG 裝置。");
        }

        // 5. 取命中的 device 底下未暫停（Paused == false）且 Category 為 cpu 或 memory 的 sensor；
        // PRTG 主機那台另外加上 corehealth sensor。
        var targetSensors = new Dictionary<long, string>();

        foreach (var sensor in allSensors)
        {
            if (sensor.Paused) continue;

            bool isSentinelDev = sentinelDeviceObjids.Contains(sensor.DeviceObjid);
            bool isPrtgDev = prtgDeviceObjids.Contains(sensor.DeviceObjid);

            if (!isSentinelDev && !isPrtgDev) continue;

            var effectiveCategory = GetEffectiveCategory(sensor);
            bool isCpu = string.Equals(effectiveCategory, PrtgSensorCategories.Cpu, StringComparison.OrdinalIgnoreCase);
            bool isMemory = string.Equals(effectiveCategory, PrtgSensorCategories.Memory, StringComparison.OrdinalIgnoreCase);
            bool isCoreHealth = IsCoreHealthSensor(sensor);

            if (isCpu)
            {
                targetSensors[sensor.Objid] = PrtgSensorCategories.Cpu;
            }
            else if (isMemory)
            {
                targetSensors[sensor.Objid] = PrtgSensorCategories.Memory;
            }
            else if (isPrtgDev && isCoreHealth)
            {
                targetSensors[sensor.Objid] = CategoryCoreHealth;
            }
        }

        // 6. 一個 sensor 都沒找到時回傳空清單並警告（不要擲例外）——
        // 守門的原則是「讀不到就放行」，不能反過來把排程卡死。
        if (targetSensors.Count == 0)
        {
            console.WriteLine("[PRTG資源守門] 未偵測到任何受監看的 PRTG 感測器。");
            return new PrtgResourceGuardTargetResult(Array.Empty<long>(), targetSensors);
        }

        var sortedObjids = targetSensors.Keys.ToList();
        return new PrtgResourceGuardTargetResult(sortedObjids, targetSensors);
    }

    /// <summary>
    /// 依主機名稱比對 PRTG 裝置（先比對純字串，比對不到時進行 DNS 解析比對 IPv4）。
    /// </summary>
    private static List<PrtgDeviceRow> FindDevicesForHost(
        string host,
        IReadOnlyList<PrtgDeviceRow> allDevices)
    {
        var matched = new List<PrtgDeviceRow>();
        var normHost = PrtgHostMapper.NormalizeIp(host);
        if (normHost != null)
        {
            foreach (var dev in allDevices)
            {
                var devIp = PrtgHostMapper.NormalizeIp(dev.Ip);
                if (devIp != null && string.Equals(devIp, normHost, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(dev);
                }
            }
        }

        if (matched.Count > 0)
        {
            return matched;
        }

        // 理由：Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，純字串比對會全數落空。
        // 當以主機字串比對不到 device 時，執行一次 DNS 解析，再以解析出的 IPv4 比對 device 的 Ip。
        var addresses = ResolveHostAddressesWithTimeout(host);
        var ipv4Strings = addresses
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => PrtgHostMapper.NormalizeIp(a.ToString()))
            .Where(ip => ip != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ipv4Strings.Count > 0)
        {
            foreach (var dev in allDevices)
            {
                var devIp = PrtgHostMapper.NormalizeIp(dev.Ip);
                if (devIp != null && ipv4Strings.Contains(devIp))
                {
                    matched.Add(dev);
                }
            }
        }

        return matched;
    }

    /// <summary>
    /// 對主機名稱進行 DNS 解析（帶 2 秒逾時保護）。
    /// 理由：Sentinel 常以 DNS 名稱設定、PRTG device 常填 IPv4，純字串比對會全數落空。
    /// 為避免解析不到的主機或網路問題拖慢整段批次偵測，使用 Task.Run 加上逾時保護。
    /// 解析失敗或逾時一律視為找不到，不擲例外。
    /// </summary>
    private static IPAddress[] ResolveHostAddressesWithTimeout(string host, int timeoutMs = 2000)
    {
        try
        {
            var task = Task.Run(() => Dns.GetHostAddresses(host));
            if (task.Wait(timeoutMs))
            {
                return task.Result;
            }
            return Array.Empty<IPAddress>();
        }
        catch (Exception)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>
    /// 取得感測器的有效語意分類（以 Category 優先，若為空則嘗試由 SensorType 對照表推導）。
    /// </summary>
    private static string? GetEffectiveCategory(PrtgSensorRow sensor)
    {
        if (!string.IsNullOrWhiteSpace(sensor.Category))
            return sensor.Category;
        if (!string.IsNullOrWhiteSpace(sensor.SensorType) &&
            PrtgSensorTypeCategoryMap.Map.TryGetValue(sensor.SensorType, out var mapped))
            return mapped;
        return null;
    }

    /// <summary>
    /// 判定感測器是否為 PRTG Core Health 核心健康感測器。
    /// </summary>
    private static bool IsCoreHealthSensor(PrtgSensorRow sensor)
    {
        if (string.IsNullOrWhiteSpace(sensor.SensorType)) return false;
        var normalized = sensor.SensorType.Replace(" ", "");
        return normalized.IndexOf("corehealth", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
