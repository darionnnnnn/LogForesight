using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>PRTG 每日擷取結果摘要。</summary>
public sealed record PrtgFetchResult(int Devices, int Sensors, int StateChanges, int Values, int Failures);

/// <summary>
/// PRTG 每日擷取服務：負責將 PRTG 的裝置結構、感測器結構、狀態變更（訊息）與 hourly 聚合數值
/// 擷取並寫入本機鏡像表（lf_prtg_*）。
/// 包含四個循序階段，各階段各自獨立容錯，任一階段失敗不中斷其餘階段。
/// </summary>
public sealed class PrtgFetchService
{
    private readonly PrtgClient _client;
    private readonly EfPrtgStore _store;
    private readonly IRunConsole _console;

    public PrtgFetchService(PrtgClient client, EfPrtgStore store, IRunConsole console)
    {
        _client = client;
        _store = store;
        _console = console;
    }

    /// <summary>
    /// 執行指定日期的 PRTG 每日擷取。
    /// </summary>
    /// <param name="day">目標日期（本地時間）</param>
    /// <param name="concurrency">hourly 數值抓取併發上限（1~3）</param>
    /// <param name="ct">取消語彙基元</param>
    public async Task<PrtgFetchResult> FetchDayAsync(DateTime day, int concurrency, CancellationToken ct)
    {
        var devicesCount = 0;
        var sensorsCount = 0;
        var stateChangesCount = 0;
        var valuesCount = 0;
        var failures = 0;
        var sensorTargets = new List<(long Objid, bool Paused)>();

        // 階段 1：device 結構全量同步
        try
        {
            _console.WriteLine("[階段 1/4] 開始同步 PRTG 裝置結構鏡像...");
            devicesCount = await FetchDevicesAsync(ct);
            _console.WriteLine($"[階段 1/4] 裝置結構同步完成，共寫入/更新 {devicesCount} 台裝置。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _console.WriteLine($"[階段 1/4] 裝置結構同步失敗：{ex.Message}");
        }

        // 階段 2：sensor 結構全量同步
        try
        {
            _console.WriteLine("[階段 2/4] 開始同步 PRTG 感測器結構鏡像...");
            (sensorsCount, sensorTargets) = await FetchSensorsAsync(ct);
            _console.WriteLine($"[階段 2/4] 感測器結構同步完成，共寫入/更新 {sensorsCount} 個感測器。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _console.WriteLine($"[階段 2/4] 感測器結構同步失敗：{ex.Message}");
        }

        // 階段 3：狀態變更（前一日增量）
        try
        {
            _console.WriteLine($"[階段 3/4] 開始同步 PRTG 狀態變更（{day:yyyy-MM-dd}）...");
            stateChangesCount = await FetchStateChangesAsync(day, ct);
            _console.WriteLine($"[階段 3/4] 狀態變更同步完成，共寫入 {stateChangesCount} 筆。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _console.WriteLine($"[階段 3/4] 狀態變更同步失敗：{ex.Message}");
        }

        // 階段 4：hourly 數值（前一日）
        try
        {
            var activeSensors = sensorTargets.Where(s => !s.Paused).ToList();
            _console.WriteLine($"[階段 4/4] 開始擷取 PRTG 每小時數值（{day:yyyy-MM-dd}，未暫停感測器：{activeSensors.Count} 個，併發：{Math.Max(concurrency, 1)}）...");
            valuesCount = await FetchValuesAsync(day, activeSensors, concurrency, ct);
            _console.WriteLine($"[階段 4/4] 每小時數值擷取完成，共寫入 {valuesCount} 筆數值。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            _console.WriteLine($"[階段 4/4] 每小時數值擷取失敗：{ex.Message}");
        }

        return new PrtgFetchResult(devicesCount, sensorsCount, stateChangesCount, valuesCount, failures);
    }

    /// <summary>階段 1：分頁抓取所有 devices 並寫入鏡像表</summary>
    private async Task<int> FetchDevicesAsync(CancellationToken ct)
    {
        var syncedAt = DateTime.Now;
        var totalWritten = 0;

        await FetchTablePagedAsync<PrtgDeviceRow>(
            content: "devices",
            columns: "objid,device,host,group,status,tags,paused,dependency",
            extraQuery: null,
            mapper: el =>
            {
                var objid = GetLongProperty(el, "objid");
                if (!objid.HasValue) return null;

                return new PrtgDeviceRow
                {
                    Objid = objid.Value,
                    Name = GetStringProperty(el, "device") ?? string.Empty,
                    GroupPath = GetStringProperty(el, "group") ?? string.Empty,
                    Ip = GetStringProperty(el, "host"),
                    Tags = GetStringProperty(el, "tags"),
                    Status = GetStringProperty(el, "status"),
                    DependencyObjid = ParseDependency(el),
                    Paused = ParsePaused(el),
                    SyncedAt = syncedAt,
                    CreatedAt = syncedAt
                };
            },
            onBatch: batch =>
            {
                totalWritten += _store.UpsertDevices(batch, syncedAt);
            },
            ct: ct);

        return totalWritten;
    }

    /// <summary>階段 2：分頁抓取所有 sensors 並寫入鏡像表，同時收集未暫停名單供階段 4 使用</summary>
    private async Task<(int TotalWritten, List<(long Objid, bool Paused)> SensorTargets)> FetchSensorsAsync(CancellationToken ct)
    {
        var syncedAt = DateTime.Now;
        var totalWritten = 0;
        var targets = new List<(long Objid, bool Paused)>();

        await FetchTablePagedAsync<PrtgSensorRow>(
            content: "sensors",
            columns: "objid,parentid,sensor,type,tags,unit,status,paused,dependency",
            extraQuery: null,
            mapper: el =>
            {
                var objid = GetLongProperty(el, "objid");
                if (!objid.HasValue) return null;

                var parentid = GetLongProperty(el, "parentid") ?? 0;
                var paused = ParsePaused(el);
                targets.Add((objid.Value, paused));

                return new PrtgSensorRow
                {
                    Objid = objid.Value,
                    DeviceObjid = parentid,
                    Name = GetStringProperty(el, "sensor") ?? string.Empty,
                    SensorType = GetStringProperty(el, "type") ?? string.Empty,
                    Tags = GetStringProperty(el, "tags"),
                    Unit = GetStringProperty(el, "unit"),
                    Status = GetStringProperty(el, "status"),
                    ThresholdsJson = null,
                    DependencyObjid = ParseDependency(el),
                    Paused = paused,
                    Category = null,
                    CategorySource = null,
                    SyncedAt = syncedAt,
                    CreatedAt = syncedAt
                };
            },
            onBatch: batch =>
            {
                totalWritten += _store.UpsertSensors(batch, syncedAt);
            },
            ct: ct);

        return (totalWritten, targets);
    }

    /// <summary>階段 3：分頁抓取 messages 並只保留目標日期的紀錄，寫入狀態變更表</summary>
    private async Task<int> FetchStateChangesAsync(DateTime day, CancellationToken ct)
    {
        var targetDate = day.Date;
        var totalWritten = 0;
        var unparseableCount = 0;
        var now = DateTime.Now;

        await FetchTablePagedAsync<PrtgStateChangeRow>(
            content: "messages",
            columns: "objid,datetime,parent,status,message",
            extraQuery: "id=0",
            mapper: el =>
            {
                var dtStr = GetStringProperty(el, "datetime");
                if (string.IsNullOrWhiteSpace(dtStr) || !DateTime.TryParse(dtStr, out var changedAt))
                {
                    unparseableCount++;
                    return null;
                }

                // 只保留 datetime 落在 day 當天的紀錄（本地時間比對，不做時區轉換）
                if (changedAt.Date != targetDate)
                {
                    return null;
                }

                var objid = GetLongProperty(el, "objid");
                if (!objid.HasValue) return null;

                return new PrtgStateChangeRow
                {
                    SensorObjid = objid.Value,
                    ChangedAt = changedAt,
                    Status = GetStringProperty(el, "status") ?? string.Empty,
                    PrevStatus = null,
                    Message = GetStringProperty(el, "message"),
                    Quality = PrtgDataQuality.Ok,
                    CreatedAt = now
                };
            },
            onBatch: batch =>
            {
                totalWritten += _store.AppendStateChanges(batch);
            },
            ct: ct);

        if (unparseableCount > 0)
        {
            _console.WriteLine($"  ⚠ 狀態變更中有 {unparseableCount} 筆紀錄無法解析時間，已略過。");
        }

        return totalWritten;
    }

    /// <summary>階段 4：對未暫停的 sensor 依併發上限擷取 hourly 聚合數值並逐 sensor 寫入鏡像表</summary>
    private async Task<int> FetchValuesAsync(
        DateTime day,
        IReadOnlyList<(long Objid, bool Paused)> activeSensors,
        int concurrency,
        CancellationToken ct)
    {
        if (activeSensors.Count == 0)
        {
            return 0;
        }

        var sdate = day.Date.ToString("yyyy-MM-dd-00-00-00");
        var edate = day.Date.AddDays(1).ToString("yyyy-MM-dd-00-00-00");
        var maxConcurrency = Math.Max(concurrency, 1);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var totalValues = 0;
        var totalUnparsed = 0;

        var tasks = activeSensors.Select(async target =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var query = $"api/historicdata.json?id={target.Objid}&avg=3600&sdate={sdate}&edate={edate}";
                var json = await _client.GetJsonAsync(query, ct);
                var rows = ParseHistoricData(json, target.Objid, out var unparsed);
                if (unparsed > 0) Interlocked.Add(ref totalUnparsed, unparsed);
                if (rows.Count > 0)
                {
                    // 逐 sensor 寫入資料庫後立即釋放記憶體，絕不累積成大清單一次寫入
                    var written = _store.UpsertValues(rows);
                    Interlocked.Add(ref totalValues, written);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (totalUnparsed > 0)
        {
            _console.WriteLine($"  ⚠ 有 {totalUnparsed} 筆數值的時間欄位無法解析而略過"
                + "（多半是 PRTG 伺服器的地區日期格式與本機不符，請比對 PRTG 的時間顯示設定）。");
        }

        return totalValues;
    }

    /// <summary>
    /// 解析 PRTG historicdata.json 回應並判定品質旗標。
    ///
    /// 【數值品質判定規則】：
    /// 1. Paused sensor：呼叫端已先篩除，整天跳過不抓亦不產生任何列。
    /// 2. 值為 null、未定義、空字串或 PRTG 回 ""：標記為 PrtgDataQuality.NoData，AvgValue 存 null。
    /// 3. 原始文字含 "unknown"（不分大小寫）或 coverage 為 0：標記為 PrtgDataQuality.Unknown，AvgValue 存 null。
    /// 4. 其餘正常數值：標記為 PrtgDataQuality.Ok，AvgValue 存實際數值。
    /// 5. Unknown 與 NoData 的列仍然寫入資料庫（AvgValue 為 null），保留該時段無可信資料之事實。
    /// </summary>
    /// <param name="unparsedCount">
    /// 時間欄位無法解析而被略過的筆數。PRTG 依伺服器地區設定輸出時間字串，格式與本機不符時
    /// 整段資料會解析失敗——這種缺口必須被看見，不能靜默略過（無聲的洞會讓後續基線以為那段本來就沒資料）。
    /// </param>
    private static List<PrtgValueRow> ParseHistoricData(string json, long sensorObjid, out int unparsedCount)
    {
        var rows = new List<PrtgValueRow>();
        var now = DateTime.Now;
        unparsedCount = 0;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("histdata", out var arrayProp) ||
            arrayProp.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in arrayProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            if (!item.TryGetProperty("datetime", out var dtProp))
            {
                unparsedCount++;
                continue;
            }
            var dtStr = dtProp.GetString();
            if (string.IsNullOrWhiteSpace(dtStr) || !DateTime.TryParse(dtStr, out var periodStart))
            {
                unparsedCount++;
                continue;
            }

            // 判定 Coverage 是否為 0
            double? coverage = null;
            var isCoverageZero = false;
            if (item.TryGetProperty("coverage", out var covProp))
            {
                if (covProp.ValueKind == JsonValueKind.Number && covProp.TryGetDouble(out var covVal))
                {
                    coverage = covVal;
                    if (covVal == 0) isCoverageZero = true;
                }
                else if (covProp.ValueKind == JsonValueKind.String)
                {
                    var rawCov = covProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(rawCov))
                    {
                        var cleaned = rawCov.TrimEnd('%').Trim();
                        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var covParsed))
                        {
                            coverage = covParsed;
                            if (covParsed == 0) isCoverageZero = true;
                        }
                    }
                }
            }

            // 取得數值屬性（支援 value_ 或 value）
            string? valueRaw = null;
            double? parsedValue = null;
            var hasValueProp = item.TryGetProperty("value_", out var valProp) || item.TryGetProperty("value", out valProp);

            if (hasValueProp)
            {
                if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out var v))
                {
                    parsedValue = v;
                    valueRaw = valProp.GetRawText();
                }
                else if (valProp.ValueKind == JsonValueKind.String)
                {
                    valueRaw = valProp.GetString();
                    if (!string.IsNullOrWhiteSpace(valueRaw) &&
                        double.TryParse(valueRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
                    {
                        parsedValue = v2;
                    }
                }
            }

            string quality;
            double? avgValue = null;

            if (!hasValueProp || valProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                string.IsNullOrWhiteSpace(valueRaw) || valueRaw == "\"\"")
            {
                quality = PrtgDataQuality.NoData;
                avgValue = null;
            }
            else if ((valueRaw != null && valueRaw.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0) || isCoverageZero)
            {
                quality = PrtgDataQuality.Unknown;
                avgValue = null;
            }
            else if (parsedValue.HasValue)
            {
                quality = PrtgDataQuality.Ok;
                avgValue = parsedValue.Value;
            }
            else
            {
                quality = PrtgDataQuality.Unknown;
                avgValue = null;
            }

            rows.Add(new PrtgValueRow
            {
                SensorObjid = sensorObjid,
                PeriodStart = periodStart,
                AvgValue = avgValue,
                MinValue = null,
                MaxValue = null,
                Coverage = coverage,
                Quality = quality,
                CreatedAt = now
            });
        }

        return rows;
    }

    /// <summary>
    /// PRTG table.json 分頁抓取與解析的單一私有輔助方法（階段 1、2、3 共用）。
    /// 每批轉換累積達 pageSize（500）即回呼寫入資料庫並清空緩衝，避免整份堆積於記憶體。
    /// 當遠端回傳空陣列時結束分頁。
    /// </summary>
    private async Task<int> FetchTablePagedAsync<T>(
        string content,
        string columns,
        string? extraQuery,
        Func<JsonElement, T?> mapper,
        Action<IReadOnlyList<T>> onBatch,
        CancellationToken ct)
    {
        const int pageSize = 500;
        var offset = 0;
        var totalMapped = 0;
        var buffer = new List<T>(pageSize);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var query = string.IsNullOrEmpty(extraQuery)
                ? $"api/table.json?content={content}&columns={columns}&start={offset}&count={pageSize}"
                : $"api/table.json?content={content}&columns={columns}&start={offset}&count={pageSize}&{extraQuery}";

            var json = await _client.GetJsonAsync(query, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(content, out var arrayProp) ||
                arrayProp.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var countInPage = 0;
            foreach (var item in arrayProp.EnumerateArray())
            {
                countInPage++;
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var mapped = mapper(item);
                if (mapped != null)
                {
                    buffer.Add(mapped);
                    if (buffer.Count >= pageSize)
                    {
                        totalMapped += buffer.Count;
                        onBatch(buffer);
                        buffer.Clear();
                    }
                }
            }

            // 停止條件有兩道：空頁，以及「未滿一頁」＝最後一頁。
            // 只靠空頁是不夠的——PRTG 前面若擺了會忽略 start 參數的代理，每次都會回同一頁非空資料，
            // 迴圈就永遠不會結束，整趟夜間批次卡死在這裡（而且沒有任何錯誤訊息）。
            if (countInPage < pageSize)
            {
                break;
            }

            offset += countInPage;
        }

        if (buffer.Count > 0)
        {
            totalMapped += buffer.Count;
            onBatch(buffer);
            buffer.Clear();
        }

        return totalMapped;
    }

    /// <summary>
    /// PRTG paused 欄位的容錯判定（唯一實作，供 devices 與 sensors 共用）。
    /// 支援 bool、數值（非 0 為 true）、字串（"true"/"-1"/"1" 為 true，"false"/"0" 為 false），無法解析時預設 false。
    /// </summary>
    private static bool ParsePaused(JsonElement el)
    {
        if (!el.TryGetProperty("paused", out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.True) return true;
        if (prop.ValueKind == JsonValueKind.False) return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var num))
            return num != 0;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString()?.Trim();
            if (bool.TryParse(s, out var b)) return b;
            if (long.TryParse(s, out var n)) return n != 0;
        }

        return false;
    }

    /// <summary>
    /// PRTG dependency 欄位的容錯判定（唯一實作，供 devices 與 sensors 共用）。
    /// 若為空、0、-1、"none"（不分大小寫）一律視為無相依性（回傳 null）。
    /// </summary>
    private static long? ParseDependency(JsonElement el)
    {
        if (!el.TryGetProperty("dependency", out var prop))
            return null;

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var num))
        {
            return num <= 0 ? null : num;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString()?.Trim();
            if (string.IsNullOrEmpty(s) ||
                string.Equals(s, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "0", StringComparison.Ordinal) ||
                string.Equals(s, "-1", StringComparison.Ordinal))
            {
                return null;
            }

            if (long.TryParse(s, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static long? GetLongProperty(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private static string? GetStringProperty(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetRawText();
            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return prop.GetRawText();
        }
        return null;
    }
}
