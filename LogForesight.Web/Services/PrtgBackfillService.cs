using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>PRTG 歷史回填的進度快照。</summary>
public record PrtgBackfillProgress(
    int DaysDone, int DaysTotal, DateTime? CurrentDate,
    int SensorsDone, int SensorsTotal,
    int StateChangesRead, int StateChangesTotal, bool ReadingStateChanges);

/// <summary>
/// PRTG 歷史回填的行程內單例執行狀態＋併發 1 的 gate。
/// 繼承自 <see cref="PrtgProbeRunState"/> 避免重複實作。
/// </summary>
public class PrtgBackfillRunState : PrtgProbeRunState
{
    private readonly object _progressLock = new();

    private int _daysDone;
    private int _daysTotal;
    private DateTime? _currentDate;
    private int _sensorsDone;
    private int _sensorsTotal;
    private int _stateChangesRead;
    private int _stateChangesTotal;
    private bool _readingStateChanges;

    /// <summary>本趟的取消來源；沒有執行中時為 null。在 _progressLock 內先搶執行權（基底自有一把鎖）再建立它，IsRunning 一轉 true 就一定有東西可取消。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>最近一趟是否被使用者停止（新一趟開始時歸零）。</summary>
    public bool Cancelled
    {
        get { lock (_progressLock) return _cancelled; }
    }

    private bool _cancelled;

    /// <summary>
    /// 搶執行權並建立本趟的取消來源。已在執行中回 false。
    /// 取鎖順序固定是 _progressLock → 基底鎖（TryBegin／Snapshot／EndRun 各自持有基底鎖），基底不會回呼本類別，不會反向。
    /// </summary>
    public bool TryBeginRun(out CancellationToken token)
    {
        lock (_progressLock)
        {
            if (!TryBegin())
            {
                token = default;
                return false;
            }
            _cts = new CancellationTokenSource();
            _cancelled = false;
            token = _cts.Token;
            return true;
        }
    }

    /// <summary>要求停止進行中的回填；沒有執行中時回 false。</summary>
    public bool TryCancel()
    {
        lock (_progressLock)
        {
            if (_cts == null || !Snapshot().IsRunning) return false;
            _cts.Cancel();
            return true;
        }
    }

    /// <summary>結束本趟：記住是否被停止、釋放並清空取消來源，再結束執行狀態。</summary>
    public void FinishRun(bool success, bool cancelled)
    {
        lock (_progressLock)
        {
            _cancelled = cancelled;
            _cts?.Dispose();
            _cts = null;
        }
        EndRun(success);
    }

    public void ResetProgress()
    {
        lock (_progressLock)
        {
            _daysDone = 0;
            _daysTotal = 0;
            _currentDate = null;
            _sensorsDone = 0;
            _sensorsTotal = 0;
            _stateChangesRead = 0;
            _stateChangesTotal = 0;
            _readingStateChanges = false;
        }
    }

    public void UpdateDay(int daysDone, int daysTotal, DateTime? currentDate)
    {
        lock (_progressLock)
        {
            _daysDone = daysDone;
            _daysTotal = daysTotal;
            _currentDate = currentDate;
            _sensorsDone = 0;
            _sensorsTotal = 0;
            // 進到逐日階段代表狀態變更已翻完
            _readingStateChanges = false;
        }
    }

    public void UpdateSensors(int sensorsDone, int sensorsTotal)
    {
        lock (_progressLock)
        {
            _sensorsDone = sensorsDone;
            _sensorsTotal = sensorsTotal;
        }
    }

    /// <summary>狀態變更區間讀取進度（已讀筆數, 約略總筆數）。</summary>
    public void UpdateStateChanges(int done, int total)
    {
        lock (_progressLock)
        {
            _stateChangesRead = done;
            _stateChangesTotal = total;
            _readingStateChanges = true;
        }
    }

    public PrtgBackfillProgress GetProgress()
    {
        lock (_progressLock)
        {
            return new PrtgBackfillProgress(
                _daysDone, _daysTotal, _currentDate,
                _sensorsDone, _sensorsTotal,
                _stateChangesRead, _stateChangesTotal, _readingStateChanges);
        }
    }
}

/// <summary>
/// 極薄的 IRunConsole adapter：將回填輸出逐行收集至 <see cref="PrtgBackfillRunState"/>。
/// </summary>
public class PrtgBackfillConsole : IRunConsole
{
    private readonly PrtgBackfillRunState _state;

    public PrtgBackfillConsole(PrtgBackfillRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// PRTG 歷史回填服務：Singleton，背景執行 PRTG 歷史回填任務並維護狀態。
/// </summary>
public class PrtgBackfillService
{
    private readonly ISystemSettingsStore _settings;
    private readonly StorageBackend _backend;
    private readonly PrtgBackfillRunState _state;
    private readonly PrtgProbeRunState _probeState;

    private readonly IHostStore _hosts;

    // 必要相依，不設預設值：回填與取數、結構同步會打同一台 PRTG，這兩道互斥是保護；
    // 做成可選的話漏注入時保護會靜默消失。
    private readonly SchedulerRunState _schedulerRunState;
    private readonly PrtgStructureSyncRunState _structureSyncState;

    public PrtgBackfillService(
        ISystemSettingsStore settings,
        StorageBackend backend,
        PrtgBackfillRunState state,
        PrtgProbeRunState probeState,
        IHostStore hosts,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncRunState structureSyncState)
    {
        _settings = settings;
        _backend = backend;
        _state = state;
        _probeState = probeState;
        _hosts = hosts;
        _schedulerRunState = schedulerRunState;
        _structureSyncState = structureSyncState;
    }

    public PrtgBackfillStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        var p = _state.GetProgress();
        return new PrtgBackfillStatusDto
        {
            IsRunning = s.IsRunning,
            StartedAt = s.StartedAt,
            CompletedAt = s.CompletedAt,
            Success = s.Success,
            LatestMessage = s.LatestMessage,
            Output = s.Output,
            DaysDone = p.DaysDone,
            DaysTotal = p.DaysTotal,
            CurrentDate = p.CurrentDate,
            SensorsDone = p.SensorsDone,
            SensorsTotal = p.SensorsTotal,
            StateChangesRead = p.StateChangesRead,
            StateChangesTotal = p.StateChangesTotal,
            ReadingStateChanges = p.ReadingStateChanges,
            Cancelled = _state.Cancelled
        };
    }

    /// <summary>要求停止進行中的回填；沒有執行中回 false。</summary>
    public bool TryCancel() => _state.TryCancel();

    /// <summary>「（已 N 分鐘）」後綴；取不到開始時間時回空字串（兩道執行中閘門共用）。</summary>
    private static string ElapsedSuffix(DateTime? startedAt)
    {
        if (!startedAt.HasValue) return "";
        var minutes = (int)Math.Max(0, (DateTime.Now - startedAt.Value).TotalMinutes);
        return $"（已 {minutes} 分鐘）";
    }

    public bool TryStart(out string? error)
    {
        error = null;
        var s = _settings.Get();

        if (!s.PrtgEnabled)
        {
            error = "PRTG 擷取未啟用，請先在 PRTG 維護頁「擷取參數」選擇取數範圍。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法執行回填。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法執行回填。";
            return false;
        }

        if (_probeState.Snapshot().IsRunning)
        {
            error = "環境探測執行中，請稍後再試。";
            return false;
        }

        // 回填不重跑結構同步、sensor 清單來自鏡像——鏡像空的就沒有東西可回填，
        // 放行只會空跑 N 天然後報成功（一筆資料都沒抓的成功最難察覺），在入口就擋下
        var prtgStoreForGate = _backend.PrtgStore();
        if (prtgStoreForGate.GetSensorTargets().Count == 0)
        {
            error = "鏡像尚無任何感測器結構。請先執行一次每日擷取（或等夜間排程跑過）再回填。";
            return false;
        }

        // 取數執行與回填同時打同一台 PRTG，兩邊都會變慢且互相拖累
        if (_schedulerRunState.IsRunning)
        {
            error = $"取數執行中{ElapsedSuffix(_schedulerRunState.StartedAt)}，回填會與它同時查詢同一台 PRTG。請等它結束，或在取數執行卡按「停止執行」後再回填。";
            return false;
        }

        // 判斷方式與快照服務一致（PrtgStructureSyncService.IsRunning＝執行狀態快照的 IsRunning）
        var syncSnapshot = _structureSyncState.Snapshot();
        if (syncSnapshot.IsRunning)
        {
            error = $"「同步結構與對應」執行中{ElapsedSuffix(syncSnapshot.StartedAt)}，請等它完成，或在 PRTG 卡按停止後再回填。";
            return false;
        }

        // 沒有任何主機對應時，逐日目標 sensor 一律是 0 個——放行只會空跑並報成功。
        // 逐日迴圈以每個回填日為基準往回找 HostMapLookbackDays 天，閘門的視窗因此要涵蓋「最舊回填日再往回」整段，
        // 否則只有今天有對應時閘門放行、每一天卻都被略過。
        var lookback = PrtgTriggeredValueFetcher.HostMapLookbackDays;
        var gateWindow = s.PrtgBackfillDays + lookback;
        var hasAnyMapping = prtgStoreForGate.GetLatestHostMapWithDate(gateWindow).Rows
            .Any(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId != null);
        if (!hasAnyMapping)
        {
            error = $"近 {gateWindow} 天沒有任何 PRTG 主機對應，回填找不到要取數的主機。請先按「同步結構與對應」建立對應後再回填。";
            return false;
        }

        if (!_state.TryBeginRun(out var runToken))
        {
            error = "回填已在執行中。";
            return false;
        }

        _state.ResetProgress();

        var console = new PrtgBackfillConsole(_state);
        PrtgClient? client = null;
        try
        {
            client = PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _state.FinishRun(false, cancelled: false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        var prtgStore = _backend.PrtgStore();
        var fetchService = new PrtgFetchService(client, prtgStore, console);
        var days = s.PrtgBackfillDays;
        var concurrency = s.PrtgFetchConcurrency;

        _ = Task.Run(async () =>
        {
            var success = false;
            var cancelled = false;
            try
            {
                using (client)
                {
                    // 取數範圍與每日擷取共用同一份設定與判定（docs/PRTG-SPEC.md §3a）
                    var (scopeHostIds, unresolvedHosts) = PrtgValueFetchScope.ResolveHostNames(
                        s.PrtgValueFetchExtraHosts,
                        _hosts.GetAll().Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

                    if (unresolvedHosts.Count > 0)
                    {
                        console.WriteLine($"⚠ 取數範圍的指定主機有 {unresolvedHosts.Count} 個對不到主機主檔，已略過：" +
                                          string.Join("、", unresolvedHosts.Take(10)));
                    }

                    success = await PrtgBackfillRunner.RunAsync(
                        fetchService, days, concurrency, console, runToken,
                        prtgStore, _backend.RecordStore(), s.PrtgSensorTypeWhitelist,
                        dayProgress: (dDone, dTotal, curDate) => _state.UpdateDay(dDone, dTotal, curDate),
                        sensorProgress: (sDone, sTotal) => _state.UpdateSensors(sDone, sTotal),
                        scope: s.PrtgValueFetchScope,
                        extraScopeHosts: scopeHostIds,
                        stateChangeProgress: (read, total) => _state.UpdateStateChanges(read, total));
                }
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                // 摘要已由 runner 印出（已停止：完成 x／N 天）
                cancelled = true;
                success = false;
            }
            catch (Exception ex)
            {
                console.WriteLine($"回填過程發生未預期錯誤：{ex.Message}");
                success = false;
            }
            finally
            {
                _state.FinishRun(success, cancelled);
            }
        });

        return true;
    }
}
