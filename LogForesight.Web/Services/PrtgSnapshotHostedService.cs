using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Core.Service;
using Microsoft.Extensions.Hosting;
using NLog;
using LogLevel = NLog.LogLevel;

namespace LogForesight.Web.Services;

/// <summary>
/// PRTG 即時快照狀態 DTO（供 E2c UI 與監控查詢）。
/// </summary>
/// <param name="LastSuccessAt">上次成功快照時間</param>
/// <param name="LastSensorCount">上次成功快照處理的感測器數量</param>
/// <param name="IntervalMinutes">目前生效間隔（含退避後）</param>
/// <param name="ConsecutiveFailures">連續失敗次數</param>
/// <param name="PendingBuckets">累積器中待寫出的小時桶數量</param>
/// <param name="PendingSamples">累積器中待寫出的總樣本數量</param>
public sealed record PrtgSnapshotStatus(
    DateTime? LastSuccessAt,
    int LastSensorCount,
    int IntervalMinutes,
    int ConsecutiveFailures,
    int PendingBuckets,
    int PendingSamples);

/// <summary>
/// PRTG 數值快照背景服務（批次 E2b）：定期發送一次 table.json 取得即時快照數值，
/// 由內部累積器依小時平均後寫入 lf_prtg_values。
/// </summary>
public class PrtgSnapshotHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private static readonly Regex IntervalRegex = new(@"^(\d+(?:\.\d+)?)\s*([a-zA-Z]+)$", RegexOptions.Compiled);

    private readonly ISystemSettingsStore _settingsStore;
    private readonly StorageBackend _backend;
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncService _structureSync;
    private readonly PrtgBackfillService _backfill;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly PrtgSnapshotAccumulator _accumulator = new();

    private DateTime? _lastSuccessAt;

    /// <summary>
    /// 上次實際對 PRTG 發出快照請求的時間（成功或失敗都算）。退避的間隔從這裡量，
    /// 不是從上次成功——PRTG 回不來時上次成功會永遠停在過去，間隔條件恆成立，
    /// 服務就會每一輪都打一次全量快照，退避形同虛設。
    /// </summary>
    private DateTime? _lastAttemptAt;
    private int _lastSensorCount;
    private int _effectiveIntervalMinutes;
    private int _consecutiveFailures;

    private DateTime? _targetRefreshHour;
    private HashSet<long>? _targetObjids;
    private Dictionary<long, string>? _sensorTypes;

    internal Func<PrtgClient>? ClientFactory { get; set; }
    internal IRunConsole? Console { get; set; }
    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;
    internal PrtgSnapshotAccumulator Accumulator => _accumulator;
    /// <summary>執行輸出只留最近這麼多筆：站台長時間運行，無上限會一直長成記憶體洩漏。</summary>
    private const int MaxExecutionOutputs = 100;

    internal List<string> ExecutionOutputs { get; } = new();

    public PrtgSnapshotHostedService(
        ISystemSettingsStore systemSettingsStore,
        StorageBackend storageBackend,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncService structureSyncService,
        PrtgBackfillService backfillService,
        IHostApplicationLifetime lifetime)
    {
        _settingsStore = systemSettingsStore ?? throw new ArgumentNullException(nameof(systemSettingsStore));
        _backend = storageBackend ?? throw new ArgumentNullException(nameof(storageBackend));
        _schedulerRunState = schedulerRunState ?? throw new ArgumentNullException(nameof(schedulerRunState));
        _structureSync = structureSyncService ?? throw new ArgumentNullException(nameof(structureSyncService));
        _backfill = backfillService ?? throw new ArgumentNullException(nameof(backfillService));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

        var settings = _settingsStore.Get();
        _effectiveIntervalMinutes = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        if (_effectiveIntervalMinutes <= 0)
        {
            _effectiveIntervalMinutes = PrtgFetchStrategy.ConservativeIntervalMinutes;
        }

        _lifetime.ApplicationStopping.Register(OnStopping);
    }

    private int ExpectedSamplesPerHour => Math.Max(1, 60 / _effectiveIntervalMinutes);

    public PrtgSnapshotStatus GetStatus() =>
        new(
            LastSuccessAt: _lastSuccessAt,
            LastSensorCount: _lastSensorCount,
            IntervalMinutes: _effectiveIntervalMinutes,
            ConsecutiveFailures: _consecutiveFailures,
            PendingBuckets: _accumulator.BucketCount,
            PendingSamples: _accumulator.SampleCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PRTG 數值快照輪詢發生未預期錯誤（不影響下次輪詢）");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct = default)
    {
        var settings = _settingsStore.Get();

        // 1. PrtgEnabled 為 false → 不跑。
        if (!settings.PrtgEnabled) return;

        // 2. 連線設定不齊（PrtgUrl 空 或 PrtgClientFactory.HasUsableCredentials(settings) 為 false）→ 不跑。
        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings)) return;

        // 3. 結構同步執行中（PrtgStructureSyncService.IsRunning）→ 不跑。
        if (_structureSync.IsRunning) return;

        // 4. 歷史回填執行中（PrtgBackfillService 的狀態 IsRunning）→ 不跑。
        if (_backfill.GetStatus().IsRunning) return;

        // 5. 取數執行正在 PRTG 階段（SchedulerRunState 的快照裡，進度 phase 以 prtg- 開頭）→ 不跑。
        if (IsFetchRunningPrtgPhase()) return;

        // 若無退避，生效間隔隨策略設定更新
        if (_consecutiveFailures == 0)
        {
            _effectiveIntervalMinutes = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        }

        // 6. 距上次嘗試的間隔 < 目前生效間隔（見 §4 退避）→ 不跑。
        //    以「上次嘗試」而非「上次成功」量：失敗也要讓出間隔，退避才真的減輕 PRTG 負擔。
        var now = Now();
        if (_lastAttemptAt.HasValue && (now - _lastAttemptAt.Value) < TimeSpan.FromMinutes(_effectiveIntervalMinutes))
        {
            return;
        }

        _lastAttemptAt = now;
        try
        {
            await ExecuteSnapshotAsync(settings, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
    }

    private bool IsFetchRunningPrtgPhase()
    {
        if (!_schedulerRunState.IsRunning) return false;
        if (_schedulerRunState.PrtgCompleted) return false;
        var prtgPhase = _schedulerRunState.PrtgProgressPhase;
        if (prtgPhase != null && prtgPhase.StartsWith("prtg-", StringComparison.OrdinalIgnoreCase))
            return true;
        var mainPhase = _schedulerRunState.ProgressPhase;
        if (mainPhase != null && mainPhase.StartsWith("prtg-", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private async Task ExecuteSnapshotAsync(SystemSettings settings, DateTime now, CancellationToken ct)
    {
        string json;
        using (var client = CreateClient(settings))
        {
            json = await client.GetJsonAsync("api/table.json?content=sensors&columns=objid,lastvalue_raw,interval&count=50000", ct);
        }

        var currentHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        if (_targetRefreshHour == null || _targetRefreshHour.Value != currentHour || _targetObjids == null)
        {
            RefreshTargets(settings, now, currentHour);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PrtgClientException("PRTG 回傳內容非預期的 JSON 物件格式。");
        }

        long? treeSize = null;
        if (root.TryGetProperty("treesize", out var tsProp))
        {
            if (tsProp.ValueKind == JsonValueKind.Number && tsProp.TryGetInt64(out var tsNum))
                treeSize = tsNum;
            else if (tsProp.ValueKind == JsonValueKind.String && long.TryParse(tsProp.GetString(), out var tsStr))
                treeSize = tsStr;
        }

        if (!root.TryGetProperty("sensors", out var sensorsArr) || sensorsArr.ValueKind != JsonValueKind.Array)
        {
            sensorsArr = default;
        }

        int totalSensorsInResponse = sensorsArr.ValueKind == JsonValueKind.Array ? sensorsArr.GetArrayLength() : 0;
        if (treeSize.HasValue && treeSize.Value > 0 && totalSensorsInResponse < treeSize.Value)
        {
            var msg = $"[PRTG快照] 只取到 {totalSensorsInResponse} 個感測器（總數 {treeSize.Value}），快照結果可能被截斷。";
            WriteOutput(msg, LogLevel.Warn);
        }

        int unparsedIntervalCount = 0;
        int addedCount = 0;

        if (sensorsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in sensorsArr.EnumerateArray())
            {
                long? objid = null;
                if (el.TryGetProperty("objid", out var objidProp))
                {
                    if (objidProp.ValueKind == JsonValueKind.Number && objidProp.TryGetInt64(out var oNum))
                        objid = oNum;
                    else if (objidProp.ValueKind == JsonValueKind.String && long.TryParse(objidProp.GetString(), out var oStr))
                        objid = oStr;
                }

                if (!objid.HasValue) continue;

                if (_targetObjids == null || !_targetObjids.Contains(objid.Value))
                    continue;

                double? lastValueRaw = null;
                if (el.TryGetProperty("lastvalue_raw", out var valProp))
                {
                    if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out var vNum))
                        lastValueRaw = vNum;
                    else if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var vStr))
                        lastValueRaw = vStr;
                }

                if (!lastValueRaw.HasValue) continue;

                string? intervalStr = null;
                if (el.TryGetProperty("interval", out var intProp))
                {
                    if (intProp.ValueKind == JsonValueKind.String)
                        intervalStr = intProp.GetString();
                    else if (intProp.ValueKind == JsonValueKind.Number && intProp.TryGetInt64(out var iNum))
                        intervalStr = iNum.ToString(CultureInfo.InvariantCulture);
                }

                double intervalSeconds = ParseIntervalSeconds(intervalStr, ref unparsedIntervalCount);

                double sampleValue;
                if (_sensorTypes != null &&
                    _sensorTypes.TryGetValue(objid.Value, out var sensorType) &&
                    PrtgVolumeSensorTypes.IsVolume(sensorType))
                {
                    sampleValue = lastValueRaw.Value * 3600.0 / intervalSeconds;
                }
                else
                {
                    sampleValue = lastValueRaw.Value;
                }

                _accumulator.Add(objid.Value, now, sampleValue);
                addedCount++;
            }
        }

        if (unparsedIntervalCount > 0)
        {
            var msg = $"{unparsedIntervalCount} 顆感測器的掃描間隔無法解析，以 60 秒計";
            WriteOutput(msg, LogLevel.Warn);
        }

        RecordSuccess(settings, addedCount, now);

        var store = _backend.PrtgStore();
        var drainedRows = _accumulator.DrainBefore(currentHour, ExpectedSamplesPerHour, now);
        if (drainedRows.Count > 0)
        {
            store.MergeSampledValues(drainedRows);
        }
    }

    private void RefreshTargets(SystemSettings settings, DateTime now, DateTime currentHour)
    {
        var store = _backend.PrtgStore();
        var today = now.Date;
        var hostMaps = store.GetHostMapForDate(today);
        var okDeviceIds = hostMaps
            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
            .Select(m => m.DeviceObjid)
            .Distinct()
            .ToList();

        if (okDeviceIds.Count == 0)
        {
            var yesterday = today.AddDays(-1);
            hostMaps = store.GetHostMapForDate(yesterday);
            okDeviceIds = hostMaps
                .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
                .Select(m => m.DeviceObjid)
                .Distinct()
                .ToList();
        }

        var targets = store.GetValueFetchTargets(settings.PrtgSensorTypeWhitelist, okDeviceIds);
        _targetObjids = targets.ToHashSet();

        var statuses = store.GetSensorStatuses();
        _sensorTypes = statuses
            .GroupBy(s => s.Objid)
            .ToDictionary(g => g.Key, g => g.First().SensorType);

        _targetRefreshHour = currentHour;
    }

    private static double ParseIntervalSeconds(string? intervalStr, ref int unparsedCount)
    {
        if (!string.IsNullOrWhiteSpace(intervalStr))
        {
            var trimmed = intervalStr.Trim();
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var numSecs) && numSecs > 0)
            {
                return numSecs;
            }

            var match = IntervalRegex.Match(trimmed);
            if (match.Success)
            {
                var valStr = match.Groups[1].Value;
                var unit = match.Groups[2].Value;

                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0)
                {
                    if (string.Equals(unit, "s", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "sec", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "seconds", StringComparison.OrdinalIgnoreCase))
                    {
                        return val;
                    }
                    if (string.Equals(unit, "m", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "min", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "minutes", StringComparison.OrdinalIgnoreCase))
                    {
                        return val * 60.0;
                    }
                    if (string.Equals(unit, "h", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "hr", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unit, "hours", StringComparison.OrdinalIgnoreCase))
                    {
                        return val * 3600.0;
                    }
                }
            }
        }

        unparsedCount++;
        return 60.0;
    }

    private void RecordSuccess(SystemSettings settings, int addedCount, DateTime now)
    {
        _lastSuccessAt = now;
        _lastSensorCount = addedCount;

        var strategyInterval = PrtgFetchStrategy.Profile(settings.PrtgFetchStrategy).SnapshotIntervalMinutes;
        if (_effectiveIntervalMinutes != strategyInterval)
        {
            int oldInterval = _effectiveIntervalMinutes;
            _effectiveIntervalMinutes = strategyInterval;
            var msg = $"PRTG 快照成功，生效間隔由 {oldInterval} 分鐘恢復為策略設定值 {strategyInterval} 分鐘。";
            WriteOutput(msg, LogLevel.Info);
        }
        _consecutiveFailures = 0;
    }

    private void RecordFailure(Exception ex)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3 && _consecutiveFailures % 3 == 0)
        {
            int oldInterval = _effectiveIntervalMinutes;
            int newInterval = Math.Min(60, oldInterval * 2);
            if (newInterval != oldInterval)
            {
                _effectiveIntervalMinutes = newInterval;
                var msg = $"PRTG 快照連續失敗達 {_consecutiveFailures} 次，生效間隔由 {oldInterval} 分鐘拉長為 {newInterval} 分鐘。";
                WriteOutput(msg, LogLevel.Warn);
            }
        }
        Log.Error(ex, "PRTG 快照執行失敗（第 {Failures} 次連續失敗）：{Message}", _consecutiveFailures, ex.Message);
    }

    private void WriteOutput(string message, NLog.LogLevel? logLevel = null)
    {
        ExecutionOutputs.Add(message);
        if (ExecutionOutputs.Count > MaxExecutionOutputs) ExecutionOutputs.RemoveAt(0);
        Console?.WriteLine(message);
        if (logLevel == NLog.LogLevel.Warn)
            Log.Warn(message);
        else if (logLevel == NLog.LogLevel.Info)
            Log.Info(message);
    }

    private void OnStopping()
    {
        try
        {
            var now = Now();
            var rows = _accumulator.DrainAll(ExpectedSamplesPerHour, now);
            if (rows.Count > 0)
            {
                _backend.PrtgStore().MergeSampledValues(rows);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "站台關閉時寫出 PRTG 快照殘存樣本失敗");
        }
    }

    internal virtual PrtgClient CreateClient(SystemSettings settings)
    {
        return ClientFactory != null
            ? ClientFactory()
            : PrtgClientFactory.Create(settings);
    }
}
