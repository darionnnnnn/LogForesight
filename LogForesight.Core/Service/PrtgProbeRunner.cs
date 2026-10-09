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
    public static Task<bool> RunAsync(PrtgClient client, IRunConsole console, CancellationToken ct = default) =>
        RunAsync(client, console, null, ct, null);

    public static Task<bool> RunAsync(PrtgClient client, IRunConsole console, PrtgProbeEvidenceContext? context, CancellationToken ct = default, Action<string>? onEvidenceJsonProduced = null) =>
        RunCoreAsync(client, console, TimeSpan.FromSeconds(15), context, ct, onEvidenceJsonProduced);

    internal static Task<bool> RunCoreAsync(PrtgClient client, IRunConsole console,
        TimeSpan dependencyBudget, CancellationToken ct = default) =>
        RunCoreAsync(client, console, dependencyBudget, null, ct, null);

    internal static async Task<bool> RunCoreAsync(PrtgClient client, IRunConsole console,
        TimeSpan dependencyBudget, PrtgProbeEvidenceContext? evidenceContext, CancellationToken ct = default,
        Action<string>? onEvidenceJsonProduced = null)
    {
        if (dependencyBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(dependencyBudget));
        console.WriteLine("══════════ PRTG 環境探測（Probe） ══════════");
        console.WriteLine("唯讀呼叫 PRTG API，產出環境結構統計供後續分析層設計參考。");
        console.WriteLine();

        var allOk = true;

        // 步驟 1：版本與連線
        string? prtgVersion = null;
        allOk &= await StepAsync(console, 1, "版本與連線", async () =>
        {
            var json = await client.GetJsonAsync("/api/status.json?id=0", ct);
            var version = ExtractVersion(json);
            if (!string.IsNullOrWhiteSpace(version))
            {
                prtgVersion = version;
                console.WriteLine($"     PRTG 版本：{version}");
            }
            else
            {
                console.WriteLine("     無法判讀版本");
            }
        }, ct);

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
        }, ct);

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
            var senTableJson = await client.GetJsonAsync("/api/table.json?content=sensors&columns=objid,device,sensor,type,tags,unit,lastvalue,parentid,status&count=50000", ct);
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
                long? objid = null;
                if (long.TryParse(GetStringProperty(el, "objid"), out var oid))
                {
                    objid = oid;
                }
                return new SensorTypeSample(type, unit, parentId, objid, GetStringProperty(el, "status"));
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
                // 只看內建表：探測常在設定補充對照表之前執行
                var builtInCategory = PrtgSensorTypeCategoryMap.Map.TryGetValue(g.Type, out var cat) ? cat : "未分類";
                console.WriteLine($"       {g.Type} | {g.Count} | {pct:F1}% | unit 樣本：{unitsStr} | 內建分類：{builtInCategory}");
            }
            console.WriteLine("     未分類的 type 可在 PRTG 維護頁『sensor type 分類補充對照』指定。");

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
        }, ct);

        if (!allOk)
        {
            console.WriteLine();
            console.WriteLine("══════════ 探測失敗 ══════════");
            return false;
        }

        // 步驟 4：相依性（dependency）使用程度
        allOk &= await StepAsync(console, 4, "相依性（Dependency）使用程度", async () =>
        {
            // 相依性只供環境概況。小批查詢逐批報進度；即使 PRTG 長時間不回應，
            // 時間／頁數上限也讓後面的資料能力探測能繼續。
            const int pageSize = 100;
            const int maxPages = 10;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var total = 0;
            var withDep = 0;
            var incomplete = false;
            var timedOut = false;
            var seenSensorIds = new HashSet<long>();
            console.WriteLine($"     每批 {pageSize} 顆，最多 {maxPages} 批／{dependencyBudget.TotalSeconds:0.#} 秒；超出會標示部分樣本。");
            for (var page = 0; page < maxPages && (sensorCount <= 0 || total < sensorCount); page++)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = dependencyBudget - watch.Elapsed;
                if (remaining <= TimeSpan.Zero) { timedOut = true; break; }
                var requestBudget = remaining < TimeSpan.FromSeconds(8) ? remaining : TimeSpan.FromSeconds(8);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(requestBudget);
                string depJson;
                try
                {
                    depJson = await client.GetJsonAsync(
                        $"/api/table.json?content=sensors&columns=objid,dependency&count={pageSize}&start={page * pageSize}",
                        budget.Token).WaitAsync(requestBudget.Add(TimeSpan.FromSeconds(1)), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                                           (budget.IsCancellationRequested || ex is TimeoutException) &&
                                           ex is OperationCanceledException or PrtgClientException or TimeoutException)
                {
                    budget.Cancel();
                    timedOut = true;
                    break;
                }
                catch (PrtgClientException ex) when (ex.Message.Contains("400") || ex.Message.Contains("dependency", StringComparison.OrdinalIgnoreCase))
                {
                    console.WriteLine("     此 PRTG 版本不支援 dependency 欄位查詢，略過此步驟");
                    return;
                }

                // 沒設 dependency 的 sensor 這一欄是缺的：保留在分母中。
                var parsedDeps = ParseTable(depJson, "sensors", el => new PrtgDependencySample(
                    long.TryParse(GetStringProperty(el, "objid"), out var id) ? id : null,
                    GetStringProperty(el, "dependency") ?? string.Empty));
                if (parsedDeps.CorruptedCount > 0)
                    console.WriteLine($"     ⚠ 第 {page + 1} 批有 {parsedDeps.CorruptedCount} 筆無法解析");
                if (parsedDeps.Rows.Count == 0 && parsedDeps.CorruptedCount >= 1)
                {
                    console.WriteLine($"     ⚠ 回應無法解析：長度 {System.Text.Encoding.UTF8.GetByteCount(depJson)} bytes，開頭：{Head200(depJson)}");
                    incomplete = true;
                    break;
                }
                var duplicateIds = 0;
                foreach (var row in parsedDeps.Rows)
                {
                    if (row.SensorObjid is long id && !seenSensorIds.Add(id))
                    {
                        duplicateIds++;
                        continue;
                    }
                    if (row.SensorObjid == null) incomplete = true;
                    total++;
                    if (HasDependency(row.Dependency)) withDep++;
                }
                console.WriteLine($"     相依性進度：已查 {page + 1} 批、{total}/{sensorCount} 顆。");
                if (parsedDeps.CorruptedCount > 0) incomplete = true;
                if (duplicateIds > 0)
                {
                    console.WriteLine($"     ⚠ 第 {page + 1} 批與前批有 {duplicateIds} 筆重複 objid，分頁可能被忽略；停止相依性取樣。");
                    incomplete = true;
                    break;
                }
                if (parsedDeps.Rows.Count < pageSize) break;
            }

            if (timedOut)
                console.WriteLine("     ⚠ 相依性樣本查詢逾時，結果標為未驗證；繼續後續探測。");
            if (sensorCount > 0 && total < sensorCount)
            {
                if (total == pageSize * maxPages && !incomplete && !timedOut)
                    console.WriteLine($"     前 {total} 顆樣本／全站 {sensorCount} 顆；下列比例不代表全站。");
                else
                    console.WriteLine($"     ⚠ 警告：Sensor 總數為 {sensorCount} 筆，本次查詢僅取樣到 {total} 筆，下列比例僅供參考");
            }
            var pct = total > 0 ? (withDep * 100.0 / total) : 0.0;
            console.WriteLine($"     有設定相依性的 Sensor 數：{withDep} / {total}（佔比 {pct:F1}%）{(total < sensorCount || incomplete || timedOut ? "；部分樣本／未驗證" : "")}");
            console.WriteLine("     （註：PRTG 預設每個 sensor 相依於父物件，此比例含預設值，不代表人工維護的相依拓撲）");
        }, ct);

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
            // 與其他單發大 count 同一道截斷偵測：只取樣到 1000 筆時「群組總數」仍會印 treesize，看不出缺口
            var fetchedGrp = parsedGroups.Rows.Count + parsedGroups.CorruptedCount;
            if (totalGrp > fetchedGrp)
            {
                console.WriteLine($"     ⚠ 警告：群組總數為 {totalGrp}，本次查詢僅取樣到 {fetchedGrp} 筆，下列名稱不完整");
            }
            var validGroupNames = parsedGroups.Rows.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            var top20 = validGroupNames.Take(20).ToList();
            console.WriteLine($"     群組總數：{totalGrp}，前 20 個群組名稱：{string.Join("，", top20)}");
        }, ct);

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
        }, ct);

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
        }, ct);

        var sampleDeviceIds = PickSampleDevices(sensorSamples);

        // 步驟 8：分頁語意診斷（不影響探測成敗）——回答「這台 PRTG 的 table.json 遵不遵守 start 位移」。
        // 結構同步與資源守門的分頁迴圈都以「遵守 start」為前提；忽略 start 或超出範圍時夾回某一頁的
        // 環境會讓分頁永遠收不斂。這一步每個 content 只做四次 count=5 的小查詢（三次位移＋一次排序對照），
        // messages 以單一裝置查詢（因為 id=0 在大型環境會逾時），結果純供人工判讀，任何一筆失敗
        // 都只印出原因、不把整趟探測算失敗。
        ct.ThrowIfCancellationRequested();
        console.WriteLine("[8] 分頁語意診斷（table.json 的 start 位移是否被遵守）");
        await DiagnosePagingAsync(client, console, "devices", "", ct);
        await DiagnosePagingAsync(client, console, "sensors", "", ct);
        if (sampleDeviceIds.Count > 0)
        {
            await DiagnosePagingAsync(client, console, "messages", $"&id={sampleDeviceIds[0]}&filter_drel=7days", ct);
        }
        else
        {
            console.WriteLine("     messages：略過（沒有可用的裝置樣本，無法以單一裝置診斷）");
        }

        // 步驟 9：相容性證據探測與逐物件查詢（不影響探測成敗）——
        // 對代表性感測器執行有限度相容性探測（最多 3 顆，各發一次快照、頻道與昨天歷史數值，產出去識別 JSON 證據區塊），
        // 並執行逐物件查詢驗證（9d-5）。
        ct.ThrowIfCancellationRequested();
        console.WriteLine("[9] 相容性證據探測與逐物件查詢驗證");
        console.WriteLine("     本步驟對最多 3 顆代表感測器（CPU／Memory／Disk 各 1）依序查詢快照、頻道與昨日歷史數值，產出去識別相容性證據，並驗證逐物件查詢");

        try
        {
            if (evidenceContext != null && string.IsNullOrWhiteSpace(evidenceContext.SourcePrtgVersion) && !string.IsNullOrWhiteSpace(prtgVersion))
            {
                evidenceContext = new PrtgProbeEvidenceContext
                {
                    SchemaVersion = evidenceContext.SchemaVersion,
                    BuildVersion = evidenceContext.BuildVersion,
                    SourcePrtgVersion = prtgVersion,
                    GeneratedAtUtc = evidenceContext.GeneratedAtUtc,
                    HostUtcOffset = evidenceContext.HostUtcOffset,
                    SourceFingerprint = evidenceContext.SourceFingerprint,
                    SettingsRevision = evidenceContext.SettingsRevision,
                    SourceTimezone = evidenceContext.SourceTimezone,
                    SourceLocale = evidenceContext.SourceLocale,
                    StorageProvider = evidenceContext.StorageProvider,
                    EfCoreProvider = evidenceContext.EfCoreProvider,
                    RetentionDays = evidenceContext.RetentionDays,
                    StorageEnvironment = evidenceContext.StorageEnvironment,
                    ScopeSummary = evidenceContext.ScopeSummary,
                    ReadinessSummary = evidenceContext.ReadinessSummary
                };
            }
            await PrtgCompatibilityProbe.ExecuteAsync(client, console, sensorSamples, evidenceContext, ct, onEvidenceJsonProduced);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     相容性證據探測無法完成（{ex.Message}）");
        }

        try
        {
            await MeasureObjectQueriesAsync(client, console, sampleDeviceIds, sensorSamples, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     9d-5：無法量測（{ex.Message}）");
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
    /// 對單一 content 發三次 count=5 查詢（start=0、start=5、start=999999）比較各頁 objid 集合，
    /// 再發一次 start=0&amp;sortby=objid 當排序對照，
    /// 歸納這台 PRTG 對 start 位移的處理方式。判讀結果與分頁迴圈的影響一起印出。
    /// </summary>
    private static async Task DiagnosePagingAsync(PrtgClient client, IRunConsole console, string content, string extraQuery, CancellationToken ct)
    {
        const int probeCount = 5;
        const int farOffset = 999999;
        var isMessages = string.Equals(content, "messages", StringComparison.OrdinalIgnoreCase);
        try
        {
            var rows0 = await ReadPagingRowsAsync(client, content, extraQuery, 0, probeCount, isMessages, ct);
            var rows1 = await ReadPagingRowsAsync(client, content, extraQuery, probeCount, probeCount, isMessages, ct);
            var rowsFar = await ReadPagingRowsAsync(client, content, extraQuery, farOffset, probeCount, isMessages, ct);
            var page0 = rows0.Select(r => r.Objid).ToList();
            var page1 = rows1.Select(r => r.Objid).ToList();
            var pageFar = rowsFar.Select(r => r.Objid).ToList();

            string Show(List<long> ids) => ids.Count == 0 ? "（空）" : string.Join(",", ids);
            console.WriteLine($"     {content}：start=0 → [{Show(page0)}]；start={probeCount} → [{Show(page1)}]；start={farOffset} → [{Show(pageFar)}]");

            if (page0.Count == 0)
            {
                console.WriteLine($"     {content}：第一頁就沒有資料，無法判定");
                return;
            }

            // 排序穩定性：分頁的前提除了「遵守 start」，還有「兩次查詢之間順序一致」。
            // 順序不穩定時同一筆會重複出現、另一筆從沒被讀到，而且完全靜默——
            // 分頁的去重擋得住重複，擋不住漏列。sortby=objid 是讓順序固定的手段，
            // 這裡實測它在這台 PRTG 上有沒有效（不支援時 PRTG 會忽略參數而非報錯）。
            var sortedRows = await ReadPagingRowsAsync(client, content, extraQuery + "&sortby=objid", 0, probeCount, isMessages, ct);
            var sorted = sortedRows.Select(r => r.Objid).ToList();
            var naturalAscending = IsAscending(page0);
            var sortedAscending = IsAscending(sorted);
            // messages 的列鍵含時間，objid 不是它的排序依據：用 objid 判順序會把「依時間遞減」誤判成順序不穩
            var messagesMonotonic = false;
            var datetimeUndeterminable = false;
            if (isMessages)
            {
                var (dtText, dtMonotonic) = DescribeDatetimeMonotonicity(rows0);
                messagesMonotonic = dtMonotonic;
                datetimeUndeterminable = dtText.StartsWith("無法判定", StringComparison.Ordinal);
                console.WriteLine($"     messages：頁內 datetime 單調＝{dtText}");
            }
            console.WriteLine($"     {content}：頁內 objid 遞增＝{(naturalAscending ? "是" : "否")}；" +
                              $"帶 sortby=objid → [{Show(sorted)}]，遞增＝{(sortedAscending ? "是" : "否")}");
            if (isMessages)
            {
                if (messagesMonotonic)
                    console.WriteLine($"     messages：✓ 依時間排序、順序穩定，分頁可行（列鍵含時間）；sortby=objid {(sortedAscending ? "有效" : "無效")}，不影響結論");
                else if (datetimeUndeterminable)
                    // 解析不了時間不代表順序壞了，只是這台 PRTG 的日期格式本機讀不懂——不能印成漏列警告
                    console.WriteLine("     messages：？ datetime 欄位本機解析失敗，無法判定順序；請對照上一行的樣本與 PRTG 的日期格式設定");
                else
                    console.WriteLine("     messages：⚠ 頁內順序不依時間也不依 objid，分頁可能漏列");
            }
            else if (!naturalAscending && sortedAscending)
                console.WriteLine($"     {content}：✓ sortby=objid 有效（預設順序不穩定，分頁必須帶它才不會漏列）");
            else if (!naturalAscending && !sortedAscending)
                console.WriteLine($"     {content}：✗ sortby=objid 無效，且預設順序非遞增——分頁可能漏列，只能改用單次大 count");
            else if (naturalAscending && !sortedAscending)
                console.WriteLine($"     {content}：⚠ 預設已遞增，但帶 sortby=objid 後反而不是——不要帶這個參數");
            else
                console.WriteLine($"     {content}：✓ 預設順序已遞增，sortby=objid 不改變結果");

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

    /// <summary>
    /// 從步驟 3 的感測器樣本中挑出最多 3 台樣本裝置供步驟 8 messages 診斷與步驟 9d-5 逐物件查詢驗證使用。
    /// 優先挑選底下有非 Up（或狀態缺失）感測器的裝置，其次按 ParentId 遞增排序。
    /// </summary>
    private static List<long> PickSampleDevices(List<SensorTypeSample> sensorSamples)
    {
        if (sensorSamples == null || sensorSamples.Count == 0)
        {
            return new List<long>();
        }

        return PickTopSampleDevices(sensorSamples
            .Where(s => s.ParentId.HasValue)
            .Select(s => ((long)s.ParentId!.Value, s.Status)));
    }

    /// <summary>
    /// 樣本裝置的排序規則（全專案唯一一份）：把感測器依所屬裝置分組，
    /// 底下有「非 Up」（狀態缺失或不以 Up 開頭）感測器的裝置優先，其次 objid 由小到大，最多取 3 台。
    /// 站台對照（<see cref="PrtgProbeSiteCheck"/>）與探測本身共用這一份，避免兩邊挑到不同的樣本。
    /// </summary>
    internal static List<long> PickTopSampleDevices(IEnumerable<(long DeviceObjid, string? Status)> sensors)
    {
        static bool IsNotUp(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return true;
            return !status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);
        }

        return sensors
            .GroupBy(s => s.DeviceObjid)
            .OrderByDescending(g => g.Any(s => IsNotUp(s.Status)))
            .ThenBy(g => g.Key)
            .Take(3)
            .Select(g => g.Key)
            .ToList();
    }

    /// <summary>
    /// 「以裝置 id 查 messages 回了什麼」的判定（全專案唯一一份）：
    /// 回傳空集合＝無資料；含下層感測器 objid＝可用；全部都是裝置自身＝只有裝置訊息；
    /// 其餘＝回了不屬於該裝置的 objid。站台對照與 9d-5 共用。
    /// </summary>
    internal static string JudgeDeviceMessagesScope(long deviceObjid, IReadOnlySet<long> childSensorObjids, IReadOnlyList<long> returnedObjids)
    {
        if (returnedObjids.Count == 0)
        {
            return "無資料，無法判定";
        }
        if (returnedObjids.Any(id => childSensorObjids.Contains(id)))
        {
            return "✓ 含下層感測器訊息";
        }
        if (returnedObjids.All(id => id == deviceObjid))
        {
            return "✗ 只有裝置自身——狀態變更取數需改為逐感測器";
        }
        return "⚠ 回傳的 objid 不屬於該裝置（id 參數可能未生效）";
    }

    /// <summary>
    /// 9d-5：逐物件查詢驗證——驗證以單一裝置查詢 sensors 與 messages 是否生效、
    /// messages 是否包含下層感測器狀態變更，以及 filter_objid 分批取值是否可用。
    /// </summary>
    private static async Task MeasureObjectQueriesAsync(
        PrtgClient client,
        IRunConsole console,
        List<long> sampleDeviceIds,
        List<SensorTypeSample> sensorSamples,
        CancellationToken ct)
    {
        if (sampleDeviceIds == null || sampleDeviceIds.Count == 0)
        {
            console.WriteLine("     9d-5：略過（沒有可用的裝置樣本）");
            return;
        }

        var bVerdicts = new List<string>();
        var bElapsedList = new List<double>();
        List<long>? firstDeviceSensors = null;
        var firstDeviceId = sampleDeviceIds[0];

        foreach (var deviceId in sampleDeviceIds)
        {
            try
            {
                // (a) 查 sensors
                var swA = System.Diagnostics.Stopwatch.StartNew();
                var jsonA = await client.GetJsonAsync($"/api/table.json?content=sensors&columns=objid&count=5000&id={deviceId}", ct);
                swA.Stop();

                var parsedA = ParseTable(jsonA, "sensors", el => long.TryParse(GetStringProperty(el, "objid"), out var id) ? new PagingRow(id, null) : null);
                var rowsA = parsedA.Rows.Select(r => r.Objid).ToList();
                var expectedSet = sensorSamples
                    .Where(s => s.ParentId.HasValue && (long)s.ParentId.Value == deviceId && s.Objid.HasValue)
                    .Select(s => s.Objid!.Value)
                    .ToHashSet();
                var expectedCount = sensorSamples.Count(s => s.ParentId.HasValue && (long)s.ParentId.Value == deviceId);

                string verdictA;
                if (rowsA.Count == 0)
                {
                    verdictA = "無法判定（沒有回傳任何感測器）";
                }
                else if (rowsA.Any(id => !expectedSet.Contains(id)))
                {
                    verdictA = "✗ 回傳含其他裝置的感測器（id 參數未生效）";
                }
                else
                {
                    verdictA = "✓ 只回該裝置的感測器";
                }

                console.WriteLine($"     9d-5：裝置 objid={deviceId} 逐裝置取感測器 耗時 {swA.Elapsed.TotalMilliseconds:F0} ms、回傳 {rowsA.Count} 顆（全站清單中該裝置 {expectedCount} 顆）→ {verdictA}");

                if (deviceId == firstDeviceId && rowsA.Count > 0)
                {
                    firstDeviceSensors = rowsA;
                }

                // (b) 查 messages
                var swB = System.Diagnostics.Stopwatch.StartNew();
                var jsonB = await client.GetJsonAsync($"/api/table.json?content=messages&columns=objid,datetime&count=50&id={deviceId}&filter_drel=7days", ct);
                swB.Stop();

                var parsedB = ParseTable(jsonB, "messages", el => long.TryParse(GetStringProperty(el, "objid"), out var id) ? new PagingRow(id, GetStringProperty(el, "datetime")) : null);
                var rowsB = parsedB.Rows.Select(r => r.Objid).ToList();
                var subSet = rowsA.ToHashSet();
                var nB = rowsB.Count;
                var kB = rowsB.Distinct().Count();
                var treesizeText = parsedB.TotalTreesize?.ToString() ?? "無";

                var verdictB = JudgeDeviceMessagesScope(deviceId, subSet, rowsB);

                console.WriteLine($"     9d-5：裝置 objid={deviceId} 逐裝置取狀態變更 耗時 {swB.Elapsed.TotalMilliseconds:F0} ms、回傳 {nB} 筆、treesize {treesizeText}、不重複 objid {kB} 個 → {verdictB}");

                bVerdicts.Add(verdictB);
                bElapsedList.Add(swB.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                console.WriteLine($"     9d-5：裝置 objid={deviceId} 無法量測（{ex.Message}）");
            }
        }

        // (c) 結論行
        if (bVerdicts.Any(v => v.StartsWith("✓", StringComparison.Ordinal)))
        {
            var avgMs = bElapsedList.Count > 0 ? bElapsedList.Average() : 0;
            console.WriteLine($"     9d-5：結論 ✓ 逐裝置查詢狀態變更可用（平均每台 {avgMs:F0} ms）");
        }
        else if (bVerdicts.Any(v => v.StartsWith("✗", StringComparison.Ordinal)))
        {
            var avgMs = bElapsedList.Count > 0 ? bElapsedList.Average() : 0;
            console.WriteLine($"     9d-5：結論 ✗ 逐裝置查詢只回裝置自身訊息，狀態變更取數需改為逐感測器（平均每台 {avgMs:F0} ms）");
        }
        else
        {
            console.WriteLine("     9d-5：結論 無法判定（樣本裝置近 7 天都沒有狀態變更）");
        }

        // (d) & (e)
        if (firstDeviceSensors == null || firstDeviceSensors.Count == 0)
        {
            console.WriteLine("     9d-5：略過逐感測器量測（沒有可用的感測器）");
            return;
        }

        var firstSensorId = firstDeviceSensors[0];

        // (d) 查逐感測器 messages
        try
        {
            var swD = System.Diagnostics.Stopwatch.StartNew();
            var jsonD = await client.GetJsonAsync($"/api/table.json?content=messages&columns=objid,datetime&count=50&id={firstSensorId}&filter_drel=7days", ct);
            swD.Stop();

            var parsedD = ParseTable(jsonD, "messages", el => long.TryParse(GetStringProperty(el, "objid"), out var id) ? new PagingRow(id, GetStringProperty(el, "datetime")) : null);
            console.WriteLine($"     9d-5：感測器 objid={firstSensorId} 逐感測器取狀態變更 耗時 {swD.Elapsed.TotalMilliseconds:F0} ms、回傳 {parsedD.Rows.Count} 筆");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     9d-5：感測器 objid={firstSensorId} 逐感測器取狀態變更無法量測（{ex.Message}）");
        }

        // (e) 查 filter_objid 分批取值
        try
        {
            var requestedBatch = firstDeviceSensors.Take(50).ToList();
            var requestedSet = requestedBatch.ToHashSet();
            var r = requestedBatch.Count;
            var filterQuery = PrtgResourceGuardProbe.BuildObjidFilter(requestedBatch);
            var url = $"/api/table.json?content=sensors&columns=objid,lastvalue_raw{filterQuery}";

            var swE = System.Diagnostics.Stopwatch.StartNew();
            var jsonE = await client.GetJsonAsync(url, ct);
            swE.Stop();

            var parsedE = ParseTable(jsonE, "sensors", el => long.TryParse(GetStringProperty(el, "objid"), out var id) ? new PagingRow(id, null) : null);
            var rowsE = parsedE.Rows.Select(r => r.Objid).ToList();
            var n = rowsE.Count;

            string verdictE;
            if (rowsE.Any(id => !requestedSet.Contains(id)))
            {
                verdictE = "⚠ 回傳了未要求的感測器（filter_objid 未生效）";
            }
            else if (n == r)
            {
                verdictE = "✓ 分批取值可用";
            }
            else
            {
                verdictE = $"⚠ 少回 {r - n} 顆";
            }

            console.WriteLine($"     9d-5：filter_objid 分批取值 要求 {r} 顆、回傳 {n} 顆、耗時 {swE.Elapsed.TotalMilliseconds:F0} ms → {verdictE}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     9d-5：filter_objid 分批取值無法量測（{ex.Message}）");
        }
    }

    /// <summary>
    /// 少於兩筆時視為遞增（無從判定，不要因為資料太少就報警）。
    /// </summary>
    private static bool IsAscending(List<long> ids)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (ids[i] <= ids[i - 1]) return false;
        }
        return true;
    }

    /// <summary>分頁診斷讀到的一列：objid 與（messages 才有的）datetime 原始字串。</summary>
    private sealed record PagingRow(long Objid, string? DatetimeText);

    /// <summary>
    /// 讀一頁 objid；messages 多帶 datetime（它的順序依據是時間）。
    /// devices／sensors 不帶 datetime，避免沒有這個欄位的 content 多要一個未知欄位。
    /// </summary>
    private static async Task<List<PagingRow>> ReadPagingRowsAsync(PrtgClient client, string content, string extraQuery, int start, int count, bool withDatetime, CancellationToken ct)
    {
        var columns = withDatetime ? "objid,datetime" : "objid";
        var json = await client.GetJsonAsync($"/api/table.json?content={content}&columns={columns}&count={count}&start={start}{extraQuery}", ct);
        var parsed = ParseTable(json, content, el => long.TryParse(GetStringProperty(el, "objid"), out var id)
            ? new PagingRow(id, GetStringProperty(el, "datetime"))
            : null);
        return parsed.Rows;
    }

    /// <summary>
    /// 頁內 datetime 是否單調（非遞增或非遞減）。有任一筆解析失敗就不下判斷，
    /// 不能拿部分可解析的子集去證明「順序沒問題」。
    /// </summary>
    private static (string Text, bool Monotonic) DescribeDatetimeMonotonicity(List<PagingRow> rows)
    {
        var times = new List<DateTime>();
        var failed = 0;
        foreach (var r in rows)
        {
            if (DateTime.TryParse(r.DatetimeText, out var dt)) times.Add(dt);
            else failed++;
        }

        if (failed > 0) return ($"無法判定（datetime 解析失敗 {failed} 筆）", false);

        var nonIncreasing = true;
        var nonDecreasing = true;
        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] > times[i - 1]) nonIncreasing = false;
            if (times[i] < times[i - 1]) nonDecreasing = false;
        }

        if (nonIncreasing) return ("是（遞減）", true);
        if (nonDecreasing) return ("是（遞增）", true);
        return ("否（不單調）", false);
    }

    /// <summary>取前 200 字並把換行與 Tab 換成空白，讓診斷行維持單行。</summary>
    private static string Head200(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var head = text.Length > 200 ? text[..200] : text;
        return head.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    private static async Task<bool> StepAsync(IRunConsole console, int index, string title, Func<Task> action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        console.WriteLine($"[{index}] {title}");
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     ✗ 失敗：{ex.Message}");
            return false;
        }
    }

    public sealed record SensorTypeSample(string Type, string? Unit, int? ParentId, long? Objid = null, string? Status = null);

    private sealed record PrtgDependencySample(long? SensorObjid, string Dependency);

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
