using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 資源守門感測器無法量測之原因常數。
/// </summary>
public static class PrtgResourceGuardUnmeasurableReasons
{
    public const string NotFound = "查無此 sensor";
    public const string NonPercentage = "非百分比值";
    public const string ApiFailed = "PRTG 取值失敗";
}

/// <summary>
/// PRTG 資源守門判定時略過感測器之原因。
/// 呼叫端可明確區分「查無此 sensor」、「狀態非 Up」、「非百分比值」與「未納入評估的分類」。
/// </summary>
public enum PrtgResourceGuardIgnoredReason
{
    None = 0,
    NotFound,
    StatusNotUp,
    NonPercentage,
    UnknownCategory
}

/// <summary>
/// PRTG 單一感測器即時值讀取結果。
/// </summary>
public sealed record PrtgResourceGuardSensorValue(
    long Objid,
    string? Device,
    string? Sensor,
    string? Status,
    double? Percentage,
    string? UnmeasurableReason = null)
;

/// <summary>
/// PRTG 資源守門即時值整批探測結果。
/// </summary>
public sealed record PrtgResourceGuardProbeResult(
    IReadOnlyDictionary<long, PrtgResourceGuardSensorValue> Values,
    bool IsSuccess,
    string? FailureReason = null)
;

/// <summary>
/// PRTG 單一感測器資源緊張判定明細。
/// </summary>
public sealed record PrtgResourceGuardSensorEvaluation(
    long Objid,
    string Category,
    string? Device,
    string? Sensor,
    string? Status,
    double? Percentage,
    bool IsMeasurable,
    PrtgResourceGuardIgnoredReason IgnoredReason,
    bool IsBreached)
;

/// <summary>
/// PRTG 資源守門緊張判定整體結果。
/// </summary>
public sealed record PrtgResourceGuardEvaluationResult(
    bool IsOverloaded,
    string? TriggeredSensorDescription,
    bool HasMeasurableSensors,
    IReadOnlyList<PrtgResourceGuardSensorEvaluation> Details)
;

/// <summary>
/// PRTG 資源守門即時值讀取與資源緊張判定服務。
/// </summary>
public static class PrtgResourceGuardProbe
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// PRTG table.json 批次查詢單批 objid 上限。
    /// </summary>
    public const int MaxBatchSize = 50;

    /// <summary>
    /// 讀取指定 sensor objid 清單的即時狀態與百分比數值。
    /// API 呼叫失敗時不擲例外，回傳無法取值結果；取消請求時（OperationCanceledException）例外穿透。
    /// </summary>
    public static async Task<PrtgResourceGuardProbeResult> FetchSensorValuesAsync(
        PrtgClient client,
        IReadOnlyList<long> objids,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (objids == null || objids.Count == 0)
        {
            return new PrtgResourceGuardProbeResult(
                new Dictionary<long, PrtgResourceGuardSensorValue>(),
                IsSuccess: true);
        }

        var distinctObjids = objids.Distinct().ToList();
        var results = new Dictionary<long, PrtgResourceGuardSensorValue>();

        try
        {
            for (int i = 0; i < distinctObjids.Count; i += MaxBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = distinctObjids.Skip(i).Take(MaxBatchSize).ToList();
                var filterQuery = string.Concat(batch.Select(id => $"&filter_objid={id}"));
                var relativePathAndQuery = $"/api/table.json?content=sensors&columns=objid,device,sensor,status,lastvalue,lastvalue_raw{filterQuery}";

                var json = await client.GetJsonAsync(relativePathAndQuery, ct);
                ParseSensorsJson(json, batch, results);
            }

            // 針對請求中但回應中未包含的 objid 補上 NotFound
            foreach (var id in distinctObjids)
            {
                if (!results.ContainsKey(id))
                {
                    results[id] = new PrtgResourceGuardSensorValue(
                        Objid: id,
                        Device: null,
                        Sensor: null,
                        Status: null,
                        Percentage: null,
                        UnmeasurableReason: PrtgResourceGuardUnmeasurableReasons.NotFound);
                }
            }

            return new PrtgResourceGuardProbeResult(results, IsSuccess: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "PRTG 資源守門即時值讀取失敗");

            var failureDict = new Dictionary<long, PrtgResourceGuardSensorValue>();
            foreach (var id in distinctObjids)
            {
                failureDict[id] = new PrtgResourceGuardSensorValue(
                    Objid: id,
                    Device: null,
                    Sensor: null,
                    Status: null,
                    Percentage: null,
                    UnmeasurableReason: PrtgResourceGuardUnmeasurableReasons.ApiFailed);
            }

            return new PrtgResourceGuardProbeResult(
                failureDict,
                IsSuccess: false,
                FailureReason: PrtgResourceGuardUnmeasurableReasons.ApiFailed);
        }
    }

    /// <summary>
    /// 解析 PRTG 感測器數值為百分比。
    /// 若提供 lastvalue_raw 則優先使用（純數字）；若無則解析 lastvalue 字串。
    /// 非百分比格式（如「12 kbit/s」「OK」「-」）一律回傳 null。
    /// 全專案唯一的百分比解析實作。
    /// </summary>
    public static double? ParsePercentage(string? lastValue, string? lastValueRaw = null)
    {
        // 1. 若 lastValue 存在且不是以百分比結尾，直接視為非百分比通道，回傳 null
        if (!string.IsNullOrWhiteSpace(lastValue))
        {
            var trimmed = lastValue.Trim();
            if (!trimmed.EndsWith('%'))
            {
                return null;
            }

            // 2. lastvalue_raw 優先：若有 raw 數值且為合法數字，優先使用
            if (!string.IsNullOrWhiteSpace(lastValueRaw))
            {
                var rawClean = lastValueRaw.Trim().Replace(",", "");
                if (double.TryParse(rawClean, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawVal))
                {
                    return rawVal;
                }
            }

            // 3. 退回解析 lastvalue 字串：必須以數字開頭、單位為 %、去除千分位逗號
            var numPart = trimmed[..^1].Trim().Replace(",", "");
            if (numPart.Length > 0 && char.IsDigit(numPart[0]) &&
                double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedVal))
            {
                return parsedVal;
            }

            return null;
        }

        // 若 lastValue 為空但 lastValueRaw 存在且為合法數字（容許 mock 僅給 lastvalue_raw 的情境）
        if (!string.IsNullOrWhiteSpace(lastValueRaw))
        {
            var rawClean = lastValueRaw.Trim().Replace(",", "");
            if (double.TryParse(rawClean, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawVal))
            {
                return rawVal;
            }
        }

        return null;
    }

    /// <summary>
    /// 依據感測器語意分類、探測值與系統門檻設定，判定當前資源是否超標緊張。
    /// </summary>
    /// <param name="sensorCategories">objid 至語意分類（cpu／memory／corehealth／unknown）之對照</param>
    /// <param name="probeResult">探測取得的感測器即時值結果</param>
    /// <param name="settings">系統設定（含 CPU 與可用記憶體門檻）</param>
    /// <returns>包含是否超標、觸發說明、是否有量測值與各感測器評估明細之結果</returns>
    public static PrtgResourceGuardEvaluationResult Evaluate(
        IReadOnlyDictionary<long, string> sensorCategories,
        PrtgResourceGuardProbeResult probeResult,
        SystemSettings settings)
    {
        var details = new List<PrtgResourceGuardSensorEvaluation>();
        var triggeredDescriptions = new List<string>();
        int measurableCount = 0;

        foreach (var (objid, category) in sensorCategories)
        {
            var cat = category?.ToLowerInvariant() ?? PrtgResourceGuardTargets.CategoryUnknown;

            // unknown 一律忽略，不參與判定
            if (cat != PrtgSensorCategories.Cpu &&
                cat != PrtgSensorCategories.Memory &&
                cat != PrtgResourceGuardTargets.CategoryCoreHealth)
            {
                probeResult.Values.TryGetValue(objid, out var unknownVal);
                details.Add(new PrtgResourceGuardSensorEvaluation(
                    Objid: objid,
                    Category: cat,
                    Device: unknownVal?.Device,
                    Sensor: unknownVal?.Sensor,
                    Status: unknownVal?.Status,
                    Percentage: unknownVal?.Percentage,
                    IsMeasurable: false,
                    IgnoredReason: PrtgResourceGuardIgnoredReason.UnknownCategory,
                    IsBreached: false));
                continue;
            }

            // 情況 1：objid 在回應中不存在
            if (!probeResult.Values.TryGetValue(objid, out var sensorVal) ||
                sensorVal.UnmeasurableReason == PrtgResourceGuardUnmeasurableReasons.NotFound ||
                (sensorVal.Status == null && sensorVal.Percentage == null))
            {
                details.Add(new PrtgResourceGuardSensorEvaluation(
                    Objid: objid,
                    Category: cat,
                    Device: sensorVal?.Device,
                    Sensor: sensorVal?.Sensor,
                    Status: sensorVal?.Status,
                    Percentage: null,
                    IsMeasurable: false,
                    IgnoredReason: PrtgResourceGuardIgnoredReason.NotFound,
                    IsBreached: false));
                continue;
            }

            // 情況 2：sensor 狀態非 Up（用既有的 PrtgSensorStatuses 判定，不要自己比字串）
            if (!PrtgSensorStatuses.IsUp(sensorVal.Status))
            {
                details.Add(new PrtgResourceGuardSensorEvaluation(
                    Objid: objid,
                    Category: cat,
                    Device: sensorVal.Device,
                    Sensor: sensorVal.Sensor,
                    Status: sensorVal.Status,
                    Percentage: sensorVal.Percentage,
                    IsMeasurable: false,
                    IgnoredReason: PrtgResourceGuardIgnoredReason.StatusNotUp,
                    IsBreached: false));
                continue;
            }

            // 情況 3：值不是百分比（null）
            if (!sensorVal.Percentage.HasValue)
            {
                details.Add(new PrtgResourceGuardSensorEvaluation(
                    Objid: objid,
                    Category: cat,
                    Device: sensorVal.Device,
                    Sensor: sensorVal.Sensor,
                    Status: sensorVal.Status,
                    Percentage: null,
                    IsMeasurable: false,
                    IgnoredReason: PrtgResourceGuardIgnoredReason.NonPercentage,
                    IsBreached: false));
                continue;
            }

            // 成功量測，參與判定
            measurableCount++;
            var pct = sensorVal.Percentage.Value;
            bool isBreached = false;

            if (cat == PrtgSensorCategories.Cpu)
            {
                // cpu: 百分比 ≥ PrtgResourceGuardCpuPercent → 超標
                isBreached = pct >= settings.PrtgResourceGuardCpuPercent;
            }
            else if (cat == PrtgSensorCategories.Memory || cat == PrtgResourceGuardTargets.CategoryCoreHealth)
            {
                // memory: 百分比 ≤ PrtgResourceGuardMemoryFreePercent → 超標
                // corehealth: 百分比 ≤ PrtgResourceGuardMemoryFreePercent → 超標
                isBreached = pct <= settings.PrtgResourceGuardMemoryFreePercent;
            }

            if (isBreached)
            {
                var devName = !string.IsNullOrWhiteSpace(sensorVal.Device) ? sensorVal.Device : $"裝置#{objid}";
                var senName = !string.IsNullOrWhiteSpace(sensorVal.Sensor) ? sensorVal.Sensor : "感測器";
                var pctText = pct.ToString("0.#", CultureInfo.InvariantCulture);
                triggeredDescriptions.Add($"{devName} / {senName} {pctText}%");
            }

            details.Add(new PrtgResourceGuardSensorEvaluation(
                Objid: objid,
                Category: cat,
                Device: sensorVal.Device,
                Sensor: sensorVal.Sensor,
                Status: sensorVal.Status,
                Percentage: pct,
                IsMeasurable: true,
                IgnoredReason: PrtgResourceGuardIgnoredReason.None,
                IsBreached: isBreached));
        }

        bool hasMeasurable = measurableCount > 0;
        bool isOverloaded = hasMeasurable && triggeredDescriptions.Count > 0;
        string? summary = isOverloaded ? string.Join("；", triggeredDescriptions) : null;

        return new PrtgResourceGuardEvaluationResult(
            IsOverloaded: isOverloaded,
            TriggeredSensorDescription: summary,
            HasMeasurableSensors: hasMeasurable,
            Details: details);
    }

    private static void ParseSensorsJson(
        string json,
        IReadOnlyList<long> batch,
        Dictionary<long, PrtgResourceGuardSensorValue> results)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("sensors", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var batchSet = new HashSet<long>(batch);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            long objid = 0;
            if (item.TryGetProperty("objid", out var objidProp))
            {
                if (objidProp.ValueKind == JsonValueKind.Number)
                {
                    objid = objidProp.GetInt64();
                }
                else if (objidProp.ValueKind == JsonValueKind.String && long.TryParse(objidProp.GetString(), out var parsedId))
                {
                    objid = parsedId;
                }
            }

            if (objid == 0 || !batchSet.Contains(objid)) continue;

            var device = GetStringProperty(item, "device");
            var sensor = GetStringProperty(item, "sensor");
            var status = GetStringProperty(item, "status");
            var lastValue = GetStringProperty(item, "lastvalue");

            string? lastValueRaw = null;
            if (item.TryGetProperty("lastvalue_raw", out var rawProp))
            {
                if (rawProp.ValueKind == JsonValueKind.Number)
                    lastValueRaw = rawProp.GetRawText();
                else if (rawProp.ValueKind == JsonValueKind.String)
                    lastValueRaw = rawProp.GetString();
            }

            var percentage = ParsePercentage(lastValue, lastValueRaw);
            string? unmeasurableReason = percentage == null
                ? PrtgResourceGuardUnmeasurableReasons.NonPercentage
                : null;

            results[objid] = new PrtgResourceGuardSensorValue(
                Objid: objid,
                Device: device,
                Sensor: sensor,
                Status: status,
                Percentage: percentage,
                UnmeasurableReason: unmeasurableReason);
        }
    }

    private static string? GetStringProperty(JsonElement el, string propertyName)
    {
        if (el.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetRawText();
        }
        return null;
    }
}
