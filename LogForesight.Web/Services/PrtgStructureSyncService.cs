using NLog;
using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Models.Dto;

namespace LogForesight.Web.Services;

/// <summary>
/// 「同步結構與對應」的行程內單例執行狀態＋併發 1 的 gate（比照回填與探測）。
/// 另外持有目前階段的進度，供狀態端點畫進度軌。
/// </summary>
public class PrtgStructureSyncRunState : PrtgProbeRunState
{
    private readonly object _progressLock = new();

    private string? _phase;
    private int _done;
    private int _total;

    public void ResetProgress()
    {
        lock (_progressLock)
        {
            _phase = null;
            _done = 0;
            _total = 0;
        }
    }

    public void UpdateProgress(string phase, int done, int total)
    {
        lock (_progressLock)
        {
            _phase = phase;
            _done = done;
            _total = total;
        }
    }

    public (string? Phase, int Done, int Total) GetProgress()
    {
        lock (_progressLock)
        {
            return (_phase, _done, _total);
        }
    }
}

/// <summary>極薄的 IRunConsole adapter：把同步輸出逐行收集到執行狀態。</summary>
public class PrtgStructureSyncConsole : IRunConsole
{
    private readonly PrtgStructureSyncRunState _state;

    public PrtgStructureSyncConsole(PrtgStructureSyncRunState state) => _state = state;

    public void WriteLine(string message = "") => _state.AppendLine(message);
}

/// <summary>
/// 「同步結構與對應」服務：Singleton，背景執行並維護狀態（docs/PRTG-SPEC.md §5a）。
///
/// 為什麼是背景工作而不是同步端點：實機的結構同步要分頁讀完整棵裝置與感測器樹，
/// 可能跑上數十分鐘，塞在 HTTP 請求裡必然逾時。
/// </summary>
public class PrtgStructureSyncService : IPrtgStructureSyncGate
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>等待手動同步結束時的輪詢間隔。同步本身以分鐘計，秒級輪詢已足夠精細。</summary>
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 取數路徑等待手動同步的上限（暫定 90 分鐘）。實機的完整結構同步以數十分鐘計，
    /// 這個值要明顯大於它，否則正常的長同步會被誤判成卡住。
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(90);

    private readonly ISystemSettingsStore _settings;
    private readonly StorageBackend _backend;
    private readonly PrtgStructureSyncRunState _state;
    private readonly SchedulerRunState _schedulerState;
    private readonly IHostStore _hosts;
    private readonly PrtgStructureSyncStatusStore _statusStore;
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>
    /// 本趟同步的取消來源；沒有執行中時為 null。
    /// volatile：寫入在啟動執行緒、讀取在按下停止鈕的請求執行緒。
    /// </summary>
    private volatile CancellationTokenSource? _cts;

    /// <summary>
    /// 最近一趟同步是否成功（取消與階段失敗都算不成功）。閘門用它決定要不要讓取數跳過自己的結構同步。
    /// 初值 false：這個行程還沒跑過成功的同步時，取數一律自己來。
    /// </summary>
    private volatile bool _lastRunSucceeded;

    public PrtgStructureSyncService(
        ISystemSettingsStore settings,
        StorageBackend backend,
        PrtgStructureSyncRunState state,
        SchedulerRunState schedulerState,
        IHostStore hosts,
        PrtgStructureSyncStatusStore statusStore,
        IHostApplicationLifetime? lifetime = null)
    {
        _lifetime = lifetime;
        // 站台關閉時中止同步：這條路徑會對 PRTG 做整棵樹的分頁查詢，
        // 沒有取消來源的話，PRTG 端卡住（TCP 半開、不回應）就會讓狀態永遠停在「執行中」，
        // 而夜間取數的 PRTG 路徑正在等它結束——當晚整條 PRTG 路徑跟著掛住，
        // 且 TryStart 會因「已在執行中」而拒絕重啟，不重開站台就回不來。
        _lifetime?.ApplicationStopping.Register(() => Cancel());
        _settings = settings;
        _backend = backend;
        _state = state;
        _schedulerState = schedulerState;
        _hosts = hosts;
        _statusStore = statusStore;
    }

    /// <summary>同步是否正在執行——取數執行的 PRTG 路徑用它決定要不要等。</summary>
    public bool IsRunning => _state.Snapshot().IsRunning;

    /// <summary>
    /// 使用者主動中止：沒有執行中時回 false 讓呼叫端回報狀態衝突，
    /// 不要把「沒東西可停」說成停止成功。
    /// </summary>
    public bool TryCancel()
    {
        if (!IsRunning) return false;
        Cancel();
        return true;
    }

    /// <summary>對進行中的同步發出取消（站台關閉時自動呼叫）。沒有執行中時無作用。</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 剛好在收尾時撞上，忽略
        }
    }

    /// <summary>
    /// 等到手動同步結束為止。取消訊號穿透（讓整趟批次的取消語意一致）。
    ///
    /// **有上限**：同步的每一步都是對外部 PRTG 的 HTTP 查詢，對方卡住（TCP 半開、不回應）
    /// 時狀態會一直停在「執行中」。無上限地等，等於讓當晚的 PRTG 路徑跟著永遠掛住。
    /// 逾時後不擲例外、直接返回，讓這一趟照常做自己的結構同步——
    /// 最壞情況是兩邊都寫鏡像（寫入本身是冪等的 upsert），比整條路徑停擺好。
    /// </summary>
    public async Task<bool> WaitUntilIdleAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MaxWait;

        while (IsRunning)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warn("等待手動「同步結構與對應」超過 {0} 分鐘仍未結束，本趟不再等待，改為自行同步結構。",
                    MaxWait.TotalMinutes);
                return false;
            }

            await Task.Delay(WaitPollInterval, ct);
        }

        // 跑完了但不成功（被取消、有階段失敗）＝鏡像是半套的，呼叫端要自己再同步一次。
        if (!_lastRunSucceeded)
        {
            Log.Warn("手動「同步結構與對應」已結束但未成功，鏡像可能不完整，本趟改為自行同步結構。");
        }

        return _lastRunSucceeded;
    }

    public PrtgStructureSyncStatusDto GetStatus()
    {
        var s = _state.Snapshot();
        var p = _state.GetProgress();
        var last = _statusStore.GetOrNull();

        return new PrtgStructureSyncStatusDto
        {
            IsRunning = s.IsRunning,
            StartedAt = s.StartedAt,
            LatestMessage = s.LatestMessage,
            Output = s.Output,
            ProgressPhase = p.Phase,
            ProgressDone = p.Done,
            ProgressTotal = p.Total,
            LastCompletedAt = last?.CompletedAt,
            LastSuccess = last?.Success,
            LastErrorMessage = last?.ErrorMessage,
            LastElapsedSeconds = last?.ElapsedSeconds,
            LastDevices = last?.Devices,
            LastSensors = last?.Sensors,
            LastMapDate = last?.MapDate,
            LastMapOk = last?.MapOk,
            LastMapManual = last?.MapManual,
            LastMapConflict = last?.MapConflict,
            LastMapUnmatched = last?.MapUnmatched,
            LastMapSkipped = last == null
                ? null
                : last.MapSkippedNoIp + last.MapSkippedExcluded + last.MapSkippedManualSibling
        };
    }

    /// <summary>把一趟的結果整份寫進持久化 store（成功、失敗、取消三條路徑共用）。</summary>
    private void Persist(PrtgStructureSyncStatus status) =>
        _statusStore.Update(existing =>
        {
            existing.CompletedAt = status.CompletedAt;
            existing.Success = status.Success;
            existing.ErrorMessage = status.ErrorMessage;
            existing.ElapsedSeconds = status.ElapsedSeconds;
            existing.Devices = status.Devices;
            existing.Sensors = status.Sensors;
            existing.MapDate = status.MapDate;
            existing.MapOk = status.MapOk;
            existing.MapManual = status.MapManual;
            existing.MapConflict = status.MapConflict;
            existing.MapUnmatched = status.MapUnmatched;
            existing.MapSkippedNoIp = status.MapSkippedNoIp;
            existing.MapSkippedExcluded = status.MapSkippedExcluded;
            existing.MapSkippedManualSibling = status.MapSkippedManualSibling;
        });

    /// <param name="error">拒絕原因；成功時為 null。</param>
    /// <param name="isConflict">
    /// true＝被互斥擋下（取數執行中、同步已在跑）——那是狀態衝突，呼叫端該回 409；
    /// false＝設定不齊等輸入面的問題，該回 400。由這裡判定，呼叫端不必解析訊息文字。
    /// </param>
    public bool TryStart(out string? error, out bool isConflict)
    {
        error = null;
        isConflict = false;
        var s = _settings.Get();

        if (!s.PrtgEnabled)
        {
            error = "PRTG 擷取未啟用，請先在 PRTG 維護頁「擷取參數」選擇取數範圍。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(s.PrtgUrl))
        {
            error = "尚未設定 PRTG 連線位址，無法同步。";
            return false;
        }

        if (!PrtgClientFactory.HasUsableCredentials(s))
        {
            error = "尚未設定 PRTG 認證資訊（API token 或帳號密碼），無法同步。";
            return false;
        }

        // 取數執行進行中不放行：那趟自己就會做結構同步，兩邊同時寫同一批鏡像表沒有意義，
        // 而且會讓對應算在寫到一半的鏡像上。
        if (_schedulerState.IsRunning)
        {
            error = "取數執行進行中，請等它結束後再同步（該趟本身就會同步結構與對應）。";
            isConflict = true;
            return false;
        }

        if (!_state.TryBegin())
        {
            error = "同步已在執行中。";
            isConflict = true;
            return false;
        }

        // 一旦 IsRunning 轉 true，閘門與停止鈕就看得到這一趟了。兩件事必須在這一刻就位：
        // ① 把「上一趟成功」的旗標歸零，否則下面任何一條 early return 都會讓閘門沿用舊結果，
        //    當晚的取數會以為鏡像剛更新過而跳過自己的結構同步；
        // ② 備好取消來源，否則使用者在啟動後立刻按停止會落在 _cts 還是 null 的空窗，
        //    畫面說「已送出停止」而同步照跑完整趟。
        _lastRunSucceeded = false;
        var cts = new CancellationTokenSource();
        _cts = cts;

        _state.ResetProgress();

        var console = new PrtgStructureSyncConsole(_state);
        PrtgClient? client;
        try
        {
            client = PrtgClientFactory.Create(s);
        }
        catch (Exception ex)
        {
            _state.AppendLine($"初始化 PRTG 連線失敗：{ex.Message}");
            _cts = null;
            cts.Dispose();
            _state.EndRun(false);
            error = $"初始化 PRTG 連線失敗：{ex.Message}";
            return false;
        }

        var prtgStore = _backend.PrtgStore();
        var fetchService = new PrtgFetchService(client, prtgStore, console);
        var concurrency = s.PrtgFetchConcurrency;

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                using (client)
                {
                    var status = await PrtgStructureSyncRunner.RunAsync(
                        fetchService, prtgStore, _hosts, new PrtgAddressResolver(),
                        concurrency, console, cts.Token,
                        progress: (phase, done, total) => _state.UpdateProgress(phase, done, total));

                    Persist(status);
                    success = status.Success;
                }
            }
            catch (OperationCanceledException)
            {
                console.WriteLine("同步已被取消（站台關閉或手動中止）。");
                // 取消也要落地：不寫的話狀態卡會沿用上一筆「成功」的摘要，
                // 而執行輸出（行程內狀態）在站台重啟後一起消失，這趟被腰斬就沒有任何痕跡。
                Persist(new PrtgStructureSyncStatus
                {
                    CompletedAt = DateTime.Now,
                    Success = false,
                    ErrorMessage = "同步已被取消（站台關閉或手動中止）",
                    MapDate = DateTime.Today
                });
                success = false;
            }
            catch (Exception ex)
            {
                console.WriteLine($"同步過程發生未預期錯誤：{ex.Message}");
                success = false;
            }
            finally
            {
                // 先寫旗標再結束執行狀態：等待方看到 IsRunning 轉 false 的下一刻就會讀它，
                // 反過來寫會讓那一瞬間讀到上一趟的結果。
                _lastRunSucceeded = success;
                _state.EndRun(success);
                _cts = null;
                cts.Dispose();
            }
        });

        return true;
    }
}
