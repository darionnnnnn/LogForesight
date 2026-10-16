using System.Globalization;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>sensor 數值的量測語意（docs/PRTG-SPEC.md §9b）。只依 sensor type 判定——PRTG 的 sensor／channel 表不回 unit 欄。</summary>
public enum PrtgValueSemantic
{
    CpuPercentUsed,
    MemoryPercentFree,
    DiskPercentFree,
    PingMs
}

/// <summary>
/// 內建 type → 語意對照。四類語意由現場 raw history 互相驗算（磁碟頻道 0＝free bytes÷total bytes、
/// 記憶體頻道 0＝available÷total）；historicdata 的第一組 value_raw 就是這個頻道。
/// 對照外的 type 一律「單位未知」，數值規則跳過它。
/// </summary>
public static class PrtgValueSemantics
{
    private static readonly IReadOnlyDictionary<string, PrtgValueSemantic> Builtin =
        new Dictionary<string, PrtgValueSemantic>(StringComparer.OrdinalIgnoreCase)
        {
            ["SNMP CPU Load"] = PrtgValueSemantic.CpuPercentUsed,
            ["SNMP Memory"] = PrtgValueSemantic.MemoryPercentFree,
            ["SNMP Disk Free"] = PrtgValueSemantic.DiskPercentFree,
            ["Ping"] = PrtgValueSemantic.PingMs
        };

    /// <summary>內建對照的 type（探測步驟 8 逐一核對）。</summary>
    public static IReadOnlyCollection<string> BuiltinTypes => (IReadOnlyCollection<string>)Builtin.Keys;

    public static bool TryResolve(string? sensorType, out PrtgValueSemantic semantic)
    {
        semantic = default;
        return !string.IsNullOrWhiteSpace(sensorType) && Builtin.TryGetValue(sensorType.Trim(), out semantic);
    }

    public static bool IsPercent(PrtgValueSemantic semantic) => semantic != PrtgValueSemantic.PingMs;

    /// <summary>值是否落在語意允許的範圍：百分比 0–100、毫秒 ≥0。</summary>
    public static bool IsPlausible(PrtgValueSemantic semantic, double value) =>
        double.IsFinite(value) && value >= 0 && (!IsPercent(semantic) || value <= 100);
}

/// <summary>
/// 一顆 sensor 的數值為何可用或不可用。就緒度卡、規則頁、主機詳情與每日執行輸出共用這一個列舉，
/// 同一顆 sensor 在各處的原因才會一致。
/// </summary>
public enum PrtgValueReason
{
    Ready,
    InsufficientDays,
    LowCoverage,
    ImplausibleValue,
    FixedValue,
    UnknownUnit,
    MappingInvalid
}

public static class PrtgValueReasonText
{
    public static string Label(PrtgValueReason reason) => reason switch
    {
        PrtgValueReason.Ready => "就緒",
        PrtgValueReason.InsufficientDays => "有效日不足",
        PrtgValueReason.LowCoverage => "品質不足",
        PrtgValueReason.ImplausibleValue => "值不合理",
        PrtgValueReason.FixedValue => "固定值",
        PrtgValueReason.UnknownUnit => "單位未知",
        PrtgValueReason.MappingInvalid => "對應無效",
        _ => reason.ToString()
    };
}

/// <summary>數值評估的一顆候選 sensor。<see cref="HostId"/> 是評估日當天的 ok／人工對應主機。</summary>
public sealed record PrtgValueSensor(long SensorObjid, long DeviceObjid, long HostId, string SensorName,
    string SensorType, string? Category);

/// <summary>每日代表值：當日可用小時的平均；可用小時不足 <see cref="PrtgValueAssessment.MinDailyUsableHours"/> 的日不列入。</summary>
public sealed record PrtgDailyValue(DateOnly Day, double Value, int UsableHours);

public sealed record PrtgValueAssessmentRow(
    PrtgValueSensor Sensor,
    PrtgValueSemantic? Semantic,
    PrtgValueReason Reason,
    int ValidDays,
    int RequiredDays,
    DateOnly? LatestValidDay,
    IReadOnlyList<PrtgDailyValue> Daily,
    int ImplausibleHours);

/// <summary>
/// 數值可信＝三條件：(1) 該日 sensor 所在裝置有 ok 或人工對應、且是評估日的同一台主機；
/// (2) 該小時是可用列（<c>ok</c>，或 <c>sampled</c> 且 coverage ≥ 75）；(3) 單位可判（<see cref="PrtgValueSemantics"/>）。
/// 另加兩道合理性檢查：範圍外的小時不採用、近 7 個有效日每小時完全相同視為固定值不評估。
/// 只讀已落盤資料，不呼叫 PRTG、不改寫原始數值；每頁最多 <see cref="PageSize"/> 顆。
/// </summary>
public sealed class PrtgValueAssessment
{
    public const int PageSize = 100;
    public const int MinDailyUsableHours = 12;
    public const int FixedValueDays = 7;
    /// <summary>過去日的對應取「該日或之前最近一日」，往回看這麼多天（與每日管線同一個視窗）。</summary>
    public const int MapLookbackDays = 31;

    private readonly EfPrtgStore _store;

    public PrtgValueAssessment(EfPrtgStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 候選＝鏡像中未暫停、所在裝置於 <paramref name="deviceToHost"/>（評估日的對應）有主機、主機在評估範圍內的 sensor；
    /// 可再以分類與監看政策的 sensor 清單（空＝不限）縮圈。依 objid 排序，分頁穩定。
    /// </summary>
    public IReadOnlyList<PrtgValueSensor> Candidates(IReadOnlyDictionary<long, long> deviceToHost,
        IReadOnlySet<long> evaluationHostIds, IReadOnlySet<long> policySensorIds, string? category = null) =>
        Candidates(_store.GetSensorStatuses(), deviceToHost, evaluationHostIds, policySensorIds, category);

    /// <summary>同上，但沿用呼叫端已讀出的 sensor 鏡像（逐日試算時只讀一次）。</summary>
    public static IReadOnlyList<PrtgValueSensor> Candidates(
        IEnumerable<(long Objid, long DeviceObjid, string? Status, string SensorType, string SensorName, string? Category)> sensors,
        IReadOnlyDictionary<long, long> deviceToHost, IReadOnlySet<long> evaluationHostIds,
        IReadOnlySet<long> policySensorIds, string? category = null)
    {
        return sensors
            .Where(s => deviceToHost.TryGetValue(s.DeviceObjid, out var hostId) && evaluationHostIds.Contains(hostId) &&
                (policySensorIds.Count == 0 || policySensorIds.Contains(s.Objid)) &&
                (category is null || string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(s => s.Objid).Select(g => g.First())
            .OrderBy(s => s.Objid)
            .Select(s => new PrtgValueSensor(s.Objid, s.DeviceObjid, deviceToHost[s.DeviceObjid], s.SensorName,
                s.SensorType, s.Category))
            .ToList();
    }

    /// <summary>sensor 鏡像（候選來源）；逐日試算時讀一次後交給靜態的 Candidates 多載。</summary>
    public List<(long Objid, long DeviceObjid, string? Status, string SensorType, string SensorName, string? Category)> SensorMirror() =>
        _store.GetSensorStatuses();

    /// <summary>
    /// 某個已完成日的 device → 主機：取該日或之前 <see cref="MapLookbackDays"/> 日內最近一份對應，只認 ok
    /// （人工對應也以 ok 寫入），且主機在 <paramref name="evaluationHostIds"/> 內。與每日管線過去日的取法相同。
    /// </summary>
    public Dictionary<long, long> DeviceToHostOn(DateOnly day, IReadOnlySet<long> evaluationHostIds)
    {
        var deviceToHost = new Dictionary<long, long>();
        foreach (var row in _store.GetLatestHostMapWithDate(MapLookbackDays, day.ToDateTime(TimeOnly.MinValue)).Rows)
        {
            if (row.MapStatus == PrtgMapStatus.Ok && row.HostId is { } hostId && evaluationHostIds.Contains(hostId))
                deviceToHost[row.DeviceObjid] = hostId;
        }
        return deviceToHost;
    }

    /// <summary>評估一頁候選（≤ <see cref="PageSize"/> 顆）在 <paramref name="completedDay"/> 往回 <paramref name="windowDays"/> 日的數值。</summary>
    public IReadOnlyList<PrtgValueAssessmentRow> AssessPage(IReadOnlyList<PrtgValueSensor> sensors,
        DateOnly completedDay, int windowDays, int requiredDays)
    {
        if (sensors.Count > PageSize)
            throw new ArgumentOutOfRangeException(nameof(sensors), $"每頁最多 {PageSize} 顆 sensor。");
        if (windowDays < 1 || requiredDays < 1 || requiredDays > windowDays)
            throw new ArgumentOutOfRangeException(nameof(windowDays), "觀察窗與最少有效日不合理。");
        if (sensors.Count == 0) return [];

        var windowStart = completedDay.AddDays(-(windowDays - 1)).ToDateTime(TimeOnly.MinValue);
        var windowEnd = completedDay.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var known = sensors.Where(s => PrtgValueSemantics.TryResolve(s.SensorType, out _)).ToList();
        var valuesBySensor = _store.GetReadinessValues(known.Select(s => s.SensorObjid).ToArray(), windowStart, windowEnd)
            .GroupBy(v => v.SensorObjid).ToDictionary(g => g.Key, g => g.ToList());
        var mapsByDevice = _store.GetReadinessMaps(known.Select(s => s.DeviceObjid).Distinct().ToArray(),
                windowStart.AddDays(-MapLookbackDays), windowEnd)
            .GroupBy(m => m.DeviceObjid)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.MapDate).ToList());

        var result = new List<PrtgValueAssessmentRow>(sensors.Count);
        foreach (var sensor in sensors)
        {
            if (!PrtgValueSemantics.TryResolve(sensor.SensorType, out var semantic))
            {
                result.Add(new(sensor, null, PrtgValueReason.UnknownUnit, 0, requiredDays, null, [], 0));
                continue;
            }
            var maps = mapsByDevice.GetValueOrDefault(sensor.DeviceObjid) ?? [];
            bool MappedToSensorHost(DateTime day)
            {
                var row = maps.LastOrDefault(m => m.MapDate.Date <= day && m.MapDate.Date > day.AddDays(-MapLookbackDays - 1));
                // 人工對應也以 ok 寫入當日對應表（PrtgHostMapper），這裡只認 ok。
                return row is { MapStatus: PrtgMapStatus.Ok } && row.HostId == sensor.HostId;
            }
            if (!MappedToSensorHost(completedDay.ToDateTime(TimeOnly.MinValue)))
            {
                result.Add(new(sensor, semantic, PrtgValueReason.MappingInvalid, 0, requiredDays, null, [], 0));
                continue;
            }

            var implausible = 0;
            var lowCoverage = 0;
            var usableByDay = new SortedDictionary<DateOnly, List<double>>();
            foreach (var row in valuesBySensor.GetValueOrDefault(sensor.SensorObjid) ?? [])
            {
                if (!MappedToSensorHost(row.PeriodStart.Date)) continue;
                var sampled = string.Equals(row.Quality, PrtgDataQuality.Sampled, StringComparison.OrdinalIgnoreCase);
                var ok = string.Equals(row.Quality, PrtgDataQuality.Ok, StringComparison.OrdinalIgnoreCase);
                if (!ok && !sampled || row.AvgValue is not { } value) continue;
                if (sampled && !(row.Coverage >= PrtgValueUsability.SampledMinCoverage)) { lowCoverage++; continue; }
                if (!PrtgValueSemantics.IsPlausible(semantic, value)) { implausible++; continue; }
                var day = DateOnly.FromDateTime(row.PeriodStart);
                if (!usableByDay.TryGetValue(day, out var hours)) usableByDay[day] = hours = new List<double>();
                hours.Add(value);
            }

            var validDays = usableByDay.Where(pair => pair.Value.Count >= MinDailyUsableHours).ToList();
            var daily = validDays.Select(pair => new PrtgDailyValue(pair.Key, pair.Value.Average(), pair.Value.Count)).ToList();
            var latest = daily.Count > 0 ? daily[^1].Day : (DateOnly?)null;
            PrtgValueReason reason;
            if (validDays.Count >= FixedValueDays &&
                validDays.TakeLast(FixedValueDays).SelectMany(pair => pair.Value).Distinct().Count() == 1)
                reason = PrtgValueReason.FixedValue;
            else if (daily.Count >= requiredDays)
                reason = PrtgValueReason.Ready;
            else if (implausible > 0)
                reason = PrtgValueReason.ImplausibleValue;
            else if (lowCoverage > 0)
                reason = PrtgValueReason.LowCoverage;
            else
                reason = PrtgValueReason.InsufficientDays;
            result.Add(new(sensor, semantic, reason, daily.Count, requiredDays, latest, daily, implausible));
        }
        return result;
    }

    /// <summary>
    /// <c>disk_free_trend</c>：就緒的磁碟剩餘 % sensor 以每日代表值跑既有趨勢評估；命中才回 finding。
    /// 資料不足或未命中回 null（原因留在 <paramref name="row"/> 與回傳的 explanation）。
    /// </summary>
    public static PrtgFinding? DiskTrendFinding(PrtgValueAssessmentRow row, KnownIssueRule rule, DateOnly completedDay,
        out string explanation)
    {
        explanation = PrtgValueReasonText.Label(row.Reason);
        if (row.Reason != PrtgValueReason.Ready || row.Semantic != PrtgValueSemantic.DiskPercentFree ||
            rule.PrtgDiskTrendThresholds is null) return null;
        var trend = PrtgDiskTrendEvaluator.Evaluate(
            row.Daily.Select(d => new PrtgDiskTrendDay(d.Day, d.Value)).ToArray(), rule.PrtgDiskTrendThresholds);
        explanation = trend.Explanation;
        if (trend.Outcome != PrtgDiskTrendOutcome.Hit || trend.LatestDay != completedDay) return null;
        var detail = string.Format(CultureInfo.InvariantCulture,
            "{0}：磁碟可用空間 {1:F1}%；近 {2} 日有效 {3} 日；資料截至 {4:yyyy-MM-dd}。{5}",
            row.Sensor.SensorName, trend.CurrentAvailablePercent, rule.PrtgDiskTrendThresholds.RecentWindowDays,
            trend.ValidDayCount, trend.LatestDay, trend.Explanation);
        return new PrtgFinding(row.Sensor.DeviceObjid, row.Sensor.SensorObjid, PrtgRuleEvaluator.RuleDiskFreeTrend,
            detail, trend.ValidDayCount, rule, Acknowledged: false) { SensorCategory = PrtgSensorCategories.Disk };
    }
}
