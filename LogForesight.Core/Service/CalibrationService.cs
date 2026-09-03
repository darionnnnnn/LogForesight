using System.Text.Json;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// 校準狀態（四選一列舉）
/// </summary>
public enum CalibrationStatus
{
    /// <summary>不足：累積量未達可用門檻，或關鍵指標為零</summary>
    Insufficient,

    /// <summary>可用：已達最低校準需求門檻</summary>
    Available,

    /// <summary>充足：已達最佳校準需求門檻</summary>
    Sufficient,

    /// <summary>無法取得：模組未啟用或保留期內完全無任何候選資料</summary>
    Unavailable
}

/// <summary>
/// 各項校準門檻與常數定義（集中管理，方便日後校準調整）
/// </summary>
public static class CalibrationConstants
{
    /// <summary>匯出檔案格式版本</summary>
    public const int CurrentFormatVersion = 1;

    // ── 1. PRTG 值型基線門檻 ──────────────────────────────────────────
    /// <summary>值型基線所需最少主機數</summary>
    public const int ValueBaselineRequiredHosts = 10;
    /// <summary>值型基線「可用」所需最少涵蓋天數</summary>
    public const int ValueBaselineAvailableDays = 28;
    /// <summary>值型基線「充足」所需最少涵蓋天數</summary>
    public const int ValueBaselineSufficientDays = 56;
    /// <summary>單日視為有效涵蓋的 ok 時數下限</summary>
    public const int ValueBaselineMinDailyOkHours = 12;
    /// <summary>值型基線回看評估窗口天數</summary>
    public const int ValueBaselineWindowDays = 56;

    // ── 2. PRTG 規則門檻 ────────────────────────────────────────────
    /// <summary>規則門檻「可用」所需狀態變更涵蓋天數</summary>
    public const int RuleThresholdAvailableDays = 28;
    /// <summary>規則門檻「可用」所需期間命中筆數</summary>
    public const int RuleThresholdAvailableHits = 30;
    /// <summary>規則門檻「充足」所需狀態變更涵蓋天數</summary>
    public const int RuleThresholdSufficientDays = 56;
    /// <summary>規則門檻「充足」所需期間命中筆數</summary>
    public const int RuleThresholdSufficientHits = 100;
    /// <summary>規則門檻回看評估窗口天數</summary>
    public const int RuleThresholdWindowDays = 56;

    // ── 3. 觸發式取數量級門檻 ────────────────────────────────────────
    /// <summary>觸發式取數「可用」所需有數值天數</summary>
    public const int TriggeredMagnitudeAvailableDays = 14;
    /// <summary>觸發式取數「充足」所需有數值天數</summary>
    public const int TriggeredMagnitudeSufficientDays = 28;
    /// <summary>觸發式取數回看評估窗口天數</summary>
    public const int TriggeredMagnitudeWindowDays = 30;

    // ── 4. 殘留判定門檻 ────────────────────────────────────────────
    /// <summary>殘留判定「可用」所需候選主機日數</summary>
    public const int ResidualAvailableHostDays = 200;
    /// <summary>殘留判定「可用」所需涵蓋相異天數</summary>
    public const int ResidualAvailableDays = 14;
    /// <summary>殘留判定「充足」所需候選主機日數</summary>
    public const int ResidualSufficientHostDays = 1000;
    /// <summary>殘留判定「充足」所需涵蓋相異天數</summary>
    public const int ResidualSufficientDays = 28;
    /// <summary>殘留候選主機日匯出筆數上限</summary>
    public const int ResidualCandidateMaxCount = 5000;
}

/// <summary>
/// 單一校準項的判定結果
/// </summary>
public sealed class CalibrationItemAssessment
{
    /// <summary>校準項目名稱</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>狀態列舉</summary>
    public CalibrationStatus Status { get; init; }

    /// <summary>狀態中文描述（不足／可用／充足／無法取得）</summary>
    public string StatusText => Status switch
    {
        CalibrationStatus.Insufficient => "不足",
        CalibrationStatus.Available => "可用",
        CalibrationStatus.Sufficient => "充足",
        CalibrationStatus.Unavailable => "無法取得",
        _ => "無法取得"
    };

    /// <summary>目前累積量的關鍵數字</summary>
    public Dictionary<string, object> KeyMetrics { get; init; } = new();

    /// <summary>門檻現值</summary>
    public Dictionary<string, object> CurrentThresholds { get; init; } = new();

    /// <summary>補充說明（條列字串）</summary>
    public List<string> Explanations { get; init; } = new();
}

/// <summary>
/// 四個校準項的總體判定摘要
/// </summary>
public sealed class CalibrationAssessmentSummary
{
    public CalibrationItemAssessment PrtgValueBaseline { get; init; } = new();
    public CalibrationItemAssessment PrtgRuleThresholds { get; init; } = new();
    public CalibrationItemAssessment TriggeredFetchMagnitude { get; init; } = new();
    public CalibrationItemAssessment ResidualCredentialThresholds { get; init; } = new();
}

/// <summary>
/// 值型基線每日聚合資料列（匯出用）
/// </summary>
public sealed record CalibrationValueBaselineRow(
    long SensorObjid,
    long DeviceObjid,
    long? HostId,
    string? HostName,
    string SensorType,
    DateTime Date,
    double? AvgValue,
    double? MinValue,
    double? MaxValue,
    int OkHours,
    int UnknownCount,
    int NodataCount);

/// <summary>
/// PRTG 規則門檻與現況命中資料集（匯出用）
/// </summary>
public sealed class CalibrationRuleThresholdDataset
{
    public List<PrtgRuleHitAggregate> DailyRuleHits { get; init; } = new();
    public List<CalibrationPrtgRuleThresholdInfo> CurrentRules { get; init; } = new();

    /// <summary>
    /// 以**最低門檻**（Down 1 分鐘／flap 1 次／Warning 1 分鐘）逐日評估得到的每 sensor-日 finding。
    /// 現行門檻下的命中數只能回答「照目前設定會報幾次」，無法回答「門檻該設多少」——
    /// 要校準門檻必須看底層 magnitude 的分佈（有多少 sensor-日的 Down 持續 X 分鐘）。
    /// </summary>
    public List<CalibrationRuleMagnitudeRow> MagnitudeSamples { get; init; } = new();

    /// <summary>各規則的 magnitude 分位數摘要（挑門檻時最先看的東西）</summary>
    public List<CalibrationRuleMagnitudeSummary> MagnitudeSummaries { get; init; } = new();
}

/// <summary>最低門檻評估得到的一筆 finding（magnitude 即持續分鐘數／往返次數）</summary>
public sealed record CalibrationRuleMagnitudeRow(
    string RuleCode,
    DateTime Date,
    long DeviceObjid,
    long? SensorObjid,
    int Magnitude);

/// <summary>單一規則的 magnitude 分佈摘要</summary>
public sealed record CalibrationRuleMagnitudeSummary(
    string RuleCode,
    int SampleCount,
    int Min,
    int P50,
    int P90,
    int P99,
    int Max,
    int HitsAtCurrentThreshold,
    int CurrentThreshold);

/// <summary>
/// PRTG 規則門檻現值資訊
/// </summary>
public sealed record CalibrationPrtgRuleThresholdInfo(
    string RuleCode,
    int Threshold,
    string Category,
    string Severity,
    bool ElevatesDayRisk,
    string Description);

/// <summary>
/// 殘留判定候選主機日指標資料列（匯出用，嚴格排除帳號等個人識別資訊）
/// </summary>
public sealed record CalibrationResidualCandidateRow(
    long HostId,
    string HostName,
    DateTime Date,
    int EventId,
    int CandidateGroupCount,
    int TotalDetailCount,
    double ConcentrationRatio,
    double? MechanicalLogonTypeRatio,
    double SingleGroupRatio,
    int CrossDayDistinctDays,
    bool IsTruncated,
    bool IsMatch);

/// <summary>
/// 校準數值匯出包（自描述 JSON 物件）
/// </summary>
public sealed class CalibrationExportPackage
{
    public int FormatVersion { get; init; } = CalibrationConstants.CurrentFormatVersion;
    public DateTime ExportedAt { get; init; }
    public CalibrationAssessmentSummary Summary { get; init; } = new();
    public List<CalibrationValueBaselineRow> ValueBaselines { get; init; } = new();
    public CalibrationRuleThresholdDataset RuleThresholds { get; init; } = new();
    public List<PrtgDailyValueMagnitude> TriggeredMagnitudes { get; init; } = new();
    public List<CalibrationResidualCandidateRow> ResidualCandidates { get; init; } = new();
}

/// <summary>
/// 校準狀態判定與匯出檔組裝服務（A3 服務層）
/// </summary>
public sealed class CalibrationService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Func<LfDbContext> _contextFactory;
    private readonly EfPrtgStore _prtgStore;
    private readonly IIssueAggregateQuery _issueQuery;
    private readonly ISystemSettingsStore _settingsStore;
    private readonly IKnownIssueRuleStore _ruleStore;

    public CalibrationService(
        Func<LfDbContext> contextFactory,
        EfPrtgStore prtgStore,
        IIssueAggregateQuery issueQuery,
        ISystemSettingsStore settingsStore,
        IKnownIssueRuleStore ruleStore)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _prtgStore = prtgStore ?? throw new ArgumentNullException(nameof(prtgStore));
        _issueQuery = issueQuery ?? throw new ArgumentNullException(nameof(issueQuery));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
    }

    /// <summary>判定結果的行程內快取（見 <see cref="AssessStatus"/>）</summary>
    private static readonly object CacheLock = new();
    private static DateTime _cachedAnchor;
    private static DateTime _cachedAt;
    private static CalibrationAssessmentSummary? _cached;

    /// <summary>判定快取存活時間：四項判定是重查詢（含逐筆反序列化），
    /// 而累積量以「天」為單位變動，十分鐘內重複計算不會得到不同結論。
    /// 匯出時會再取一次判定摘要，這個快取讓同一次匯出不必重跑整組查詢。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 評估四項校準指標的累積量、狀態與補充說明。
    /// 結果在行程內快取 <see cref="CacheTtl"/>；`forceRefresh` 為 true 時略過快取重算。
    /// </summary>
    public CalibrationAssessmentSummary AssessStatus(DateTime? anchor = null, bool forceRefresh = false)
    {
        var anchorDate = (anchor ?? DateTime.Today).Date;

        if (!forceRefresh)
        {
            lock (CacheLock)
            {
                if (_cached != null && _cachedAnchor == anchorDate && DateTime.Now - _cachedAt < CacheTtl)
                {
                    return _cached;
                }
            }
        }
        var settings = _settingsStore.Get();
        var allSensors = _prtgStore.GetAllSensors();

        var item1 = AssessPrtgValueBaseline(anchorDate, settings, allSensors);
        var item2 = AssessPrtgRuleThresholds(anchorDate, settings, allSensors);
        var item3 = AssessTriggeredFetchMagnitude(anchorDate, settings, allSensors);
        var item4 = AssessResidualCredentialThresholds(anchorDate, settings);

        var summary = new CalibrationAssessmentSummary
        {
            PrtgValueBaseline = item1,
            PrtgRuleThresholds = item2,
            TriggeredFetchMagnitude = item3,
            ResidualCredentialThresholds = item4
        };

        lock (CacheLock)
        {
            _cached = summary;
            _cachedAnchor = anchorDate;
            _cachedAt = DateTime.Now;
        }

        return summary;
    }

    /// <summary>清空判定快取（測試用；正式路徑靠 TTL 自然過期）</summary>
    internal static void ClearAssessmentCache()
    {
        lock (CacheLock)
        {
            _cached = null;
            _cachedAnchor = default;
            _cachedAt = default;
        }
    }

    /// <summary>
    /// 組裝校準數值匯出物件（包含檔案摘要與四大資料集）。
    /// </summary>
    public CalibrationExportPackage BuildExportPackage(DateTime? anchor = null)
    {
        var anchorDate = (anchor ?? DateTime.Today).Date;
        var now = DateTime.Now;
        var settings = _settingsStore.Get();
        var allSensors = _prtgStore.GetAllSensors();

        var summary = AssessStatus(anchorDate);

        // 1. 值型基線資料集：最近 56 天 per-sensor 每日聚合
        var valueBaselineFrom = anchorDate.AddDays(-(CalibrationConstants.ValueBaselineWindowDays - 1));
        var valueBaselineToExclusive = anchorDate.AddDays(1);
        var dailyAggs = _prtgStore.GetDailyValueAggregations(valueBaselineFrom, valueBaselineToExclusive);

        var sensorsByObjid = allSensors.ToDictionary(s => s.Objid);
        var (_, hostMapRows) = _prtgStore.GetLatestHostMapWithDate(30, anchorDate);
        var hostByDevice = hostMapRows
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .ToDictionary(m => m.DeviceObjid, m => (HostId: m.HostId!.Value, HostName: m.HostName ?? string.Empty));

        var whitelist = settings.PrtgSensorTypeWhitelist;
        var whitelistSet = (whitelist != null && whitelist.Count > 0)
            ? new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase)
            : null;

        var valueBaselineRows = new List<CalibrationValueBaselineRow>();
        foreach (var agg in dailyAggs)
        {
            if (!sensorsByObjid.TryGetValue(agg.SensorObjid, out var sensor)) continue;
            if (whitelistSet != null && !whitelistSet.Contains(sensor.SensorType)) continue;

            long? hostId = null;
            string? hostName = null;
            if (hostByDevice.TryGetValue(sensor.DeviceObjid, out var hostInfo))
            {
                hostId = hostInfo.HostId;
                hostName = hostInfo.HostName;
            }

            valueBaselineRows.Add(new CalibrationValueBaselineRow(
                agg.SensorObjid,
                sensor.DeviceObjid,
                hostId,
                hostName,
                sensor.SensorType,
                agg.Date,
                agg.AvgValue,
                agg.MinValue,
                agg.MaxValue,
                agg.OkCount,
                agg.UnknownCount,
                agg.NodataCount
            ));
        }

        // 2. 規則門檻資料集：近 56 天每日命中數 ＋ 目前四條規則門檻現值
        var ruleFrom = anchorDate.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1));
        var ruleHits = _issueQuery.AggregatePrtgRuleHits(ruleFrom, anchorDate);

        var allRules = KnownIssueCatalog.ResolveRules(_ruleStore);
        var prtgRules = allRules
            .Where(r => string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.PrtgRuleCode))
            .Select(r => new CalibrationPrtgRuleThresholdInfo(
                r.PrtgRuleCode!,
                r.PrtgThreshold,
                r.Category.ToString(),
                r.Severity.ToString(),
                r.ElevatesDayRisk,
                r.Description
            ))
            .ToList();

        var (magnitudeSamples, magnitudeSummaries) = BuildRuleMagnitudeDistribution(ruleFrom, anchorDate, settings, prtgRules);

        var ruleDataset = new CalibrationRuleThresholdDataset
        {
            DailyRuleHits = ruleHits,
            MagnitudeSamples = magnitudeSamples,
            MagnitudeSummaries = magnitudeSummaries,
            CurrentRules = prtgRules
        };

        // 3. 觸發式量級資料集：近 30 天每日相異 sensor 數與品質列數
        var triggeredFrom = anchorDate.AddDays(-(CalibrationConstants.TriggeredMagnitudeWindowDays - 1));
        var triggeredToExclusive = anchorDate.AddDays(1);
        var triggeredMagnitudes = _prtgStore.GetDailyValueMagnitudes(triggeredFrom, triggeredToExclusive);

        // 4. 殘留判定資料集：候選主機日指標（最多 5000 筆，排除任何帳號文字）
        var residualCandidates = BuildResidualCandidateRows(anchorDate, settings.RawEventRetentionDays);

        return new CalibrationExportPackage
        {
            FormatVersion = CalibrationConstants.CurrentFormatVersion,
            ExportedAt = now,
            Summary = summary,
            ValueBaselines = valueBaselineRows,
            RuleThresholds = ruleDataset,
            TriggeredMagnitudes = triggeredMagnitudes,
            ResidualCandidates = residualCandidates
        };
    }

    private CalibrationItemAssessment AssessPrtgValueBaseline(
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["RequiredHosts"] = CalibrationConstants.ValueBaselineRequiredHosts,
            ["AvailableCoverageDays"] = CalibrationConstants.ValueBaselineAvailableDays,
            ["SufficientCoverageDays"] = CalibrationConstants.ValueBaselineSufficientDays,
            ["MinDailyOkHours"] = CalibrationConstants.ValueBaselineMinDailyOkHours
        };

        if (!settings.PrtgEnabled || allSensors.Count == 0)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "PRTG 值型基線",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["WhitelistedSensors"] = 0,
                    ["MappedHosts"] = 0,
                    ["MaxCoverageDays"] = 0,
                    ["HostsReachingAvailable"] = 0,
                    ["HostsReachingSufficient"] = 0
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string> { "請先在 PRTG 維護頁完成連線設定並於排程作業頁啟用 PRTG 擷取" }
            };
        }

        var whitelist = settings.PrtgSensorTypeWhitelist;
        var whitelistSet = (whitelist != null && whitelist.Count > 0)
            ? new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase)
            : null;

        var whitelistedSensors = whitelistSet != null
            ? allSensors.Where(s => !s.Paused && whitelistSet.Contains(s.SensorType)).ToList()
            : allSensors.Where(s => !s.Paused).ToList();

        if (whitelistedSensors.Count == 0)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "PRTG 值型基線",
                Status = CalibrationStatus.Insufficient,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["WhitelistedSensors"] = 0,
                    ["MappedHosts"] = 0,
                    ["MaxCoverageDays"] = 0,
                    ["HostsReachingAvailable"] = 0,
                    ["HostsReachingSufficient"] = 0
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string> { "sensor type 白名單沒有命中任何 sensor，請到 PRTG 維護頁檢查" }
            };
        }

        var (_, hostMapRows) = _prtgStore.GetLatestHostMapWithDate(30, anchor);
        var okDeviceMap = hostMapRows
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .ToDictionary(m => m.DeviceObjid, m => (HostId: m.HostId!.Value, HostName: m.HostName ?? string.Empty));

        var from = anchor.Date.AddDays(-(CalibrationConstants.ValueBaselineWindowDays - 1));
        var toExclusive = anchor.Date.AddDays(1);
        var dailyAggs = _prtgStore.GetDailyValueAggregations(from, toExclusive);

        // 有效資料的起訖日：回答「這批基線涵蓋哪一段期間」，與涵蓋天數是兩件事
        // （中間可能有斷檔）。白名單內完全沒有數值的 sensor 數同理——
        // 它區分「還沒累積夠」與「這些 sensor 根本沒在取數」。
        var coverage = _prtgStore.GetValueCoverageSummary(from, toExclusive);
        var whitelistedObjids = whitelistedSensors.Select(x => x.Objid).ToHashSet();
        var coverageInScope = coverage.Where(c => whitelistedObjids.Contains(c.SensorObjid)).ToList();
        var earliestOk = coverageInScope
            .Where(c => c.EarliestOkPeriod.HasValue)
            .Select(c => c.EarliestOkPeriod!.Value)
            .DefaultIfEmpty()
            .Min();
        var latestOk = coverageInScope
            .Where(c => c.LatestOkPeriod.HasValue)
            .Select(c => c.LatestOkPeriod!.Value)
            .DefaultIfEmpty()
            .Max();
        var sensorsWithoutValues = whitelistedObjids.Count - coverageInScope.Count(c => c.OkCount > 0);
        // 未對應 sensor：白名單內但所屬 device 沒有成功對應到主機——這些 sensor 就算有數值
        // 也進不了主機層的基線，與「有對應但還沒累積夠」是不同的問題，補充說明的方向也不同
        var unmappedSensors = whitelistedSensors.Count(x => !okDeviceMap.ContainsKey(x.DeviceObjid));

        var sensorCoverageDays = dailyAggs
            .Where(a => a.OkCount >= CalibrationConstants.ValueBaselineMinDailyOkHours)
            .GroupBy(a => a.SensorObjid)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Date.Date).Distinct().Count());

        var hostCoverageDays = new Dictionary<long, int>();
        foreach (var sensor in whitelistedSensors)
        {
            if (!okDeviceMap.TryGetValue(sensor.DeviceObjid, out var hostInfo)) continue;
            var hostId = hostInfo.HostId;
            var sDays = sensorCoverageDays.GetValueOrDefault(sensor.Objid, 0);
            if (!hostCoverageDays.TryGetValue(hostId, out var currentMax) || sDays > currentMax)
            {
                hostCoverageDays[hostId] = sDays;
            }
        }

        var mappedHostsCount = hostCoverageDays.Count;
        var maxCoverageDays = hostCoverageDays.Values.DefaultIfEmpty(0).Max();
        var hostsAvailable = hostCoverageDays.Values.Count(d => d >= CalibrationConstants.ValueBaselineAvailableDays);
        var hostsSufficient = hostCoverageDays.Values.Count(d => d >= CalibrationConstants.ValueBaselineSufficientDays);

        // 分母為零一律判不足：若無任何主機或涵蓋天數為 0，不得判為可用
        CalibrationStatus status;
        var valueBaselineNoData = mappedHostsCount == 0 || maxCoverageDays == 0;
        if (valueBaselineNoData)
        {
            status = CalibrationStatus.Insufficient;
        }
        else if (hostsSufficient >= CalibrationConstants.ValueBaselineRequiredHosts)
        {
            status = CalibrationStatus.Sufficient;
        }
        else if (hostsAvailable >= CalibrationConstants.ValueBaselineRequiredHosts)
        {
            status = CalibrationStatus.Available;
        }
        else
        {
            status = CalibrationStatus.Insufficient;
        }

        var explanations = new List<string>();
        if (valueBaselineNoData)
        {
            explanations.Add("期間無事件，門檻無從校準，維持預設值");
        }
        if (settings.PrtgRetentionDays < CalibrationConstants.ValueBaselineSufficientDays)
        {
            explanations.Add($"PRTG 資料保留天數目前為 {settings.PrtgRetentionDays} 天，小於充足所需的 {CalibrationConstants.ValueBaselineSufficientDays} 天，資料會在累積足夠前被清掉，請調高");
        }

        if (status == CalibrationStatus.Insufficient)
        {
            if (mappedHostsCount < CalibrationConstants.ValueBaselineRequiredHosts)
            {
                explanations.Add($"目前只有 {mappedHostsCount} 台主機有基線資料，需要 {CalibrationConstants.ValueBaselineRequiredHosts} 台；可對特定主機執行歷史回填");
            }
            if (maxCoverageDays < CalibrationConstants.ValueBaselineAvailableDays)
            {
                var needed = CalibrationConstants.ValueBaselineAvailableDays - maxCoverageDays;
                explanations.Add($"目前最長涵蓋 {maxCoverageDays} 天，還需要約 {needed} 天");
            }
        }
        else if (status == CalibrationStatus.Available)
        {
            if (hostsSufficient < CalibrationConstants.ValueBaselineRequiredHosts)
            {
                if (maxCoverageDays < CalibrationConstants.ValueBaselineSufficientDays)
                {
                    var needed = CalibrationConstants.ValueBaselineSufficientDays - maxCoverageDays;
                    explanations.Add($"目前最長涵蓋 {maxCoverageDays} 天，還需要約 {needed} 天");
                }
                else
                {
                    explanations.Add($"目前只有 {hostsSufficient} 台主機達到充足標準（56 天），需要 {CalibrationConstants.ValueBaselineRequiredHosts} 台；可對特定主機執行歷史回填");
                }
            }
        }

        return new CalibrationItemAssessment
        {
            ItemName = "PRTG 值型基線",
            Status = status,
            KeyMetrics = new Dictionary<string, object>
            {
                ["WhitelistedSensors"] = whitelistedSensors.Count,
                ["MappedHosts"] = mappedHostsCount,
                ["MaxCoverageDays"] = maxCoverageDays,
                ["HostsReachingAvailable"] = hostsAvailable,
                ["HostsReachingSufficient"] = hostsSufficient,
                ["EarliestOkDate"] = earliestOk == default ? "—" : earliestOk.ToString("yyyy-MM-dd"),
                ["LatestOkDate"] = latestOk == default ? "—" : latestOk.ToString("yyyy-MM-dd"),
                ["SensorsWithoutValues"] = sensorsWithoutValues,
                ["UnmappedSensors"] = unmappedSensors
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private CalibrationItemAssessment AssessPrtgRuleThresholds(
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["AvailableCoverageDays"] = CalibrationConstants.RuleThresholdAvailableDays,
            ["AvailableHits"] = CalibrationConstants.RuleThresholdAvailableHits,
            ["SufficientCoverageDays"] = CalibrationConstants.RuleThresholdSufficientDays,
            ["SufficientHits"] = CalibrationConstants.RuleThresholdSufficientHits
        };

        if (!settings.PrtgEnabled || allSensors.Count == 0)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "PRTG 規則門檻",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["DistinctCoverageDays"] = 0,
                    ["TotalRuleHits"] = 0,
                    ["DownSensorDays"] = 0,
                    ["FlappingSensorDays"] = 0,
                    ["WarningSensorDays"] = 0,
                    ["SilentDeviceDays"] = 0
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string> { "請先在 PRTG 維護頁完成連線設定並於排程作業頁啟用 PRTG 擷取" }
            };
        }

        var from = anchor.Date.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1));
        var toExclusive = anchor.Date.AddDays(1);
        var stateSummary = _prtgStore.GetStateChangeCoverageSummary(from, toExclusive);
        var distinctDates = stateSummary.DistinctDates;

        var ruleHits = _issueQuery.AggregatePrtgRuleHits(from, anchor.Date);
        var totalHits = ruleHits.Sum(h => h.HitCount);

        // 逐規則的 sensor-日數：門檻校準是逐規則進行的，四條加總會讓「down 只有 3 筆但
        // flapping 有 100 筆」被誤判成資料充足，而 down 的門檻仍然無從校準。
        int HitsOf(string ruleCode) => ruleHits
            .Where(h => string.Equals(h.RuleCode, ruleCode, StringComparison.OrdinalIgnoreCase))
            .Sum(h => h.HitCount);

        var downHits = HitsOf(PrtgRuleEvaluator.RuleDown);
        var flappingHits = HitsOf(PrtgRuleEvaluator.RuleFlapping);
        var warningHits = HitsOf(PrtgRuleEvaluator.RuleWarning);
        var silentHits = HitsOf(PrtgRuleEvaluator.RuleSilent);

        // 分母為零一律判不足：若期間內變更涵蓋天數或命中筆數為 0，不得判為可用
        CalibrationStatus status;
        var ruleNoData = distinctDates == 0 || totalHits == 0;
        if (ruleNoData)
        {
            status = CalibrationStatus.Insufficient;
        }
        else if (distinctDates >= CalibrationConstants.RuleThresholdSufficientDays &&
                 downHits >= CalibrationConstants.RuleThresholdSufficientHits)
        {
            status = CalibrationStatus.Sufficient;
        }
        else if (distinctDates >= CalibrationConstants.RuleThresholdAvailableDays &&
                 downHits >= CalibrationConstants.RuleThresholdAvailableHits)
        {
            status = CalibrationStatus.Available;
        }
        else
        {
            status = CalibrationStatus.Insufficient;
        }

        var explanations = new List<string>();
        if (ruleNoData)
        {
            explanations.Add("期間無事件，門檻無從校準，維持預設值");
        }
        if (settings.PrtgRetentionDays < CalibrationConstants.RuleThresholdSufficientDays)
        {
            explanations.Add($"PRTG 資料保留天數目前為 {settings.PrtgRetentionDays} 天，小於充足所需的 {CalibrationConstants.RuleThresholdSufficientDays} 天，資料會在累積足夠前被清掉，請調高");
        }

        if (status == CalibrationStatus.Insufficient)
        {
            if (distinctDates < CalibrationConstants.RuleThresholdAvailableDays)
            {
                var needed = CalibrationConstants.RuleThresholdAvailableDays - distinctDates;
                explanations.Add($"目前最長涵蓋 {distinctDates} 天，還需要約 {needed} 天");
            }
            if (downHits < CalibrationConstants.RuleThresholdAvailableHits)
            {
                explanations.Add($"目前 down 規則命中 {downHits} 筆（四條規則合計 {totalHits} 筆），需要 {CalibrationConstants.RuleThresholdAvailableHits} 筆");
            }
        }
        else if (status == CalibrationStatus.Available)
        {
            if (distinctDates < CalibrationConstants.RuleThresholdSufficientDays)
            {
                var needed = CalibrationConstants.RuleThresholdSufficientDays - distinctDates;
                explanations.Add($"目前最長涵蓋 {distinctDates} 天，還需要約 {needed} 天");
            }
            if (downHits < CalibrationConstants.RuleThresholdSufficientHits)
            {
                explanations.Add($"目前 down 規則命中 {downHits} 筆（四條規則合計 {totalHits} 筆），充足需要 {CalibrationConstants.RuleThresholdSufficientHits} 筆");
            }
        }

        return new CalibrationItemAssessment
        {
            ItemName = "PRTG 規則門檻",
            Status = status,
            KeyMetrics = new Dictionary<string, object>
            {
                ["DistinctCoverageDays"] = distinctDates,
                ["TotalRuleHits"] = totalHits,
                ["DownSensorDays"] = downHits,
                ["FlappingSensorDays"] = flappingHits,
                ["WarningSensorDays"] = warningHits,
                ["SilentDeviceDays"] = silentHits
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private CalibrationItemAssessment AssessTriggeredFetchMagnitude(
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["AvailableDays"] = CalibrationConstants.TriggeredMagnitudeAvailableDays,
            ["SufficientDays"] = CalibrationConstants.TriggeredMagnitudeSufficientDays,
            ["WindowDays"] = CalibrationConstants.TriggeredMagnitudeWindowDays
        };

        if (!settings.PrtgEnabled || allSensors.Count == 0)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "觸發式取數量級",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["DaysWithValues"] = 0,
                    ["WindowDays"] = CalibrationConstants.TriggeredMagnitudeWindowDays
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string> { "請先在 PRTG 維護頁完成連線設定並於排程作業頁啟用 PRTG 擷取" }
            };
        }

        var from = anchor.Date.AddDays(-(CalibrationConstants.TriggeredMagnitudeWindowDays - 1));
        var toExclusive = anchor.Date.AddDays(1);
        var magnitudes = _prtgStore.GetDailyValueMagnitudes(from, toExclusive);
        var withValues = magnitudes.Where(m => m.TotalCount > 0).ToList();
        var daysWithValues = withValues.Count;

        // 每晚量級的散布：只看「有幾晚」無法判斷取數規模是否穩定
        // （14 晚每晚 3 個 sensor 與 14 晚每晚 800 個，對校準的意義完全不同）
        static int Median(List<int> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        var sensorCounts = withValues.Select(m => m.SensorCount).ToList();
        var rowCounts = withValues.Select(m => m.TotalCount).ToList();
        var okTotal = withValues.Sum(m => m.OkCount);
        var rowsTotal = rowCounts.Sum();
        var okRatio = rowsTotal > 0 ? (double)okTotal / rowsTotal : 0.0;

        // 分母為零一律判不足：若有數值天數為 0，不得判為可用
        CalibrationStatus status;
        var triggeredNoData = daysWithValues == 0;
        if (triggeredNoData)
        {
            status = CalibrationStatus.Insufficient;
        }
        else if (daysWithValues >= CalibrationConstants.TriggeredMagnitudeSufficientDays)
        {
            status = CalibrationStatus.Sufficient;
        }
        else if (daysWithValues >= CalibrationConstants.TriggeredMagnitudeAvailableDays)
        {
            status = CalibrationStatus.Available;
        }
        else
        {
            status = CalibrationStatus.Insufficient;
        }

        var explanations = new List<string>();
        if (triggeredNoData)
        {
            explanations.Add("期間無事件，門檻無從校準，維持預設值");
        }
        if (settings.PrtgRetentionDays < CalibrationConstants.TriggeredMagnitudeSufficientDays)
        {
            explanations.Add($"PRTG 資料保留天數目前為 {settings.PrtgRetentionDays} 天，小於充足所需的 {CalibrationConstants.TriggeredMagnitudeSufficientDays} 天，資料會在累積足夠前被清掉，請調高");
        }

        if (status == CalibrationStatus.Insufficient)
        {
            var needed = CalibrationConstants.TriggeredMagnitudeAvailableDays - daysWithValues;
            explanations.Add($"目前最長涵蓋 {daysWithValues} 天，還需要約 {needed} 天");
        }
        else if (status == CalibrationStatus.Available)
        {
            var needed = CalibrationConstants.TriggeredMagnitudeSufficientDays - daysWithValues;
            explanations.Add($"目前最長涵蓋 {daysWithValues} 天，還需要約 {needed} 天");
        }

        return new CalibrationItemAssessment
        {
            ItemName = "觸發式取數量級",
            Status = status,
            KeyMetrics = new Dictionary<string, object>
            {
                ["DaysWithValues"] = daysWithValues,
                ["WindowDays"] = CalibrationConstants.TriggeredMagnitudeWindowDays,
                ["SensorsPerNightMin"] = sensorCounts.DefaultIfEmpty(0).Min(),
                ["SensorsPerNightMedian"] = Median(sensorCounts),
                ["SensorsPerNightMax"] = sensorCounts.DefaultIfEmpty(0).Max(),
                ["RowsPerNightMin"] = rowCounts.DefaultIfEmpty(0).Min(),
                ["RowsPerNightMedian"] = Median(rowCounts),
                ["RowsPerNightMax"] = rowCounts.DefaultIfEmpty(0).Max(),
                ["OkRatio"] = Math.Round(okRatio, 3)
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private CalibrationItemAssessment AssessResidualCredentialThresholds(
        DateTime anchor, SystemSettings settings)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["AvailableHostDays"] = CalibrationConstants.ResidualAvailableHostDays,
            ["AvailableCoverageDays"] = CalibrationConstants.ResidualAvailableDays,
            ["SufficientHostDays"] = CalibrationConstants.ResidualSufficientHostDays,
            ["SufficientCoverageDays"] = CalibrationConstants.ResidualSufficientDays
        };

        using var ctx = _contextFactory();

        var cutoff = anchor.Date.AddDays(-settings.RawEventRetentionDays);

        var candidateRows = CandidateRecordsQuery(ctx, cutoff, anchor.Date)
            .Select(r => new { r.HostId, r.RecordDate })
            .ToList()
            .Select(r => (r.HostId, Date: r.RecordDate.Date))
            .Distinct()
            .ToList();

        // 註：這裡的候選是「有登入失敗簽章且未精簡」的主機日，與匯出端同一個查詢。
        // 匯出端還會逐筆反序列化後再要求 LoginFailureDetails 非空（明細真的存在），
        // 因此匯出筆數可能少於這裡的計數——差額即「有簽章但明細已不在」的舊資料。

        var candidateHostDays = candidateRows.Count;
        var distinctDays = candidateRows.Select(r => r.Date).Distinct().Count();

        if (candidateHostDays == 0)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "殘留判定門檻",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["CandidateHostDays"] = 0,
                    ["DistinctCoverageDays"] = 0
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string>
                {
                    "請確認 4625／4771 與 Linux 登入失敗規則為啟用狀態、分析排程正常執行"
                }
            };
        }

        // 分母為零一律判不足：若候選主機日數或相異天數為 0，不得判為可用
        CalibrationStatus status;
        if (candidateHostDays >= CalibrationConstants.ResidualSufficientHostDays &&
            distinctDays >= CalibrationConstants.ResidualSufficientDays)
        {
            status = CalibrationStatus.Sufficient;
        }
        else if (candidateHostDays >= CalibrationConstants.ResidualAvailableHostDays &&
                 distinctDays >= CalibrationConstants.ResidualAvailableDays)
        {
            status = CalibrationStatus.Available;
        }
        else
        {
            status = CalibrationStatus.Insufficient;
        }

        var explanations = new List<string>();
        if (settings.RawEventRetentionDays < CalibrationConstants.ResidualSufficientDays)
        {
            explanations.Add($"原始事件內容保留天數目前為 {settings.RawEventRetentionDays} 天，小於充足所需的 {CalibrationConstants.ResidualSufficientDays} 天，資料會在累積足夠前被清掉，請調高");
        }

        if (status == CalibrationStatus.Insufficient)
        {
            if (candidateHostDays < CalibrationConstants.ResidualAvailableHostDays)
            {
                explanations.Add("請確認 4625／4771 與 Linux 登入失敗規則為啟用狀態、分析排程正常執行");
                explanations.Add($"目前只有 {candidateHostDays} 個候選主機日，需要 {CalibrationConstants.ResidualAvailableHostDays} 個；可擴大分析天數或等待資料累積");
            }
            if (distinctDays < CalibrationConstants.ResidualAvailableDays)
            {
                var needed = CalibrationConstants.ResidualAvailableDays - distinctDays;
                explanations.Add($"目前最長涵蓋 {distinctDays} 天，還需要約 {needed} 天");
            }
        }
        else if (status == CalibrationStatus.Available)
        {
            if (candidateHostDays < CalibrationConstants.ResidualSufficientHostDays)
            {
                explanations.Add($"目前有 {candidateHostDays} 個候選主機日，充足需要 {CalibrationConstants.ResidualSufficientHostDays} 個");
            }
            if (distinctDays < CalibrationConstants.ResidualSufficientDays)
            {
                var needed = CalibrationConstants.ResidualSufficientDays - distinctDays;
                explanations.Add($"目前最長涵蓋 {distinctDays} 天，還需要約 {needed} 天");
            }
        }

        // 截斷比例與命中數要逐筆算指標才知道，與匯出資料集同一份來源（受同一個上限保護）。
        // 這兩個數字是校準的重點之一：截斷比例高代表明細封頂在拖累判定，
        // 命中數則回答「現行門檻下實際命中多少」。
        var sampledRows = BuildResidualCandidateRows(anchor, settings.RawEventRetentionDays);
        var truncatedRatio = sampledRows.Count > 0
            ? Math.Round((double)sampledRows.Count(r => r.IsTruncated) / sampledRows.Count, 3)
            : 0.0;
        var matchedCount = sampledRows.Count(r => r.IsMatch);

        return new CalibrationItemAssessment
        {
            ItemName = "殘留判定門檻",
            Status = status,
            KeyMetrics = new Dictionary<string, object>
            {
                ["CandidateHostDays"] = candidateHostDays,
                ["DistinctCoverageDays"] = distinctDays,
                ["SampledCandidates"] = sampledRows.Count,
                ["TruncatedRatio"] = truncatedRatio,
                ["MatchedCount"] = matchedCount
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private List<CalibrationResidualCandidateRow> BuildResidualCandidateRows(
        DateTime anchor, int rawEventRetentionDays)
    {
        using var ctx = _contextFactory();

        var cutoff = anchor.Date.AddDays(-rawEventRetentionDays);

        var candidateRuleIds = LinuxAuthParser.LoginFailureRuleIds;

        var candidateDailyRows = CandidateRecordsQuery(ctx, cutoff, anchor.Date)
            .OrderByDescending(r => r.RecordDate)
            .ThenByDescending(r => r.RecordId)
            .Take(CalibrationConstants.ResidualCandidateMaxCount)
            .Select(r => new { r.HostId, r.HostName, r.RecordDate, r.ContentJson })
            .ToList();

        var result = new List<CalibrationResidualCandidateRow>();
        foreach (var row in candidateDailyRows)
        {
            if (string.IsNullOrWhiteSpace(row.ContentJson)) continue;

            DailyAnalysisRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<DailyAnalysisRecord>(row.ContentJson);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[校準] 反序列化主機 {Host} 日期 {Date:yyyy-MM-dd} 失敗，略過此筆", row.HostName, row.RecordDate);
                continue;
            }

            if (record?.TopIssues == null || record.TopIssues.Count == 0) continue;

            foreach (var issue in record.TopIssues)
            {
                bool isCandidate = (issue.EventId == 4625 || issue.EventId == 4771) ||
                                   (string.Equals(issue.LogName, "Linux", StringComparison.OrdinalIgnoreCase) && candidateRuleIds.Contains(issue.EventKey));

                if (!isCandidate || issue.LoginFailureDetails == null || issue.LoginFailureDetails.Count == 0)
                {
                    continue;
                }

                // history 必須傳真的：條件 4（跨日重現）在 history 為 null 時直接判否，
                // 傳 null 會讓匯出檔的「是否命中」整欄恆為 false——而校準要看的正是
                // 「現行門檻下實際命中多少」，恆 false 等於這份資料集廢掉
                var history = HistoryForCandidate(ctx, row.HostId, record.Date);
                var metrics = ResidualCredentialDetector.EvaluateMetrics(issue, history, record.Date);
                if (metrics == null) continue;

                // 嚴格排除任何使用者帳號與來源明細文字，僅輸出結構統計指標
                result.Add(new CalibrationResidualCandidateRow(
                    record.HostId,
                    record.Host,
                    record.Date,
                    metrics.EventId,
                    issue.LoginFailureDetails.Count,
                    metrics.TotalDetailCount,
                    metrics.ConcentrationRatio,
                    metrics.MechanicalLogonTypeRatio,
                    metrics.SingleGroupRatio,
                    metrics.CrossDayDistinctDays,
                    metrics.IsTruncated,
                    metrics.IsMatch
                ));

                if (result.Count >= CalibrationConstants.ResidualCandidateMaxCount)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 取得某主機日往前的歷史紀錄，供殘留判定的條件 4（跨日重現）比對。
    /// 窗長與判定端一致（<c>ResidualCredentialDetector</c> 的回看天數），
    /// 只取同一台主機——判定本來就是逐主機看「同一組帳號來源是否跨日重現」。
    /// </summary>
    private static List<DailyAnalysisRecord> HistoryForCandidate(LfDbContext ctx, long hostId, DateTime targetDate)
    {
        var from = targetDate.Date.AddDays(-ResidualCredentialDetector.HistoryWindowDaysForCalibration);

        var rows = ctx.DailyRecords.AsNoTracking()
            .Where(r => r.HostId == hostId && !r.DetailPruned &&
                        r.RecordDate >= from && r.RecordDate < targetDate.Date)
            .Select(r => r.ContentJson)
            .ToList();

        var history = new List<DailyAnalysisRecord>();
        foreach (var json in rows)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var rec = JsonSerializer.Deserialize<DailyAnalysisRecord>(json);
                if (rec != null) history.Add(rec);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "[校準] 歷史紀錄反序列化失敗，略過此筆");
            }
        }

        return history;
    }

    /// <summary>
    /// 殘留判定的候選紀錄查詢（判定與匯出共用同一份，兩邊口徑必須一致）。
    ///
    /// 候選＝**含登入失敗明細**且未精簡、且落在詳情保留期內的主機日：
    /// Windows 為 EventId 4625／4771，Linux 為 LogName=Linux 且 EventKey 屬於登入失敗規則
    /// （見 <c>LogAggregator.ExtractLoginFailureDetailsForGroup</c>——只有這些簽章會產生明細）。
    ///
    /// 以 EXISTS 子查詢表達而不是「先撈一份 RecordId 集合再 Contains」：
    /// 後者在正式機會把數十萬個 id 載進記憶體並展開成巨大的 IN 清單，
    /// SQLite 逼近變數上限、SQL Server 撞參數上限，而測試資料量小永遠看不到。
    /// 日期過濾也必須下推到這一層，否則等於全表掃描。
    /// </summary>
    private static IQueryable<DailyRecordRow> CandidateRecordsQuery(LfDbContext ctx, DateTime cutoff, DateTime anchorDate)
    {
        var candidateRuleIds = LinuxAuthParser.LoginFailureRuleIds;

        return ctx.DailyRecords.AsNoTracking()
            .Where(r => !r.DetailPruned && r.RecordDate >= cutoff && r.RecordDate <= anchorDate)
            .Where(r => ctx.TopIssues.Any(t => t.RecordId == r.RecordId &&
                ((t.EventId == 4625 || t.EventId == 4771) ||
                 (t.LogName == "Linux" && candidateRuleIds.Contains(t.EventKey)))));
    }

    /// <summary>
    /// 以最低門檻逐日評估，取得每 sensor-日 finding 的 magnitude 分佈。
    ///
    /// 為什麼要最低門檻：現行門檻下的命中數只回答「照目前設定會報幾次」。
    /// 要決定門檻該設多少，必須看底層分佈——有多少 sensor-日的 Down 持續 30 分鐘、
    /// 多少持續 120 分鐘。門檻設 1（不是 0）：flap 與 warning 的判定是 `>=`，
    /// 設 0 會讓「當日零事件」的 sensor 也被算成命中，分母整個爛掉。
    ///
    /// sensor 母體與夜間批次一致（白名單過濾），且比照 PRTG-SPEC §9 回查前一日的變更
    /// ——`down` 與 `warning` 要靠前一日最後一筆推導當日零時的起始狀態。
    /// </summary>
    private (List<CalibrationRuleMagnitudeRow> Samples, List<CalibrationRuleMagnitudeSummary> Summaries)
        BuildRuleMagnitudeDistribution(
            DateTime from,
            DateTime toInclusive,
            SystemSettings settings,
            List<CalibrationPrtgRuleThresholdInfo> currentRules)
    {
        var samples = new List<CalibrationRuleMagnitudeRow>();

        var whitelist = new HashSet<string>(
            settings.PrtgSensorTypeWhitelist ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var allSensors = _prtgStore.GetSensorStatuses();
        var filteredSensors = whitelist.Count == 0
            ? allSensors
            : allSensors.Where(s => whitelist.Contains(s.SensorType)).ToList();

        if (filteredSensors.Count == 0)
        {
            return (samples, new List<CalibrationRuleMagnitudeSummary>());
        }

        var allowedSensorObjids = filteredSensors.Select(s => s.Objid).ToHashSet();
        var sensorToDevice = filteredSensors
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().DeviceObjid);
        var sensorStatuses = filteredSensors
            .Select(s => (s.Objid, s.DeviceObjid, s.Status))
            .ToList();

        // 最低門檻：任何有事件的 sensor-日都納入，得到的是全量分佈
        var minimumThresholds = new PrtgRuleThresholds(DownMinutes: 1, FlapCount: 1, WarningMinutes: 1);
        var allRuleCodes = new HashSet<string>(
            new[] { PrtgRuleEvaluator.RuleDown, PrtgRuleEvaluator.RuleFlapping, PrtgRuleEvaluator.RuleWarning },
            StringComparer.OrdinalIgnoreCase);

        for (var day = from.Date; day <= toInclusive.Date; day = day.AddDays(1))
        {
            var allChanges = _prtgStore.GetStateChanges(day.AddDays(-1), day.AddDays(1));
            var changes = allChanges.Where(c => allowedSensorObjids.Contains(c.SensorObjid)).ToList();
            if (changes.Count == 0) continue;

            // silent 不納入：它的判定來源是 sensor 現況（非變更表），逐日重放會讓
            // 每一天都拿到「今天的現況」而全部相同，那不是分佈、是同一個數字複製 N 份
            var findings = PrtgRuleEvaluator.Evaluate(
                day, changes, sensorToDevice, sensorStatuses, minimumThresholds, allRuleCodes);

            foreach (var f in findings)
            {
                samples.Add(new CalibrationRuleMagnitudeRow(f.RuleCode, day, f.DeviceObjid, f.SensorObjid, f.Magnitude));
            }
        }

        var thresholdByRule = currentRules.ToDictionary(
            r => r.RuleCode, r => r.Threshold, StringComparer.OrdinalIgnoreCase);

        var summaries = samples
            .GroupBy(r => r.RuleCode, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var sorted = g.Select(r => r.Magnitude).OrderBy(v => v).ToList();
                var current = thresholdByRule.TryGetValue(g.Key, out var t) ? t : 0;
                return new CalibrationRuleMagnitudeSummary(
                    g.Key,
                    sorted.Count,
                    sorted[0],
                    Percentile(sorted, 0.50),
                    Percentile(sorted, 0.90),
                    Percentile(sorted, 0.99),
                    sorted[^1],
                    sorted.Count(v => v >= current),
                    current);
            })
            .OrderBy(r => r.RuleCode, StringComparer.Ordinal)
            .ToList();

        return (samples, summaries);
    }

    /// <summary>最近秩位法（nearest-rank）：第 ceil(p × N) 個值。樣本為空時由呼叫端保證不會進來。</summary>
    private static int Percentile(List<int> sortedValues, double p)
    {
        var rank = (int)Math.Ceiling(p * sortedValues.Count);
        if (rank < 1) rank = 1;
        if (rank > sortedValues.Count) rank = sortedValues.Count;
        return sortedValues[rank - 1];
    }
}
