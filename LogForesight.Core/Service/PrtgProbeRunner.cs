using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 環境探測（probe）執行器：唯讀呼叫 PRTG API，產出環境結構統計，
/// 作為後續分析層與感測器分類設計的輸入。
/// </summary>
public static class PrtgProbeRunner
{
    public static async Task<bool> RunAsync(PrtgClient client, IRunConsole console, CancellationToken ct = default)
    {
        console.WriteLine("══════════ PRTG 環境探測（Probe） ══════════");
        console.WriteLine("唯讀呼叫 PRTG API，產出環境結構統計供後續分析層設計參考。");
        console.WriteLine();

        var allOk = true;

        // 步驟 1：版本與連線
        allOk &= await StepAsync(console, 1, "版本與連線", async () =>
        {
            var json = await client.GetJsonAsync("/api/status.json?id=0", ct);
            var version = ExtractVersion(json);
            if (!string.IsNullOrWhiteSpace(version))
            {
                console.WriteLine($"     PRTG 版本：{version}");
            }
            else
            {
                console.WriteLine("     無法判讀版本");
            }
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測終止（連線或認證失敗） ══════════");
            return false;
        }

        // 步驟 2：device 與 sensor 總數
        int deviceCount = 0;
        int sensorCount = 0;
        allOk &= await StepAsync(console, 2, "Device 與 Sensor 總數", async () =>
        {
            var devJson = await client.GetJsonAsync("/api/table.json?content=devices&columns=objid&count=1", ct);
            var devTable = ParseTable(devJson, "devices", _ => true);
            deviceCount = devTable.TotalTreesize ?? devTable.Rows.Count;

            var senJson = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid&count=1", ct);
            var senTable = ParseTable(senJson, "sensors", _ => true);
            sensorCount = senTable.TotalTreesize ?? senTable.Rows.Count;

            console.WriteLine($"     Device 總數：{deviceCount}，Sensor 總數：{sensorCount}");
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測失敗 ══════════");
            return false;
        }

        // 步驟 3：sensor type 分布（最重要）
        allOk &= await StepAsync(console, 3, "Sensor Type 分布", async () =>
        {
            var senTableJson = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid,device,sensor,type,tags,unit&count=50000", ct);
            var parsedSensors = ParseTable(senTableJson, "sensors", el =>
            {
                var type = GetStringProperty(el, "type");
                if (string.IsNullOrWhiteSpace(type)) return null;
                var unit = GetStringProperty(el, "unit");
                return new SensorTypeSample(type, unit);
            });

            if (parsedSensors.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedSensors.CorruptedCount} 筆 sensor 無法解析");
            }

            var validSamples = parsedSensors.Rows;
            if (sensorCount > 0 && validSamples.Count < sensorCount)
            {
                console.WriteLine($"     ⚠ 警告：Sensor 總數為 {sensorCount} 筆，本次查詢僅取樣到 {validSamples.Count} 筆");
            }

            var groups = validSamples
                .GroupBy(s => s.Type, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Type = g.Key,
                    Count = g.Count(),
                    Units = g.Select(x => x.Unit)
                             .Where(u => !string.IsNullOrWhiteSpace(u))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Take(3)
                             .ToList()
                })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var totalValid = validSamples.Count;
            console.WriteLine($"     [Type 分布明細]（共 {groups.Count} 種不重複 Type，有效樣本 {totalValid} 筆）：");
            foreach (var g in groups)
            {
                var pct = totalValid > 0 ? (g.Count * 100.0 / totalValid) : 0.0;
                var unitsStr = g.Units.Count > 0 ? string.Join(", ", g.Units) : "無";
                console.WriteLine($"       {g.Type} | {g.Count} | {pct:F1}% | unit 樣本：{unitsStr}");
            }

            // 累積百分比門檻：50% / 80% / 90% / 95%
            var thresholds = new[] { 50, 80, 90, 95 };
            var thresholdTypesCount = new Dictionary<int, int>();
            int runningCount = 0;
            int currentTypeIdx = 0;
            foreach (var g in groups)
            {
                currentTypeIdx++;
                runningCount += g.Count;
                double currentPct = totalValid > 0 ? (runningCount * 100.0 / totalValid) : 0.0;
                foreach (var t in thresholds)
                {
                    if (!thresholdTypesCount.ContainsKey(t) && currentPct >= t)
                    {
                        thresholdTypesCount[t] = currentTypeIdx;
                    }
                }
            }

            var thresholdSummaries = thresholds.Select(t =>
            {
                var count = thresholdTypesCount.TryGetValue(t, out var c) ? c : groups.Count;
                return $"累積達 {t}% 需要 {count} 個 type";
            });

            console.WriteLine($"     [總結] 不重複 type 數量：{groups.Count} 種；{string.Join("，", thresholdSummaries)}");
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測失敗 ══════════");
            return false;
        }

        // 步驟 4：相依性（dependency）使用程度
        allOk &= await StepAsync(console, 4, "相依性（Dependency）使用程度", async () =>
        {
            string depJson;
            try
            {
                depJson = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid,dependency&count=50000", ct);
            }
            catch (PrtgClientException ex) when (ex.Message.Contains("400") || ex.Message.Contains("dependency", StringComparison.OrdinalIgnoreCase))
            {
                console.WriteLine("     此 PRTG 版本不支援 dependency 欄位查詢，略過此步驟");
                return;
            }

            var parsedDeps = ParseTable(depJson, "sensors", el =>
            {
                var dep = GetStringProperty(el, "dependency");
                return dep;
            });

            if (parsedDeps.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedDeps.CorruptedCount} 筆無法解析");
            }

            var withDep = parsedDeps.Rows.Count(d => HasDependency(d));
            var total = parsedDeps.Rows.Count;
            var pct = total > 0 ? (withDep * 100.0 / total) : 0.0;
            console.WriteLine($"     有設定相依性的 Sensor 數：{withDep} / {total}（佔比 {pct:F1}%）");
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測失敗 ══════════");
            return false;
        }

        // 步驟 5：群組樹概要
        allOk &= await StepAsync(console, 5, "群組樹概要", async () =>
        {
            var grpJson = await client.GetJsonAsync("/api/table.json?content=groups&columns=objid,group&count=1000", ct);
            var parsedGroups = ParseTable(grpJson, "groups", el =>
            {
                var grp = GetStringProperty(el, "group");
                return grp;
            });

            if (parsedGroups.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedGroups.CorruptedCount} 筆 group 無法解析");
            }

            var totalGrp = parsedGroups.TotalTreesize ?? parsedGroups.Rows.Count;
            var validGroupNames = parsedGroups.Rows.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            var top20 = validGroupNames.Take(20).ToList();
            console.WriteLine($"     群組總數：{totalGrp}，前 20 個群組名稱：{string.Join("，", top20)}");
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測失敗 ══════════");
            return false;
        }

        // 步驟 6：IP 覆蓋概要（供主機對應評估）
        allOk &= await StepAsync(console, 6, "IP 覆蓋概要（供主機對應評估）", async () =>
        {
            var devHostJson = await client.GetJsonAsync("/api/table.json?content=devices&columns=objid,device,host,group&count=50000", ct);
            var parsedDevHosts = ParseTable(devHostJson, "devices", el =>
            {
                var host = GetStringProperty(el, "host");
                return host;
            });

            if (parsedDevHosts.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedDevHosts.CorruptedCount} 筆 device 無法解析");
            }

            int totalDevices = parsedDevHosts.Rows.Count;
            int withHost = 0;
            int ipv4Count = 0;
            int dnsCount = 0;

            foreach (var h in parsedDevHosts.Rows)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                withHost++;
                if (IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4Count++;
                }
                else
                {
                    dnsCount++;
                }
            }

            console.WriteLine($"     Device 總筆數：{totalDevices}");
            console.WriteLine($"     有設定 host 值的 Device 數：{withHost}");
            console.WriteLine($"     其中為 IPv4 位址者：{ipv4Count} 台");
            console.WriteLine($"     其中非 IPv4（DNS 名稱或其它）者：{dnsCount} 台");
        });

        console.WriteLine();
        if (allOk)
        {
            console.WriteLine("══════════ PRTG 環境探測完成 ══════════");
        }
        else
        {
            console.WriteLine("══════════ PRTG 環境探測中斷（部分步驟失敗） ══════════");
        }

        return allOk;
    }

    private static async Task<bool> StepAsync(IRunConsole console, int index, string title, Func<Task> action)
    {
        console.WriteLine($"[{index}] {title}");
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     ✗ 失敗：{ex.Message}");
            return false;
        }
    }

    private sealed record SensorTypeSample(string Type, string? Unit);

    private sealed record TableParseResult<T>(List<T> Rows, int CorruptedCount, int? TotalTreesize);

    /// <summary>
    /// PRTG table.json 共用解析輔助方法：取出 content 相應陣列、逐筆轉換、容錯記錄損壞筆數，並讀取 treesize。
    /// 全類別唯一的 JsonDocument 資料陣列解析點。
    /// </summary>
    private static TableParseResult<T> ParseTable<T>(string json, string contentName, Func<JsonElement, T?> mapper)
    {
        var list = new List<T>();
        var corrupted = 0;
        int? treesize = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TableParseResult<T>(list, 1, null);
            }

            if (root.TryGetProperty("treesize", out var tsProp))
            {
                if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt32(out var tsVal))
                {
                    treesize = tsVal;
                }
                else if (tsProp.ValueKind == JsonValueKind.String && int.TryParse(tsProp.GetString(), out var tsParsed))
                {
                    treesize = tsParsed;
                }
            }

            if (root.TryGetProperty(contentName, out var arrayProp) && arrayProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arrayProp.EnumerateArray())
                {
                    try
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            corrupted++;
                            continue;
                        }

                        var mapped = mapper(item);
                        if (mapped != null)
                        {
                            list.Add(mapped);
                        }
                        else
                        {
                            corrupted++;
                        }
                    }
                    catch
                    {
                        corrupted++;
                    }
                }
            }
        }
        catch
        {
            corrupted++;
        }

        return new TableParseResult<T>(list, corrupted, treesize);
    }

    private static string? GetStringProperty(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetRawText();
            if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                return prop.GetRawText();
        }
        return null;
    }

    private static string? ExtractVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("Version", out var v1) && v1.ValueKind == JsonValueKind.String)
                return v1.GetString();
            if (root.TryGetProperty("version", out var v2) && v2.ValueKind == JsonValueKind.String)
                return v2.GetString();
            if (root.TryGetProperty("prtg-version", out var v3) && v3.ValueKind == JsonValueKind.String)
                return v3.GetString();
            if (root.TryGetProperty("Prtg-Version", out var v4) && v4.ValueKind == JsonValueKind.String)
                return v4.GetString();

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasDependency(string? dep)
    {
        if (string.IsNullOrWhiteSpace(dep)) return false;
        var trimmed = dep.Trim();
        if (trimmed == "0" || trimmed == "-1" || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
