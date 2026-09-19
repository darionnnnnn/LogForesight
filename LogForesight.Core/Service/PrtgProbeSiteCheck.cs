using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// 環境探測的「站台對照」段：以本機鏡像與主機對應對照 PRTG，回答管理者升級前的三個問題——
/// 取數範圍有多大、下次結構同步會清掉多少感測器、範圍內的裝置逐台查詢成不成立、大概要跑多久。
///
/// 唯讀，不寫任何資料表；也不檢查 PrtgEnabled（探測本來就要能在啟用前跑），
/// 所以鏡像與主機對應可能是空的，那是正常情況而不是錯誤。
/// 站台對照不影響探測成敗：除了取消以外任何例外都在內部吞掉並印一行原因。
/// </summary>
public static class PrtgProbeSiteCheck
{
    public static async Task RunAsync(
        PrtgClient client, IRunConsole console,
        EfPrtgStore store, IHostStore hostStore,
        SystemSettings settings, IReadOnlyList<Sentinel> sentinels,
        CancellationToken ct = default)
    {
        console.WriteLine();
        console.WriteLine("══════════ 站台對照 ══════════");
        console.WriteLine("以本機鏡像與主機對應對照 PRTG：取數範圍有多大、下次同步會清掉什麼、範圍內的裝置逐台查詢成不成立。最多發 6 次 table.json。");

        // ── [S1] 取數範圍試算（純本機，不打 PRTG）────────────────────────────
        console.WriteLine("[S1] 取數範圍試算");

        var devices = new List<PrtgDeviceRow>();
        var sensors = new List<PrtgSensorRow>();
        PrtgScopeResult? scope = null;
        string? skipReason = null;

        try
        {
            devices = store.GetAllDevices();
            sensors = store.GetAllSensors();

            if (devices.Count == 0)
            {
                console.WriteLine("     鏡像尚未同步，無法試算；請先執行「同步結構與對應」。");
                skipReason = "鏡像尚未同步";
            }
            else
            {
                // 靜音 console：守門自動偵測的警告是給同步流程看的，混進探測輸出會蓋掉這一段的結論
                scope = PrtgScopeDevices.Compute(
                    store, hostStore, new PrtgMirrorGuardSource(store), settings, sentinels,
                    new SilentConsole(), new PrtgAddressResolver());

                var inScope = scope.DeviceObjids;
                console.WriteLine($"     取數範圍：{inScope.Count} 台裝置（對應 {scope.Mapped}、衝突 {scope.Conflict}、人工 {scope.Manual}、守門 {scope.Guard}）");

                // 範圍外的感測器＝下次結構同步成功後會被過期清除的那一批
                var outOfScope = sensors.Count(s => !inScope.Contains(s.DeviceObjid));
                var mirrorLine = $"     鏡像現況：裝置 {devices.Count} 台、感測器 {sensors.Count} 顆，其中範圍外 {outOfScope} 顆";
                if (outOfScope > 0) mirrorLine += "——下次結構同步成功後會清除";
                console.WriteLine(mirrorLine);

                if (inScope.Count == 0)
                {
                    console.WriteLine("     ⚠ 取數範圍是空的：尚未對應到任何主機，感測器與狀態變更同步會整段略過。請到 PRTG 維護頁完成主機對應。");
                    skipReason = "取數範圍是空的";
                }
                else if (scope.Mapped == 0)
                {
                    console.WriteLine("     ⚠ 範圍內沒有任何對應成功的裝置（只有守門或人工項）：下次同步不會清除感測器鏡像。");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            console.WriteLine($"     取數範圍無法試算（{ex.Message}）");
            skipReason = "取數範圍試算失敗";
        }

        // ── [S2] 範圍內裝置逐台查詢實測（打 PRTG，唯讀）──────────────────────
        console.WriteLine("[S2] 範圍內裝置逐台查詢實測");

        if (skipReason != null || scope == null)
        {
            console.WriteLine($"     略過（{skipReason ?? "取數範圍試算失敗"}）");
            return;
        }

        var sampleDevices = PickSampleDevices(scope.DeviceObjids, sensors);
        if (sampleDevices.Count == 0)
        {
            console.WriteLine("     略過（範圍內沒有可量測的裝置）");
            return;
        }

        var deviceNames = devices.ToDictionary(d => d.Objid, d => d.Name);
        var verdicts = new List<string>();
        var sensorStageMs = new List<double>();
        var changeStageMs = new List<double>();

        foreach (var deviceObjid in sampleDevices)
        {
            try
            {
                // (a) 逐裝置取感測器
                var swA = System.Diagnostics.Stopwatch.StartNew();
                var jsonA = await client.GetJsonAsync(
                    $"/api/table.json?content=sensors&columns=objid&count=5000&id={deviceObjid}", ct);
                swA.Stop();
                var returnedSensors = ParseObjids(jsonA, "sensors");
                sensorStageMs.Add(swA.Elapsed.TotalMilliseconds);

                var mirrorCount = sensors.Count(s => s.DeviceObjid == deviceObjid);
                var nameText = deviceNames.TryGetValue(deviceObjid, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? $"（{name}）"
                    : string.Empty;
                var diffText = returnedSensors.Count != mirrorCount ? "——與鏡像不同，下次同步後更新" : string.Empty;
                console.WriteLine($"     裝置 objid={deviceObjid}{nameText}逐裝置取感測器 耗時 {swA.Elapsed.TotalMilliseconds:F0} ms、回傳 {returnedSensors.Count} 顆（鏡像 {mirrorCount} 顆）{diffText}");

                // (b) 逐裝置取狀態變更
                var swB = System.Diagnostics.Stopwatch.StartNew();
                var jsonB = await client.GetJsonAsync(
                    $"/api/table.json?content=messages&columns=objid,datetime&count=50&id={deviceObjid}&filter_drel=7days", ct);
                swB.Stop();
                var returnedMessages = ParseObjids(jsonB, "messages");
                changeStageMs.Add(swB.Elapsed.TotalMilliseconds);

                var verdict = PrtgProbeRunner.JudgeDeviceMessagesScope(deviceObjid, returnedSensors.ToHashSet(), returnedMessages);
                verdicts.Add(verdict);
                console.WriteLine($"     裝置 objid={deviceObjid} 逐裝置取狀態變更 耗時 {swB.Elapsed.TotalMilliseconds:F0} ms、回傳 {returnedMessages.Count} 筆 → {verdict}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                console.WriteLine($"     裝置 objid={deviceObjid} 無法量測（{ex.Message}）");
            }
        }

        if (verdicts.Any(v => v.StartsWith("✓", StringComparison.Ordinal)))
            console.WriteLine("     結論 ✓ 範圍內裝置逐台查詢狀態變更可用");
        else if (verdicts.Any(v => v.StartsWith("✗", StringComparison.Ordinal)))
            console.WriteLine("     結論 ✗ 逐裝置查詢只回裝置自身訊息，狀態變更取數需改為逐感測器");
        else
            console.WriteLine("     結論 無法判定（樣本裝置近 7 天都沒有狀態變更）");

        if (sensorStageMs.Count > 0 && changeStageMs.Count > 0)
        {
            var concurrency = Math.Max(settings.PrtgFetchConcurrency, 1);
            var total = scope.DeviceObjids.Count;
            var sensorSeconds = EstimateStageSeconds(sensorStageMs.Average(), total, concurrency);
            var changeSeconds = EstimateStageSeconds(changeStageMs.Average(), total, concurrency);
            console.WriteLine($"     估算：範圍 {total} 台、併發 {concurrency}——感測器階段約 {sensorSeconds} 秒、狀態變更階段約 {changeSeconds} 秒（以 7 天級距量測；回望 30 天的資料量更大，實際會更久）");
        }
    }

    /// <summary>
    /// 階段耗時估算（純函式）：平均每台毫秒 × 台數 ÷ 併發 ÷ 1000，無條件進位到整數秒。
    /// 抽出來是為了讓測試直接驗算式，不必依賴實際計時。
    /// </summary>
    internal static int EstimateStageSeconds(double avgMsPerDevice, int deviceCount, int concurrency)
    {
        if (avgMsPerDevice <= 0 || deviceCount <= 0) return 0;
        var effective = Math.Max(concurrency, 1);
        return (int)Math.Ceiling(avgMsPerDevice * deviceCount / effective / 1000.0);
    }

    /// <summary>
    /// 從取數範圍內挑最多 3 台樣本裝置。排序規則不在這裡——一律走
    /// <see cref="PrtgProbeRunner.PickTopSampleDevices"/>（底下有非 Up 感測器者優先、其次 objid 小者優先）。
    /// 範圍內但鏡像沒有任何感測器的裝置，以一筆狀態 "Up" 的虛擬項參加排序：
    /// 「沒有感測器」等同「沒有非 Up 感測器」，這樣它仍是候選，但不會擠掉有異常的裝置。
    /// </summary>
    private static List<long> PickSampleDevices(IReadOnlySet<long> scopeDeviceObjids, List<PrtgSensorRow> sensors)
    {
        var entries = sensors
            .Where(s => scopeDeviceObjids.Contains(s.DeviceObjid))
            .Select(s => (s.DeviceObjid, s.Status))
            .ToList();

        var covered = entries.Select(e => e.DeviceObjid).ToHashSet();
        foreach (var objid in scopeDeviceObjids)
        {
            if (!covered.Contains(objid)) entries.Add((objid, "Up"));
        }

        return PrtgProbeRunner.PickTopSampleDevices(entries);
    }

    /// <summary>
    /// 取 table.json 回應中指定陣列的 objid 清單；解析不了的整份回空清單、單列壞掉就跳過那一列。
    /// 這一段是唯讀診斷，不需要 treesize 與損壞筆數。
    /// </summary>
    private static List<long> ParseObjids(string json, string contentName)
    {
        var result = new List<long>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            if (!root.TryGetProperty(contentName, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("objid", out var prop)) continue;
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var num))
                    result.Add(num);
                else if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
                    result.Add(parsed);
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }

    /// <summary>吞掉一切輸出的 console：只給取數範圍試算用，避免守門偵測的警告混進探測輸出。</summary>
    private sealed class SilentConsole : IRunConsole
    {
        public void WriteLine(string message = "") { }
    }
}
