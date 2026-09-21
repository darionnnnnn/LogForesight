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

    /// <summary>狀態變更逐裝置查詢進度（已完成台數, 總台數）；欄位沿用 StateChangesRead／StateChangesTotal 的名稱。</summary>
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
/// 同時是立即執行「一併補齊 PRTG 數值」的接續執行者（<see cref="IPrtgBackfillTail"/>），兩條入口共用同一個執行狀態。
/// </summary>
public class PrtgBackfillService : IPrtgBackfillTail
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
    private readonly ISentinelStore _sentinels;

    /// <summary>鏡像沒有感測器時，回填自己先做一次結構同步。</summary>
    private readonly PrtgStructureSyncService _structureSync;

    public PrtgBackfillService(
        ISystemSettingsStore settings,
        StorageBackend backend,
        PrtgBackfillRunState state,
        PrtgProbeRunState probeState,
        IHostStore hosts,
        SchedulerRunState schedulerRunState,
        PrtgStructureSyncRunState structureSyncState,
        ISentinelStore sentinels,
        PrtgStructureSyncService structureSync)
    {
        _sentinels = sentinels;
        _settings = settings;
        _backend = backend;
        _state = state;
        _probeState = probeState;
        _hosts = hosts;
        _schedulerRunState = schedulerRunState;
        _structureSyncState = structureSyncState;
        _structureSync = structureSync;
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

    /// <summary>
    /// 清除監看範圍外資料（PRTG 維護頁確認）現在能不能做：取數執行、結構同步、回填任一在跑就不行，回傳原因；null＝可以。
    /// 放在這裡是因為本服務本來就持有這三個執行狀態（與 <see cref="TryStart"/> 的互斥同一組）。
    /// </summary>
    public string? ScopePurgeConflict()
    {
        if (_schedulerRunState.IsRunning)
            return $"取數執行中{ElapsedSuffix(_schedulerRunState.StartedAt)}，請等它結束後再清除。";
        var syncSnapshot = _structureSyncState.Snapshot();
        if (syncSnapshot.IsRunning)
            return $"「同步結構與對應」執行中{ElapsedSuffix(syncSnapshot.StartedAt)}，請等它完成後再清除。";
        if (_state.Snapshot().IsRunning)
            return "歷史回填執行中，請等它完成或按停止後再清除。";
        return null;
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

    /// <summary>回填啟動前準備好的一趟（已佔住執行狀態）。</summary>
    private sealed record PreparedRun(SystemSettings Settings, int Days, PrtgClient Client, CancellationToken Token, bool NeedsStructureSync);

    /// <summary>「近 N 天」的對應視窗：逐日迴圈以每個回填日為基準往回找 HostMapLookbackDays 天，閘門要涵蓋「最舊回填日再往回」整段。</summary>
    private static int MapGateWindow(int days) => days + PrtgTriggeredValueFetcher.HostMapLookbackDays;

    /// <summary>近 <see cref="MapGateWindow"/> 天是否有任何主機對應（<see cref="TryStart"/> 與接續回填共用，天數各自帶入）。</summary>
    private static bool HasAnyMapping(EfPrtgStore prtgStore, int days) =>
        prtgStore.GetLatestHostMapWithDate(MapGateWindow(days)).Rows
            .Any(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId != null);

    /// <param name="error">拒絕原因；成功時為 null。</param>
    /// <param name="isConflict">
    /// true＝被互斥擋下（環境探測／取數／結構同步／回填自己正在跑）——狀態衝突，呼叫端該回 409；
    /// false＝設定或前提不齊，該回 400。與 <see cref="PrtgStructureSyncService.TryStart"/> 同一套。
    /// </param>
    public bool TryStart(out string? error, out bool isConflict)
    {
        var s = _settings.Get();
        if (!TryPrepare(s, s.PrtgBackfillDays, blockWhenSchedulerRunning: true, out var run, out error, out isConflict))
            return false;

        _ = Task.Run(() => ExecuteAsync(run!, new PrtgBackfillConsole(_state), hostIds: null));
        return true;
    }

    /// <summary>
    /// 立即執行的接續回填：同一趟的一部分，因此**不**以「取數執行中」擋下；其餘擋門照舊。
    /// 天數用傳入值（不讀 PrtgBackfillDays）。輸出同時寫進回填狀態卡與本趟的執行紀錄。
    /// </summary>
    public async Task<bool> RunTailAsync(int days, IReadOnlyCollection<long>? hostIds, IRunConsole console, CancellationToken ct)
    {
        var s = _settings.Get();
        if (!TryPrepare(s, days, blockWhenSchedulerRunning: false, out var run, out var error, out _))
        {
            console.WriteLine($"  ⚠ 接續補 PRTG 數值未執行：{error}");
            return false;
        }

        // 整趟被停止時一併停止回填；回填也可以在維護頁的狀態卡單獨停止
        bool success;
        using (ct.Register(() => _state.TryCancel()))
        {
            success = await ExecuteAsync(run!, new TeeRunConsole(new PrtgBackfillConsole(_state), console), hostIds);
        }
        ct.ThrowIfCancellationRequested();
        return success;
    }

    /// <summary>
    /// 兩條入口共用的擋門與啟動：通過時已佔住執行狀態並建立 PRTG 連線，呼叫端必須接著呼叫 <see cref="ExecuteAsync"/>。
    /// </summary>
    /// <param name="blockWhenSchedulerRunning">手動回填要避開取數執行；接續回填本身就是那一趟，不檢查。</param>
    private bool TryPrepare(SystemSettings s, int days, bool blockWhenSchedulerRunning,
        out PreparedRun? run, out string? error, out bool isConflict)
    {
        run = null;
        error = null;
        isConflict = false;

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
            isConflict = true;
            return false;
        }

        // 取數執行與回填同時打同一台 PRTG，兩邊都會變慢且互相拖累
        if (blockWhenSchedulerRunning && _schedulerRunState.IsRunning)
        {
            error = $"取數執行中{ElapsedSuffix(_schedulerRunState.StartedAt)}，回填會與它同時查詢同一台 PRTG。請等它結束，或在取數執行卡按「停止執行」後再回填。";
            isConflict = true;
            return false;
        }

        // 判斷方式與快照服務一致（PrtgStructureSyncService.IsRunning＝執行狀態快照的 IsRunning）
        var syncSnapshot = _structureSyncState.Snapshot();
        if (syncSnapshot.IsRunning)
        {
            error = $"「同步結構與對應」執行中{ElapsedSuffix(syncSnapshot.StartedAt)}，請等它完成，或在 PRTG 卡按停止後再回填。";
            isConflict = true;
            return false;
        }

        // 回填的 sensor 清單來自鏡像：鏡像空的就在背景先同步一次結構（對應閘門留到同步後再判斷）。
        // 鏡像有東西時照舊在入口判斷對應：沒有任何主機對應時逐日目標 sensor 一律是 0 個，放行只會空跑並報成功。
        var prtgStore = _backend.PrtgStore();
        var needsStructureSync = prtgStore.GetSensorTargets().Count == 0;
        if (!needsStructureSync && !HasAnyMapping(prtgStore, days))
        {
            error = $"近 {MapGateWindow(days)} 天沒有任何 PRTG 主機對應，回填找不到要取數的主機。請先按「同步結構與對應」建立對應後再回填。";
            return false;
        }

        if (!_state.TryBeginRun(out var runToken))
        {
            error = "回填已在執行中。";
            isConflict = true;
            return false;
        }

        _state.ResetProgress();

        PrtgClient client;
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

        run = new PreparedRun(s, days, client, runToken, needsStructureSync);
        return true;
    }

    /// <summary>回填主體（兩條入口共用）：必要時先同步結構，再逐日回填；結束時釋放執行狀態。回傳是否成功。</summary>
    private async Task<bool> ExecuteAsync(PreparedRun run, IRunConsole console, IReadOnlyCollection<long>? hostIds)
    {
        var s = run.Settings;
        var runToken = run.Token;
        var prtgStore = _backend.PrtgStore();
        var success = false;
        var cancelled = false;
        try
        {
            using (run.Client)
            {
                if (run.NeedsStructureSync)
                {
                    console.WriteLine("鏡像尚無感測器結構，先同步結構…");
                    var syncError = await _structureSync.SyncForBackfillAsync(runToken);
                    runToken.ThrowIfCancellationRequested();
                    if (syncError != null)
                    {
                        console.WriteLine($"結構同步失敗：{syncError}，請到 PRTG 維護頁檢查連線。");
                        return false;
                    }

                    if (!HasAnyMapping(prtgStore, run.Days))
                    {
                        console.WriteLine("PRTG 裝置沒有任何一台對應到主機清單中的主機（請檢查主機 IP 與 PRTG 裝置位址，或在鏡像狀態頁手動指派）。");
                        return false;
                    }

                    console.WriteLine("結構同步完成，開始回填。");
                }

                var fetchService = new PrtgFetchService(run.Client, prtgStore,
                    new PrtgFreshnessStore(_backend.Blob(PrtgFreshnessStore.BlobKey)), console,
                    PrtgSensorTypeCategoryMap.ParseOverrides(s.PrtgSensorTypeCategoryOverrides).Map);

                // 數值取數對象與排程取數共用同一份設定與判定（docs/PRTG-SPEC.md §3a）
                var (scopeHostIds, unresolvedHosts) = PrtgValueFetchScope.ResolveHostNames(
                    s.PrtgValueFetchExtraHosts,
                    _hosts.GetAll().Select(h => (h.HostId, h.HostName, h.Active, h.MergedInto.HasValue)));

                if (unresolvedHosts.Count > 0)
                {
                    console.WriteLine($"⚠ 取數範圍的指定主機有 {unresolvedHosts.Count} 個對不到主機主檔，已略過：" +
                                      string.Join("、", unresolvedHosts.Take(10)));
                }

                // 監看裝置：回填不做主機對應，直接以既有對應算一次，整趟共用；指定主機時只算那些主機
                var scopeResult = PrtgScopeDevices.Compute(
                    prtgStore, _hosts, new PrtgMirrorGuardSource(prtgStore), s, _sentinels.GetAll(),
                    console, new PrtgAddressResolver(), hostIds);
                console.WriteLine($"監看裝置：{scopeResult.DeviceObjids.Count} 台");

                success = await PrtgBackfillRunner.RunAsync(
                    fetchService, run.Days, s.PrtgFetchConcurrency, console, runToken,
                    scopeResult.DeviceObjids,
                    prtgStore, _backend.RecordStore(), s.PrtgSensorTypeWhitelist,
                    dayProgress: (dDone, dTotal, curDate) => _state.UpdateDay(dDone, dTotal, curDate),
                    sensorProgress: (sDone, sTotal) => _state.UpdateSensors(sDone, sTotal),
                    scope: s.PrtgValueFetchScope,
                    extraScopeHosts: scopeHostIds,
                    stateChangeProgress: (doneDevices, totalDevices) => _state.UpdateStateChanges(doneDevices, totalDevices));
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

        return success;
    }

    /// <summary>接續回填的輸出同時寫進回填狀態卡與本趟執行紀錄。</summary>
    private sealed class TeeRunConsole : IRunConsole
    {
        private readonly IRunConsole _first;
        private readonly IRunConsole _second;

        public TeeRunConsole(IRunConsole first, IRunConsole second)
        {
            _first = first;
            _second = second;
        }

        public void WriteLine(string message = "")
        {
            _first.WriteLine(message);
            _second.WriteLine(message);
        }
    }
}
