using System.Text.Json;
using System.Data;
using System.Data.Common;
using LogForesight.Core.Analysis;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
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
    public const int CurrentFormatVersion = 3;

    // ── 1. PRTG 值型基線門檻 ──────────────────────────────────────────
    /// <summary>值型基線所需最少主機數</summary>
    public const int ValueBaselineRequiredHosts = 10;
    /// <summary>值型基線「可用」所需最少涵蓋天數</summary>
    public const int ValueBaselineAvailableDays = 28;
    /// <summary>值型基線「充足」所需最少涵蓋天數</summary>
    public const int ValueBaselineSufficientDays = 56;
    /// <summary>單日視為有效涵蓋的可用小時數下限</summary>
    public const int ValueBaselineMinDailyUsableHours = 12;
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

    // ── 3. 數值取得量級門檻 ────────────────────────────────────────
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
    public const int ResidualCandidateJsonMaxCharacters = 1 * 1024 * 1024;
    public const int ResidualCandidateJsonMaxBytes = 32 * 1024 * 1024;
    public const int ResidualHistoryMaxRows = 250_000;
    public const int ResidualHistoryJsonMaxCharacters = 1 * 1024 * 1024;
    public const int ResidualHistoryObjectGraphBudgetBytes = 256 * 1024 * 1024;
    public const int ResidualDatabasePageRows = 500;
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
    int NodataCount,
    int SampledHours,
    double? MinObserved,
    double? MaxObserved);

/// <summary>
/// PRTG 規則門檻與現況命中資料集（匯出用）
/// </summary>
public sealed class CalibrationRuleThresholdDataset
{
    public List<PrtgRuleHitAggregate> DailyRuleHits { get; init; } = new();
    public List<CalibrationPrtgRuleThresholdInfo> CurrentRules { get; init; } = new();
    public int MagnitudeSemanticsVersion { get; init; } = 2;
    public string MagnitudeBasis { get; init; } = "trusted-covered-timeline-v1; Down Magnitude/ThresholdMagnitude is continuous trusted episode minutes and DayMagnitude is daily overlap minutes; Warning is daily cumulative minutes; Flapping is daily Down-to-Up transitions.";
    public string FormalEvaluationScope { get; init; } = "trusted-status-timeline-only";
    public List<string> FormalEvaluationSupportedRuleCodes { get; init; } = new()
    {
        PrtgRuleEvaluator.RuleDown,
        PrtgRuleEvaluator.RuleFlapping,
        PrtgRuleEvaluator.RuleWarning
    };
    public List<string> UnsupportedByMagnitudeAnalysisRuleCodes { get; init; } = new();
    public string EvidenceFingerprint { get; init; } = string.Empty;
    public DateTimeOffset AsOf { get; init; }
    public List<string> Explanations { get; init; } = new();
    public List<CalibrationFormalRuleHitCount> FormalCurrentHitCounts { get; init; } = new();

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
    int Magnitude)
{
    public int? ThresholdMagnitude { get; init; }
    public int? DayMagnitude { get; init; }
}

/// <summary>以同一可信 timeline/as-of 和正式門檻 evaluator 重算 down/flapping/warning 的命中筆數，按規則識別分列。</summary>
public sealed record CalibrationFormalRuleHitCount(string RuleCode, string RuleId, string? SensorCategory,
    int Threshold, int FindingCount);

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
    int? Threshold,
    string Category,
    string Severity,
    bool ElevatesDayRisk,
    string Description,
    string? SensorCategory,
    string ThresholdKind,
    bool RuleEnabled);

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
/// 每顆感測器的分佈摘要（匯出用）
/// </summary>
public sealed record CalibrationValueSensorSummary(
    long SensorObjid,
    string? HostName,
    string SensorType,
    string? Unit,               // 取自鏡像 PrtgSensorRow.Unit
    bool IsVolumeNormalized,    // PrtgVolumeSensorTypes.IsVolume(SensorType)
    int Days,                   // UsableCount > 0 的日數
    int UsableHours,            // UsableCount 總和
    double SampledRatio,        // SampledCount 總和 ÷ UsableCount 總和（分母 0 時為 0），Math.Round(..., 3)
    double? Mean,
    double? StdDev,
    double? P50,
    double? P90,
    double? P99,
    double? Max);

/// <summary>
/// 每種感測器類型的分佈與小時曲線（匯出用）
/// </summary>
public sealed record CalibrationValueTypeProfile(
    string SensorType,
    int SensorCount,            // 該 type 在 ValueSensorSummaries 中的感測器數
    int UsableHours,            // 該 type 的 UsableHours 總和
    double? DailyP50,           // 該 type 所有感測器的每日平均值合在一起的分位數
    double? DailyP90,
    double? DailyP99,
    double? DailyMax,
    double?[] HourlyCurve);     // 長度固定 24，索引＝小時；取自 GetUsableHourlyProfileByType，沒資料的小時為 null

/// <summary>
/// 校準匯出上下文環境與口徑說明（匯出用）
/// </summary>
public sealed record CalibrationExportContext(
    string Detail,                          // "full" 或 "summary"
    string FetchStrategy,                   // 設定的取數策略字串
    int SnapshotIntervalMinutes,            // PrtgFetchStrategy.Profile(策略).SnapshotIntervalMinutes
    int SnapshotTargets,
    IReadOnlyList<string> SensorTypeWhitelist,
    int PrtgRetentionDays,
    DateTime WindowFrom,
    DateTime WindowToExclusive,
    int MirrorDeviceCount,                  // GetMirrorSummary().DeviceCount
    int MirrorSensorCount,                  // GetMirrorSummary().SensorCount
    double SampledMinCoverage,              // PrtgValueUsability.SampledMinCoverage
    string StatisticsBasis)
{
    public CalibrationExportContext() : this(
        "full",
        string.Empty,
        0,
        0,
        Array.Empty<string>(),
        0,
        default,
        default,
        0,
        0,
        PrtgValueUsability.SampledMinCoverage,
        string.Empty)
    {
    }
}

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
    public List<CalibrationValueSensorSummary> ValueSensorSummaries { get; init; } = new();
    public List<CalibrationValueTypeProfile> ValueTypeProfiles { get; init; } = new();
    public CalibrationExportContext Context { get; init; } = new();
}

/// <summary>
/// 校準狀態判定與匯出檔組裝服務（A3 服務層）
/// </summary>
public sealed class CalibrationService
{
    private static readonly SemaphoreSlim CalibrationCaptureAdmission = new(1, 1);
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int MaxPolicySensorsForCalibration = 15000;
    private const int CalibrationSensorTypeMaxChars = 128;
    private const int CalibrationSensorStatusMaxChars = 64;
    private const int CalibrationCategoryMaxChars = 64;
    private const int CalibrationMapStatusMaxChars = 16;
    private const int MaxTimelineBlobRowsForCalibration = 30000;
    private const int MaxPolicyBlobCharactersForCalibration = 4 * 1024 * 1024;
    private const int MaxTimelineBlobCharactersForCalibration = 4 * 1024 * 1024;
    private const long MaxTimelineCaptureBytesForCalibration = 128L * 1024 * 1024;
    private const int MaxMapRowsForCalibration = 250000;
    private const int MaxMagnitudeSamplesForCalibration = 100000;

    private readonly Func<LfDbContext> _contextFactory;
    private readonly EfPrtgStore _prtgStore;
    private readonly IIssueAggregateQuery _issueQuery;
    private readonly ISystemSettingsStore _settingsStore;
    private readonly IKnownIssueRuleStore _ruleStore;
    private readonly string _backendCacheIdentity;
    private readonly PrtgCalibrationCaptureLimits _captureLimits;

    public CalibrationService(
        Func<LfDbContext> contextFactory,
        EfPrtgStore prtgStore,
        IIssueAggregateQuery issueQuery,
        ISystemSettingsStore settingsStore,
        IKnownIssueRuleStore ruleStore,
        PrtgCalibrationCaptureLimits? captureLimits = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _prtgStore = prtgStore ?? throw new ArgumentNullException(nameof(prtgStore));
        _issueQuery = issueQuery ?? throw new ArgumentNullException(nameof(issueQuery));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _captureLimits = captureLimits ?? EfPrtgStore.DefaultCalibrationCaptureLimits;
        using (var context = _contextFactory())
        {
            var connection = context.Database.GetDbConnection();
            var identity = $"{context.Database.ProviderName}|{connection.ConnectionString}";
            if (connection.ConnectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
                identity += $"|memory:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(connection)}";
            _backendCacheIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        }
    }

    /// <summary>判定結果的行程內快取（見 <see cref="AssessStatus"/>）</summary>
    private static readonly object CacheLock = new();
    private static DateTime _cachedAnchor;
    private static DateTime _cachedAt;
    private static CalibrationAssessmentSummary? _cached;
    private static string _cachedAssessmentKey = string.Empty;
    private List<CalibrationResidualCandidateRow>? _capturedResidualRows;

    private readonly object _ruleMagnitudeLock = new();
    private RuleMagnitudeAnalysis? _cachedRuleMagnitudeAnalysis;

    private sealed record RuleMagnitudeAnalysis(string SourceFingerprint, string Fingerprint, string RuleStamp,
        long HostMapDataRevision, long PolicyVersion, long ScopeVersion, long[] PolicySensorIds,
        string ResourceIdentityStamp, string ResourceIdentityVersionStamp, string TimelineStamp,
        string SensorStamp, string SettingsStamp, DateTimeOffset AsOf,
        List<CalibrationRuleMagnitudeRow> Samples, List<CalibrationRuleMagnitudeSummary> Summaries,
        List<CalibrationFormalRuleHitCount> FormalHits, List<string> Explanations,
        List<CalibrationPrtgRuleThresholdInfo> CurrentRules,
        int CoveredDays, int TotalHits, int DownHits, int FlappingHits, int WarningHits, int SilentHits, bool IsUnavailable);

    /// <summary>判定快取存活時間：四項判定是重查詢（含逐筆反序列化），
    /// 而累積量以「天」為單位變動，十分鐘內重複計算不會得到不同結論。
    /// 匯出時會再取一次判定摘要，這個快取讓同一次匯出不必重跑整組查詢。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 評估四項校準指標的累積量、狀態與補充說明。
    /// 結果在行程內快取 <see cref="CacheTtl"/>；`forceRefresh` 為 true 時略過快取重算。
    /// </summary>
    public CalibrationAssessmentSummary AssessStatus(DateTime? anchor = null, bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!CalibrationCaptureAdmission.Wait(0))
            throw new CalibrationCapacityException("另一個校準資料擷取正在執行，請稍後重試。", retryable: true);
        using var budget = new PrtgCalibrationCaptureBudget();
        try
        {
            var anchorDate = (anchor ?? DateTime.Today).Date;
            var settings = _settingsStore.Get();
            var from = anchorDate.AddDays(-(CalibrationConstants.ValueBaselineWindowDays - 1));
            var toExclusive = anchorDate.AddDays(1);
            var capture = _prtgStore.CaptureCalibrationData(from, toExclusive, settings.PrtgSensorTypeWhitelist,
                anchorDate, _captureLimits, cancellationToken, budget);
            return AssessStatusWithCapture(anchorDate, settings, capture, forceRefresh, cancellationToken, budget);
        }
        finally
        {
            CalibrationCaptureAdmission.Release();
        }
    }

    /// <summary>Runs status capture and its consuming response under one logical memory admission.</summary>
    public TResult WithAssessment<TResult>(bool forceRefresh,
        Func<CalibrationAssessmentSummary, PrtgCalibrationCaptureBudget, TResult> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (!CalibrationCaptureAdmission.Wait(0))
            throw new CalibrationCapacityException("另一個校準資料擷取正在執行，請稍後重試。", retryable: true);
        using var budget = new PrtgCalibrationCaptureBudget();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchorDate = DateTime.Today;
            var settings = _settingsStore.Get();
            var capture = _prtgStore.CaptureCalibrationData(anchorDate.AddDays(-(CalibrationConstants.ValueBaselineWindowDays - 1)),
                anchorDate.AddDays(1), settings.PrtgSensorTypeWhitelist, anchorDate, _captureLimits, cancellationToken, budget);
            var summary = AssessStatusWithCapture(anchorDate, settings, capture, forceRefresh, cancellationToken, budget);
            return consume(summary, budget);
        }
        finally
        {
            CalibrationCaptureAdmission.Release();
        }
    }

    private CalibrationAssessmentSummary AssessStatusWithCapture(DateTime anchorDate, SystemSettings settings,
        PrtgCalibrationDataCapture capture, bool forceRefresh, CancellationToken cancellationToken,
        PrtgCalibrationCaptureBudget budget, RuleMagnitudeAnalysis? sharedRuleAnalysis = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _capturedResidualRows = null;
        budget.Charge(capture.Sensors.Count * 8L, "sensor reference index");
        var allSensors = capture.Sensors.ToList();
        var analysis = sharedRuleAnalysis ?? GetRuleMagnitudeAnalysis(anchorDate, settings, forceRefresh, budget);
        var assessmentKey = BuildAssessmentCacheKey(anchorDate, settings, allSensors, analysis.Fingerprint);
        if (!forceRefresh)
        {
            lock (CacheLock)
            {
                if (_cached != null && _cachedAnchor == anchorDate && _cachedAssessmentKey == assessmentKey && DateTime.Now - _cachedAt < CacheTtl)
                    return _cached;
            }
        }

        budget.Charge(checked(capture.Sensors.Count * 64L + capture.ValueCoverage.Count * 64L),
            "baseline status sensor and coverage indexes");
        var item1 = AssessPrtgValueBaseline(anchorDate, settings, allSensors, capture);
        var item2 = AssessPrtgRuleThresholds(anchorDate, settings, allSensors, analysis);
        var item3 = AssessTriggeredFetchMagnitude(anchorDate, settings, allSensors, capture.DailyMagnitudes);
        var item4 = AssessResidualCredentialThresholds(anchorDate, settings, cancellationToken, budget);
        budget.Charge(64 * 1024L, "status DTO assembly");

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
            _cachedAssessmentKey = assessmentKey;
        }

        budget.Charge(EstimateStatusDtoBytes(summary), "status response DTO");

        return summary;
    }

    private static long EstimateStatusDtoBytes(CalibrationAssessmentSummary summary)
    {
        static long EstimateItem(CalibrationItemAssessment item)
        {
            static long EstimateDictionary(Dictionary<string, object> values) => values.Sum(pair =>
                256L + 2L * pair.Key.Length + (pair.Value is string text ? 2L * text.Length : 256L));

            return checked(2048L + 2L * item.ItemName.Length +
                EstimateDictionary(item.KeyMetrics) + EstimateDictionary(item.CurrentThresholds) +
                item.Explanations.Sum(text => 256L + 2L * text.Length));
        }

        return checked(512L * 1024L + EstimateItem(summary.PrtgValueBaseline) +
            EstimateItem(summary.PrtgRuleThresholds) + EstimateItem(summary.TriggeredFetchMagnitude) +
            EstimateItem(summary.ResidualCredentialThresholds));
    }

    /// <summary>清空判定快取（測試用；正式路徑靠 TTL 自然過期）</summary>
    internal static void ClearAssessmentCache()
    {
        lock (CacheLock)
        {
            _cached = null;
            _cachedAnchor = default;
            _cachedAt = default;
            _cachedAssessmentKey = string.Empty;
        }
    }

    /// <summary>
    /// 組裝校準數值匯出物件（包含檔案摘要與四大資料集）。
    /// </summary>
    public CalibrationExportPackage BuildExportPackage(DateTime? anchor = null, bool summaryOnly = false)
    {
        if (!CalibrationCaptureAdmission.Wait(0))
            throw new CalibrationCapacityException("另一個校準資料擷取正在執行，請稍後重試。", retryable: true);
        using var budget = new PrtgCalibrationCaptureBudget();
        try
        {
            var package = BuildExportPackageCore(anchor, summaryOnly, CancellationToken.None, budget);
            budget.Charge(EstimateExportDtoBytes(package), "export DTO assembly");
            return package;
        }
        finally
        {
            CalibrationCaptureAdmission.Release();
        }
    }

    /// <summary>在同一個 process-wide capture admission 內組包並消費，避免匯出序列化期間再配置另一份全量封包。</summary>
    public TResult WithExportPackage<TResult>(bool summaryOnly,
        Func<CalibrationExportPackage, PrtgCalibrationCaptureBudget, TResult> consume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (!CalibrationCaptureAdmission.Wait(0))
            throw new CalibrationCapacityException("另一個校準資料擷取正在執行，請稍後重試。", retryable: true);
        using var budget = new PrtgCalibrationCaptureBudget();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = BuildExportPackageCore(null, summaryOnly, cancellationToken, budget);
            budget.Charge(EstimateExportDtoBytes(package), "export DTO assembly");
            return consume(package, budget);
        }
        finally
        {
            CalibrationCaptureAdmission.Release();
        }
    }

    private CalibrationExportPackage BuildExportPackageCore(DateTime? anchor, bool summaryOnly,
        CancellationToken cancellationToken, PrtgCalibrationCaptureBudget budget)
    {
        var anchorDate = (anchor ?? DateTime.Today).Date;
        var now = DateTime.Now;
        var settings = _settingsStore.Get();
        var valueBaselineFrom = anchorDate.AddDays(-(CalibrationConstants.ValueBaselineWindowDays - 1));
        var valueBaselineToExclusive = anchorDate.AddDays(1);
        var capturedWhitelist = (settings.PrtgSensorTypeWhitelist ?? []).ToArray();
        var capture = _prtgStore.CaptureCalibrationData(valueBaselineFrom, valueBaselineToExclusive,
            capturedWhitelist, anchorDate, _captureLimits, cancellationToken, budget);
        budget.Charge(capture.Sensors.Count * 8L, "sensor reference index");
        var allSensors = capture.Sensors.ToList();
        settings = _settingsStore.Get();
        if (!capturedWhitelist.SequenceEqual(settings.PrtgSensorTypeWhitelist ?? [], StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("校準 sensor 白名單在一致性資料擷取期間變更，請重試。");
        var ruleAnalysis = GetRuleMagnitudeAnalysis(anchorDate, settings, forceRefresh: true, budget: budget);
        var summary = AssessStatusWithCapture(anchorDate, settings, capture, forceRefresh: true,
            cancellationToken, budget, ruleAnalysis);

        // 1. 值型基線資料集：取自同一個有界 SQL snapshot 的 56 天每日聚合。
        var dailyAggs = capture.DailyAggregations;
        // Summary mode keeps compact exact-statistic accumulators only. It must not pay for
        // per-row DTOs or LINQ GroupBy lists that it never emits.
        budget.Charge(checked(dailyAggs.Count * (summaryOnly ? 80L : 960L)),
            summaryOnly ? "compact exact summary statistic accumulators" : "full daily DTO and summary grouping assembly");
        budget.Charge(capture.Sensors.Count * 1024L + capture.HostMaps.Count * 256L,
            "sensor and host-map export lookup indexes");
        var sensorsByObjid = allSensors.ToDictionary(s => s.Objid);
        var hostByDevice = capture.HostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .ToDictionary(m => m.DeviceObjid, m => (HostId: m.HostId!.Value, HostName: m.HostName ?? string.Empty));

        var whitelist = settings.PrtgSensorTypeWhitelist;
        var whitelistSet = (whitelist != null && whitelist.Count > 0)
            ? new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase)
            : null;

        var valueBaselineRows = new List<CalibrationValueBaselineRow>();
        if (!summaryOnly)
        {
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
                    agg.NodataCount,
                    agg.SampledCount,
                    agg.MinObserved,
                    agg.MaxObserved
                ));
            }
        }

        // 1b. 每顆感測器的分佈摘要（ValueSensorSummaries）
        var valueSensorSummaries = new List<CalibrationValueSensorSummary>();
        var sensorStats = new Dictionary<long, (int UsableHours, int SampledHours, int Days, List<double> Samples)>();
        var typeSamplesByName = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var agg in dailyAggs)
        {
            if (!sensorsByObjid.TryGetValue(agg.SensorObjid, out var sensor) ||
                (whitelistSet != null && !whitelistSet.Contains(sensor.SensorType))) continue;

            if (!sensorStats.TryGetValue(agg.SensorObjid, out var accumulator))
                accumulator = (0, 0, 0, new List<double>(CalibrationConstants.ValueBaselineWindowDays));
            accumulator.UsableHours = checked(accumulator.UsableHours + agg.UsableCount);
            accumulator.SampledHours = checked(accumulator.SampledHours + agg.SampledCount);
            if (agg.UsableCount > 0) accumulator.Days++;
            if (agg.AvgValue.HasValue) accumulator.Samples.Add(agg.AvgValue.Value);
            sensorStats[agg.SensorObjid] = accumulator;
            if (agg.AvgValue.HasValue)
            {
                if (!typeSamplesByName.TryGetValue(sensor.SensorType, out var typeSamples))
                    typeSamplesByName[sensor.SensorType] = typeSamples = new List<double>();
                typeSamples.Add(agg.AvgValue.Value);
            }
        }

        foreach (var pair in sensorStats.OrderBy(p => p.Key))
        {
            var sensor = sensorsByObjid[pair.Key];
            string? hostName = hostByDevice.TryGetValue(sensor.DeviceObjid, out var hostInfo) ? hostInfo.HostName : null;
            var stats = pair.Value;
            var sampledRatio = stats.UsableHours > 0 ? Math.Round((double)stats.SampledHours / stats.UsableHours, 3) : 0.0;
            var samples = stats.Samples;
            samples.Sort();

            double? mean = samples.Count > 0 ? samples.Average() : null;
            double? stdDev = CalculatePopulationStdDev(samples, mean);
            double? p50 = Percentile(samples, 50);
            double? p90 = Percentile(samples, 90);
            double? p99 = Percentile(samples, 99);
            double? max = samples.Count > 0 ? samples[^1] : null;

            valueSensorSummaries.Add(new CalibrationValueSensorSummary(
                SensorObjid: sensor.Objid,
                HostName: hostName,
                SensorType: sensor.SensorType,
                Unit: sensor.Unit,
                IsVolumeNormalized: PrtgVolumeSensorTypes.IsVolume(sensor.SensorType),
                Days: stats.Days,
                UsableHours: stats.UsableHours,
                SampledRatio: sampledRatio,
                Mean: mean,
                StdDev: stdDev,
                P50: p50,
                P90: p90,
                P99: p99,
                Max: max
            ));
        }

        // 1c. 每種感測器類型的分佈與小時曲線（ValueTypeProfiles）
        var hourlyProfiles = capture.HourlyProfiles;
        var hourlyByType = hourlyProfiles
            .GroupBy(p => p.SensorType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Hour, p => p.AvgValue), StringComparer.OrdinalIgnoreCase);

        var valueTypeProfiles = new List<CalibrationValueTypeProfile>();
        var summariesByType = valueSensorSummaries
            .GroupBy(s => s.SensorType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var typeGroup in summariesByType)
        {
            var sensorType = typeGroup.Key;
            var sensorCount = typeGroup.Count();
            var usableHours = typeGroup.Sum(s => s.UsableHours);

            var typeSamples = typeSamplesByName.TryGetValue(sensorType, out var collectedSamples)
                ? collectedSamples
                : new List<double>();
            typeSamples.Sort();

            double? dailyP50 = Percentile(typeSamples, 50);
            double? dailyP90 = Percentile(typeSamples, 90);
            double? dailyP99 = Percentile(typeSamples, 99);
            double? dailyMax = typeSamples.Count > 0 ? typeSamples[^1] : null;

            var hourlyCurve = new double?[24];
            if (hourlyByType.TryGetValue(sensorType, out var hourMap))
            {
                for (int h = 0; h < 24; h++)
                {
                    if (hourMap.TryGetValue(h, out var avgVal))
                    {
                        hourlyCurve[h] = avgVal;
                    }
                }
            }

            valueTypeProfiles.Add(new CalibrationValueTypeProfile(
                SensorType: sensorType,
                SensorCount: sensorCount,
                UsableHours: usableHours,
                DailyP50: dailyP50,
                DailyP90: dailyP90,
                DailyP99: dailyP99,
                DailyMax: dailyMax,
                HourlyCurve: hourlyCurve
            ));
        }

        // 1d. 匯出上下文（CalibrationExportContext）
        var mirrorSummary = capture.MirrorSummary;
        var snapshotTargets = allSensors.Count(sensor => !sensor.Paused &&
            (whitelistSet == null || whitelistSet.Contains(sensor.SensorType)) && hostByDevice.ContainsKey(sensor.DeviceObjid));
        var exportContext = new CalibrationExportContext(
            Detail: summaryOnly ? "summary" : "full",
            FetchStrategy: settings.PrtgFetchStrategy ?? string.Empty,
            SnapshotIntervalMinutes: PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes,
            SnapshotTargets: snapshotTargets,
            SensorTypeWhitelist: settings.PrtgSensorTypeWhitelist ?? new List<string>(),
            PrtgRetentionDays: settings.PrtgRetentionDays,
            WindowFrom: valueBaselineFrom,
            WindowToExclusive: valueBaselineToExclusive,
            MirrorDeviceCount: mirrorSummary.DeviceCount,
            MirrorSensorCount: mirrorSummary.SensorCount,
            SampledMinCoverage: PrtgValueUsability.SampledMinCoverage,
            StatisticsBasis: "每感測器與每 type 的分位數、平均與標準差以「可用列的每日平均值」為樣本（非每小時值）；HourlyCurve 為各小時段可用列的平均值；可用列＝ok，或 coverage ≥ SampledMinCoverage 的 sampled；流量類（IsVolumeNormalized）的 sampled 值為估算的小時量。"
        );

        // 2. 規則門檻資料集：近 56 天每日命中數 ＋ 規則庫全部 PRTG 規則的門檻現值（含適用分類）
        var ruleFrom = anchorDate.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1));
        // 靜音不排除：校準看的是規則實際命中量，靜音只是讀取側的顯示決定，排掉會讓門檻建議失真
        var ruleHits = _issueQuery.AggregatePrtgRuleHitsBounded(
            IssueExclusion.None, ruleFrom, anchorDate, null, 200_000, budget);
        budget.Charge(ruleHits.Count * 192L, "PRTG daily rule-hit DTOs");

        var ruleDataset = new CalibrationRuleThresholdDataset
        {
            DailyRuleHits = ruleHits,
            MagnitudeSamples = ruleAnalysis.Samples,
            MagnitudeSummaries = ruleAnalysis.Summaries,
            CurrentRules = ruleAnalysis.CurrentRules,
            UnsupportedByMagnitudeAnalysisRuleCodes = ruleAnalysis.CurrentRules
                .Select(rule => rule.RuleCode).Distinct(StringComparer.Ordinal)
                .Where(code => code is not (PrtgRuleEvaluator.RuleDown or PrtgRuleEvaluator.RuleFlapping or PrtgRuleEvaluator.RuleWarning))
                .OrderBy(code => code, StringComparer.Ordinal).ToList(),
            EvidenceFingerprint = ruleAnalysis.Fingerprint,
            AsOf = ruleAnalysis.AsOf,
            Explanations = ruleAnalysis.Explanations,
            FormalCurrentHitCounts = ruleAnalysis.FormalHits
        };

        // 3. 觸發式量級資料集：近 30 天每日相異 sensor 數與品質列數
        budget.Charge(capture.DailyMagnitudes.Count * 16L, "triggered magnitude export list references");
        var triggeredMagnitudes = capture.DailyMagnitudes.ToList();

        // 4. 殘留判定資料集：候選主機日指標（最多 5000 筆，排除任何帳號文字）
        var residualCandidates = BuildResidualCandidateRows(anchorDate, settings.RawEventRetentionDays, cancellationToken, budget);

        var package = new CalibrationExportPackage
        {
            FormatVersion = CalibrationConstants.CurrentFormatVersion,
            ExportedAt = now,
            Summary = summary,
            ValueBaselines = valueBaselineRows,
            RuleThresholds = ruleDataset,
            TriggeredMagnitudes = triggeredMagnitudes,
            ResidualCandidates = residualCandidates,
            ValueSensorSummaries = valueSensorSummaries,
            ValueTypeProfiles = valueTypeProfiles,
            Context = exportContext
        };
        if (!string.Equals(ruleAnalysis.RuleStamp, GetValidatedPrtgRuleSnapshot(budget).Stamp, StringComparison.Ordinal))
            throw new InvalidOperationException("校準規則在匯出組裝期間變更，請重試。");
        if (ruleAnalysis.HostMapDataRevision != _prtgStore.ReadHostMapDataRevision())
            throw new InvalidOperationException("校準主機對應在匯出組裝期間變更，請重試。");
        if (!HasCurrentRuleAuthority(ruleAnalysis, budget))
            throw new InvalidOperationException("校準來源版本或設定在匯出組裝期間變更，請重試。");
        return package;
    }

    private bool HasCurrentRuleAuthority(RuleMagnitudeAnalysis captured, PrtgCalibrationCaptureBudget budget)
    {
        using var guardReservation = budget.ReserveTransient(8L * 1024 * 1024,
            "final calibration authority version check");
        var settings = _settingsStore.Get();
        if (CalibrationRuleSettingsStamp(settings) != captured.SettingsStamp ||
            _prtgStore.ReadHostMapDataRevision() != captured.HostMapDataRevision ||
            new EfJsonBlobStore(_contextFactory, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() != captured.ScopeVersion ||
            new EfJsonBlobStore(_contextFactory, PrtgMonitoringPolicyStore.BlobKey).ReadVersion() != captured.PolicyVersion)
            return false;

        var statuses = ReadCalibrationSensorStatuses(captured.PolicySensorIds, out var sensorLimit);
        var timelineVersions = ReadCalibrationTimelineVersions(captured.PolicySensorIds, out var timelineLimit);
        var identityVersions = ReadCalibrationResourceIdentityVersions(captured.PolicySensorIds, out var identityLimit);
        return !sensorLimit && !timelineLimit && !identityLimit &&
            CalibrationSensorStamp(statuses) == captured.SensorStamp &&
            CalibrationVersionStamp(timelineVersions) == captured.TimelineStamp &&
            CalibrationVersionStamp(identityVersions) == captured.ResourceIdentityVersionStamp;
    }

    private static long EstimateExportDtoBytes(CalibrationExportPackage package) => checked(
        package.ValueBaselines.Count * 256L + package.ValueSensorSummaries.Count * 192L +
        package.ValueTypeProfiles.Count * 256L + package.RuleThresholds.DailyRuleHits.Count * 192L +
        package.RuleThresholds.MagnitudeSamples.Count * 320L + package.RuleThresholds.MagnitudeSummaries.Count * 192L +
        package.RuleThresholds.CurrentRules.Count * 4096L + package.TriggeredMagnitudes.Count * 256L +
        package.ResidualCandidates.Count * 512L + 256 * 1024L);

    private CalibrationItemAssessment AssessPrtgValueBaseline(
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors,
        PrtgCalibrationDataCapture capture)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["RequiredHosts"] = CalibrationConstants.ValueBaselineRequiredHosts,
            ["AvailableCoverageDays"] = CalibrationConstants.ValueBaselineAvailableDays,
            ["SufficientCoverageDays"] = CalibrationConstants.ValueBaselineSufficientDays,
            ["MinDailyUsableHours"] = CalibrationConstants.ValueBaselineMinDailyUsableHours
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
                Explanations = new List<string> { "請先在 PRTG 維護頁「連線」頁籤完成連線設定，並在「擷取參數」頁籤選擇取數範圍以啟用擷取" }
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

        var okDeviceMap = capture.HostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .ToDictionary(m => m.DeviceObjid, m => (HostId: m.HostId!.Value, HostName: m.HostName ?? string.Empty));

        var dailyAggs = capture.DailyAggregations;

        // 有效資料的起訖日：回答「這批基線涵蓋哪一段期間」，與涵蓋天數是兩件事
        // （中間可能有斷檔）。白名單內完全沒有數值的 sensor 數同理——
        // 它區分「還沒累積夠」與「這些 sensor 根本沒在取數」。
        var coverage = capture.ValueCoverage;
        var whitelistedObjids = whitelistedSensors.Select(x => x.Objid).ToHashSet();
        var coverageInScope = coverage.Where(c => whitelistedObjids.Contains(c.SensorObjid)).ToList();
        var earliestOk = coverageInScope
            .Where(c => c.EarliestUsablePeriod.HasValue)
            .Select(c => c.EarliestUsablePeriod!.Value)
            .DefaultIfEmpty()
            .Min();
        var latestOk = coverageInScope
            .Where(c => c.LatestUsablePeriod.HasValue)
            .Select(c => c.LatestUsablePeriod!.Value)
            .DefaultIfEmpty()
            .Max();
        var sensorsWithoutValues = whitelistedObjids.Count - coverageInScope.Count(c => c.UsableCount > 0);
        // 未對應 sensor：白名單內但所屬 device 沒有成功對應到主機——這些 sensor 就算有數值
        // 也進不了主機層的基線，與「有對應但還沒累積夠」是不同的問題，補充說明的方向也不同
        var unmappedSensors = whitelistedSensors.Count(x => !okDeviceMap.ContainsKey(x.DeviceObjid));

        // SQL capture is already grouped by sensor and date, so count qualifying days directly
        // without retaining one LINQ group/list/date object for every daily row.
        var sensorCoverageDays = new Dictionary<long, int>();
        foreach (var aggregate in dailyAggs)
        {
            if (aggregate.UsableCount < CalibrationConstants.ValueBaselineMinDailyUsableHours) continue;
            sensorCoverageDays[aggregate.SensorObjid] = sensorCoverageDays.GetValueOrDefault(aggregate.SensorObjid) + 1;
        }

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

        // 只算白名單範圍內的 sensor，與本項其他統計（coverageInScope）同一口徑
        var sampledHours = dailyAggs.Where(a => whitelistedObjids.Contains(a.SensorObjid)).Sum(a => a.SampledCount);
        if (sampledHours > 0)
        {
            explanations.Add($"可用小時中有 {sampledHours} 小時為快照取樣值（coverage ≥ {PrtgValueUsability.SampledMinCoverage:0} 的取樣列才計入）");
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

        var snapshotTargets = allSensors.Count(sensor => !sensor.Paused &&
            (whitelistSet == null || whitelistSet.Contains(sensor.SensorType)) && okDeviceMap.ContainsKey(sensor.DeviceObjid));
        var sampled24h = capture.SampledCoverage;
        var snapshotSensors24h = sampled24h.SensorCount;
        var snapshotCoverage24h = sampled24h.AverageCoverage.HasValue
            ? Math.Round(sampled24h.AverageCoverage.Value, 1)
            : 0.0;
        // 匯出的 ValueBaselines 依白名單篩選，預告大小要用同一個範圍
        var valueBaselineRows = dailyAggs.Count(a => whitelistedObjids.Contains(a.SensorObjid));

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
                ["UnmappedSensors"] = unmappedSensors,
                ["SnapshotTargets"] = snapshotTargets,
                ["SnapshotSensors24h"] = snapshotSensors24h,
                ["SnapshotCoverage24h"] = snapshotCoverage24h,
                ["ValueBaselineRows"] = valueBaselineRows
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private CalibrationItemAssessment AssessPrtgRuleThresholds(
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors, RuleMagnitudeAnalysis analysis)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["AvailableCoverageDays"] = CalibrationConstants.RuleThresholdAvailableDays,
            ["AvailableHits"] = CalibrationConstants.RuleThresholdAvailableHits,
            ["SufficientCoverageDays"] = CalibrationConstants.RuleThresholdSufficientDays,
            ["SufficientHits"] = CalibrationConstants.RuleThresholdSufficientHits
        };

        if (!settings.PrtgEnabled || allSensors.Count == 0 || analysis.IsUnavailable)
        {
            return new CalibrationItemAssessment
            {
                ItemName = "PRTG 規則門檻",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["EvidenceFingerprint"] = analysis.Fingerprint,
                    ["AsOf"] = analysis.AsOf,
                    ["DistinctCoverageDays"] = analysis.CoveredDays,
                    ["TotalRuleHits"] = 0,
                    ["DownSensorDays"] = 0,
                    ["FlappingSensorDays"] = 0,
                    ["WarningSensorDays"] = 0,
                    ["SilentDeviceDays"] = 0
                },
                CurrentThresholds = thresholds,
                Explanations = !settings.PrtgEnabled || allSensors.Count == 0
                    ? new List<string> { "請先在 PRTG 維護頁「連線」頁籤完成連線設定，並在「擷取參數」頁籤選擇取數範圍以啟用擷取" }
                    : analysis.Explanations
            };
        }

        var distinctDates = analysis.CoveredDays;
        var totalHits = analysis.TotalHits;
        var downHits = analysis.DownHits;
        var flappingHits = analysis.FlappingHits;
        var warningHits = analysis.WarningHits;
        var silentHits = analysis.SilentHits;

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

        var explanations = new List<string>(analysis.Explanations);
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
                ["EvidenceFingerprint"] = analysis.Fingerprint,
                ["AsOf"] = analysis.AsOf,
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
        DateTime anchor, SystemSettings settings, List<PrtgSensorRow> allSensors,
        IReadOnlyList<PrtgDailyValueMagnitude> magnitudes)
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
                ItemName = "數值取得量級",
                Status = CalibrationStatus.Unavailable,
                KeyMetrics = new Dictionary<string, object>
                {
                    ["DaysWithValues"] = 0,
                    ["WindowDays"] = CalibrationConstants.TriggeredMagnitudeWindowDays
                },
                CurrentThresholds = thresholds,
                Explanations = new List<string> { "請先在 PRTG 維護頁「連線」頁籤完成連線設定，並在「擷取參數」頁籤選擇取數範圍以啟用擷取" }
            };
        }

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
        var sampledTotal = withValues.Sum(m => m.SampledCount);
        var usableTotal = withValues.Sum(m => m.UsableCount);
        var rowsTotal = rowCounts.Sum();
        var okRatio = rowsTotal > 0 ? (double)okTotal / rowsTotal : 0.0;
        var usableRatio = rowsTotal > 0 ? (double)usableTotal / rowsTotal : 0.0;
        var sampledRatio = rowsTotal > 0 ? (double)sampledTotal / rowsTotal : 0.0;

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
            ItemName = "數值取得量級",
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
                ["OkRatio"] = Math.Round(okRatio, 3),
                ["UsableRatio"] = Math.Round(usableRatio, 3),
                ["SampledRatio"] = Math.Round(sampledRatio, 3)
            },
            CurrentThresholds = thresholds,
            Explanations = explanations
        };
    }

    private sealed class CalibrationReadSnapshot : IDisposable
    {
        private readonly LfDbContext _context;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;
        private readonly DbTransaction? _providerTransaction;
        private readonly DbConnection _connection;
        private readonly bool _sqlite;
        private readonly int _previousTempStore;

        private CalibrationReadSnapshot(LfDbContext context,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
            DbTransaction? providerTransaction, bool sqlite, int previousTempStore = 0)
        {
            _context = context;
            _transaction = transaction;
            _providerTransaction = providerTransaction;
            _connection = context.Database.GetDbConnection();
            _sqlite = sqlite;
            _previousTempStore = previousTempStore;
        }

        public static CalibrationReadSnapshot Begin(LfDbContext context)
        {
            var connection = context.Database.GetDbConnection();
            if (context.Database.IsSqlite())
            {
                if (connection is not SqliteConnection sqlite)
                    throw new CalibrationCapacityException("無法確認 SQLite 殘留資料 snapshot journal mode。");
                context.Database.OpenConnection();
                var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString);
                var privateMemory = builder.DataSource == ":memory:" && builder.Cache != SqliteCacheMode.Shared;
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode";
                var mode = Convert.ToString(pragma.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                if (!privateMemory && !string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new CalibrationCapacityException("殘留校準擷取需要正式 SQLite WAL journal mode。");
                int previousTempStore;
                using (var tempStore = connection.CreateCommand())
                {
                    tempStore.CommandText = "PRAGMA temp_store";
                    previousTempStore = Convert.ToInt32(tempStore.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    tempStore.CommandText = "PRAGMA temp_store=FILE";
                    tempStore.ExecuteNonQuery();
                }
                using (var queryOnly = connection.CreateCommand())
                {
                    queryOnly.CommandText = "PRAGMA query_only=ON";
                    queryOnly.ExecuteNonQuery();
                }
                try
                {
                    var providerTransaction = sqlite.BeginTransaction(deferred: true);
                    var transaction = context.Database.UseTransaction(providerTransaction);
                    if (transaction is null)
                    {
                        providerTransaction.Dispose();
                        throw new CalibrationCapacityException("無法建立殘留校準 SQLite deferred snapshot transaction。");
                    }
                    return new CalibrationReadSnapshot(context, transaction, providerTransaction, sqlite: true,
                        previousTempStore: previousTempStore);
                }
                catch
                {
                    using var queryOnly = connection.CreateCommand();
                    queryOnly.CommandText = "PRAGMA query_only=OFF";
                    queryOnly.ExecuteNonQuery();
                    queryOnly.CommandText = $"PRAGMA temp_store={previousTempStore}";
                    queryOnly.ExecuteNonQuery();
                    throw;
                }
            }
            if (!context.Database.IsSqlServer())
                throw new CalibrationCapacityException("目前 provider 無法提供殘留校準 snapshot 一致性。");
            try
            {
                var enabled = context.Database.SqlQueryRaw<int>(
                        "SELECT CONVERT(int, snapshot_isolation_state) AS [Value] FROM sys.databases WHERE database_id = DB_ID()")
                    .AsEnumerable().FirstOrDefault();
                if (enabled != 1)
                    throw new CalibrationCapacityException("SQL Server 必須啟用 ALLOW_SNAPSHOT_ISOLATION 才能評估殘留校準。");
                return new CalibrationReadSnapshot(context,
                    context.Database.BeginTransaction(IsolationLevel.Snapshot), null, sqlite: false);
            }
            catch (CalibrationCapacityException) { throw; }
        }

        public void Dispose()
        {
            _transaction.Dispose();
            _providerTransaction?.Dispose();
            if (!_sqlite) return;
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=OFF";
            command.ExecuteNonQuery();
            command.CommandText = $"PRAGMA temp_store={_previousTempStore}";
            command.ExecuteNonQuery();
        }
    }

    private CalibrationItemAssessment AssessResidualCredentialThresholds(
        DateTime anchor, SystemSettings settings, CancellationToken cancellationToken,
        PrtgCalibrationCaptureBudget budget)
    {
        var thresholds = new Dictionary<string, object>
        {
            ["AvailableHostDays"] = CalibrationConstants.ResidualAvailableHostDays,
            ["AvailableCoverageDays"] = CalibrationConstants.ResidualAvailableDays,
            ["SufficientHostDays"] = CalibrationConstants.ResidualSufficientHostDays,
            ["SufficientCoverageDays"] = CalibrationConstants.ResidualSufficientDays
        };

        var capture = ExecuteResidualSnapshot(budget, cancellationToken, ctx =>
        {
            var cutoff = anchor.Date.AddDays(-settings.RawEventRetentionDays);

            var candidateHostDays = CandidateRecordsQuery(ctx, cutoff, anchor.Date)
                .Select(r => new { r.HostId, Date = r.RecordDate.Date }).Distinct().Count();
            var distinctDays = CandidateRecordsQuery(ctx, cutoff, anchor.Date)
                .Select(r => r.RecordDate.Date).Distinct().Count();
            cancellationToken.ThrowIfCancellationRequested();
            var rows = BuildResidualCandidateRowsCore(ctx, anchor, settings.RawEventRetentionDays, cancellationToken, budget);
            return (CandidateHostDays: candidateHostDays, DistinctDays: distinctDays, Rows: rows);
        });
        var candidateHostDays = capture.CandidateHostDays;
        var distinctDays = capture.DistinctDays;
        _capturedResidualRows = capture.Rows;

        // 註：這裡的候選是「有登入失敗簽章且未精簡」的主機日，與匯出端同一個查詢。
        // 匯出端還會逐筆反序列化後再要求 LoginFailureDetails 非空（明細真的存在），
        // 因此匯出筆數可能少於這裡的計數——差額即「有簽章但明細已不在」的舊資料。

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
        var sampledRows = capture.Rows;
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

    /// <summary>
    /// 殘留候選列由同一個 read snapshot 計算，供摘要與匯出共用；不跨請求快取，
    /// 因為 NetIQ 日誌可在同一錨點日期內新增或修正。
    /// </summary>
    private List<CalibrationResidualCandidateRow> BuildResidualCandidateRows(
        DateTime anchor, int rawEventRetentionDays, CancellationToken cancellationToken,
        PrtgCalibrationCaptureBudget budget)
    {
        if (_capturedResidualRows is not null) return _capturedResidualRows;
        var rows = ExecuteResidualSnapshot(budget, cancellationToken,
            ctx => BuildResidualCandidateRowsCore(ctx, anchor, rawEventRetentionDays, cancellationToken, budget));
        return _capturedResidualRows = rows;
    }

    /// <summary>
    /// Each provider retry gets a new context and a new snapshot transaction. The callback keeps
    /// all residual counts, candidate pages, and history reads inside that one snapshot. Budget
    /// charges and callback results are published only after transaction disposal succeeds.
    /// </summary>
    internal T ExecuteResidualSnapshot<T>(PrtgCalibrationCaptureBudget budget, CancellationToken cancellationToken,
        Func<LfDbContext, T> capture)
    {
        using var strategyContext = _contextFactory();
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return strategy.Execute(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptBudget = budget.BeginAttempt();
            T result;
            using (var context = _contextFactory())
            {
                using (CalibrationReadSnapshot.Begin(context))
                {
                    context.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));
                    result = capture(context);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            attemptBudget.Commit();
            return result;
        });
    }

    private sealed record ResidualCandidateInput(long RecordId, long HostId, string? HostName,
        DateTime RecordDate, string? ContentJson, int ContentLength);
    private sealed record ResidualCandidateMetadata(long RecordId, long HostId, string? HostName,
        DateTime RecordDate, int ContentLength);

    private List<CalibrationResidualCandidateRow> BuildResidualCandidateRowsCore(
        LfDbContext ctx, DateTime anchor, int rawEventRetentionDays, CancellationToken cancellationToken,
        PrtgCalibrationCaptureBudget budget)
    {
        var cutoff = anchor.Date.AddDays(-rawEventRetentionDays);
        var candidateRuleIds = LinuxAuthParser.LoginFailureRuleIds;
        var candidateDailyRows = new List<ResidualCandidateInput>(CalibrationConstants.ResidualCandidateMaxCount);
        DateTime? afterDate = null;
        long? afterRecordId = null;
        long candidateBytes = 0;
        while (candidateDailyRows.Count < CalibrationConstants.ResidualCandidateMaxCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateQuery = CandidateRecordsQuery(ctx, cutoff, anchor.Date);
            if (afterDate.HasValue)
                candidateQuery = candidateQuery.Where(r => r.RecordDate < afterDate.Value ||
                    (r.RecordDate == afterDate.Value && r.RecordId < afterRecordId!.Value));
            var pageSize = Math.Min(CalibrationConstants.ResidualDatabasePageRows,
                CalibrationConstants.ResidualCandidateMaxCount - candidateDailyRows.Count);
            var metadataPage = candidateQuery.OrderByDescending(r => r.RecordDate).ThenByDescending(r => r.RecordId)
                .Take(pageSize)
                .Select(r => new ResidualCandidateMetadata(r.RecordId, r.HostId,
                    r.HostName == null ? null : r.HostName.Substring(0,
                        (r.HostName + "x").Length - 1 > 257 ? 257 : (r.HostName + "x").Length - 1),
                    r.RecordDate,
                    r.ContentJson == null ? 0 : (r.ContentJson + "x").Length - 1))
                .ToList();
            if (metadataPage.Count == 0) break;
            if (metadataPage.Any(row => row.HostName?.Length > 256 ||
                    row.ContentLength > CalibrationConstants.ResidualCandidateJsonMaxCharacters))
                throw new CalibrationCapacityException("殘留候選資料的單筆內容超過校準匯出安全上限。");
            var pageBytes = metadataPage.Sum(row => 128L + 2L * ((row.HostName?.Length ?? 0) + row.ContentLength));
            if (candidateBytes + pageBytes > CalibrationConstants.ResidualCandidateJsonMaxBytes)
                throw new CalibrationCapacityException("殘留候選資料超過校準匯出總量上限；完整資料未截斷。");
            budget.Charge(pageBytes, "residual candidate source JSON");
            var metadataIds = metadataPage.Select(row => row.RecordId).ToArray();
            var contentByRecordId = ctx.DailyRecords.AsNoTracking().Where(r => metadataIds.Contains(r.RecordId))
                .Select(r => new
                {
                    r.RecordId,
                    ContentJson = r.ContentJson == null ? null : r.ContentJson.Substring(0,
                        (r.ContentJson + "x").Length - 1 > CalibrationConstants.ResidualCandidateJsonMaxCharacters + 1
                            ? CalibrationConstants.ResidualCandidateJsonMaxCharacters + 1 : (r.ContentJson + "x").Length - 1)
                }).ToDictionary(row => row.RecordId, row => row.ContentJson);
            if (contentByRecordId.Count != metadataPage.Count)
                throw new CalibrationCapacityException("殘留候選資料在擷取期間變更，已拒絕不完整輸出。");
            var page = metadataPage.Select(row => new ResidualCandidateInput(row.RecordId, row.HostId, row.HostName,
                row.RecordDate, contentByRecordId[row.RecordId], row.ContentLength)).ToList();
            candidateDailyRows.AddRange(page);
            candidateBytes += pageBytes;
            afterDate = metadataPage[^1].RecordDate;
            afterRecordId = metadataPage[^1].RecordId;
        }

        // 條件 4（跨日重現）要看同主機回看窗內的歷史。逐候選各查一次是 N+1
        // （5000 筆候選＝5000 次查詢＋每次最多 7 份 blob 反序列化，同一台主機的 30 個候選日
        // 會把同一批歷史重複反序列化 30 次）。改為一次撈回看窗內全部候選主機的紀錄，
        // 在記憶體按主機分組、逐份只反序列化一次。
        var historyByHost = LoadHistoryForHosts(
            ctx,
            candidateDailyRows.Select(r => r.HostId).Distinct().ToList(),
            candidateDailyRows.Count > 0 ? candidateDailyRows.Min(r => r.RecordDate) : anchor.Date,
            candidateDailyRows.Count > 0 ? candidateDailyRows.Max(r => r.RecordDate) : anchor.Date, cancellationToken, budget);

        var result = new List<CalibrationResidualCandidateRow>();
        foreach (var row in candidateDailyRows)
        {
            if (string.IsNullOrWhiteSpace(row.ContentJson)) continue;

            var graphBytes = budget.ValidateDailyRecordJson(row.ContentJson, "殘留候選");
            using var graphReservation = budget.ReserveTransient(graphBytes, "residual candidate deserialized object graph");

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
                var history = HistoryForCandidate(historyByHost, row.HostId, record.Date);
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
    /// 一次載入候選主機在整個回看範圍內的紀錄（不含精簡者），按主機分組、每份只反序列化一次。
    /// 範圍＝最早候選日往前一個回看窗，到最晚候選日（不含）；窗長與判定端同一常數。
    /// </summary>
    private static Dictionary<long, List<DailyAnalysisRecord>> LoadHistoryForHosts(
        LfDbContext ctx, IReadOnlyCollection<long> hostIds, DateTime earliestCandidate, DateTime latestCandidate,
        CancellationToken cancellationToken, PrtgCalibrationCaptureBudget budget)
    {
        var byHost = new Dictionary<long, List<DailyAnalysisRecord>>();
        if (hostIds.Count == 0) return byHost;

        var from = earliestCandidate.Date.AddDays(-(ResidualCredentialDetector.HistoryWindowDaysForCalibration - 1));
        var toExclusive = latestCandidate.Date;
        var hostScope = EfPrtgStore.CalibrationSensorIdScope.Create(ctx, hostIds, "ResidualHosts");
        using (hostScope)
        {
            var query = ctx.DailyRecords.AsNoTracking()
                .Where(r => hostScope.Query.Contains(r.HostId) && !r.DetailPruned &&
                            r.RecordDate >= from && r.RecordDate < toExclusive);
            var rowCount = query.Count();
            if (rowCount > CalibrationConstants.ResidualHistoryMaxRows)
                throw new CalibrationCapacityException($"殘留歷史資料超過 {CalibrationConstants.ResidualHistoryMaxRows} 列安全上限；完整回看未截斷。");

            long? afterRecordId = null;
            long historyBytes = 0;
            var loadedRows = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageQuery = query;
                if (afterRecordId.HasValue) pageQuery = pageQuery.Where(r => r.RecordId > afterRecordId.Value);
                var page = pageQuery.OrderBy(r => r.RecordId).Take(CalibrationConstants.ResidualDatabasePageRows)
                    .Select(r => new
                    {
                        r.RecordId, r.HostId,
                        ContentLength = r.ContentJson == null ? 0 : (r.ContentJson + "x").Length - 1
                    }).ToList();
                if (page.Count == 0) break;
                if (page.Any(row => row.ContentLength > CalibrationConstants.ResidualHistoryJsonMaxCharacters))
                    throw new CalibrationCapacityException("殘留歷史單筆內容超過校準反序列化安全上限。");
                // Budget for the JSON plus a conservative 8x deserialized object-graph expansion.
                var pageBytes = page.Sum(row => 128L + 16L * row.ContentLength);
                if (pageBytes > budget.RemainingBytes)
                    throw new CalibrationCapacityException("殘留歷史反序列化物件圖超過校準作業總預算；完整回看未截斷。" );
                loadedRows += page.Count;

                var recordIds = page.Select(row => row.RecordId).ToArray();
                using var pageInputBudget = budget.ReserveTransient(page.Sum(row => 128L + 2L * row.ContentLength),
                    "residual history JSON page");
                var contentByRecordId = ctx.DailyRecords.AsNoTracking().Where(r => recordIds.Contains(r.RecordId))
                    .Select(r => new
                    {
                        r.RecordId,
                        ContentJson = r.ContentJson == null ? null : r.ContentJson.Substring(0,
                            (r.ContentJson + "x").Length - 1 > CalibrationConstants.ResidualHistoryJsonMaxCharacters + 1
                                ? CalibrationConstants.ResidualHistoryJsonMaxCharacters + 1 : (r.ContentJson + "x").Length - 1)
                    }).ToDictionary(row => row.RecordId, row => row.ContentJson);
                if (contentByRecordId.Count != page.Count)
                    throw new CalibrationCapacityException("殘留歷史在擷取期間變更，完整回看已拒絕。");

                long pageGraphBytes = 0;
                foreach (var content in contentByRecordId.Values)
                {
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    pageGraphBytes = checked(pageGraphBytes + budget.ValidateDailyRecordJson(content, "殘留歷史"));
                }
                if (historyBytes + pageGraphBytes > CalibrationConstants.ResidualHistoryObjectGraphBudgetBytes)
                    throw new CalibrationCapacityException("殘留歷史反序列化物件圖超過總量安全上限；完整回看未截斷。");
                historyBytes += pageGraphBytes;
                budget.Charge(pageGraphBytes, "residual history deserialized object graphs");

                foreach (var row in page)
                {
                    var contentJson = contentByRecordId[row.RecordId];
                    if (string.IsNullOrWhiteSpace(contentJson)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<DailyAnalysisRecord>(contentJson);
                        if (rec == null) continue;
                        if (!byHost.TryGetValue(row.HostId, out var list))
                        {
                            list = new List<DailyAnalysisRecord>();
                            byHost[row.HostId] = list;
                        }
                        list.Add(rec);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(ex, "[校準] 歷史紀錄反序列化失敗，略過此筆");
                    }
                }
                afterRecordId = page[^1].RecordId;
            }
            if (loadedRows != rowCount)
                throw new CalibrationCapacityException("殘留歷史在擷取期間變更，完整回看已拒絕。");
        }

        return byHost;
    }

    /// <summary>
    /// 某主機日的歷史：從已載入的分組中切出該日往前一個回看窗（不含當日）。
    /// 判定端 <c>HasCrossDayRecurrence</c> 自己也會依窗過濾，這裡先切是為了少傳無關的日子。
    /// </summary>
    private static List<DailyAnalysisRecord> HistoryForCandidate(
        Dictionary<long, List<DailyAnalysisRecord>> historyByHost, long hostId, DateTime targetDate)
    {
        if (!historyByHost.TryGetValue(hostId, out var all)) return new List<DailyAnalysisRecord>();

        var from = targetDate.Date.AddDays(-(ResidualCredentialDetector.HistoryWindowDaysForCalibration - 1));
        return all.Where(r => r.Date.Date >= from && r.Date.Date < targetDate.Date).ToList();
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

    private Dictionary<DateTime, List<PrtgHostMapRow>> ReadCalibrationHostMaps(
        DateTime from, DateTime to,
        IReadOnlyCollection<(long Objid, long DeviceObjid, string? Status, string SensorType, string? Category)> sensorStatuses,
        PrtgCalibrationCaptureBudget budget, out bool exceeded)
    {
        var rowsByDay = new Dictionary<DateTime, List<PrtgHostMapRow>>();
        var totalRows = 0;
        exceeded = false;
        var deviceIds = sensorStatuses.Select(s => s.DeviceObjid).Distinct().ToArray();
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            var cutoff = day.Date.AddDays(-29);
            using var ctx = _contextFactory();
            var latestDate = ctx.PrtgHostMaps.AsNoTracking()
                .Where(m => m.MapDate >= cutoff && m.MapDate <= day.Date)
                .Max(m => (DateTime?)m.MapDate);
            if (latestDate == null)
            {
                rowsByDay[day] = [];
                continue;
            }

            var dayRows = new List<PrtgHostMapRow>();
            foreach (var chunk in deviceIds.Chunk(1000))
            {
                var remaining = MaxMapRowsForCalibration - totalRows - dayRows.Count;
                var query = ctx.PrtgHostMaps.AsNoTracking()
                    .Where(m => m.MapDate == latestDate.Value && chunk.Contains(m.DeviceObjid));
                var matchingRows = query.LongCount();
                if (matchingRows > remaining) { exceeded = true; return new(); }
                budget.Charge(matchingRows * 192L, "bounded host-map snapshot page objects and buffers");
                var projected = query.OrderBy(m => m.DeviceObjid)
                    .Take(remaining + 1)
                    .Select(m => new
                    {
                        m.MapDate, m.DeviceObjid, m.HostId,
                        MapStatusLength = m.MapStatus == null ? 0 : (m.MapStatus + "x").Length - 1,
                        MapStatus = m.MapStatus == null ? "" : m.MapStatus.Substring(0,
                            (m.MapStatus + "x").Length - 1 > CalibrationMapStatusMaxChars + 1
                                ? CalibrationMapStatusMaxChars + 1 : (m.MapStatus + "x").Length - 1)
                    })
                    .ToList();
                if (projected.Count > remaining) { exceeded = true; return new(); }
                if (projected.Any(m => m.MapStatusLength > CalibrationMapStatusMaxChars))
                    throw new CalibrationCapacityException("PRTG 主機對應 MapStatus 超過欄位上限；校準匯出已拒絕。");
                dayRows.AddRange(projected.Select(m => new PrtgHostMapRow
                {
                    MapDate = m.MapDate, DeviceObjid = m.DeviceObjid, HostId = m.HostId, MapStatus = m.MapStatus
                }));
            }
            totalRows += dayRows.Count;
            rowsByDay[day] = dayRows;
        }
        return rowsByDay;
    }

    private (string? Content, long Version, int ContentLength) ReadCalibrationBlobSnapshot(string key, int maxCharacters)
    {
        using var ctx = _contextFactory();
        var row = ctx.Blobs.AsNoTracking().Where(b => b.BlobKey == key)
            .Select(b => new
            {
                b.Version,
                ContentLength = b.Content.Length,
                Content = b.Content.Substring(0, maxCharacters + 1)
            })
            .FirstOrDefault();
        return row == null ? (null, 0, 0) : (row.Content, row.Version, row.ContentLength);
    }

    private List<(long Objid, long DeviceObjid, string? Status, string SensorType, string? Category)> ReadCalibrationSensorStatuses(
        IReadOnlyCollection<long> selectedIds, out bool exceeded)
    {
        var statuses = new List<(long Objid, long DeviceObjid, string? Status, string SensorType, string? Category)>();
        exceeded = false;
        foreach (var chunk in selectedIds.Chunk(1000))
        {
            var remaining = MaxPolicySensorsForCalibration - statuses.Count;
            using var ctx = _contextFactory();
            var rows = ctx.PrtgSensors.AsNoTracking()
                .Where(s => !s.Paused && chunk.Contains(s.Objid))
                .OrderBy(s => s.Objid)
                .Take(remaining + 1)
                .Select(s => new
                {
                    s.Objid, s.DeviceObjid,
                    StatusLength = s.Status == null ? 0 : (s.Status + "x").Length - 1,
                    Status = s.Status == null ? null : s.Status.Substring(0,
                        (s.Status + "x").Length - 1 > CalibrationSensorStatusMaxChars + 1
                            ? CalibrationSensorStatusMaxChars + 1 : (s.Status + "x").Length - 1),
                    SensorTypeLength = s.SensorType == null ? 0 : (s.SensorType + "x").Length - 1,
                    SensorType = s.SensorType == null ? "" : s.SensorType.Substring(0,
                        (s.SensorType + "x").Length - 1 > CalibrationSensorTypeMaxChars + 1
                            ? CalibrationSensorTypeMaxChars + 1 : (s.SensorType + "x").Length - 1),
                    CategoryLength = s.Category == null ? 0 : (s.Category + "x").Length - 1,
                    Category = s.Category == null ? null : s.Category.Substring(0,
                        (s.Category + "x").Length - 1 > CalibrationCategoryMaxChars + 1
                            ? CalibrationCategoryMaxChars + 1 : (s.Category + "x").Length - 1)
                })
                .ToList();
            if (rows.Count > remaining) { exceeded = true; return []; }
            if (rows.Any(row => row.StatusLength > CalibrationSensorStatusMaxChars ||
                    row.SensorTypeLength > CalibrationSensorTypeMaxChars ||
                    row.CategoryLength > CalibrationCategoryMaxChars))
                throw new CalibrationCapacityException("PRTG sensor 狀態、類型或分類文字超過欄位上限；校準匯出已拒絕。");
            statuses.AddRange(rows.Select(s => (s.Objid, s.DeviceObjid, s.Status, s.SensorType, s.Category)));
        }
        return statuses;
    }

    private Dictionary<long, long> ReadCalibrationTimelineVersions(IReadOnlyCollection<long> selectedIds, out bool exceeded)
    {
        var versions = selectedIds.ToDictionary(id => id, _ => 0L);
        exceeded = false;
        foreach (var chunk in selectedIds.Chunk(1000))
        {
            using var ctx = _contextFactory();
            var keys = chunk.Select(id => PrtgSensorTimelineStore.Prefix + id).ToArray();
            var rows = ctx.Blobs.AsNoTracking()
                .Where(b => keys.Contains(b.BlobKey))
                .Select(b => new { b.BlobKey, b.Version })
                .Take(MaxTimelineBlobRowsForCalibration + 1)
                .ToList();
            if (rows.Count > MaxTimelineBlobRowsForCalibration) { exceeded = true; return versions; }
            foreach (var row in rows)
                if (long.TryParse(row.BlobKey.AsSpan(PrtgSensorTimelineStore.Prefix.Length), out var sensorId) && versions.ContainsKey(sensorId))
                    versions[sensorId] = row.Version;
        }
        return versions;
    }

    private Dictionary<long, long> ReadCalibrationResourceIdentityVersions(IReadOnlyCollection<long> selectedIds,
        out bool exceeded)
    {
        var versions = selectedIds.ToDictionary(id => id, _ => 0L);
        exceeded = false;
        foreach (var chunk in selectedIds.Chunk(1000))
        {
            using var ctx = _contextFactory();
            var keys = chunk.Select(id => PrtgResourceIdentityStore.Prefix +
                id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var rows = ctx.Blobs.AsNoTracking().Where(blob => keys.Contains(blob.BlobKey))
                .Select(blob => new { blob.BlobKey, blob.Version })
                .Take(1001).ToList();
            if (rows.Count > 1000) { exceeded = true; return versions; }
            foreach (var row in rows)
            {
                var suffix = row.BlobKey.AsSpan(PrtgResourceIdentityStore.Prefix.Length);
                if (long.TryParse(suffix, out var sensorId) && versions.ContainsKey(sensorId))
                    versions[sensorId] = row.Version;
            }
        }
        return versions;
    }

    private static string CalibrationVersionStamp(IReadOnlyDictionary<long, long> versions) =>
        string.Join("|", versions.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));

    private static string CalibrationRuleSettingsStamp(SystemSettings settings)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.PrtgEnabled,
            settings.PrtgUrl,
            settings.PrtgRetentionDays,
            settings.PrtgFetchStrategy,
            settings.RawEventRetentionDays,
            SensorTypeWhitelist = settings.PrtgSensorTypeWhitelist ?? []
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
    }

    private sealed record ValidatedPrtgRuleSnapshot(List<KnownIssueRule> Rules, string Stamp);

    private ValidatedPrtgRuleSnapshot GetValidatedPrtgRuleSnapshot(PrtgCalibrationCaptureBudget budget)
    {
        // 校準維持唯讀並沿用正式規則的有效內容：成功載入（含刻意留空）就驗證原清單；
        // 規則庫不存在或載入失敗時只在記憶體套用內建種子，不嘗試寫回。
        List<KnownIssueRule> sourceRules;
        if (!_ruleStore.Exists)
        {
            sourceRules = KnownIssueSeed.CreateRules();
        }
        else
        {
            budget.Charge(24L * 1024 * 1024, "bounded rules JSON parse and validated snapshot");
            var loaded = _ruleStore.LoadBounded(1024 * 1024);
            if (loaded.IsCapacityExceeded)
                throw new CalibrationCapacityException(loaded.Error ?? "規則資料超過校準容量上限，已拒絕使用替代規則。" );
            sourceRules = loaded.Success && loaded.Content != null
                ? loaded.Content.Rules
                : KnownIssueSeed.CreateRules();
        }
        var validated = RuleValidator.Validate(sourceRules).ValidRules
            .Where(r => string.Equals(r.Platform, "prtg", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var stampPayload = validated.Select(r => new
            {
                r.Id, r.Enabled, r.PrtgRuleCode, r.PrtgThreshold, r.PrtgSensorCategory,
                Category = r.Category.ToString(), Severity = r.Severity.ToString(), r.ElevatesDayRisk, r.Description
            }).ToArray();
        var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(stampPayload)));
        return new ValidatedPrtgRuleSnapshot(validated, stamp);
    }

    private RuleMagnitudeAnalysis GetRuleMagnitudeAnalysis(DateTime anchor, SystemSettings settings, bool forceRefresh,
        PrtgCalibrationCaptureBudget budget)
    {
        var hostMapDataRevision = _prtgStore.ReadHostMapDataRevision();
        budget.Charge(16L * 1024 * 1024, "bounded monitoring policy and parsed arrays");
        var policySnapshot = ReadCalibrationBlobSnapshot(PrtgMonitoringPolicyStore.BlobKey, MaxPolicyBlobCharactersForCalibration);
        var capacityExceeded = policySnapshot.ContentLength > MaxPolicyBlobCharactersForCalibration ||
            (policySnapshot.Content?.Length ?? 0) > MaxPolicyBlobCharactersForCalibration;
        if (!capacityExceeded && !string.IsNullOrWhiteSpace(policySnapshot.Content))
        {
            var policyGraphBytes = budget.ValidatePolicyJson(policySnapshot.Content, "PRTG monitoring policy");
            budget.Charge(policyGraphBytes, "parsed PRTG policy object graph");
        }
        var policy = new PrtgMonitoringPolicy();
        if (!capacityExceeded && !string.IsNullOrWhiteSpace(policySnapshot.Content))
        {
            try { policy = JsonSerializer.Deserialize<PrtgMonitoringPolicy>(policySnapshot.Content, LogForesight.Core.Persistence.LfJsonOptions.Pretty) ?? new PrtgMonitoringPolicy(); }
            catch (JsonException) { }
        }
        var scopeVersion = new EfJsonBlobStore(_contextFactory, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion();
        var from = anchor.Date.AddDays(-(CalibrationConstants.RuleThresholdWindowDays - 1));
        var to = anchor.Date;
        var rawPolicySensorIds = policy.SensorIds ?? [];
        var policySensorIds = rawPolicySensorIds.Distinct().OrderBy(x => x).Take(MaxPolicySensorsForCalibration + 1).ToArray();
        if (policySensorIds.Length > MaxPolicySensorsForCalibration) capacityExceeded = true;
        var policyReady = !capacityExceeded && settings.PrtgEnabled && !string.IsNullOrWhiteSpace(settings.PrtgUrl) &&
            policy.HostIds is { Count: > 0 } && policySensorIds.Length > 0 &&
            !string.IsNullOrWhiteSpace(policy.CoreSystemId) && !string.IsNullOrWhiteSpace(policy.SourceGeneration) &&
            !string.IsNullOrWhiteSpace(policy.EndpointHint) && policy.ValidFrom != default &&
            !string.IsNullOrWhiteSpace(policy.SourceTimeZoneId) && !string.IsNullOrWhiteSpace(policy.SourceCultureName) &&
            policy.Ready(settings.PrtgUrl);
        var ruleSnapshot = GetValidatedPrtgRuleSnapshot(budget);
        var allPrtgRules = ruleSnapshot.Rules;
        var allRules = allPrtgRules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.PrtgRuleCode))
            .ToList();
        var statuses = ReadCalibrationSensorStatuses(policySensorIds, out var sensorLimit);
        budget.Charge(statuses.Count * 256L, "policy sensor status rows");
        if (sensorLimit) capacityExceeded = true;
        var resourceIdentityVersions = ReadCalibrationResourceIdentityVersions(policySensorIds, out var resourceVersionLimit);
        budget.Charge(resourceIdentityVersions.Count * 48L, "resource identity version tokens");
        var resourceIdentityVersionStamp = CalibrationVersionStamp(resourceIdentityVersions);
        budget.Charge(policySensorIds.Length * 256L, "resource identity rows");
        var resourceIdentities = _prtgStore.GetResourceIdentities(policySensorIds);
        var resourceIdentityStamp = string.Join("|", resourceIdentities.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}:{p.Value.Epoch}:{p.Value.Generation}:{p.Value.ChannelGeneration}"));
        var mapLimit = false;
        var mapsByDay = policyReady && !capacityExceeded
            ? ReadCalibrationHostMaps(from, to, statuses, budget, out mapLimit)
            : new Dictionary<DateTime, List<PrtgHostMapRow>>();
        if (policyReady && !capacityExceeded && mapLimit) capacityExceeded = true;
        var timelineRowsLimit = false;
        var timelineVersions = policyReady && !capacityExceeded
            ? ReadCalibrationTimelineVersions(policySensorIds, out timelineRowsLimit)
            : policySensorIds.ToDictionary(id => id, _ => 0L);
        if (policyReady && !capacityExceeded && timelineRowsLimit) capacityExceeded = true;
        if (resourceVersionLimit) capacityExceeded = true;
        var mapStamp = string.Join("|", mapsByDay.OrderBy(day => day.Key).SelectMany(day => day.Value.OrderBy(m => m.DeviceObjid)
            .Select(m => $"{day.Key:yyyy-MM-dd}:{m.DeviceObjid}:{m.HostId}:{m.MapStatus}")));
        var ruleStamp = ruleSnapshot.Stamp;
        var timelineStamp = string.Join("|", timelineVersions.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
        var sensorStamp = CalibrationSensorStamp(statuses);
        var settingsStamp = CalibrationRuleSettingsStamp(settings);
        var fingerprintInput = $"{_backendCacheIdentity}|{anchor.Date:O}|{policySnapshot.Version}|{scopeVersion}|map-data:{hostMapDataRevision}|resource-identity:{resourceIdentityStamp}|resource-identity-versions:{resourceIdentityVersionStamp}|settings:{settingsStamp}|{ruleStamp}|{timelineStamp}|{mapStamp}|{sensorStamp}|capacity:{capacityExceeded}";
        var sourceFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprintInput)));
        lock (_ruleMagnitudeLock)
            if (!forceRefresh && _cachedRuleMagnitudeAnalysis is { } cached && cached.SourceFingerprint == sourceFingerprint && DateTime.Now - _cachedRuleMagnitudeAt < CacheTtl)
                return cached;

        var capturedNow = DateTimeOffset.Now;
        var anchorDayEnd = new DateTimeOffset(DateTime.SpecifyKind(anchor.Date.AddDays(1), DateTimeKind.Local));
        var evaluationAsOf = anchor.Date < DateTime.Today ? anchorDayEnd : capturedNow;
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{sourceFingerprint}|asof:{evaluationAsOf:O}")));
        var explanations = new List<string>();
        if (!policyReady && !capacityExceeded) explanations.Add("校準不可用：尚無已確認且符合目前端點的 PRTG 監控政策。");
        var samples = new List<CalibrationRuleMagnitudeRow>();
        var formalHits = new Dictionary<(string Code, string Id, string? Category, int Threshold), int>();
        var coveredDays = new HashSet<DateTime>();
        var totalHits = 0; var downHits = 0; var flapHits = 0; var warningHits = 0; var silentHits = 0;
        var sourceMismatch = false; var mappingMismatch = false; var generationMismatch = false; var coverageGap = false;
        var captureBytes = 0L;
        var captureChanged = false;
        if (policyReady && !capacityExceeded)
        {
            budget.Charge(MaxMagnitudeSamplesForCalibration * 320L + allRules.Count * 1024L,
                "timeline finding samples and formal hit aggregates");
            var selected = policySensorIds.ToHashSet();
            var sensorRows = statuses.Where(s => selected.Contains(s.Objid)).OrderBy(s => s.Objid).ToArray();
            // 門檻探索分佈獨立於規則啟用狀態；升級或管理者停用規則後，仍保留相同的低門檻事件樣本。
            var lowRules = new[] { PrtgRuleEvaluator.RuleDown, PrtgRuleEvaluator.RuleFlapping, PrtgRuleEvaluator.RuleWarning }
                .Select(code => new KnownIssueRule
                {
                    Id = $"calibration-{code}", Platform = "prtg", Enabled = true,
                    PrtgRuleCode = code, PrtgThreshold = 1
                }).ToList();
            foreach (var sensor in sensorRows)
            {
                using var timelineInputBudget = budget.ReserveTransient(
                    MaxTimelineBlobCharactersForCalibration * 2L, "single PRTG timeline JSON input");
                var snapshot = ReadCalibrationBlobSnapshot(PrtgSensorTimelineStore.Prefix + sensor.Objid, MaxTimelineBlobCharactersForCalibration);
                if (snapshot.Version != timelineVersions.GetValueOrDefault(sensor.Objid)) { captureChanged = true; break; }
                if (string.IsNullOrWhiteSpace(snapshot.Content)) { coverageGap = true; continue; }
                if (snapshot.ContentLength > MaxTimelineBlobCharactersForCalibration || snapshot.Content.Length > MaxTimelineBlobCharactersForCalibration)
                { capacityExceeded = true; break; }
                captureBytes += (long)snapshot.Content.Length * sizeof(char);
                if (snapshot.Content.Length > MaxTimelineBlobCharactersForCalibration || captureBytes > MaxTimelineCaptureBytesForCalibration)
                { capacityExceeded = true; break; }
                var timelineGraphBytes = budget.ValidateTimelineJson(snapshot.Content, "PRTG timeline");
                using var timelineGraphBudget = budget.ReserveTransient(timelineGraphBytes, "deserialized timeline object graph and evaluator state");
                PrtgSensorTimelineEvidence? proof;
                try { proof = JsonSerializer.Deserialize<PrtgSensorTimelineEvidence>(snapshot.Content, LogForesight.Core.Persistence.LfJsonOptions.Pretty); }
                catch (JsonException) { proof = null; }
                if (proof == null || proof.Coverage.Count > 20000 || proof.States.Count > 20000)
                { generationMismatch = true; continue; }
                if (proof.SensorId != sensor.Objid || string.IsNullOrWhiteSpace(proof.ResourceGeneration) ||
                    proof.Coverage.Any(c => c.SensorObjid != proof.SensorId || c.SourceGeneration != proof.SourceGeneration || c.ResourceGeneration != proof.ResourceGeneration) ||
                    proof.Coverage.Any(c => c.From < proof.ValidFrom) ||
                    proof.States.Any(s => s.SensorObjid != proof.SensorId || s.SourceGeneration != proof.SourceGeneration ||
                        s.ResourceGeneration != proof.ResourceGeneration || s.At < proof.ValidFrom))
                { generationMismatch = true; continue; }
                if (proof.SourceGeneration != policy.SourceGeneration) { sourceMismatch = true; continue; }
                if (!policy.HostIds.Contains(proof.HostId) || !resourceIdentities.TryGetValue(sensor.Objid, out var resourceIdentity) ||
                    !PrtgResourceQualification.IsCurrent(proof, resourceIdentity, policy.SourceGeneration,
                        sensor.Objid, sensor.DeviceObjid, proof.HostId)) { mappingMismatch = true; continue; }
                var proofDays = false;
                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    var start = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Local));
                    var dayEnd = new DateTimeOffset(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Local));
                    var end = dayEnd < evaluationAsOf ? dayEnd : evaluationAsOf;
                    if (end <= start) continue;
                    if (proof.ValidFrom > end) { coverageGap = true; continue; }
                    var spans = proof.Coverage.Where(c => c.SensorObjid == sensor.Objid && c.SourceGeneration == proof.SourceGeneration &&
                        c.ResourceGeneration == proof.ResourceGeneration && c.From < end && c.Through > start)
                        .Select(c => (From: c.From < start ? start : c.From, Through: c.Through > end ? end : c.Through))
                        .OrderBy(c => c.From).ToArray();
                    var cursor = start;
                    foreach (var span in spans)
                    {
                        if (span.From > cursor) break;
                        if (span.Through > cursor) cursor = span.Through;
                    }
                    var fullCoverage = cursor >= end;
                    if (!fullCoverage) coverageGap = true;
                    if (spans.Length == 0) continue;
                    if (!mapsByDay.TryGetValue(day, out var mapRows)) { mappingMismatch = true; continue; }
                    var mappedHost = mapRows.Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && m.DeviceObjid == sensor.DeviceObjid)
                        .Select(m => (long?)m.HostId).FirstOrDefault();
                    if (mappedHost != proof.HostId) { mappingMismatch = true; continue; }
                    var bounded = new PrtgSensorTimelineEvidence
                    {
                        SensorId = proof.SensorId, HostId = proof.HostId, SourceGeneration = proof.SourceGeneration,
                        ResourceGeneration = proof.ResourceGeneration, IdentityEpoch = proof.IdentityEpoch,
                        ChannelGeneration = proof.ChannelGeneration, IdentityFingerprint = proof.IdentityFingerprint,
                        MappingRevision = proof.MappingRevision, ValidFrom = proof.ValidFrom, LastAttemptAt = proof.LastAttemptAt,
                        QualityReason = proof.QualityReason, Coverage = proof.Coverage.Where(c => c.From < end)
                            .Select(c => c with { Through = c.Through > end ? end : c.Through }).Where(c => c.Through > c.From).ToList(),
                        States = proof.States.Where(s => s.At <= end).ToList()
                    };
                    var statusInput = new PrtgSensorStatusInput(sensor.Objid, sensor.DeviceObjid, sensor.Status, sensor.SensorType, sensor.Category);
                    var evidence = new Dictionary<long, PrtgSensorTimelineEvidence> { [sensor.Objid] = bounded };
                    var coveredPeriods = bounded.Periods(start, end);
                    if (coveredPeriods.Count > 0) proofDays = true;
                    var knownCursor = start;
                    var fullKnownDay = fullCoverage && end == dayEnd && coveredPeriods.Count > 0;
                    foreach (var period in coveredPeriods.OrderBy(p => p.From))
                    {
                        if (period.From > knownCursor || !IsCalibrationKnownStatus(period.Status)) fullKnownDay = false;
                        if (period.Through > knownCursor) knownCursor = period.Through;
                    }
                    if (knownCursor < end) fullKnownDay = false;
                    if (fullKnownDay) coveredDays.Add(day);
                    var minimum = PrtgCoveredRuleEvaluator.Evaluate(day, [statusInput], evidence, lowRules);
                    foreach (var finding in minimum)
                    {
                        if (samples.Count >= MaxMagnitudeSamplesForCalibration) { capacityExceeded = true; break; }
                        samples.Add(new CalibrationRuleMagnitudeRow(finding.RuleCode, day, finding.DeviceObjid, finding.SensorObjid,
                            finding.ThresholdMagnitude ?? finding.Magnitude)
                        { ThresholdMagnitude = finding.ThresholdMagnitude, DayMagnitude = finding.Magnitude });
                    }
                    if (capacityExceeded) break;
                    var formal = PrtgCoveredRuleEvaluator.Evaluate(day, [statusInput], evidence, allRules);
                    foreach (var finding in formal)
                    {
                        totalHits++;
                        if (finding.RuleCode == PrtgRuleEvaluator.RuleDown) downHits++;
                        else if (finding.RuleCode == PrtgRuleEvaluator.RuleFlapping) flapHits++;
                        else if (finding.RuleCode == PrtgRuleEvaluator.RuleWarning) warningHits++;
                        else if (finding.RuleCode == PrtgRuleEvaluator.RuleSilent) silentHits++;
                        var key = (finding.RuleCode, finding.Rule.Id, finding.SensorCategory, finding.Rule.PrtgThreshold);
                        formalHits[key] = formalHits.GetValueOrDefault(key) + 1;
                    }
                }
                if (proofDays == false && proof.Coverage.Count > 0) coverageGap = true;
                if (capacityExceeded) break;
            }
            var finalVersions = ReadCalibrationTimelineVersions(policySensorIds, out var finalRowsLimit);
            var finalStatuses = ReadCalibrationSensorStatuses(policySensorIds, out var finalSensorLimit);
            var finalSensorStamp = CalibrationSensorStamp(finalStatuses);
            var finalResourceIdentityVersions = ReadCalibrationResourceIdentityVersions(policySensorIds, out var finalResourceVersionLimit);
            if (finalRowsLimit || finalSensorLimit || finalSensorStamp != sensorStamp ||
                finalVersions.Any(v => timelineVersions.GetValueOrDefault(v.Key) != v.Value) ||
                finalResourceVersionLimit || resourceIdentityVersionStamp != CalibrationVersionStamp(finalResourceIdentityVersions) ||
                scopeVersion != new EfJsonBlobStore(_contextFactory, EfPrtgStore.ScopeRevisionBlobKey).ReadVersion() ||
                policySnapshot.Version != new EfJsonBlobStore(_contextFactory, PrtgMonitoringPolicyStore.BlobKey).ReadVersion()) captureChanged = true;
        }
        if (!string.Equals(ruleStamp, GetValidatedPrtgRuleSnapshot(budget).Stamp, StringComparison.Ordinal)) captureChanged = true;
        if (hostMapDataRevision != _prtgStore.ReadHostMapDataRevision()) captureChanged = true;
        if (captureChanged) explanations.Add("校準不可用：收集期間來源版本或主機對應變更；已拒絕混合版本結果。");
        if (sourceMismatch) explanations.Add("校準不可用：timeline 來源世代與目前政策不一致。");
        if (mappingMismatch) explanations.Add("校準不可用：timeline 範圍修訂或該日主機對應已變更。");
        if (generationMismatch) explanations.Add("校準不可用：timeline resource generation、coverage 或 state 身分不一致，可信品質無效。");
        if (coverageGap) explanations.Add("可信 timeline 的已涵蓋區間仍依正式 evaluator 計入 finding 與分佈；不完整或含 Unknown 的 sensor-day 不計入完整涵蓋日數，缺口不以 state-change 或鏡像資料補足。");
        explanations.Add("本次重算的 FormalCurrentHitCounts 與 MagnitudeSamples 僅涵蓋可信 status timeline 的 down、flapping、warning 三種規則。DailyRuleHits 是既有持久化歷史彙總，可能包含其他規則族群，不能當成本次重算結果。");
        explanations.Add("未支援規則代碼會列在 UnsupportedByMagnitudeAnalysisRuleCodes；這代表本分析未評估，不能解讀為零命中或符合規則。silent 所需的 presence 證據不在本分析輸入中；磁碟趨勢與資源時段規則也不由 status timeline evaluator 重算。");
        explanations.Add("CurrentRules 的 RuleEnabled 僅表示規則目錄開關；本匯出不證明資源規則具有有效 profile 或來源資格，也不證明 CPU／記憶體正式規則具備同版 trial 與 Maintain 授權。");
        explanations.Add("MagnitudeSummaries.HitsAtCurrentThreshold 是不限分類規則門檻對可信樣本的分佈對照，不代表正式 finding 命中數；正式命中依 rule id/category 列於 FormalCurrentHitCounts。");
        explanations.Add("每個 sensor timeline 目前最多保存 31 天；超過此窗的 56 天充足門檻不以 raw state-change 或舊鏡像補足。");
        if (capacityExceeded || captureChanged)
        {
            samples.Clear(); formalHits.Clear(); coveredDays.Clear();
            totalHits = downHits = flapHits = warningHits = silentHits = 0;
            if (capacityExceeded)
                explanations.Add("校準不可用：可信 timeline 輸入超出校準明確容量上限；已拒收整份分析，沒有輸出部分結果。");
        }
        var summaries = samples.GroupBy(r => r.RuleCode, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var values = g.Select(r => r.ThresholdMagnitude ?? r.Magnitude).OrderBy(v => v).ToList();
            var global = allRules.Where(r => r.PrtgRuleCode == g.Key && r.PrtgSensorCategory == null).OrderBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault();
            var threshold = global?.PrtgThreshold ?? 0;
            return new CalibrationRuleMagnitudeSummary(g.Key, values.Count, values[0], Percentile(values, .50), Percentile(values, .90),
                Percentile(values, .99), values[^1], threshold == 0 ? 0 : values.Count(v => v >= threshold), threshold);
        }).OrderBy(r => r.RuleCode, StringComparer.Ordinal).ToList();
        var blocked = !policyReady || capacityExceeded || captureChanged || (coveredDays.Count == 0 && samples.Count == 0);
        if (blocked && policyReady && !explanations.Any(x => x.Contains("不可用：", StringComparison.Ordinal)))
            explanations.Add("校準不可用：as-of 視窗沒有可評估的可信保存 timeline 區間。");
        else if (!blocked && samples.Count == 0)
            explanations.Add("可信 timeline 涵蓋存在，但本視窗沒有正式 finding 樣本；門檻分佈仍不足。");
        var currentRules = allPrtgRules.Where(r => !string.IsNullOrWhiteSpace(r.PrtgRuleCode))
            .Select(r =>
            {
                var code = r.PrtgRuleCode!;
                var thresholdKind = code switch
                {
                    PrtgRuleEvaluator.RuleDown or PrtgRuleEvaluator.RuleWarning => "minutes",
                    PrtgRuleEvaluator.RuleFlapping => "state-transitions",
                    PrtgRuleEvaluator.RuleSilent => "days",
                    PrtgRuleEvaluator.RuleDiskFreeTrend => "disk-trend-profile",
                    PrtgRuleEvaluator.RuleResourceCpuPressure or PrtgRuleEvaluator.RuleResourceMemoryPressure or
                        PrtgRuleEvaluator.RuleResourceDiskPressure => "resource-period-profile",
                    _ => "unspecified"
                };
                int? threshold = code is PrtgRuleEvaluator.RuleDiskFreeTrend or
                    PrtgRuleEvaluator.RuleResourceCpuPressure or PrtgRuleEvaluator.RuleResourceMemoryPressure or
                    PrtgRuleEvaluator.RuleResourceDiskPressure ? null : r.PrtgThreshold;
                return new CalibrationPrtgRuleThresholdInfo(
                    code, threshold, r.Category.ToString(), r.Severity.ToString(),
                    r.ElevatesDayRisk, r.Description, r.PrtgSensorCategory, thresholdKind, r.Enabled);
            }).ToList();
        var result = new RuleMagnitudeAnalysis(sourceFingerprint, fingerprint, ruleStamp, hostMapDataRevision,
            policySnapshot.Version, scopeVersion, policySensorIds, resourceIdentityStamp, resourceIdentityVersionStamp,
            timelineStamp, sensorStamp, settingsStamp, evaluationAsOf,
            samples, summaries, formalHits.Select(kv => new CalibrationFormalRuleHitCount(kv.Key.Code, kv.Key.Id, kv.Key.Category, kv.Key.Threshold, kv.Value)).ToList(),
            explanations, currentRules, coveredDays.Count, totalHits, downHits, flapHits, warningHits, silentHits, blocked);
        lock (_ruleMagnitudeLock) { _cachedRuleMagnitudeAnalysis = result; _cachedRuleMagnitudeAt = DateTime.Now; }
        return result;
    }

    private DateTime _cachedRuleMagnitudeAt;

    private static string BuildAssessmentCacheKey(DateTime anchor, SystemSettings settings, List<PrtgSensorRow> sensors, string evidenceFingerprint)
    {
        var settingsStamp = $"{settings.PrtgEnabled}|{settings.PrtgUrl}|{settings.PrtgRetentionDays}|{settings.PrtgFetchStrategy}|{string.Join(",", settings.PrtgSensorTypeWhitelist ?? [])}";
        var sensorStamp = string.Join("|", sensors.OrderBy(s => s.Objid).Select(s => $"{s.Objid}:{s.DeviceObjid}:{s.SensorType}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{anchor:O}|{settingsStamp}|{sensorStamp}|{evidenceFingerprint}")));
    }

    /// <summary>最近秩位法（nearest-rank）的索引：第 ceil(fraction × N) 個值（0 起算後減 1），夾在 [0, N-1]。兩個分位數方法共用這一份。</summary>
    private static int NearestRankIndex(int count, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * count);
        if (rank < 1) rank = 1;
        if (rank > count) rank = count;
        return rank - 1;
    }

    private static bool IsCalibrationKnownStatus(string? status) =>
        PrtgSensorStatuses.IsUp(status) || PrtgSensorStatuses.IsDown(status) ||
        PrtgSensorStatuses.IsWarning(status) ||
        string.Equals(status?.Trim(), PrtgSensorStatuses.Paused, StringComparison.OrdinalIgnoreCase);

    private static string CalibrationSensorStamp(
        IEnumerable<(long Objid, long DeviceObjid, string? Status, string SensorType, string? Category)> sensors) =>
        string.Join("|", sensors.OrderBy(s => s.Objid).Select(s => $"{s.Objid}:{s.DeviceObjid}:{s.SensorType}:{s.Category}:{s.Status}"));

    /// <summary>最近秩位法（nearest-rank）：第 ceil(p × N) 個值，p 為 0～1。樣本為空時由呼叫端保證不會進來。</summary>
    private static int Percentile(List<int> sortedValues, double p) =>
        sortedValues[NearestRankIndex(sortedValues.Count, p)];

    /// <summary>最近秩位法（nearest-rank）：P(p) = sorted[ceil(p / 100.0 * n) - 1]，p 為 0～100；n == 0 時為 null。</summary>
    private static double? Percentile(List<double> sortedValues, double p) =>
        sortedValues.Count == 0 ? null : sortedValues[NearestRankIndex(sortedValues.Count, p / 100.0)];

    /// <summary>母體標準差（除以 n）；n == 0 時為 null。</summary>
    private static double? CalculatePopulationStdDev(IReadOnlyList<double> values, double? mean)
    {
        if (values == null || values.Count == 0 || !mean.HasValue) return null;
        var avg = mean.Value;
        double sumSquaredDiff = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            var diff = values[i] - avg;
            sumSquaredDiff += diff * diff;
        }
        return Math.Sqrt(sumSquaredDiff / values.Count);
    }
}

public sealed class CalibrationCapacityException(string message, bool retryable = false)
    : InvalidOperationException(message)
{
    public bool Retryable { get; } = retryable;
}
