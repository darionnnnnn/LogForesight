using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using NLog;

namespace LogForesight.Core.Service;

/// <summary>PRTG 每日擷取結果摘要。</summary>
public sealed record PrtgFetchResult(int Devices, int Sensors, int StateChanges, int Values, int Failures)
{
    /// <summary>本趟算出的監看裝置；null＝計算擲例外（或提早返回沒算）。範圍外清除的保護判斷用。</summary>
    public PrtgScopeResult? Scope { get; init; }

    /// <summary>裝置鏡像本趟是否成功更新（同 scopeProvider 收到的引數）</summary>
    public bool DevicesRefreshed { get; init; }

    /// <summary>失敗階段的原因（與 <see cref="Failures"/> 一一對應、依發生順序）；畫面與逐日說明用。</summary>
    public IReadOnlyList<string> StageFailures { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 影響過去日評估資料、且要整段重做的失敗：裝置或感測器鏡像沒取齊（未收斂、擲例外、範圍算不出來）、狀態變更階段擲例外。
    /// 單台裝置的失敗不算（已取到的裝置是完整的，只補那幾台，見 <see cref="PendingSensorDevices"/>）；分類填入與數值階段的失敗也不算。
    /// </summary>
    public bool HistoryAffectingFailure { get; init; }

    /// <summary>感測器沒同步到的裝置（失敗或停損後沒送出），下一個窗口只補這些；已完成的裝置不重查。</summary>
    public IReadOnlyList<long> PendingSensorDevices { get; init; } = Array.Empty<long>();

    /// <summary>狀態變更沒取到的裝置（失敗或停損後沒送出），下一個窗口只補這些。</summary>
    public IReadOnlyList<long> PendingMessageDevices { get; init; } = Array.Empty<long>();
}

/// <summary>PRTG 狀態變更區間擷取結果摘要。</summary>
public sealed record PrtgStateChangeRangeResult(
    IReadOnlyDictionary<DateTime, int> WrittenByDay, // 各日「新增」筆數（鍵為日期、只含區間內有列的日期）
    int TotalWritten,
    int ReadRows,       // 各物件加總
    int Pages,          // 各物件加總
    bool StoppedEarly,  // 至少一個物件依時間提早停止
    bool Converged,     // 沒有任何物件失敗
    string? Error,
    int QueriedObjects, // 逐一查詢的物件數
    int FailedObjects,  // 例外或分頁未收斂的物件數
    int EmptyObjects)
{
    /// <summary>沒取到的裝置（失敗或停損後沒送出）</summary>
    public IReadOnlyList<long> PendingDevices { get; init; } = Array.Empty<long>();
}

/// <summary>範圍補抓結果：寫入的感測器數、取得失敗的裝置、查詢成功但 PRTG 端一顆感測器都沒有的裝置。</summary>
public sealed record PrtgSensorBackfillResult(
    int SensorsWritten,
    IReadOnlyList<long> FailedDevices,
    IReadOnlyList<long> EmptyDevices);

/// <summary>
/// PRTG 每日擷取服務：負責將 PRTG 的裝置結構、感測器結構、狀態變更（訊息）與 hourly 聚合數值
/// 擷取並寫入本機鏡像表（lf_prtg_*）。
/// 包含四個循序階段，各階段各自獨立容錯，任一階段失敗不中斷其餘階段。
/// </summary>
public sealed class PrtgFetchService
{
    // 進度回報的 phase 字面值。Web 端 SchedulerRunState 依 "prtg-" 前綴歸入 PRTG 進度軌，
    // 前端 runs.js 另有一份標籤對照表——新增 phase 必須同步該表，否則畫面會印出裸 phase 字串。
    /// <summary>訊號：PRTG 回應異常、全部暫停（done＝第幾次、total＝暫停秒數）</summary>
    public const string PrtgPausedPhase = "prtg-paused";
    /// <summary>訊號：PRTG 停損、本趟不再取（done＝待補台數）</summary>
    public const string PrtgHaltedPhase = "prtg-halted";
    /// <summary>階段 1：device 結構同步</summary>
    public const string PrtgSyncDevicesPhase = "prtg-sync-devices";
    /// <summary>階段 2：sensor 結構同步</summary>
    public const string PrtgSyncSensorsPhase = "prtg-sync-sensors";
    /// <summary>階段 3：狀態變更（messages）同步</summary>
    public const string PrtgSyncMessagesPhase = "prtg-sync-messages";
    /// <summary>階段 4：每日 hourly 數值</summary>
    public const string PrtgValuesPhase = "prtg-values";
    /// <summary>觸發式數值取數</summary>
    public const string PrtgTriggeredPhase = "prtg-triggered";

    /// <summary>
    /// 階段 2 逐台查詢感測器的裝置數上限：範圍內裝置數不超過它時逐台以 <c>id=</c> 查詢，超過時改一次全站分頁再以範圍過濾。
    /// 取這個值的理由：超過時逐台查詢的固定成本（每台一次往返）高於一次全站分頁；尚無實機數據佐證，有實測再調。
    /// </summary>
    internal const int PerDeviceSensorFetchLimit = 500;

    /// <summary>
    /// 「查詢成功但回 0 個感測器」的裝置，鏡像列的寬限期：上次刷新在這段時間內的列本趟不刪。
    /// 36 小時＝夜間同步一天一趟再留半天餘裕——連續兩趟夜間都回 0 才會清掉。
    /// </summary>
    private static readonly TimeSpan EmptyDeviceGrace = TimeSpan.FromHours(36);

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly PrtgClient _client;
    private readonly EfPrtgStore _store;
    private readonly PrtgFreshnessStore _freshness;
    private readonly IRunConsole _console;
    private readonly PrtgResourceGuard? _guard;
    private readonly IReadOnlyDictionary<string, string> _categoryOverrides;
    private readonly bool _retryTransientPaging;

    /// <summary>
    /// 等待重試的等待函式（測試注入零等待；null＝真的等）。表格分頁的重試與數值取數的共用暫停閘共用這一個注入點。
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? RetryDelay { get; init; }

    /// <summary>
    /// 暫停與停損訊號（phase, done, total）。跟進度回呼分開：回填與結構同步把進度回呼的數字直接當進度條，
    /// 訊號混進去畫面會跳成「1/15」。只有夜間管線接它，其他路徑不顯示暫停。
    /// </summary>
    internal Action<string, int, int>? Signal { get; set; }

    /// <summary>數值取數暫停長度的隨機誤差來源（回傳 [0,1)）；測試注入固定值以取得確定的暫停長度，null＝隨機。</summary>
    internal Func<double>? ValueFetchJitter { get; init; }

    /// <summary>停損冷卻到期判斷用的時鐘；測試注入可手動推進的時鐘。</summary>
    internal TimeProvider TimeSource { get; init; } = TimeProvider.System;

    // 數值取數停損後的冷卻（實例層：觸發式取數與回填都以同一個實例反覆呼叫 FetchValuesAsync）
    private readonly object _valueFetchCooldownLock = new();
    private DateTimeOffset _valueFetchCooldownUntil = DateTimeOffset.MinValue;
    private bool _valueFetchCooldownNoticed;

    /// <param name="categoryOverrides">
    /// sensor type 語意分類補充對照表，由建構端以
    /// <see cref="PrtgSensorTypeCategoryMap.ParseOverrides"/> 自目前設定解析出的 Map 傳入
    /// （錯誤行已被略過；設定層存檔時已擋，這裡只防禦舊資料）。
    /// </param>
    /// <param name="retryTransientPaging">
    /// 表格分頁讀取（devices、sensors、messages）遇到逾時／連線失敗／5xx／429 時等待重試（<see cref="PrtgTablePager.RetryDelays"/>）。
    /// 預設關；只有夜間管線、結構同步與歷史回填這類無人值守、慢一點沒關係的路徑開啟，探測與快照維持單次失敗即回報。
    /// </param>
    public PrtgFetchService(PrtgClient client, EfPrtgStore store, PrtgFreshnessStore freshness, IRunConsole console,
        IReadOnlyDictionary<string, string> categoryOverrides, PrtgResourceGuard? guard = null,
        bool retryTransientPaging = false)
    {
        _retryTransientPaging = retryTransientPaging;
        _client = client;
        _store = store;
        _freshness = freshness;
        _console = console;
        _categoryOverrides = categoryOverrides;
        _guard = guard;
    }

    /// <summary>
    /// 正式路徑唯一的組裝點：新鮮度 blob 與分類補充對照都由這裡依當下設定決定，
    /// 每日管線、快照、回填、結構同步與探測不再各自組建構參數（測試可直接用建構子傳替身）。
    /// </summary>
    public static PrtgFetchService Create(PrtgClient client, StorageBackend backend, EfPrtgStore store,
        SystemSettings settings, IRunConsole console, PrtgResourceGuard? guard, bool retryTransientPaging = false) =>
        new(client, store, new PrtgFreshnessStore(backend.Blob(PrtgFreshnessStore.BlobKey)), console,
            PrtgSensorTypeCategoryMap.ParseOverrides(settings.PrtgSensorTypeCategoryOverrides).Map, guard,
            retryTransientPaging);

    /// <summary>
    /// 記錄某類 PRTG 資料成功完成一次擷取（見 <see cref="PrtgFreshnessStore"/>）。
    /// 觸發式取數與歷史回填沿用本服務的 store，不各自另建。
    /// </summary>
    public void RecordFreshness(string category, int count) => _freshness.Record(category, count);

    /// <summary>
    /// 執行指定日期的 PRTG 每日擷取。
    /// </summary>
    /// <param name="day">目標日期（本地時間）</param>
    /// <param name="concurrency">hourly 數值抓取併發上限（1~8）</param>
    /// <param name="ct">取消語彙基元</param>
    /// <param name="scopeProvider">
    /// 取數範圍提供者（必填）。引數＝devicesRefreshed：本趟階段 1 執行了、沒有擲例外、已收斂且寫入裝置數大於 0 才為 true。
    /// syncStructure 為 true 時在階段 1 之後、階段 2 之前呼叫恰一次；為 false 時在階段 3 之前呼叫恰一次。
    /// 呼叫端在這裡做主機對應（對應要用剛更新的裝置鏡像）並回傳範圍。
    /// 範圍用於縮圈取數：階段 2 只同步範圍內裝置的感測器（並清除範圍外的鏡像列），階段 3 只查範圍內裝置的狀態變更。
    /// </param>
    /// <param name="syncStructure">
    /// 是否同步 device／sensor 結構（階段 1、2）。每日擷取為 true。
    /// 歷史回填傳 false：結構鏡像永遠是「現況」，逐日回填時重跑它既是對 PRTG 做 N 次無謂的全量查詢，
    /// 也會把 <c>synced_at</c>（最後結構同步時間）改寫成回填當下，讓鏡像狀態顯示失真。
    /// 為 false 時 sensor 清單改從鏡像讀取。
    /// </param>
    /// <param name="fetchValues">
    /// 是否擷取 hourly 數值（階段 4）。每日擷取與歷史回填預設為 true。
    /// 觸發式流程傳 false（略過階段 4，改由觸發式取數獨立呼叫 <see cref="FetchValuesForSensorsAsync"/>）。
    /// </param>
    /// <param name="progress">進度回呼（stage, done, total），null＝不回報</param>
    /// <param name="stateChangesFrom">狀態變更區間起點（非 null 時階段 3 使用此起點到今天，null 時維持 day-1）</param>
    public async Task<PrtgFetchResult> FetchDayAsync(
        DateTime day, int concurrency, CancellationToken ct,
        Func<bool, PrtgScopeResult> scopeProvider,
        bool syncStructure = true, bool fetchValues = true,
        Action<string, int, int>? progress = null,
        DateTime? stateChangesFrom = null)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        var devicesCount = 0;
        var sensorsCount = 0;
        var stateChangesCount = 0;
        var valuesCount = 0;
        var failures = 0;
        // 各階段失敗的原因：計數之外一併留下訊息，讓逐日說明與執行詳情說得出「是哪個階段、為什麼」（docs/PRTG-SPEC.md §3）。
        IReadOnlyList<long> pendingSensorDevices = Array.Empty<long>();
        IReadOnlyList<long> pendingMessageDevices = Array.Empty<long>();
        // 所有失敗都經 Fail，不要再直接 failures++——直接加計數的那一處原因就不會出現在畫面上。
        var stageFailures = new List<string>();
        var historyAffectingFailure = false;
        void Fail(string message)
        {
            failures++;
            stageFailures.Add(message.Trim());
            _console.WriteLine(message);
        }
        var sensorTargets = new List<(long Objid, bool Paused)>();
        var devicesRefreshed = false;
        // 取數範圍：階段 2 只同步範圍內裝置的感測器、階段 3 只查範圍內裝置的狀態變更；null＝計算失敗（兩階段都略過、不清除）
        PrtgScopeResult? scope = null;

        void ResolveScope()
        {
            try
            {
                scope = scopeProvider(devicesRefreshed);
                _console.WriteLine($"[範圍] 監看裝置：{scope.DeviceObjids.Count} 台裝置（對應 {scope.Mapped}、衝突 {scope.Conflict}、人工 {scope.Manual}、守門 {scope.Guard}）");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                historyAffectingFailure = true;
                Fail($"[範圍] ✗ 監看裝置計算失敗：{ex.Message}");
            }
        }

        if (!syncStructure)
        {
            // 不同步結構時沒有新的裝置鏡像，provider 收到 false。放在「鏡像沒有感測器」的提早返回之前：
            // 夜間在手動同步剛完成時也走這條路，對應仍要照做，不能因鏡像沒有感測器就被略過。
            ResolveScope();
            sensorTargets = _store.GetSensorTargets();
            // 只有要取數值（歷史回填）時才因鏡像沒有感測器而提早返回；夜間沿用手動同步剛更新的鏡像時
            // （fetchValues 為 false）取數範圍可能本來就是空的，狀態變更階段自己會說明並略過，不該被這裡報成失敗。
            if (sensorTargets.Count == 0 && fetchValues)
            {
                // 鏡像還沒有任何 sensor（例如剛設定完就按回填、每日擷取一次都沒跑過）。
                // 不計失敗的話，接下來每一天都是「0 個 sensor 可抓 → 0 筆 → 無失敗」，
                // 整趟回填會被報成成功——一筆資料都沒抓的成功，正是最難察覺的那種。
                Fail("  ✗ 鏡像尚無任何感測器結構，無法回填。請先執行一次每日擷取（或等夜間排程跑過）再回填。");
                return new PrtgFetchResult(0, 0, 0, 0, failures) { StageFailures = stageFailures, HistoryAffectingFailure = true };
            }
            _console.WriteLine($"[結構] 沿用既有鏡像的 {sensorTargets.Count} 個感測器（回填不重跑結構同步）。");
        }

        if (syncStructure)
        {
            // 階段 1：device 結構全量同步
            // 本趟寫入的列 SyncedAt 都等於這個時間；早於它的列就是這趟全站同步沒出現的裝置
            var devicesSyncStartedAt = DateTime.Now;
            try
            {
                _console.WriteLine("[階段 1/4] 開始同步 PRTG 裝置結構鏡像...");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var outcome = await FetchDevicesAsync(devicesSyncStartedAt, ct, progress);
                stopwatch.Stop();
                devicesCount = outcome.Written;
                _console.WriteLine($"[階段 1/4] 裝置結構同步完成，共寫入/更新 {devicesCount} 台裝置{FormatStageStats(stopwatch, outcome)}。");
                // 分頁未收斂：已寫入的部分留著（寫入是冪等 upsert），但這一階段必須計為失敗，
                // 否則畫面會把「少了一大塊的鏡像」顯示成一次成功的同步。
                if (!outcome.Converged)
                {
                    historyAffectingFailure = true;
                    Fail($"[階段 1/4] ✗ {outcome.Error}已寫入 {devicesCount} 台裝置，鏡像不完整。");
                }
                else
                {
                    _freshness.Record(PrtgFreshnessStore.Devices, devicesCount);
                }
                devicesRefreshed = outcome.Converged && devicesCount > 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                historyAffectingFailure = true;
                Fail($"[階段 1/4] 裝置結構同步失敗：{ex.Message}");
            }

            // 清除 PRTG 端已不存在的裝置：只有階段 1 無例外、分頁收斂且有寫入時，「沒刷新到」才代表「已被刪除」。
            // 必須在 ResolveScope 之前——主機對應要用清除後的裝置表，已刪裝置才不會再對到主機。
            if (devicesRefreshed)
            {
                try
                {
                    var stale = _store.DeleteDevicesNotSyncedSince(devicesSyncStartedAt);
                    if (stale.SkippedBySafety)
                    {
                        _console.WriteLine($"[階段 1/4] ⚠ 有 {stale.Stale} 台裝置（超過鏡像 {stale.Total} 台的一半）本趟未出現在 PRTG 回應中，疑似帳號權限或查詢範圍變動，本趟不清除。");
                    }
                    else if (stale.Deleted > 0)
                    {
                        _console.WriteLine($"[階段 1/4] 已清除 {stale.Deleted} 台 PRTG 端已不存在的裝置。");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fail($"[階段 1/4] 過期裝置清除失敗：{ex.Message}");
                }
            }

            // 主機對應要用剛更新的裝置鏡像，且範圍要在感測器同步之前就定下來
            ResolveScope();

            // 階段 2：只同步取數範圍內裝置的感測器
            // 本趟寫入的列 SyncedAt 都等於這個時間（逐台與全站模式共用）；早於它的列就是本趟沒被刷新到的感測器
            var sensorsSyncStartedAt = DateTime.Now;
            // 本趟感測器鏡像是否「完整刷新」——成立時「沒刷新到」才代表範圍外或 PRTG 端已刪除
            var sensorsRefreshed = false;
            IReadOnlyCollection<long> sensorPendingForPurge = Array.Empty<long>();
            // 本趟「查詢成功但回 0 顆」的裝置：清除時給一趟寬限（見 DeleteSensorsNotSyncedSince）
            IReadOnlyCollection<long> emptyDevices = Array.Empty<long>();
            var scopeDevices = scope?.DeviceObjids;
            if (scopeDevices == null || scopeDevices.Count == 0)
            {
                // 範圍計算失敗已在 ResolveScope 計過 failures，這裡不重複計；範圍為空則是設定面的結果，不算失敗
                _console.WriteLine(scopeDevices == null
                    ? "[階段 2/4] 監看裝置無法取得，略過感測器同步。"
                    : "[階段 2/4] 沒有任何監看裝置，略過感測器同步。");
            }
            else
            {
                try
                {
                    _console.WriteLine("[階段 2/4] 開始同步 PRTG 感測器結構鏡像...");
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    if (scopeDevices.Count <= PerDeviceSensorFetchLimit)
                    {
                        var (written, targets, failedDevices, empties, pendingDevices) = await FetchSensorsForDevicesAsync(
                            scopeDevices.OrderBy(id => id).ToList(), concurrency, sensorsSyncStartedAt, ct, progress);
                        stopwatch.Stop();
                        sensorsCount = written;
                        sensorTargets = targets;
                        _console.WriteLine($"[階段 2/4] 感測器結構同步完成：範圍內 {scopeDevices.Count} 台裝置、寫入/更新 {sensorsCount} 個感測器{FormatStageStats(stopwatch, new StageOutcome(written, 0, true, null))}。");
                        if (failedDevices.Count > 0)
                        {
                            var shown = string.Join("、", failedDevices.OrderBy(id => id).Take(5));
                            var more = failedDevices.Count > 5 ? " 等" : "";
                            Fail($"[階段 2/4] ✗ {failedDevices.Count} 台裝置的感測器取得失敗：{shown}{more}");
                        }
                        else
                        {
                            _freshness.Record(PrtgFreshnessStore.Sensors, sensorsCount);
                        }
                        emptyDevices = empties;
                        pendingSensorDevices = pendingDevices;
                        sensorPendingForPurge = failedDevices;
                        // 沒同步到的裝置在清除時保留（見下方 preserve），其餘裝置的過期感測器照清
                        sensorsRefreshed = sensorsCount > 0;
                    }
                    else
                    {
                        progress?.Invoke(PrtgSyncSensorsPhase, 0, 0);
                        var (outcome, targets) = await FetchSensorsSiteWideAsync(scopeDevices, sensorsSyncStartedAt, ct);
                        stopwatch.Stop();
                        progress?.Invoke(PrtgSyncSensorsPhase, scopeDevices.Count, scopeDevices.Count);
                        sensorsCount = outcome.Written;
                        sensorTargets = targets;
                        _console.WriteLine($"[階段 2/4] 感測器結構同步完成：範圍內 {scopeDevices.Count} 台裝置、寫入/更新 {sensorsCount} 個感測器{FormatStageStats(stopwatch, outcome)}。");
                        if (!outcome.Converged)
                        {
                            historyAffectingFailure = true;
                            Fail($"[階段 2/4] ✗ {outcome.Error}已寫入 {sensorsCount} 個感測器，鏡像不完整。");
                            // 感測器名單只有半套，階段 4 會照這份名單抓數值——不講的話，
                            // 數值表會安靜地少一大塊而看不出邊界在哪。
                            _console.WriteLine("[階段 2/4] ⚠ 感測器名單不完整，本趟的數值擷取只會涵蓋已取得的部分。");
                        }
                        else
                        {
                            _freshness.Record(PrtgFreshnessStore.Sensors, sensorsCount);
                        }
                        sensorsRefreshed = outcome.Converged && sensorsCount > 0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    historyAffectingFailure = true;
                    Fail($"[階段 2/4] 感測器結構同步失敗：{ex.Message}");
                }
            }

            // 清除本趟沒被刷新的感測器（範圍外的、PRTG 端已刪的）：只有範圍可信、階段 2 無例外、
            // 全站模式已收斂、且有寫入時（逐台模式沒同步到的裝置一律保留，只清已完成的），「沒刷新到」才代表「不該留在鏡像」。
            // 必須在語意分類重算之前，免得替即將刪除的列白做工。
            // 另一道保險：範圍內沒有任何對應成功的裝置時不清；「主機主檔暫時讀到空清單 → 對應全數落空 →
            // 範圍只剩守門裝置」會把整份鏡像清光，沒有對應成功的主機時鏡像留著也無害。
            // 指定主機更新只刷新了那幾台的感測器，其餘裝置「沒刷新到」不代表範圍外——清了就是把整份鏡像清光。
            if (sensorsRefreshed && scope!.Mapped == 0)
            {
                sensorsRefreshed = false;
                _console.WriteLine("[階段 2/4] 監看裝置中沒有任何對應成功的裝置，本趟不清除感測器鏡像。");
            }
            var sensorPurgeScopeReason = sensorsRefreshed
                ? PrtgScopePurge.CheckScope(scope, devicesRefreshed)
                : null;
            if (sensorsRefreshed && sensorPurgeScopeReason != null)
            {
                sensorsRefreshed = false;
                if (scope!.IsPartial)
                    _console.WriteLine("[階段 2/4] 本趟只處理部分主機，不清除感測器鏡像。");
                else
                    _console.WriteLine($"[階段 2/4] {sensorPurgeScopeReason}，本趟不清除感測器鏡像。");
            }
            if (sensorsRefreshed)
            {
                var sensorPurgeShrinkReason = PrtgScopePurge.CheckShrink(
                    scope!.DeviceObjids.Count, _store.ScopeBaseline().Get());
                if (sensorPurgeShrinkReason != null)
                {
                    sensorsRefreshed = false;
                    _console.WriteLine($"[階段 2/4] ⚠ {sensorPurgeShrinkReason}，本趟不清除感測器鏡像。");
                }
            }
            if (sensorsRefreshed)
            {
                try
                {
                    // 沒同步到的裝置沒有被刷新不代表感測器已刪除，一顆都不能清
                    var (deleted, graceKept) = _store.DeleteSensorsNotSyncedSince(
                        sensorsSyncStartedAt, emptyDevices, sensorsSyncStartedAt - EmptyDeviceGrace,
                        scope!.PreserveDeviceObjids.Concat(sensorPendingForPurge).ToHashSet());
                    if (deleted > 0)
                        _console.WriteLine($"[階段 2/4] 已清除 {deleted} 個範圍外或 PRTG 端已不存在的感測器。");
                    if (graceKept > 0)
                        _console.WriteLine($"[階段 2/4] ⚠ {emptyDevices.Count} 台裝置本趟回傳 0 個感測器，鏡像中原有的 {graceKept} 個先保留一趟（下一趟仍為 0 才清除）：{string.Join("、", emptyDevices.OrderBy(id => id).Take(5))}{(emptyDevices.Count > 5 ? " 等" : "")}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Fail($"[階段 2/4] 過期感測器清除失敗：{ex.Message}");
                }
            }

            // 語意分類重算：未分類與自動分類的列依對照表更新，人工指定的分類不會被洗掉
            try
            {
                var categorized = _store.ApplyAutoCategories(_categoryOverrides);
                if (categorized > 0)
                    _console.WriteLine($"[階段 2/4] 已更新 {categorized} 個感測器的語意分類。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Fail($"[階段 2/4] 語意分類自動填入失敗：{ex.Message}");
            }
        }

        // 階段 3：狀態變更（前一日～今日區間）
        try
        {
            // 規則評估看 day-1 ～ day+1 的變更，只寫目標日會讓跨午夜的 down 持續時間算不準；今天的部分列之後會被冪等補齊。
            // 若 day.Date.AddDays(-1) > DateTime.Today（理論上不會），兩端互換不必處理——直接以 day 當兩端。
            var fromDate = stateChangesFrom?.Date ?? day.Date.AddDays(-1);
            var toDate = DateTime.Today;
            if (fromDate > toDate)
            {
                fromDate = day.Date;
                toDate = day.Date;
            }

            // 範圍計算失敗時傳 null：本階段略過且不重複計 failures（ResolveScope 已計）
            var rangeResult = await FetchStateChangesRangeAsync(fromDate, toDate, scope?.DeviceObjids, concurrency, ct, progress);
            stateChangesCount = rangeResult.TotalWritten;
            pendingMessageDevices = rangeResult.PendingDevices;
            if (!rangeResult.Converged)
            {
                // 不標 HistoryAffectingFailure：已取到的裝置是完整的，下一個窗口只補這幾台，不必整段重做
                Fail($"[階段 3/4] ✗ {rangeResult.Error}已寫入 {stateChangesCount} 筆狀態變更，{rangeResult.PendingDevices.Count} 台待補。");
            }
            else if (scope != null)
            {
                // 範圍無法取得時本階段是略過，不算一次成功的擷取
                _freshness.Record(PrtgFreshnessStore.StateChanges, stateChangesCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            historyAffectingFailure = true;
            Fail($"[階段 3/4] 狀態變更同步失敗：{ex.Message}");
        }

        // 階段 4：hourly 數值（前一日）
        if (fetchValues)
        {
            try
            {
                var activeSensors = sensorTargets.Where(s => !s.Paused).ToList();
                _console.WriteLine($"[階段 4/4] 開始擷取 PRTG 每小時數值（{day:yyyy-MM-dd}，未暫停感測器：{activeSensors.Count} 個，併發：{Math.Max(concurrency, 1)}）...");
                var (written, failedSensorCount) = await FetchValuesAsync(day, activeSensors, concurrency, ct, progress, PrtgValuesPhase);
                valuesCount = written;
                _console.WriteLine($"[階段 4/4] 每小時數值擷取完成，共寫入 {valuesCount} 筆數值。");

                // 部分 sensor 失敗屬正常損耗（其餘資料照樣落地）；但「有 sensor 要抓、卻一筆都沒抓到」
                // 代表這個階段實質上沒成功，必須計入 failures，否則回填會把整天報成成功。
                if (failedSensorCount > 0 && written == 0)
                {
                    Fail("[階段 4/4] 所有感測器的數值擷取皆失敗，本階段視為失敗。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Fail($"[階段 4/4] 每小時數值擷取失敗：{ex.Message}");
            }
        }
        else
        {
            _console.WriteLine("[階段 4/4] 數值擷取改由觸發式流程執行，本階段略過。");
        }

        return new PrtgFetchResult(devicesCount, sensorsCount, stateChangesCount, valuesCount, failures)
        {
            StageFailures = stageFailures,
            HistoryAffectingFailure = historyAffectingFailure,
            PendingSensorDevices = pendingSensorDevices,
            PendingMessageDevices = pendingMessageDevices,
            Scope = scope,
            DevicesRefreshed = devicesRefreshed
        };
    }

    /// <summary>
    /// 同日續跑：只補上一趟沒完成的裝置（感測器與狀態變更），已完成的不重查。不做過期清除：
    /// 只刷新了這幾台，其餘裝置「沒刷新到」不代表已刪除。回傳仍沒完成的裝置。
    /// </summary>
    internal async Task<(IReadOnlyList<long> Sensors, IReadOnlyList<long> Messages)> FetchPendingDevicesAsync(
        IReadOnlyList<long> sensorDevices, IReadOnlyList<long> messageDevices, DateTime stateChangesFrom,
        int concurrency, CancellationToken ct, Action<string, int, int>? progress = null)
    {
        IReadOnlyList<long> leftSensors = Array.Empty<long>();
        if (sensorDevices.Count > 0)
        {
            var (written, _, _, _, pending) = await FetchSensorsForDevicesAsync(sensorDevices, concurrency, DateTime.Now, ct, progress);
            leftSensors = pending;
            _console.WriteLine($"[續補] 感測器：{sensorDevices.Count} 台待補，寫入／更新 {written} 個，仍待補 {pending.Count} 台。");
            if (written > 0) _store.ApplyAutoCategories(_categoryOverrides);
        }

        IReadOnlyList<long> leftMessages = Array.Empty<long>();
        if (messageDevices.Count > 0)
        {
            var range = await FetchStateChangesRangeAsync(stateChangesFrom, DateTime.Today, messageDevices, concurrency, ct, progress);
            leftMessages = range.PendingDevices;
            _console.WriteLine($"[續補] 狀態變更：{messageDevices.Count} 台待補，新增 {range.TotalWritten} 筆，仍待補 {leftMessages.Count} 台。");
        }
        return (leftSensors, leftMessages);
    }

    /// <summary>
    /// 只擷取指定 sensor 的當日 hourly 數值（觸發式取數用）。
    /// 回傳實際寫入筆數與失敗的 sensor 數；併發與單 sensor 失敗隔離沿用階段 4 的既有機制。
    /// </summary>
    public async Task<(int Written, int FailedSensors)> FetchValuesForSensorsAsync(
        DateTime day, IReadOnlyList<long> sensorObjids, int concurrency, CancellationToken ct,
        Action<string, int, int>? progress = null)
    {
        if (sensorObjids == null || sensorObjids.Count == 0)
        {
            return (0, 0);
        }

        var targets = sensorObjids.Select(id => (Objid: id, Paused: false)).ToList();
        var (written, failed) = await FetchValuesAsync(day, targets, concurrency, ct, progress, PrtgTriggeredPhase);
        return (written, failed);
    }

    /// <summary>
    /// 白天範圍補抓：只為指定裝置逐台取感測器寫進鏡像（數值快照服務呼叫，補上新進取數範圍、鏡像還沒有感測器的裝置）。
    /// 逐台取法、單台失敗隔離、寫入鎖與夜間階段 2 逐台模式同一份（<see cref="FetchSensorsForDevicesAsync"/>）；
    /// 寫完照階段 2 重算語意分類。**不清除任何列**——這裡只看得到少數幾台，「沒刷新到」不代表該刪。
    /// 寫入列的 SyncedAt 是呼叫當下，晚於任何已在進行中的結構同步起點，不會被那趟的「未刷新即刪除」清掉。
    /// </summary>
    public async Task<PrtgSensorBackfillResult> BackfillSensorsForDevicesAsync(
        IReadOnlyList<long> deviceObjids, int concurrency, CancellationToken ct, bool requireCompleteDevice = false,
        IReadOnlyDictionary<long, PrtgRecentStateChangeQueueItem>? recentStateQueueItems = null)
    {
        var (written, _, failedDevices, emptyDevices, _) = await FetchSensorsForDevicesAsync(
            deviceObjids, concurrency, DateTime.Now, ct, progress: null, requireCompleteDevice: requireCompleteDevice,
            recentStateQueueItems: recentStateQueueItems);
        _store.ApplyAutoCategories(_categoryOverrides);
        return new PrtgSensorBackfillResult(written, failedDevices, emptyDevices);
    }

    /// <summary>階段 1：分頁抓取所有 devices 並寫入鏡像表</summary>
    private async Task<StageOutcome> FetchDevicesAsync(DateTime syncedAt, CancellationToken ct, Action<string, int, int>? progress = null)
    {
        var totalWritten = 0;

        var paged = await RunPagedStageAsync(() => FetchTablePagedAsync<PrtgDeviceRow>(
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
                    // 有長度上限的字串欄一律先截斷（上限與 LfDbContext 的 HasMaxLength 對齊）：
                    // PRTG 沒有長度保證，SQL Server 端超長會讓整批 500 筆一起寫入失敗、SQLite 靜默通過。
                    // Ip 截掉的一定不是 IPv4（最長 15 字元），對主機對應沒有影響。
                    Name = ColumnClamp.Clamp(GetStringProperty(el, "device"), 255) ?? string.Empty,
                    GroupPath = ColumnClamp.Clamp(GetStringProperty(el, "group"), 512) ?? string.Empty,
                    Ip = ColumnClamp.Clamp(GetStringProperty(el, "host"), 64),
                    Tags = GetStringProperty(el, "tags"),
                    Status = ColumnClamp.Clamp(GetStringProperty(el, "status"), 64),
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
            ct: ct,
            phase: PrtgSyncDevicesPhase,
            progress: progress,
            stageLabel: "階段 1/4 裝置結構"));

        return StageOutcome.From(totalWritten, paged);
    }

    /// <summary>
    /// 階段 2（逐台模式，範圍內裝置數不超過 <see cref="PerDeviceSensorFetchLimit"/>）：
    /// 逐台以 <c>id={裝置 objid}</c> 分頁抓取感測器並寫入鏡像表，同時收集名單供階段 4 使用。
    /// 單台失敗（例外或分頁未收斂）只記入失敗清單、不影響其他台；取消照舊往外擲。
    /// </summary>
    /// <returns>寫入數、感測器名單（objid、是否暫停）、失敗的裝置 objid 清單、查詢成功但取回 0 顆的裝置 objid 清單</returns>
    private async Task<(int Written, List<(long Objid, bool Paused)> Targets, List<long> FailedDevices, List<long> EmptyDevices, IReadOnlyList<long> PendingDevices)> FetchSensorsForDevicesAsync(
        IReadOnlyList<long> deviceObjids, int concurrency, DateTime syncedAt, CancellationToken ct,
        Action<string, int, int>? progress, bool requireCompleteDevice = false,
        IReadOnlyDictionary<long, PrtgRecentStateChangeQueueItem>? recentStateQueueItems = null)
    {
        var totalDevices = deviceObjids.Count;
        progress?.Invoke(PrtgSyncSensorsPhase, 0, totalDevices);

        var filter = deviceObjids.ToHashSet();
        var maxConcurrency = requireCompleteDevice ? Math.Clamp(concurrency, 1, 4) : Math.Max(concurrency, 1);
        // targets／empty／寫入在併發下共用同一把鎖（寫入本身也序列化，避免同一個 store 被並行 upsert）
        var sync = new object();
        var totalWritten = 0;
        var targets = new List<(long Objid, bool Paused)>();
        var empty = new List<long>();
        var completed = 0;

        var (failed, pendingDevices) = await RunDevicesWithPauseAsync(deviceObjids, maxConcurrency, "感測器同步", async deviceObjid =>
        {
            // 本台取回（通過範圍過濾）的列數：查詢成功且為 0 才算「PRTG 端確實沒有感測器」
            var deviceRows = 0;
            // 重排時同一台會再跑一次：寫入是冪等 upsert，但名單與計數只在整台成功才併入，否則會重複累計
            var deviceWritten = 0;
            var deviceTargets = new List<(long Objid, bool Paused)>();
            var staged = requireCompleteDevice ? new List<PrtgSensorRow>() : null;
            long stagedTextBytes = 0;
            var deviceFilter = requireCompleteDevice ? new HashSet<long> { deviceObjid } : filter;
            // 逐台不帶 phase／stageLabel：逐台印分頁進度會洗版，進度改由本方法以「台」回報
            var paged = await FetchSensorPagesAsync($"id={deviceObjid}", deviceFilter, syncedAt,
                batch =>
                {
                    if (staged is not null)
                    {
                        // 補抓完整前不寫鏡像；取消後仍能由「尚無 sensor」辨識並重試。
                        if (staged.Count + batch.Count > 15_000)
                            throw new InvalidOperationException("單一裝置補抓超過 15,000 個 sensor，已拒絕提交不完整鏡像。");
                        foreach (var row in batch)
                            stagedTextBytes += 2L * (row.Name.Length + (row.SensorType?.Length ?? 0) +
                                (row.Tags?.Length ?? 0) + (row.Unit?.Length ?? 0) + (row.Status?.Length ?? 0));
                        if (stagedTextBytes > 8L * 1024 * 1024)
                            throw new InvalidOperationException("單一裝置補抓文字超過 8 MiB，已拒絕提交不完整鏡像。");
                        staged.AddRange(batch);
                        deviceRows += batch.Count;
                        return;
                    }
                    lock (sync)
                    {
                        deviceRows += batch.Count;
                        deviceWritten += _store.UpsertSensors(batch, syncedAt);
                        deviceTargets.AddRange(batch.Select(r => (r.Objid, r.Paused)));
                    }
                },
                ct, stageLabel: null, strictScope: requireCompleteDevice, allowPagerRetry: false);
            if (paged.Error != null)
            {
                return (Exception?)paged.Exception ?? new InvalidOperationException(paged.Error);
            }
            if (deviceRows == 0)
            {
                lock (sync) empty.Add(deviceObjid);
            }
            else if (staged is not null)
            {
                ct.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (recentStateQueueItems != null && recentStateQueueItems.TryGetValue(deviceObjid, out var queueItem))
                        totalWritten += _store.UpsertSensorsAndEnqueueRecentStateChanges(staged, syncedAt, queueItem);
                    else
                        totalWritten += _store.UpsertSensors(staged, syncedAt, requireAtomic: true);
                    targets.AddRange(staged.Select(r => (r.Objid, r.Paused)));
                }
            }
            else
            {
                lock (sync)
                {
                    totalWritten += deviceWritten;
                    targets.AddRange(deviceTargets);
                }
            }
            return null;
        }, () =>
        {
            var done = Interlocked.Increment(ref completed);
            progress?.Invoke(PrtgSyncSensorsPhase, done, totalDevices);
        }, ct);

        return (totalWritten, targets, failed.ToList(), empty, pendingDevices);
    }

    /// <summary>
    /// 逐台取數的共用派發（sensors、messages）。夜間這類開啟重試的路徑共用一個暫停閘：
    /// 任一台可重試失敗就全部暫停（政策與數值取數同一份），失敗的台在全部送完後重排一次，再失敗或停損後沒送出的記入待補。
    /// 沒開重試的路徑（快照、探測）維持單次失敗即記失敗、不等待。
    /// </summary>
    /// <param name="fetchOne">取一台；回傳 null＝成功，其他＝失敗原因（取消穿透）</param>
    /// <returns>沒完成的裝置（失敗或沒送出），與其中值得再試的（永久失敗如 404 不算：重試治不好，留在待補會讓「待補評估」永遠掛著）</returns>
    private async Task<(IReadOnlyList<long> Failed, IReadOnlyList<long> Pending)> RunDevicesWithPauseAsync(
        IReadOnlyList<long> devices, int maxConcurrency, string stageName,
        Func<long, Task<Exception?>> fetchOne, Action onSettled, CancellationToken ct)
    {
        // 停損後的冷卻：同一趟後面的階段不再打剛停損的 PRTG，全部記待補；下一趟是新實例，不受影響
        if (_retryTransientPaging && ValueFetchCoolingDown)
        {
            _console.WriteLine($"  PRTG 仍在停損後的冷卻期，本趟{stageName} {devices.Count} 台不送請求、留待下一個窗口。");
            Signal?.Invoke(PrtgHaltedPhase, devices.Count, 0);
            foreach (var _ in devices) onSettled();
            var all = devices.ToList();
            return (all, all);
        }

        var session = new PauseSession(new PrtgValueFetchRetryPolicy(ValueFetchJitter),
            new PauseGate(RetryDelay ?? PrtgTablePager.DefaultWait, TimeSource));
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var sync = new object();
        var pending = new List<long>();
        var permanent = new HashSet<long>();
        var requeue = new List<long>();

        async Task RunPassAsync(IReadOnlyList<long> ids, bool lastPass)
        {
            await Task.WhenAll(ids.Select(async id =>
            {
                // 閘放行才排隊拿名額（暫停期間不佔名額）；排隊時閘又被拉起就放回去重等，暫停中不送新請求
                while (true)
                {
                    await session.Gate.WaitAsync(ct);
                    await semaphore.WaitAsync(ct);
                    if (!session.Gate.IsRaised) break;
                    semaphore.Release();
                }
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (session.Stopped)
                    {
                        lock (sync) pending.Add(id);
                        return;
                    }

                    var sentGeneration = session.Gate.Generation;
                    Exception? failure;
                    try
                    {
                        failure = await fetchOne(id);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    if (failure == null)
                    {
                        session.RecordSuccess(sentGeneration);
                        return;
                    }

                    var retryable = _retryTransientPaging && failure is PrtgClientException client && session.Policy.ShouldRetry(client);
                    lock (sync)
                    {
                        (retryable && !lastPass ? requeue : pending).Add(id);
                        if (IsPermanentFailure(failure)) permanent.Add(id);
                    }
                    if (retryable) OnRetryableDeviceFailure(session, (PrtgClientException)failure, sentGeneration, stageName, ct);
                }
                finally
                {
                    semaphore.Release();
                    if (!lastPass) onSettled();
                }
            }));
        }

        try
        {
            await RunPassAsync(devices, lastPass: false);
            if (requeue.Count > 0)
            {
                Log.Info("[PRTG] {Stage}第一輪有 {Count} 台可重試失敗，暫停後重排一次", stageName, requeue.Count);
                await RunPassAsync(requeue.ToList(), lastPass: true);
            }
        }
        finally
        {
            session.Gate.Reset(); // 最後一台失敗時拉起的暫停已經沒有人要等
        }

        if (session.Stopped)
        {
            _console.WriteLine($"  ⚠ PRTG {stageName}停損（{session.StopReason}）：{pending.Count} 台待補，下一個窗口續補。");
            Log.Warn("[PRTG] {Stage}停損（{Reason}）：{Count} 台待補", stageName, session.StopReason, pending.Count);
            Signal?.Invoke(PrtgHaltedPhase, pending.Count, 0);
        }
        return (pending, pending.Where(id => !permanent.Contains(id)).ToList());
    }

    /// <summary>
    /// 再試結果也一樣的失敗，不進待補（進了會讓那幾天每晚重評、每個窗口多跑一趟、「待補評估」永遠掛著）：
    /// PRTG 明確回「這個請求不成立」的 4xx（如裝置已刪除的 404；401／403 是憑證問題、429 是限流，修好就能補），
    /// 以及這台的分頁確定性不收斂（回應形狀不對、頁數或鏡像保護上限）。逾時、傳輸、5xx、憑證與未知失敗都還值得下個窗口再試。
    /// </summary>
    private static bool IsPermanentFailure(Exception failure) => failure switch
    {
        PrtgClientException { Kind: PrtgFailureKind.HttpStatus, StatusCode: >= 400 and < 500 and not (401 or 403 or 429) } => true,
        PrtgClientException => false,
        PrtgPagingNotConvergedException or InvalidOperationException => true,
        _ => false
    };

    private void OnRetryableDeviceFailure(PauseSession session, PrtgClientException ex, long sentGeneration, string stageName, CancellationToken ct)
    {
        var decision = session.OnRetryableFailure(ex.RetryAfter, sentGeneration, "台", ct);
        if (decision.Pause > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(decision.Pause.TotalSeconds);
            Signal?.Invoke(PrtgPausedPhase, decision.Consecutive, seconds);
            // 只寫 Info：PRTG 慢的夜晚不該讓趟次變成「有警告」，停損才出聲
            _console.WriteLine($"  PRTG {stageName}回應異常（{ex.Message}），全部暫停 {seconds} 秒後繼續（連續失敗 {decision.Consecutive} 台）。");
            Log.Info("[PRTG] {Stage}失敗（{Reason}），全部暫停 {Seconds} 秒後繼續（連續失敗 {Consecutive} 台）",
                stageName, ex.Message, seconds, decision.Consecutive);
        }
        if (decision.StopReason != null) EnterValueFetchCooldown();
    }

    /// <summary>
    /// 階段 2（全站模式，範圍內裝置數超過 <see cref="PerDeviceSensorFetchLimit"/>）：
    /// 一次全站分頁抓取感測器，mapper 只保留 <c>parentid</c> 在範圍內的列（範圍外不寫入、不進名單）。
    /// 進度單位是「台」，列數不能當分子，所以不把 phase／progress 交給分頁器（每 N 頁一行的文字進度照舊）。
    /// </summary>
    private async Task<(StageOutcome Outcome, List<(long Objid, bool Paused)> SensorTargets)> FetchSensorsSiteWideAsync(
        IReadOnlySet<long> scopeDevices, DateTime syncedAt, CancellationToken ct)
    {
        var totalWritten = 0;
        var targets = new List<(long Objid, bool Paused)>();

        var paged = await FetchSensorPagesAsync(null, scopeDevices, syncedAt,
            batch =>
            {
                totalWritten += _store.UpsertSensors(batch, syncedAt);
                targets.AddRange(batch.Select(r => (r.Objid, r.Paused)));
            },
            ct, stageLabel: "階段 2/4 感測器結構");

        return (StageOutcome.From(totalWritten, paged), targets);
    }

    /// <summary>
    /// 感測器表的分頁讀取（逐台與全站兩種取法唯一的呼叫點）：同一組欄位、同一個 mapper、同一個範圍過濾語意，
    /// 兩種取法寫進鏡像的內容因此相同。
    /// </summary>
    private Task<PagedStageResult> FetchSensorPagesAsync(
        string? extraQuery, IReadOnlySet<long> scopeDevices, DateTime syncedAt,
        Action<IReadOnlyList<PrtgSensorRow>> onBatch, CancellationToken ct, string? stageLabel, bool strictScope = false,
        bool allowPagerRetry = true)
        => RunPagedStageAsync(() => FetchTablePagedAsync<PrtgSensorRow>(
            content: "sensors",
            columns: "objid,parentid,sensor,type,tags,unit,status,paused,dependency",
            extraQuery: extraQuery,
            mapper: el =>
            {
                if (strictScope && !scopeDevices.Contains(GetLongProperty(el, "parentid") ?? 0))
                    throw new InvalidOperationException("感測器補抓回應包含要求裝置之外的資料，拒絕提交或標記為空。");
                return MapSensorRow(el, scopeDevices, syncedAt);
            },
            onBatch: onBatch,
            ct: ct,
            phase: null,
            progress: null,
            allowPagerRetry: allowPagerRetry,
            stageLabel: stageLabel));

    /// <summary>sensors 表單列轉鏡像列（唯一一份 mapper）；parentid 不在取數範圍內的列回 null（不寫入）。</summary>
    private static PrtgSensorRow? MapSensorRow(JsonElement el, IReadOnlySet<long> scopeDevices, DateTime syncedAt)
    {
        var objid = GetLongProperty(el, "objid");
        if (!objid.HasValue) return null;

        var parentid = GetLongProperty(el, "parentid") ?? 0;
        if (!scopeDevices.Contains(parentid)) return null;

        return new PrtgSensorRow
        {
            Objid = objid.Value,
            DeviceObjid = parentid,
            // 截斷理由同 device mapper（上限對齊 LfDbContext）
            Name = ColumnClamp.Clamp(GetStringProperty(el, "sensor"), 255) ?? string.Empty,
            SensorType = ColumnClamp.Clamp(GetStringProperty(el, "type"), 128) ?? string.Empty,
            Tags = GetStringProperty(el, "tags"),
            Unit = ColumnClamp.Clamp(GetStringProperty(el, "unit"), 64),
            Status = ColumnClamp.Clamp(GetStringProperty(el, "status"), 64),
            ThresholdsJson = null,
            DependencyObjid = ParseDependency(el),
            Paused = ParsePaused(el),
            Category = null,
            CategorySource = null,
            SyncedAt = syncedAt,
            CreatedAt = syncedAt
        };
    }

    /// <summary>
    /// 區間抓取狀態變更：只對取數範圍內的物件逐一以 <c>id={objid}</c> 分頁查 messages，依日分桶寫入狀態變更表。
    /// 每個物件各自一份提早停止狀態；單一物件失敗只記入失敗清單、不影響其他物件；取消照舊往外擲。
    /// </summary>
    /// <param name="scopeDeviceObjids">取數範圍裝置；null＝範圍計算失敗（呼叫端已計過失敗），空＝範圍內沒有裝置。兩者都不發任何請求。</param>
    public async Task<PrtgStateChangeRangeResult> FetchStateChangesRangeAsync(
        DateTime fromDate, DateTime toDate, IReadOnlyCollection<long>? scopeDeviceObjids,
        int concurrency, CancellationToken ct, Action<string, int, int>? progress = null)
    {
        var from = fromDate.Date;
        var to = toDate.Date;

        if (scopeDeviceObjids == null || scopeDeviceObjids.Count == 0)
        {
            // 範圍計算失敗已在前面計過 failures，這裡不重複計；範圍為空是設定面的結果，不算失敗
            _console.WriteLine(scopeDeviceObjids == null
                ? "[階段 3/4] 監看裝置無法取得，略過狀態變更同步。"
                : "[階段 3/4] 沒有任何監看裝置，略過狀態變更同步。");
            return new PrtgStateChangeRangeResult(
                WrittenByDay: new Dictionary<DateTime, int>(), TotalWritten: 0, ReadRows: 0, Pages: 0,
                StoppedEarly: false, Converged: true, Error: null,
                QueriedObjects: 0, FailedObjects: 0, EmptyObjects: 0);
        }

        var objects = StateChangeQueryObjects(scopeDeviceObjids);
        var totalObjects = objects.Count;
        var threshold = from.AddDays(-1);
        var now = DateTime.Now;
        var maxConcurrency = Math.Max(concurrency, 1);

        // 下列累計在併發下共用同一把鎖（寫入本身也序列化，避免同一個 store 被並行 append）
        var sync = new object();
        var totalWritten = 0;
        var unparseableCount = 0;
        var writtenByDay = new Dictionary<DateTime, int>();
        var writtenSensors = new HashSet<long>();
        var pages = 0;
        var readRows = 0;
        var stoppedEarly = false;
        var emptyObjects = 0;
        var completed = 0;
        var stringFallback = 0;
        var treeSizeShortDevices = 0;

        _console.WriteLine($"[階段 3/4] 開始同步 PRTG 狀態變更（{from:yyyy-MM-dd} ～ {to:yyyy-MM-dd}，逐裝置查詢 {totalObjects} 台，併發 {maxConcurrency}）...");
        progress?.Invoke(PrtgSyncMessagesPhase, 0, totalObjects);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var (failed, pendingDevices) = await RunDevicesWithPauseAsync(objects, maxConcurrency, "狀態變更同步", async objid =>
        {
            if (_guard != null) await _guard.WaitIfBusyAsync(ct);

            // 本物件區間內取到的列數：用來判斷「區間內無訊息」
            var inRangeRows = 0;
            var paged = await RunPagedStageAsync(() => FetchTablePagedAsync<PrtgStateChangeRow>(
                content: "messages",
                columns: "objid,datetime,status,message",
                extraQuery: StateChangesQuery(objid, from),
                mapper: el =>
                {
                    if (!TryParseMessageTime(el, out var changedAt, out var fromString))
                    {
                        Interlocked.Increment(ref unparseableCount);
                        return null;
                    }
                    if (fromString) Interlocked.Increment(ref stringFallback);

                    // 區間以日期計、含兩端：fromDate.Date <= changedAt.Date <= toDate.Date 的列寫入
                    if (changedAt.Date < from || changedAt.Date > to)
                    {
                        return null;
                    }

                    var sensorObjid = GetLongProperty(el, "objid");
                    if (!sensorObjid.HasValue) return null;

                    inRangeRows++;
                    return new PrtgStateChangeRow
                    {
                        SensorObjid = sensorObjid.Value,
                        ChangedAt = changedAt,
                        Status = ColumnClamp.Clamp(GetStringProperty(el, "status"), 64) ?? string.Empty,
                        PrevStatus = null,
                        Message = GetStringProperty(el, "message"),
                        Quality = PrtgDataQuality.Ok,
                        CreatedAt = now
                    };
                },
                onBatch: batch =>
                {
                    lock (sync)
                    {
                        // AppendStateChanges 只回總數，按日期分組各呼叫一次才能得到正確的每日新增數
                        foreach (var group in batch.GroupBy(r => r.ChangedAt.Date))
                        {
                            var written = _store.AppendStateChanges(group.ToList());
                            totalWritten += written;
                            writtenByDay[group.Key] = writtenByDay.GetValueOrDefault(group.Key) + written;
                        }
                        foreach (var row in batch) writtenSensors.Add(row.SensorObjid);
                    }
                },
                ct: ct,
                // 逐物件不帶 phase／progress／stageLabel：逐台印分頁進度會洗版，進度改由本方法以「台」回報
                phase: null,
                progress: null,
                stageLabel: null,
                // messages 的 objid 是「發出訊息的 sensor」而不是訊息自己的 id：
                // 同一天同一顆 sensor 會有很多列。列鍵要與 lf_prtg_state_changes 的去重鍵
                //（sensor_objid + changed_at）一致，否則每顆 sensor 只會留下第一筆狀態變更。
                rowKey: el => GetStringProperty(el, "objid") is { } id
                    ? id + "|" + (GetStringProperty(el, "datetime") ?? string.Empty)
                    : null,
                // 提早停止的判斷只看本頁，閉包不帶跨頁／跨物件狀態
                stopAfterPage: pageElements =>
                {
                    // 1. 本頁至少一列；
                    if (pageElements.Count == 0) return false;

                    // 2. 本頁每一列的 datetime 都能解析（任一筆失敗 → 不停）；
                    var dts = new List<DateTime>(pageElements.Count);
                    foreach (var el in pageElements)
                    {
                        if (!TryParseMessageTime(el, out var dt, out _))
                        {
                            return false;
                        }
                        dts.Add(dt);
                    }

                    // 3. 本頁列依時間非遞增（前一列 >= 後一列；有任一處遞增 → 不停）；
                    for (var i = 0; i < dts.Count - 1; i++)
                    {
                        if (dts[i] < dts[i + 1]) return false;
                    }

                    // 4. 本頁最新的一筆（第一列）早於 fromDate.Date.AddDays(-1)（即整頁都比區間起點再早一天以上）。
                    return dts[0] < threshold;
                },
                allowPagerRetry: false));

            lock (sync)
            {
                pages += paged.Result?.Pages ?? paged.Exception?.Pages ?? 0;
                readRows += paged.Result?.ReadRows ?? paged.Exception?.ReadRows ?? 0;
                if (paged.Result?.StoppedEarly == true) stoppedEarly = true;
                if (paged.Result?.TreeSizeShort == true) treeSizeShortDevices++;
                if (paged.Error == null && inRangeRows == 0) emptyObjects++;
            }
            return paged.Error == null ? null : (Exception?)paged.Exception ?? new InvalidOperationException(paged.Error);
        }, () =>
        {
            var done = Interlocked.Increment(ref completed);
            progress?.Invoke(PrtgSyncMessagesPhase, done, totalObjects);
        }, ct);
        stopwatch.Stop();

        if (unparseableCount > 0)
        {
            _console.WriteLine($"  ⚠ 狀態變更中有 {unparseableCount} 筆紀錄無法解析時間（缺 datetime_raw 且顯示字串也解析不了），已略過。");
        }
        if (stringFallback > 0)
        {
            _console.WriteLine($"  ⚠ 狀態變更中有 {stringFallback} 筆缺 datetime_raw，改以顯示字串依服務主機地區設定解析；若 PRTG 帳號時區與服務主機不同，這些列的時間會偏移。");
        }

        if (treeSizeShortDevices > 0)
        {
            // 只進執行輸出、行首不放警告符號：這不是問題，不該讓趟次變「有警告」，但要查得到
            _console.WriteLine($"[階段 3/4] {treeSizeShortDevices} 台裝置的 messages 讀取少於 treesize（僅提醒，帶時間篩選時語意未證實）。");
        }

        var converged = failed.Count == 0;
        var head = converged ? "狀態變更同步完成" : "狀態變更同步未完成";
        var elapsed = stopwatch.Elapsed.TotalSeconds;
        _console.WriteLine($"[階段 3/4] {head}：查詢 {totalObjects} 台、讀取 {readRows} 筆、新增 {totalWritten} 筆（其餘已存在）、{emptyObjects} 台區間內無訊息、平均每台 {elapsed / totalObjects:F1} 秒（總耗時 {elapsed:F1} 秒）。");
        // 各日新增數：回望多日時看得出「哪一天真的有變更、哪一天是空的」，
        // 只印總數的話，某一天整段沒取到與那天本來就沒事長得一模一樣
        if (writtenByDay.Count > 1)
        {
            _console.WriteLine("[階段 3/4] 各日新增：" + string.Join("、",
                writtenByDay.OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key:MM-dd} {kv.Value} 筆")));
        }

        string? error = null;
        if (!converged)
        {
            error = $"{failed.Count} 台裝置的狀態變更取得失敗。";
            var shownIds = failed.OrderBy(id => id).Take(5).ToList();
            var names = _store.GetDeviceNamesByObjids(shownIds);
            var shown = string.Join("、", shownIds.Select(id =>
                names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? $"{name}({id})" : id.ToString(CultureInfo.InvariantCulture)));
            var more = failed.Count > 5 ? " 等" : "";
            _console.WriteLine($"[階段 3/4] ✗ {failed.Count} 台裝置的狀態變更取得失敗：{shown}{more}");
        }
        else if (readRows == 0)
        {
            // 範圍內每一台都沒讀到任何列：可能是真的都沒事，也可能是 id=<裝置> 不含底下感測器的訊息——要讓人看得見
            _console.WriteLine($"[階段 3/4] ⚠ 範圍內 {totalObjects} 台裝置近期都沒有任何狀態變更——若 PRTG 上確實有告警，可能是以裝置查詢不含底下感測器的訊息，請執行環境探測並查看 9d-5 的結論。");
        }

        if (writtenSensors.Count > 0)
        {
            var mirrored = _store.GetSensorTargets().Select(t => t.Objid).ToHashSet();
            var notMirrored = writtenSensors.Count(id => !mirrored.Contains(id));
            if (notMirrored > 0)
            {
                _console.WriteLine($"[階段 3/4] 其中 {notMirrored} 顆感測器尚未在鏡像中（新增的感測器會在下次結構同步後補上），狀態變更已照常寫入。");
            }
        }

        return new PrtgStateChangeRangeResult(
            WrittenByDay: writtenByDay,
            TotalWritten: totalWritten,
            ReadRows: readRows,
            Pages: pages,
            StoppedEarly: stoppedEarly,
            Converged: converged,
            Error: error,
            QueriedObjects: totalObjects,
            FailedObjects: failed.Count,
            EmptyObjects: emptyObjects)
        {
            PendingDevices = pendingDevices
        };
    }

    /// <summary>
    /// 要對哪些 PRTG 物件查 messages——這是**唯一切換點**。目前回傳範圍內裝置的 objid（由小到大），
    /// 預期 PRTG 的 <c>id=&lt;裝置&gt;</c> 會一併回傳底下感測器的訊息。
    /// 若探測 9d-5 證實 <c>id=&lt;裝置&gt;</c> 不含下層感測器訊息，改成回傳範圍內未暫停感測器的 objid 即可，其餘流程不必動。
    /// </summary>
    private static IReadOnlyList<long> StateChangeQueryObjects(IReadOnlyCollection<long> scopeDeviceObjids)
        => scopeDeviceObjids.OrderBy(id => id).ToList();

    /// <summary>
    /// 階段 4：對未暫停的 sensor 依併發上限擷取 hourly 聚合數值並逐 sensor 寫入鏡像表。
    /// 失敗處理在這裡單點完成（夜間階段 4、觸發式取數、回填三個呼叫端自動受惠），分三段：
    /// 第一輪（並行，碰到可重試失敗就拉起共用暫停閘）→ 補抓輪（第一輪失敗的可重試顆，一次一顆）→ 彙總。
    /// 政策細節見 <see cref="PrtgValueFetchRetryPolicy"/>；停損不是例外，呼叫端照舊拿到 <c>(寫入筆數, 失敗顆數)</c>。
    /// </summary>
    private async Task<(int Written, int FailedSensors)> FetchValuesAsync(
        DateTime day,
        IReadOnlyList<(long Objid, bool Paused)> activeSensors,
        int concurrency,
        CancellationToken ct,
        Action<string, int, int>? progress = null,
        string stage = PrtgValuesPhase)
    {
        if (activeSensors.Count == 0)
        {
            return (0, 0);
        }

        var totalSensors = activeSensors.Count;
        progress?.Invoke(stage, 0, totalSensors);

        // 停損後的冷卻：不送任何請求，全部計失敗。觸發式取數每 30 秒、回填逐日都用同一個實例重複呼叫，
        // 沒有這一道的話每次呼叫都重新累計 10 顆，PRTG 掛掉的整段時間會被反覆打。
        if (TryRejectDuringValueFetchCooldown(totalSensors))
        {
            progress?.Invoke(stage, totalSensors, totalSensors);
            return (0, totalSensors);
        }

        var run = new ValueFetchRun(day, activeSensors, new PauseSession(
            new PrtgValueFetchRetryPolicy(ValueFetchJitter),
            new PauseGate(RetryDelay ?? PrtgTablePager.DefaultWait, TimeSource)));

        try
        {
            await FetchValuesFirstRoundAsync(run, concurrency, ct, progress, stage);
            await FetchValuesSupplementRoundAsync(run, ct);
        }
        finally
        {
            run.Gate.Reset(); // 最後一顆失敗時拉起的暫停已經沒有人要等
        }
        return SummarizeValueFetch(run);
    }

    /// <summary>單顆 sensor 取數失敗的輕量記錄：例外型別短名、訊息（截前 500 字）、記錄當下是否為逾時。</summary>
    internal readonly record struct SensorFetchFailure(string Kind, string Message, bool IsTimeout)
    {
        internal const int MaxMessageLength = 500;
    }

    /// <summary>
    /// 一次 <see cref="FetchValuesAsync"/> 的共用狀態。每顆 sensor 的結果寫在自己的索引位置（不同顆互不干擾），
    /// 政策與停損旗標的變動由 <see cref="Sync"/> 序列化。
    /// </summary>
    private sealed class ValueFetchRun
    {
        internal ValueFetchRun(DateTime day, IReadOnlyList<(long Objid, bool Paused)> sensors, PauseSession session)
        {
            Day = day;
            Sensors = sensors;
            Session = session;
            Sdate = day.Date.ToString("yyyy-MM-dd-00-00-00");
            Edate = day.Date.AddDays(1).ToString("yyyy-MM-dd-00-00-00");
            States = new ValueFetchState[sensors.Count];
            Failures = new SensorFetchFailure?[sensors.Count];
            Retryable = new bool[sensors.Count];
            Supplemented = new bool[sensors.Count];
        }

        internal DateTime Day { get; }
        internal IReadOnlyList<(long Objid, bool Paused)> Sensors { get; }
        internal PauseSession Session { get; }
        internal PrtgValueFetchRetryPolicy Policy => Session.Policy;
        internal PauseGate Gate => Session.Gate;
        internal string Sdate { get; }
        internal string Edate { get; }
        internal ValueFetchState[] States { get; }
        /// <summary>每顆最近一次失敗的輕量記錄（不保留整個例外與堆疊，避免整輪結束前常駐大量例外物件）。</summary>
        internal SensorFetchFailure?[] Failures { get; }
        /// <summary>該顆最近一次失敗是不是可重試（補抓清單的依據）。</summary>
        internal bool[] Retryable { get; }
        /// <summary>該顆有沒有進過補抓輪（「補抓後仍失敗」的計數依據）。</summary>
        internal bool[] Supplemented { get; }
        internal bool Stopped => Session.Stopped;
        internal string? StopReason => Session.StopReason;
        internal string? FirstFailureMessage;
        internal int Values;
        internal int Unparsed;
        internal int OutsideDay;
        internal int StringFallback;
        internal int ZoneDrift;
    }

    private enum ValueFetchState : byte { Pending = 0, Succeeded, Failed, NotSent }

    /// <summary>第一輪：依併發上限並行取每一顆；進度只在這一輪推進（done 最終等於 total）。</summary>
    private async Task FetchValuesFirstRoundAsync(
        ValueFetchRun run, int concurrency, CancellationToken ct, Action<string, int, int>? progress, string stage)
    {
        var totalSensors = run.Sensors.Count;
        var maxConcurrency = Math.Max(concurrency, 1);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var completedSensors = 0;

        var tasks = Enumerable.Range(0, totalSensors).Select(async index =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                await TryFetchSensorValuesAsync(run, index, supplement: false, ct);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completedSensors);
                progress?.Invoke(stage, done, totalSensors);
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 補抓輪：第一輪失敗且可重試的顆，第一輪結束後一次一顆再抓一次（同樣先過暫停閘與配額）。
    /// 補抓不推進進度；已停損就不補。補抓仍失敗的顆維持失敗。
    /// </summary>
    private async Task FetchValuesSupplementRoundAsync(ValueFetchRun run, CancellationToken ct)
    {
        if (run.Stopped) return;

        var candidates = Enumerable.Range(0, run.Sensors.Count)
            .Where(i => run.States[i] == ValueFetchState.Failed && run.Retryable[i])
            .ToList();
        if (candidates.Count == 0) return;

        // 只寫 Info：PRTG 慢的夜晚不該讓趟次變成「有警告」
        Log.Info("[PRTG] {Day:yyyy-MM-dd} 數值取數第一輪有 {Count} 顆可重試失敗，第一輪結束後逐顆補抓",
            run.Day, candidates.Count);

        var recovered = 0;
        foreach (var index in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (run.Stopped) break;
            await TryFetchSensorValuesAsync(run, index, supplement: true, ct);
            if (run.States[index] == ValueFetchState.Succeeded) recovered++;
        }

        Log.Info("[PRTG] {Day:yyyy-MM-dd} 數值取數補抓結束：{Recovered}/{Count} 顆補抓成功",
            run.Day, recovered, candidates.Count);
    }

    /// <summary>
    /// 取一顆 sensor 的當日數值並寫入。流程：等暫停閘放行 → 守門 → 送出請求 → 寫入。
    /// 單一 sensor 的失敗（逾時、404、PRTG 暫時 5xx）只影響它自己：記在 <see cref="ValueFetchRun"/> 的該索引，不往外擲。
    /// 讓例外往外逃會被階段層的 catch 接住，於是「三千個 sensor 已寫進去、第七個逾時」會被回報成
    /// 「數值 0 筆、階段失敗」——已落地的資料反而看不見。只有取消（OCE 且 ct 已取消）穿透。
    /// 只有「請求本身」擲出的可重試 <see cref="PrtgClientException"/> 才進暫停／補抓；寫入失敗等其他例外一律不重試。
    /// </summary>
    private async Task TryFetchSensorValuesAsync(ValueFetchRun run, int index, bool supplement, CancellationToken ct)
    {
        var target = run.Sensors[index];
        var requestPhase = false;
        var sentGeneration = 0L;
        try
        {
            // 閘放行之後才呼叫守門；守門可能等很久，等完再看一次閘（等的期間別顆可能已經拉起暫停）
            await run.Gate.WaitAsync(ct);
            if (_guard != null)
            {
                await _guard.WaitIfBusyAsync(ct);
                await run.Gate.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            if (run.Stopped)
            {
                // 已停損：第一輪還沒送出的顆計失敗、不送請求；補抓輪的顆維持第一輪的失敗
                if (!supplement) run.States[index] = ValueFetchState.NotSent;
                return;
            }

            if (supplement) run.Supplemented[index] = true;
            var query = $"api/historicdata.json?id={target.Objid}&avg={HistoricAverageSeconds}&sdate={run.Sdate}&edate={run.Edate}";
            requestPhase = true;
            sentGeneration = run.Gate.Generation;
            var json = await _client.GetJsonAsync(query, ct);
            requestPhase = false;
            // PRTG 有回應就代表它活著：序列與連續失敗數歸零（後續的解析或寫入失敗是本機的事，與 PRTG 無關）。
            // 暫停拉起之前就送出的慢回應不算：它不能證明 PRTG 在這次暫停之後恢復了
            run.Session.RecordSuccess(sentGeneration);

            var rows = ParseHistoricData(json, target.Objid, out var unparsed, out var stringFallback, out var zoneDrift);
            // 有些 PRTG 版本可能忽略 sdate/edate；不能因為 API 回了更多列就寫入請求日以外的資料。
            var outsideDay = rows.RemoveAll(r => r.PeriodStart < run.Day.Date || r.PeriodStart >= run.Day.Date.AddDays(1));
            if (outsideDay > 0) Interlocked.Add(ref run.OutsideDay, outsideDay);
            if (unparsed > 0) Interlocked.Add(ref run.Unparsed, unparsed);
            if (stringFallback > 0) Interlocked.Add(ref run.StringFallback, stringFallback);
            if (zoneDrift > 0) Interlocked.Add(ref run.ZoneDrift, zoneDrift);
            if (rows.Count > 0)
            {
                // 逐 sensor 寫入資料庫後立即釋放記憶體，絕不累積成大清單一次寫入
                var written = _store.UpsertValues(rows);
                Interlocked.Add(ref run.Values, written);
            }

            run.States[index] = ValueFetchState.Succeeded;
            run.Failures[index] = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryable = requestPhase && ex is PrtgClientException clientEx && run.Policy.ShouldRetry(clientEx);
            run.States[index] = ValueFetchState.Failed;
            run.Failures[index] = new SensorFetchFailure(
                ex.GetType().Name,
                ex.Message.Length > SensorFetchFailure.MaxMessageLength ? ex.Message[..SensorFetchFailure.MaxMessageLength] : ex.Message,
                IsTimeoutException(ex, CancellationToken.None));
            run.Retryable[index] = retryable;
            Interlocked.CompareExchange(ref run.FirstFailureMessage, ex.Message, null);
            if (retryable) OnRetryableValueFailure(run, (PrtgClientException)ex, sentGeneration, ct);
        }
    }

    /// <summary>可重試失敗的處理：判定在 <see cref="PauseSession"/>（與逐台路徑同一份），這裡只負責出聲與冷卻。</summary>
    private void OnRetryableValueFailure(ValueFetchRun run, PrtgClientException ex, long sentGeneration, CancellationToken ct)
    {
        var decision = run.Session.OnRetryableFailure(ex.RetryAfter, sentGeneration, "顆", ct);
        if (decision.Pause > TimeSpan.Zero)
        {
            Signal?.Invoke(PrtgPausedPhase, decision.Consecutive, (int)Math.Ceiling(decision.Pause.TotalSeconds));
            Log.Info("[PRTG] {Day:yyyy-MM-dd} 數值取數失敗（{Reason}），全部工作暫停 {Seconds} 秒後繼續（連續失敗 {Consecutive} 顆）",
                run.Day, ex.Message, (int)Math.Ceiling(decision.Pause.TotalSeconds), decision.Consecutive);
        }

        if (decision.StopReason != null) EnterValueFetchCooldown();
    }

    /// <summary>目前是否在停損後的冷卻期（呼叫 <see cref="FetchValuesAsync"/> 會被回絕）；觸發式輪詢據此決定要不要先把主機記成已抓。</summary>
    internal bool ValueFetchCoolingDown
    {
        get { lock (_valueFetchCooldownLock) return TimeSource.GetUtcNow() < _valueFetchCooldownUntil; }
    }

    /// <summary>停損：記下冷卻到期時間，並讓下一次第一個被回絕的呼叫再出聲一次。</summary>
    private void EnterValueFetchCooldown()
    {
        lock (_valueFetchCooldownLock)
        {
            _valueFetchCooldownUntil = TimeSource.GetUtcNow() + PrtgValueFetchRetryPolicy.Cooldown;
            _valueFetchCooldownNoticed = false;
        }
    }

    /// <summary>冷卻期內回 true（呼叫端不送任何請求）；只有冷卻期內第一次被回絕時輸出一行，之後的回絕靜默。</summary>
    private bool TryRejectDuringValueFetchCooldown(int sensorCount)
    {
        TimeSpan remaining;
        bool notice;
        lock (_valueFetchCooldownLock)
        {
            var now = TimeSource.GetUtcNow();
            if (now >= _valueFetchCooldownUntil) return false;
            remaining = _valueFetchCooldownUntil - now;
            notice = !_valueFetchCooldownNoticed;
            _valueFetchCooldownNoticed = true;
        }

        if (notice)
        {
            _console.WriteLine($"  ⚠ PRTG 數值取數仍在停損後的冷卻期（約 {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分鐘後恢復），"
                + $"本次 {sensorCount} 顆不送請求、計為失敗，留待下一趟。");
        }
        return true;
    }

    /// <summary>彙總：失敗計數、停損與各種損耗的 ⚠ 輸出，回傳 <c>(寫入筆數, 失敗顆數)</c>。</summary>
    private (int Written, int FailedSensors) SummarizeValueFetch(ValueFetchRun run)
    {
        var failedSensors = 0;
        var timedOutSensors = 0;
        var supplementFailedSensors = 0;
        var notSent = 0;
        var supplementNotReached = 0;
        for (var i = 0; i < run.Sensors.Count; i++)
        {
            switch (run.States[i])
            {
                case ValueFetchState.Succeeded:
                    break;
                case ValueFetchState.NotSent:
                    failedSensors++;
                    notSent++;
                    break;
                default:
                    failedSensors++;
                    if (run.Failures[i] is { IsTimeout: true }) timedOutSensors++;
                    if (run.Supplemented[i]) supplementFailedSensors++;
                    else if (run.Retryable[i]) supplementNotReached++;
                    break;
            }
        }

        if (run.Stopped)
        {
            // 第一輪就停損：已停止的是「沒送出」的那些；第一輪全送完才停損：已停止的是沒補抓到的那些
            var stopped = notSent > 0 ? notSent : supplementNotReached;
            _console.WriteLine($"  ⚠ PRTG 持續無法回應，本趟停止數值取數（已停止 {stopped} 顆），其餘留待下一趟。");
            Log.Warn("[PRTG] {Day:yyyy-MM-dd} 數值取數停損：{Reason}；已停止 {Stopped} 顆不再送請求，同一服務實例冷卻 {Minutes} 分鐘",
                run.Day, run.StopReason, stopped, (int)PrtgValueFetchRetryPolicy.Cooldown.TotalMinutes);
        }

        if (failedSensors > 0)
        {
            _console.WriteLine($"  ⚠ 數值擷取失敗 {failedSensors} 顆（其中逾時 {timedOutSensors}、補抓後仍失敗 {supplementFailedSensors}）。"
                + $"其餘感測器不受影響；缺的數值可由下一趟或回填補上。首則錯誤：{run.FirstFailureMessage}");
        }

        // 時間以 datetime_raw（UTC）為準；下面兩種情況都要看得見，不能靜默接受。
        if (run.StringFallback > 0)
        {
            _console.WriteLine($"  ⚠ 有 {run.StringFallback} 筆數值缺 datetime_raw，改以顯示字串依服務主機地區設定推得時段；"
                + "若 PRTG 帳號時區與服務主機不同，這些列會落在錯的小時。");
        }

        if (run.ZoneDrift > 0)
        {
            _console.WriteLine($"  ⚠ 有 {run.ZoneDrift} 筆數值的顯示字串與 datetime_raw 換算結果差 30 分鐘以上——"
                + "PRTG 帳號時區與服務主機不同。數值已依 datetime_raw 落在正確的小時；請把 PRTG 帳號時區設成與服務主機相同，"
                + "否則取數日期邊界會與 PRTG 畫面錯開。");
        }

        if (run.Unparsed > 0)
        {
            _console.WriteLine($"  ⚠ 有 {run.Unparsed} 筆數值的時間欄位無法解析而略過"
                + "（缺 datetime_raw，顯示字串也無法以服務主機地區設定解析）。");
        }

        if (run.OutsideDay > 0)
        {
            _console.WriteLine($"  ⚠ PRTG 回應含 {run.OutsideDay} 筆請求日期以外的數值，已略過而未寫入。");
        }

        return (run.Values, failedSensors);
    }

    private static bool IsTimeoutException(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        if (ex is OperationCanceledException)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is AggregateException aex)
            return aex.InnerExceptions.Any(inner => IsTimeoutException(inner, ct));

        // PrtgClient 把逾時包成 PrtgClientException，真正的型別在 InnerException。
        // 不比對訊息字串：那是在地化的，而且 PRTG 自己的錯誤訊息也可能帶到相同字樣。
        if (ex.InnerException != null && IsTimeoutException(ex.InnerException, ct))
        {
            return true;
        }

        return false;
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
    /// 時間欄位無法解析而被略過的筆數（缺 datetime_raw 且顯示字串也解析不了）——這種缺口必須被看見，
    /// 不能靜默略過（無聲的洞會讓後續基線以為那段本來就沒資料）。
    /// </param>
    /// <param name="stringFallbackCount">缺 datetime_raw、改用顯示字串推得時段的筆數。</param>
    /// <param name="zoneDriftCount">顯示字串與 datetime_raw 換算差 30 分鐘以上（帳號時區與服務主機不同）的筆數。</param>
    private static List<PrtgValueRow> ParseHistoricData(
        string json, long sensorObjid, out int unparsedCount, out int stringFallbackCount, out int zoneDriftCount)
    {
        stringFallbackCount = 0;
        zoneDriftCount = 0;
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

            if (!TryResolvePeriodStart(item, out var periodStart, out var usedString, out var zoneDrift))
            {
                unparsedCount++;
                continue;
            }
            if (usedString) stringFallbackCount++;
            if (zoneDrift) zoneDriftCount++;

            // 判定 Coverage 是否為 0
            double? coverage = null;
            var isCoverageZero = false;
            // coverage_raw 是 PRTG 的原始整數（10000 代表 100%），優先採用；沒有才退回帶單位的 coverage 字串。
            if (TryGetFirstProperty(item, "coverage_raw", out var covRawProp) && TryReadNumber(covRawProp, out var covRawVal))
            {
                coverage = covRawVal / 100.0;
                if (covRawVal == 0) isCoverageZero = true;
            }
            else if (item.TryGetProperty("coverage", out var covProp))
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
            JsonElement valProp2 = default;
            double valRawVal = 0;
            // value_raw 是 PRTG 的原始數字（value 則帶單位、依伺服器地區格式化），優先採用第一組（主要頻道）。
            var hasValueRaw = TryGetFirstProperty(item, "value_raw", out var valRawProp) && TryReadNumber(valRawProp, out valRawVal);
            var hasValueProp = hasValueRaw || TryGetFirstProperty(item, "value_", out valProp2) || TryGetFirstProperty(item, "value", out valProp2);

            if (hasValueRaw)
            {
                parsedValue = valRawVal;
                valueRaw = valRawProp.ValueKind == JsonValueKind.String
                    ? valRawProp.GetString()
                    : valRawProp.GetRawText();
            }
            else if (hasValueProp)
            {
                if (valProp2.ValueKind == JsonValueKind.Number && valProp2.TryGetDouble(out var v))
                {
                    parsedValue = v;
                    valueRaw = valProp2.GetRawText();
                }
                else if (valProp2.ValueKind == JsonValueKind.String)
                {
                    valueRaw = valProp2.GetString();
                    if (!string.IsNullOrWhiteSpace(valueRaw) &&
                        double.TryParse(valueRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
                    {
                        parsedValue = v2;
                    }
                }
            }

            string quality;
            double? avgValue = null;

            if (!hasValueProp || (!hasValueRaw && valProp2.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) ||
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
    /// 從 historicdata 的一小時聚合列取出時段起點（服務主機本地時間）。
    /// <c>datetime_raw</c> 為主：它是 UTC 的**時段結束**時刻——現場 PRTG 24.1.92 同一列 raw 46275.6666666667
    /// ＝9/10 16:00 UTC，顯示字串「下午 11:00:00 - 上午 12:00:00」是 +08 的 23:00～00:00，結束時刻正好相符。
    /// 起點＝raw 換成本地時間再減一個聚合間隔（請求固定 <c>avg=3600</c>）。
    /// 顯示字串只用來交叉檢查（差 30 分鐘以上計 <paramref name="zoneDrift"/>）與 raw 缺席時的退路。
    /// </summary>
    private static bool TryResolvePeriodStart(JsonElement item, out DateTime periodStart, out bool usedString, out bool zoneDrift)
    {
        usedString = false;
        zoneDrift = false;
        var hasDisplay = TryParseDisplayTime(DisplayPeriodHead(item), out var displayStart);
        if (TryReadRawUtcAsLocal(item, "datetime_raw", out var periodEnd))
        {
            periodStart = periodEnd.AddSeconds(-HistoricAverageSeconds);
            zoneDrift = hasDisplay && Math.Abs((displayStart - periodStart).TotalMinutes) > 30;
            return true;
        }
        usedString = hasDisplay;
        periodStart = hasDisplay ? displayStart : default;
        return hasDisplay;
    }

    /// <summary>historicdata 顯示字串「起 - 訖」的前段（＝時段起點）。</summary>
    private static string? DisplayPeriodHead(JsonElement item)
    {
        if (!item.TryGetProperty("datetime", out var dtProp) || dtProp.ValueKind != JsonValueKind.String) return null;
        var dtStr = dtProp.GetString();
        if (string.IsNullOrWhiteSpace(dtStr)) return null;
        var sepIndex = dtStr.IndexOf(" - ", StringComparison.Ordinal);
        return (sepIndex >= 0 ? dtStr[..sepIndex] : dtStr).Trim();
    }

    /// <summary>historicdata 請求的聚合間隔（秒）；與查詢字串的 <c>avg=</c> 同一個值。</summary>
    private const int HistoricAverageSeconds = 3600;

    /// <summary>
    /// 取出同名屬性中的「第一個」。PRTG 的 historicdata 一列會為每個頻道重複 value／value_raw，
    /// 第一組才是主要頻道；JsonElement.TryGetProperty 在重複鍵時回的是最後一個，不能用。
    /// </summary>
    private static bool TryGetFirstProperty(JsonElement item, string name, out JsonElement found)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.Ordinal))
            {
                found = prop.Value;
                return true;
            }
        }
        found = default;
        return false;
    }

    /// <summary>
    /// 讀取 JSON 屬性中的數字：數字型別直接取，字串型別以 InvariantCulture 嘗試解析。
    /// </summary>
    private static bool TryReadNumber(JsonElement prop, out double value)
    {
        value = 0;
        if (prop.ValueKind == JsonValueKind.Number) return prop.TryGetDouble(out value);
        if (prop.ValueKind == JsonValueKind.String)
        {
            var text = prop.GetString();
            return !string.IsNullOrWhiteSpace(text) &&
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    /// <summary>
    /// 一個分頁階段的產出。Converged=false 代表分頁翻到上限仍未到結尾——
    /// 已寫入的筆數仍然有效（寫入是冪等 upsert），呼叫端據此把階段計為失敗但保留數字。
    /// </summary>
    private sealed record StageOutcome(int Written, int Duplicates, bool Converged, string? Error)
    {
        public static StageOutcome From(int written, PagedStageResult paged) =>
            new(written, paged.Result?.DuplicateRows ?? 0, paged.Error == null, paged.Error);
    }

    private sealed record PagedStageResult(PrtgPagerResult? Result, string? Error, PrtgPagingNotConvergedException? Exception = null);

    /// <summary>
    /// 讀 PRTG 的 <c>*_raw</c> 時間（OLE 日期）並轉成服務主機本地時間。現場 PRTG 24.1.92 驗算：
    /// raw 的基準是 **UTC**（<c>lastcheck_raw</c> 與請求當下同為 UTC；顯示字串則依帳號時區與 Date Format）。
    /// 以 raw 為主就不必設定帳號時區、文化或日期格式，也不會有月日對調。
    /// </summary>
    private static bool TryReadRawUtcAsLocal(JsonElement item, string property, out DateTime local)
    {
        local = default;
        if (!TryGetFirstProperty(item, property, out var raw) || !TryReadNumber(raw, out var oaNumber)) return false;
        try
        {
            var utc = DateTime.SpecifyKind(DateTime.FromOADate(oaNumber), DateTimeKind.Utc);
            local = DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>以服務主機文化、再以 InvariantCulture 解析 PRTG 的顯示時間字串（raw 缺席時的退路）。</summary>
    private static bool TryParseDisplayTime(string? text, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(text.Trim(), culture, DateTimeStyles.None, out parsed))
            {
                parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                return true;
            }
        }
        return false;
    }

    /// <summary>messages 一列的發生時間：<c>datetime_raw</c>（UTC）為主，缺席才退顯示字串。</summary>
    private static bool TryParseMessageTime(JsonElement el, out DateTime changedAt, out bool fromString)
    {
        fromString = false;
        if (TryReadRawUtcAsLocal(el, "datetime_raw", out changedAt)) return true;
        fromString = TryParseDisplayTime(GetStringProperty(el, "datetime"), out changedAt);
        return fromString;
    }

    /// <summary>
    /// 執行一次分頁讀取，把「分頁未收斂」轉成可回報的結果而不是例外——
    /// 它與連線失敗不同，前面已經寫進鏡像的資料是有效的，不該讓整個階段的數字歸零。
    /// 其他例外照樣往外擲，由呼叫端的既有 catch 處理。
    /// </summary>
    private static async Task<PagedStageResult> RunPagedStageAsync(Func<Task<PrtgPagerResult>> fetch)
    {
        try
        {
            return new PagedStageResult(await fetch(), null);
        }
        catch (PrtgPagingNotConvergedException ex)
        {
            return new PagedStageResult(null, ex.Message, ex);
        }
    }

    /// <summary>階段完成行的附註：耗時一定寫，重複列數只在非零時寫。</summary>
    private static string FormatStageStats(System.Diagnostics.Stopwatch stopwatch, StageOutcome outcome)
    {
        var stats = $"（耗時 {stopwatch.Elapsed.TotalSeconds:F1} 秒";
        if (outcome.Duplicates > 0) stats += $"、跳過重複列 {outcome.Duplicates} 筆";
        return stats + "）";
    }

    /// <summary>
    /// PRTG table.json 分頁讀取的呼叫入口（階段 1、2、3 共用），實作在 <see cref="PrtgTablePager"/>（全專案唯一一份分頁邏輯）。
    /// 分頁 5000／寫入批次 500。
    /// </summary>
    private Task<PrtgPagerResult> FetchTablePagedAsync<T>(
        string content,
        string columns,
        string? extraQuery,
        Func<JsonElement, T?> mapper,
        Action<IReadOnlyList<T>> onBatch,
        CancellationToken ct,
        string? phase = null,
        Action<string, int, int>? progress = null,
        string? stageLabel = null,
        Func<JsonElement, string?>? rowKey = null,
        Func<IReadOnlyList<JsonElement>, bool>? stopAfterPage = null,
        bool allowPagerRetry = true)
        => PrtgTablePager.FetchAsync(
            _client, _console, content, columns, extraQuery, mapper, onBatch, ct, phase, progress, stageLabel,
            rowKey: rowKey, stopAfterPage: stopAfterPage,
            // 逐台路徑由共用暫停閘負責等待（RunDevicesWithPauseAsync）；這裡再等一次就是兩層退避疊加
            retry: _retryTransientPaging && allowPagerRetry ? new PrtgPagerRetry(RetryDelay) : null);

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


    /// <summary>
    /// messages 端點對單一物件（<c>id={objid}</c>）的查詢字串。PRTG 的 messages 沒有「只取某一天」的參數，只有相對區間
    /// <c>filter_drel</c>（today／yesterday／7days／30days／12months）；不帶它就是把該物件
    /// 的訊息歷史從頭翻到尾，再由用戶端丟掉 99%。這裡取「涵蓋得到目標日的最小級距」，
    /// **用戶端的當日過濾仍然保留**——參數被舊版或代理忽略時結果照樣正確，只是慢。
    /// 刻意不用 today／yesterday：跨午夜的執行窗口在階段 3 跑過零點時，目標日就會落在
    /// 前天，這兩個級距會整段漏掉。目標日超過 12 個月時不帶參數（全量翻），那是超出保留期的
    /// 極端回填，不值得為它多一個級距。
    /// </summary>
    private static string StateChangesQuery(long objid, DateTime targetDate)
    {
        var daysAgo = (DateTime.Today - targetDate.Date).Days;
        // 級距是**滾動時間窗**（7days＝now-7d 起算）而非日曆日：目標日剛好落在邊界那天時，
        // 該日凌晨到執行時刻之間的變更會落在窗外。因此邊界一律往上跳一階，寧可多抓。
        var drel = daysAgo switch
        {
            < 7 => "7days",
            < 30 => "30days",
            < 365 => "12months",
            _ => null
        };

        return drel == null ? $"id={objid}" : $"id={objid}&filter_drel={drel}";
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
