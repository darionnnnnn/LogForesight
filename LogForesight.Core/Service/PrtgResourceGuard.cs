using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 資源守門閘門服務（批次F 階段3）。
/// 在 NetIQ 與 PRTG 取數路徑前檢查監看目標主機之資源狀況（CPU／可用記憶體）；
/// 連續超標達門檻時進入暫停，資源回落後自動恢復，單趟達累計暫停上限時放行不再暫停。
/// </summary>
public sealed class PrtgResourceGuard : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly PrtgClient _client;
    private readonly bool _ownsClient;
    private readonly SystemSettings _settings;
    private readonly PrtgResourceGuardTargetResult _targets;
    private readonly BatchRunRecorder _recorder;
    private readonly IRunConsole _console;
    private readonly IRunProgress? _progress;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _consecutiveStrikes;
    private int _checkCount;
    private int _totalPausedMinutes;
    private bool _maxPauseExceeded;
    private bool _warnedUnmeasurable;
    private DateTime? _lastCheckTime;
    private long _checkVersion;

    /// <summary>
    /// 內部等待注入點（測試可替換成立即完成之委派，記錄被要求等待的時間）。
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// 內部時間注入點（測試可替換以模擬時間推進）。
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public PrtgResourceGuard(
        PrtgClient client,
        SystemSettings settings,
        PrtgResourceGuardTargetResult targets,
        BatchRunRecorder recorder,
        IRunConsole console,
        IRunProgress? progress)
        : this(client, settings, targets, recorder, console, progress, ownsClient: false)
    {
    }

    private PrtgResourceGuard(
        PrtgClient client,
        SystemSettings settings,
        PrtgResourceGuardTargetResult targets,
        BatchRunRecorder recorder,
        IRunConsole console,
        IRunProgress? progress,
        bool ownsClient)
    {
        _ownsClient = ownsClient;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _progress = progress;
    }

    /// <summary>
    /// 由已儲存設定建立守門的**唯一入口**（含 client 建立與受監看目標偵測）。
    /// 守門是「讀不到就放行」的輔助機制，它自己的建構失敗——密文損毀讓解密擲例外、
    /// 鏡像表讀取失敗、Sentinel 清單讀取失敗——**都不能反過來讓整趟夜間批次在啟動前就掛掉**：
    /// 建構任何一步失敗一律回 null（＝本趟不守門），記警告與 Milestone 讓執行詳情看得到。
    /// 守門未啟用、位址或認證不齊時同樣回 null，且零成本（不建 client、不做偵測）。
    /// 回傳的守門**擁有**自建的 client，呼叫端以 using 釋放。
    /// </summary>
    public static PrtgResourceGuard? TryCreate(
        SystemSettings settings,
        EfPrtgStore prtgStore,
        ISentinelStore sentinelStore,
        BatchRunRecorder recorder,
        IRunConsole console,
        IRunProgress? progress)
    {
        if (!settings.PrtgResourceGuardEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.PrtgUrl) || !PrtgClientFactory.HasUsableCredentials(settings))
        {
            // 使用者明確勾了守門卻沒給位址或認證：不能無聲跳過，否則執行紀錄一個字都沒有、
            // 使用者會以為守門在跑。
            var msg = "[PRTG資源守門] 警告：守門已啟用，但 PRTG 位址或認證未設定，本趟不守門。";
            recorder.Milestone(msg);
            console.WriteLine(msg);
            return null;
        }

        PrtgClient? client = null;
        try
        {
            client = PrtgClientFactory.Create(settings);
            var targets = PrtgResourceGuardTargets.Resolve(prtgStore, settings, sentinelStore.GetAll(), console);
            return new PrtgResourceGuard(client, settings, targets, recorder, console, progress, ownsClient: true);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            Log.Warn(ex, "PRTG 資源守門建構失敗，本趟不守門");
            var msg = $"[PRTG資源守門] 警告：守門建構失敗，本趟不守門（{ex.GetType().Name}）。";
            recorder.Milestone(msg);
            console.WriteLine(msg);
            return null;
        }
    }

    /// <summary>只釋放自建的 client；外部傳入的 client 由外部負責。</summary>
    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// 若監控主機資源緊張則等待，直到資源回落或達單趟暫停上限才放行。
    /// </summary>
    public async Task WaitIfBusyAsync(CancellationToken ct)
    {
        // 1. 守門未啟用：立刻返回（no-op），不得有任何 API 呼叫或延遲
        if (!_settings.PrtgResourceGuardEnabled)
        {
            return;
        }

        // 2. 單趟累計暫停上限已達：旗標一次性放行，這趟之後不再暫停
        if (_maxPauseExceeded)
        {
            return;
        }

        var startVersion = Volatile.Read(ref _checkVersion);

        await _gate.WaitAsync(ct);
        try
        {
            // 等待鎖的期間，若已有前一個呼叫者完成了一次檢查或暫停，其餘呼叫者跟著一起等後直接放行
            if (Volatile.Read(ref _checkVersion) != startVersion)
            {
                return;
            }

            if (_maxPauseExceeded)
            {
                return;
            }

            var now = UtcNow();
            if (_lastCheckTime.HasValue && _settings.PrtgResourceGuardCheckSeconds > 0 &&
                (now - _lastCheckTime.Value).TotalSeconds < _settings.PrtgResourceGuardCheckSeconds)
            {
                return;
            }

            // 無受監看目標時，視為無法量測，放行並每趟只警告一次
            if (_targets.SensorObjids == null || _targets.SensorObjids.Count == 0)
            {
                WarnUnmeasurableOnce("未設定或未偵測到任何受監看感測器");
                _lastCheckTime = UtcNow();
                _checkVersion++;
                return;
            }

            // 讀取即時值
            _checkCount++;
            var probeResult = await PrtgResourceGuardProbe.FetchSensorValuesAsync(_client, _targets.SensorObjids, ct);
            _lastCheckTime = UtcNow();

            if (!probeResult.IsSuccess)
            {
                WarnUnmeasurableOnce($"讀取 PRTG 感測器即時值失敗（{probeResult.FailureReason}）");
                _checkVersion++;
                return;
            }

            var evalResult = PrtgResourceGuardProbe.Evaluate(_targets.SensorCategories, probeResult, _settings);
            if (!evalResult.HasMeasurableSensors)
            {
                WarnUnmeasurableOnce("受監看感測器目前皆無法量測");
                _checkVersion++;
                return;
            }

            if (!evalResult.IsOverloaded)
            {
                _consecutiveStrikes = 0;
                _checkVersion++;
                return;
            }

            _consecutiveStrikes++;
            if (_consecutiveStrikes < _settings.PrtgResourceGuardStrikes)
            {
                // 超標但未達 strikes 門檻，放行
                _checkVersion++;
                return;
            }

            // 達到 strikes 門檻，進入暫停
            var pauseMsg = $"[PRTG資源守門] 進入暫停：資源緊張（{evalResult.TriggeredSensorDescription}），第 {_checkCount} 次檢查，預計等待 {_settings.PrtgResourceGuardPauseMinutes} 分鐘。";
            _recorder.Milestone(pauseMsg);
            _console.WriteLine(pauseMsg);
            _progress?.Report(RunPhases.GuardPaused, 0, 0);

            // 暫停重試迴圈
            while (true)
            {
                await DelayAsync(TimeSpan.FromMinutes(_settings.PrtgResourceGuardPauseMinutes), ct);
                _totalPausedMinutes += _settings.PrtgResourceGuardPauseMinutes;
                _lastCheckTime = UtcNow();

                // 理由：門檻設錯時整晚只會暫停、什麼都沒分析，而且每晚重演。超過累計上限後放行並警告，這趟不再暫停。
                if (_totalPausedMinutes >= _settings.PrtgResourceGuardMaxPauseMinutes)
                {
                    _maxPauseExceeded = true;
                    var limitMsg = $"[PRTG資源守門] 警告：單趟累計暫停時間已達上限（{_totalPausedMinutes} 分鐘），放行並不再暫停。";
                    _recorder.Milestone(limitMsg);
                    _console.WriteLine(limitMsg);
                    _progress?.Report(RunPhases.GuardResumed, 0, 0);
                    _consecutiveStrikes = 0;
                    _checkVersion++;
                    return;
                }

                _checkCount++;
                var retryProbe = await PrtgResourceGuardProbe.FetchSensorValuesAsync(_client, _targets.SensorObjids, ct);
                if (!retryProbe.IsSuccess)
                {
                    WarnUnmeasurableOnce($"讀取 PRTG 感測器即時值失敗（{retryProbe.FailureReason}）");
                    var resumeMsg = $"[PRTG資源守門] 離開暫停：讀取感測器即時值失敗，放行不阻擋，第 {_checkCount} 次檢查。";
                    _recorder.Milestone(resumeMsg);
                    _console.WriteLine(resumeMsg);
                    _progress?.Report(RunPhases.GuardResumed, 0, 0);
                    _consecutiveStrikes = 0;
                    _checkVersion++;
                    return;
                }

                var retryEval = PrtgResourceGuardProbe.Evaluate(_targets.SensorCategories, retryProbe, _settings);
                if (!retryEval.HasMeasurableSensors)
                {
                    WarnUnmeasurableOnce("受監看感測器目前皆無法量測");
                    var resumeMsg = $"[PRTG資源守門] 離開暫停：受監看感測器無法量測，放行不阻擋，第 {_checkCount} 次檢查。";
                    _recorder.Milestone(resumeMsg);
                    _console.WriteLine(resumeMsg);
                    _progress?.Report(RunPhases.GuardResumed, 0, 0);
                    _consecutiveStrikes = 0;
                    _checkVersion++;
                    return;
                }

                if (!retryEval.IsOverloaded)
                {
                    // 資源已回落，離開暫停
                    var resumeMsg = $"[PRTG資源守門] 離開暫停：資源已回落正常，第 {_checkCount} 次檢查，恢復執行。";
                    _recorder.Milestone(resumeMsg);
                    _console.WriteLine(resumeMsg);
                    _progress?.Report(RunPhases.GuardResumed, 0, 0);
                    _consecutiveStrikes = 0;
                    _checkVersion++;
                    return;
                }

                // 仍超標，繼續暫停迴圈
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void WarnUnmeasurableOnce(string reason)
    {
        if (_warnedUnmeasurable) return;
        _warnedUnmeasurable = true;
        _console.WriteLine($"[PRTG資源守門] 警告：{reason}，放行不阻擋。");
    }
}
