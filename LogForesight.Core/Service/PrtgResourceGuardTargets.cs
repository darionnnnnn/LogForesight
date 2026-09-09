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
    /// <param name="ignoreOverride">
    /// true＝跳過覆寫清單、強制走自動偵測。給維護頁「自動偵測並填入」按鈕用：
    /// 管理者最常見的操作是「已經手填了一些 objid，想重抓一次」，不忽略覆寫的話
    /// 只會把手填值原樣吐回來。守門執行本身一律傳 false（覆寫優先是它的既定契約）。
    /// </param>
    public static PrtgResourceGuardTargetResult Resolve(
        IPrtgResourceGuardSource source,
        SystemSettings settings,
        IReadOnlyList<Sentinel> sentinels,
        IRunConsole console,
        IPrtgAddressResolver resolver,
        bool ignoreOverride = false)
    {
        // 1. 覆寫優先：PrtgResourceGuardSensorObjids 非空時，直接用它（逐項 long.TryParse，parse 失敗略過並警告）。
        // 依規格：設定了 objid 清單時，回傳的就是那些 objid，且完全不查 device（即使鏡像表為空也回得出清單）。
        if (!ignoreOverride && settings.PrtgResourceGuardSensorObjids != null && settings.PrtgResourceGuardSensorObjids.Count > 0)
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
                var sensors = source.GetSensors();
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
            catch (Exception ex)
            {
                // 分類讀不到就全部視為 unknown → Evaluate 一律忽略 → 守門靜默失效。
                // 這條要讓人看得到，否則排查時只會看到「守門沒作用」而找不到原因。
                console.WriteLine($"[PRTG資源守門] 警告：讀取 sensor 分類失敗，覆寫清單全部視為無法判定（{ex.GetType().Name}）。");
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

        var allDevices = source.GetDevices();
        var allSensors = source.GetSensors();

        // 3. 對每個位址比對 device
        var sentinelDeviceObjids = new HashSet<long>();
        foreach (var sHost in sentinelHosts)
        {
            var matchedDevs = FindDevicesForHost(sHost, allDevices, resolver);
            if (matchedDevs.Count == 0)
            {
                console.WriteLine($"[PRTG資源守門] 找不到主機「{sHost}」對應的 PRTG 裝置{SourceHint(source)}。");
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
            var prtgDevs = FindDevicesForHost(prtgHost, allDevices, resolver);
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
            console.WriteLine($"[PRTG資源守門] 找不到 PRTG 主機「{prtgHost}」對應的 PRTG 裝置{SourceHint(source)}。");
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
    /// 訊息裡的來源註記：讀鏡像時「找不到」多半是鏡像還沒同步過，直接查 PRTG 時
    /// 「找不到」才代表 PRTG 上真的沒有這台。兩者的處置完全不同，訊息要分得出來。
    /// </summary>
    private static string SourceHint(IPrtgResourceGuardSource source) =>
        source.SourceLabel == "live"
            ? "（已直接查詢 PRTG）"
            : "（查的是本機鏡像；PRTG 剛啟用時請先執行「同步結構與對應」）";

    /// <summary>
    /// 依主機名稱比對 PRTG 裝置（先用解析器比對 IP，對不到時退回主機名稱字面比對）。
    /// </summary>
    private static List<PrtgDeviceRow> FindDevicesForHost(
        string host,
        IReadOnlyList<PrtgDeviceRow> allDevices,
        IPrtgAddressResolver resolver)
    {
        var matched = new List<PrtgDeviceRow>();

        // 1. 兩邊都用解析器（IP 直接比、名稱先 DNS 解析成 IP 再比）
        var resolvedHost = resolver.Resolve(host);
        if (resolvedHost != null)
        {
            foreach (var dev in allDevices)
            {
                var devIp = resolver.Resolve(dev.Ip);
                if (devIp != null && string.Equals(devIp, resolvedHost, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(dev);
                }
            }
        }

        if (matched.Count > 0)
        {
            return matched;
        }

        // 最後退路：主機名稱字面比對。device 的 Ip 欄位也可能填 DNS 名稱，
        // 而內網名稱未必進得了 DNS（解析失敗時上面兩段都落空）——兩邊填同一個名稱時仍應命中。
        // 只在前兩段都對不到時才走，且比對前先去掉 scheme 與 port。
        if (matched.Count == 0)
        {
            var hostToken = PrtgAddress.HostToken(host);
            if (hostToken != null)
            {
                foreach (var dev in allDevices)
                {
                    var devToken = PrtgAddress.HostToken(dev.Ip);
                    if (devToken != null && string.Equals(devToken, hostToken, StringComparison.OrdinalIgnoreCase))
                    {
                        matched.Add(dev);
                    }
                }
            }
        }

        return matched;
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
