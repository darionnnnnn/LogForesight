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
        // 樣本留在外層，供步驟 7 與 device host 資料交叉統計（不再多打一次 API）
        List<SensorTypeSample> sensorSamples = new();
        allOk &= await StepAsync(console, 3, "Sensor Type 分布", async () =>
        {
            // 註：PRTG sensors 表沒有 unit 欄位（未知欄位會被靜默忽略），單位資訊實際在
            // lastvalue 的格式化字串（如「92 %」「12 kbit/s」）；unit 欄保留作 fallback。
            var senTableJson = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid,device,sensor,type,tags,unit,lastvalue,parentid&count=50000", ct);
            var parsedSensors = ParseTable(senTableJson, "sensors", el =>
            {
                var type = GetStringProperty(el, "type");
                if (string.IsNullOrWhiteSpace(type)) return null;
                var unit = GetStringProperty(el, "unit");
                if (string.IsNullOrWhiteSpace(unit))
                {
                    unit = ExtractUnitFromLastValue(GetStringProperty(el, "lastvalue"));
                }
                int? parentId = null;
                if (int.TryParse(GetStringProperty(el, "parentid"), out var pid))
                {
                    parentId = pid;
                }
                return new SensorTypeSample(type, unit, parentId);
            });

            if (parsedSensors.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedSensors.CorruptedCount} 筆 sensor 無法解析");
            }

            var validSamples = parsedSensors.Rows;
            sensorSamples = validSamples;
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
            console.WriteLine("     （註：PRTG 預設每個 sensor 相依於父物件，此比例含預設值，不代表人工維護的相依拓撲）");
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
        var ipv4DeviceIds = new HashSet<int>();
        allOk &= await StepAsync(console, 6, "IP 覆蓋概要（供主機對應評估）", async () =>
        {
            var devHostJson = await client.GetJsonAsync("/api/table.json?content=devices&columns=objid,device,host,group&count=50000", ct);
            var parsedDevHosts = ParseTable(devHostJson, "devices", el =>
            {
                var host = GetStringProperty(el, "host");
                int? objid = null;
                if (int.TryParse(GetStringProperty(el, "objid"), out var oid))
                {
                    objid = oid;
                }
                return new DeviceHostSample(objid, host);
            });

            if (parsedDevHosts.CorruptedCount > 0)
            {
                console.WriteLine($"     ⚠ 有 {parsedDevHosts.CorruptedCount} 筆 device 無法解析");
            }

            int totalDevices = parsedDevHosts.Rows.Count;
            int withHost = 0;
            int ipv4Count = 0;
            int ipv6Count = 0;
            int dnsCount = 0;
            var invalidSamples = new List<string>();
            int invalidCount = 0;

            // 判定與主機對應、資源守門用同一份純語法層（PrtgAddress）：
            // 「10.1.2.3:8080」在主機對應算 IP，這裡就不能算成名稱，否則探測結論與實際對應結果對不上。
            foreach (var d in parsedDevHosts.Rows)
            {
                if (string.IsNullOrWhiteSpace(d.Host)) continue;
                withHost++;
                var normalized = PrtgAddress.Normalize(d.Host);
                if (normalized != null && IPAddress.TryParse(normalized, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4Count++;
                    if (d.Objid.HasValue)
                    {
                        ipv4DeviceIds.Add(d.Objid.Value);
                    }
                }
                else if (normalized != null)
                {
                    // 合法 IPv6：主機對應比得到，但步驟 7 的「IPv4 覆蓋」不算它
                    ipv6Count++;
                }
                else if (PrtgAddress.IsDnsCandidate(PrtgAddress.HostToken(d.Host)))
                {
                    dnsCount++;
                }
                else
                {
                    // 打壞的 IP（10.2xx.x.x）、含備註或空白——主機對應與守門都不會拿它去 DNS，等於沒設 host
                    invalidCount++;
                    if (invalidSamples.Count < 5)
                        invalidSamples.Add($"objid {d.Objid?.ToString() ?? "?"}「{d.Host}」");
                }
            }

            console.WriteLine($"     Device 總筆數：{totalDevices}");
            // 與 sensor 步驟同一道截斷偵測：單次大 count 拿到的筆數少於 treesize 就是被截斷，
            // 少了這行，下面的 IP 覆蓋比例會用不完整的分母算出好看的數字。
            if (deviceCount > 0 && totalDevices < deviceCount)
            {
                console.WriteLine($"     ⚠ 警告：Device 總數為 {deviceCount} 筆，本次查詢僅取樣到 {totalDevices} 筆");
            }
            else if (deviceCount > 0 && totalDevices > deviceCount)
            {
                console.WriteLine($"     ⚠ 注意：treesize 回報 {deviceCount} 筆但實際取得 {totalDevices} 筆——treesize 不是這個 content 的實際總筆數");
            }
            console.WriteLine($"     有設定 host 值的 Device 數：{withHost}");
            console.WriteLine($"     其中為 IPv4 位址者：{ipv4Count} 台");
            console.WriteLine($"     其中為 DNS 名稱者：{dnsCount} 台（主機對應需靠 DNS 解析）");
            if (ipv6Count > 0)
            {
                console.WriteLine($"     其中為 IPv6 位址者：{ipv6Count} 台");
            }
            if (invalidCount > 0)
            {
                console.WriteLine($"     其中無法判定（打壞的 IP 或含備註）者：{invalidCount} 台——不會被解析也對不到主機，建議到 PRTG 修正：" +
                                  string.Join("、", invalidSamples) + (invalidCount > invalidSamples.Count ? "…" : ""));
            }
        });

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ PRTG 環境探測中斷（部分步驟失敗） ══════════");
            return false;
        }

        // 步驟 7：type × IPv4 覆蓋交叉統計（重用步驟 3 與 6 的資料，不另打 API）——
        // 回答「各 type 有多少 sensor 落在可做主機對應（IPv4）的 device 上」，供擷取縮圈規劃使用
        allOk &= await StepAsync(console, 7, "Type × IPv4 覆蓋（sensor 位於 IPv4 device 上的比例）", () =>
        {
            var withParent = sensorSamples.Where(s => s.ParentId.HasValue).ToList();
            if (withParent.Count == 0 || ipv4DeviceIds.Count == 0)
            {
                console.WriteLine("     無 parentid 或 IPv4 device 資料，略過此統計（舊版 PRTG 可能不支援 parentid 欄位）");
                return Task.CompletedTask;
            }

            var crossGroups = withParent
                .GroupBy(s => s.Type, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Type = g.Key,
                    Total = g.Count(),
                    OnIpv4 = g.Count(s => ipv4DeviceIds.Contains(s.ParentId!.Value))
                })
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var g in crossGroups)
            {
                var pct = g.Total > 0 ? (g.OnIpv4 * 100.0 / g.Total) : 0.0;
                console.WriteLine($"       {g.Type} | {g.OnIpv4}/{g.Total} | {pct:F1}%");
            }

            return Task.CompletedTask;
        });

        // 步驟 8：分頁語意診斷（不影響探測成敗）——回答「這台 PRTG 的 table.json 遵不遵守 start 位移」。
        // 結構同步與資源守門的分頁迴圈都以「遵守 start」為前提；忽略 start 或超出範圍時夾回某一頁的
        // 環境會讓分頁永遠收不斂。這一步只做三次 count=5 的小查詢，結果純供人工判讀，任何一筆失敗
        // 都只印出原因、不把整趟探測算失敗。
        console.WriteLine("[8] 分頁語意診斷（table.json 的 start 位移是否被遵守）");
        foreach (var (content, extra) in new[] { ("devices", ""), ("sensors", ""), ("messages", "&id=0&filter_drel=7days") })
        {
            await DiagnosePagingAsync(client, console, content, extra, ct);
        }

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

    /// <summary>
    /// 對單一 content 發三次 count=5 查詢（start=0、start=5、start=999999），比較各頁 objid 集合，
    /// 歸納這台 PRTG 對 start 位移的處理方式。判讀結果與分頁迴圈的影響一起印出。
    /// </summary>
    private static async Task DiagnosePagingAsync(PrtgClient client, IRunConsole console, string content, string extraQuery, CancellationToken ct)
    {
        const int probeCount = 5;
        const int farOffset = 999999;
        try
        {
            var page0 = await ReadObjidsAsync(client, content, extraQuery, 0, probeCount, ct);
            var page1 = await ReadObjidsAsync(client, content, extraQuery, probeCount, probeCount, ct);
            var pageFar = await ReadObjidsAsync(client, content, extraQuery, farOffset, probeCount, ct);

            string Show(List<long> ids) => ids.Count == 0 ? "（空）" : string.Join(",", ids);
            console.WriteLine($"     {content}：start=0 → [{Show(page0)}]；start={probeCount} → [{Show(page1)}]；start={farOffset} → [{Show(pageFar)}]");

            if (page0.Count == 0)
            {
                console.WriteLine($"     {content}：第一頁就沒有資料，無法判定");
                return;
            }

            var sameAsFirst = page1.SequenceEqual(page0);
            var farSameAsFirst = pageFar.SequenceEqual(page0);

            if (page0.Count < probeCount)
            {
                console.WriteLine($"     {content}：總筆數不足 {probeCount} 筆，無法判定 start 語意（資料太少，分頁本來就只有一頁）");
            }
            else if (sameAsFirst)
            {
                console.WriteLine($"     {content}：✗ 完全忽略 start——第二頁與第一頁相同。分頁在這個環境抓不到第一頁以外的資料，只能靠單次大 count 抓完");
            }
            else if (pageFar.Count == 0)
            {
                console.WriteLine($"     {content}：✓ 遵守 start，超出範圍回空頁（分頁迴圈可正常收斂）");
            }
            else if (farSameAsFirst)
            {
                console.WriteLine($"     {content}：⚠ 遵守 start，但超出範圍時回到第一頁——總筆數剛好是頁大小整數倍時，分頁迴圈會在最後多讀一次第一頁；需要「本頁無新 objid 即停」的保險絲");
            }
            else
            {
                console.WriteLine($"     {content}：⚠ 遵守 start，但超出範圍時夾到最後一頁（非空、與第一頁不同）——總筆數剛好是頁大小整數倍時會重讀末頁；需要「本頁無新 objid 即停」的保險絲");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     {content}：無法判定（{ex.Message}）");
        }
    }

    private static async Task<List<long>> ReadObjidsAsync(PrtgClient client, string content, string extraQuery, int start, int count, CancellationToken ct)
    {
        var json = await client.GetJsonAsync($"/api/table.json?content={content}&columns=objid&count={count}&start={start}{extraQuery}", ct);
        var parsed = ParseTable(json, content, el => long.TryParse(GetStringProperty(el, "objid"), out var id) ? (long?)id : null);
        return parsed.Rows.Select(r => r!.Value).ToList();
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

    private sealed record SensorTypeSample(string Type, string? Unit, int? ParentId);

    private sealed record DeviceHostSample(int? Objid, string? Host);

    /// <summary>
    /// 從 lastvalue 的格式化字串（如「92 %」「12 kbit/s」「&lt;1 ms」「1,234 msec」）萃取單位。
    /// 只在字串以數值（或比較符號）開頭時視為「數值＋單位」；純文字狀態（如「OK」）與「-」回 null。
    /// </summary>
    private static string? ExtractUnitFromLastValue(string? lastValue)
    {
        if (string.IsNullOrWhiteSpace(lastValue)) return null;
        var s = lastValue.Trim();

        var first = s[0];
        if (!char.IsDigit(first) && first != '<' && first != '>' && first != '~' && first != '-' && first != '+')
            return null;
        if (s == "-") return null;

        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '<' or '>' or '=' or '~' or ',' or '.' or ' ' or '-' or '+'))
        {
            i++;
        }

        var unit = s[i..].Trim();
        return unit.Length == 0 ? null : unit;
    }

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
