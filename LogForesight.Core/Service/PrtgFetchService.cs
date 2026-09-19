using System.Globalization;
using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>PRTG 每日擷取結果摘要。</summary>
public sealed record PrtgFetchResult(int Devices, int Sensors, int StateChanges, int Values, int Failures);

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
    int EmptyObjects);  // 本趟區間內一筆都沒取到的物件數

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

    private readonly PrtgClient _client;
    private readonly EfPrtgStore _store;
    private readonly PrtgFreshnessStore _freshness;
    private readonly IRunConsole _console;
    private readonly PrtgResourceGuard? _guard;
    private readonly IReadOnlyDictionary<string, string> _categoryOverrides;

    /// <param name="categoryOverrides">
    /// sensor type 語意分類補充對照表，由建構端以
    /// <see cref="PrtgSensorTypeCategoryMap.ParseOverrides"/> 自目前設定解析出的 Map 傳入
    /// （錯誤行已被略過；設定層存檔時已擋，這裡只防禦舊資料）。
    /// </param>
    public PrtgFetchService(PrtgClient client, EfPrtgStore store, PrtgFreshnessStore freshness, IRunConsole console,
        IReadOnlyDictionary<string, string> categoryOverrides, PrtgResourceGuard? guard = null)
    {
        _client = client;
        _store = store;
        _freshness = freshness;
        _console = console;
        _categoryOverrides = categoryOverrides;
        _guard = guard;
    }

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
        var sensorTargets = new List<(long Objid, bool Paused)>();
        var devicesRefreshed = false;
        // 取數範圍：階段 2 只同步範圍內裝置的感測器、階段 3 只查範圍內裝置的狀態變更；null＝計算失敗（兩階段都略過、不清除）
        PrtgScopeResult? scope = null;

        void ResolveScope()
        {
            try
            {
                scope = scopeProvider(devicesRefreshed);
                _console.WriteLine($"[範圍] 取數範圍：{scope.DeviceObjids.Count} 台裝置（對應 {scope.Mapped}、衝突 {scope.Conflict}、人工 {scope.Manual}、守門 {scope.Guard}）");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                _console.WriteLine($"[範圍] ✗ 取數範圍計算失敗：{ex.Message}");
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
                failures++;
                _console.WriteLine("  ✗ 鏡像尚無任何感測器結構，無法回填。請先執行一次每日擷取（或等夜間排程跑過）再回填。");
                return new PrtgFetchResult(0, 0, 0, 0, failures);
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
                    failures++;
                    _console.WriteLine($"[階段 1/4] ✗ {outcome.Error}已寫入 {devicesCount} 台裝置，鏡像不完整。");
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
                failures++;
                _console.WriteLine($"[階段 1/4] 裝置結構同步失敗：{ex.Message}");
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
                    failures++;
                    _console.WriteLine($"[階段 1/4] 過期裝置清除失敗：{ex.Message}");
                }
            }

            // 主機對應要用剛更新的裝置鏡像，且範圍要在感測器同步之前就定下來
            ResolveScope();

            // 階段 2：只同步取數範圍內裝置的感測器
            // 本趟寫入的列 SyncedAt 都等於這個時間（逐台與全站模式共用）；早於它的列就是本趟沒被刷新到的感測器
            var sensorsSyncStartedAt = DateTime.Now;
            // 本趟感測器鏡像是否「完整刷新」——成立時「沒刷新到」才代表範圍外或 PRTG 端已刪除
            var sensorsRefreshed = false;
            // 本趟「查詢成功但回 0 顆」的裝置：清除時給一趟寬限（見 DeleteSensorsNotSyncedSince）
            IReadOnlyCollection<long> emptyDevices = Array.Empty<long>();
            var scopeDevices = scope?.DeviceObjids;
            if (scopeDevices == null || scopeDevices.Count == 0)
            {
                // 範圍計算失敗已在 ResolveScope 計過 failures，這裡不重複計；範圍為空則是設定面的結果，不算失敗
                _console.WriteLine(scopeDevices == null
                    ? "[階段 2/4] 取數範圍無法取得，略過感測器同步。"
                    : "[階段 2/4] 取數範圍內沒有任何裝置，略過感測器同步。");
            }
            else
            {
                try
                {
                    _console.WriteLine("[階段 2/4] 開始同步 PRTG 感測器結構鏡像...");
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    if (scopeDevices.Count <= PerDeviceSensorFetchLimit)
                    {
                        var (written, targets, failedDevices, empties) = await FetchSensorsForDevicesAsync(
                            scopeDevices.OrderBy(id => id).ToList(), concurrency, sensorsSyncStartedAt, ct, progress);
                        stopwatch.Stop();
                        sensorsCount = written;
                        sensorTargets = targets;
                        _console.WriteLine($"[階段 2/4] 感測器結構同步完成：範圍內 {scopeDevices.Count} 台裝置、寫入/更新 {sensorsCount} 個感測器{FormatStageStats(stopwatch, new StageOutcome(written, 0, true, null))}。");
                        if (failedDevices.Count > 0)
                        {
                            failures++;
                            var shown = string.Join("、", failedDevices.OrderBy(id => id).Take(5));
                            var more = failedDevices.Count > 5 ? " 等" : "";
                            _console.WriteLine($"[階段 2/4] ✗ {failedDevices.Count} 台裝置的感測器取得失敗：{shown}{more}");
                        }
                        else
                        {
                            _freshness.Record(PrtgFreshnessStore.Sensors, sensorsCount);
                        }
                        emptyDevices = empties;
                        sensorsRefreshed = failedDevices.Count == 0 && sensorsCount > 0;
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
                            failures++;
                            _console.WriteLine($"[階段 2/4] ✗ {outcome.Error}已寫入 {sensorsCount} 個感測器，鏡像不完整。");
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
                    failures++;
                    _console.WriteLine($"[階段 2/4] 感測器結構同步失敗：{ex.Message}");
                }
            }

            // 清除本趟沒被刷新的感測器（範圍外的、PRTG 端已刪的）：只有範圍非空、階段 2 無例外、
            // 全站模式已收斂／逐台模式零失敗、且有寫入時，「沒刷新到」才代表「不該留在鏡像」。
            // 必須在語意分類重算之前，免得替即將刪除的列白做工。
            // 另一道保險：範圍內沒有任何對應成功的裝置時不清。感測器清除沒有裝置那種「過半不刪」（首次套用取數範圍本來就會刪九成以上），
            // 而「主機主檔暫時讀到空清單 → 對應全數落空 → 範圍只剩守門裝置」會把整份鏡像清光；沒有對應成功的主機時鏡像留著也無害。
            if (sensorsRefreshed && scope!.Mapped == 0)
            {
                sensorsRefreshed = false;
                _console.WriteLine("[階段 2/4] 取數範圍內沒有任何對應成功的裝置，本趟不清除感測器鏡像。");
            }
            if (sensorsRefreshed)
            {
                try
                {
                    var (deleted, graceKept) = _store.DeleteSensorsNotSyncedSince(
                        sensorsSyncStartedAt, emptyDevices, sensorsSyncStartedAt - EmptyDeviceGrace);
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
                    failures++;
                    _console.WriteLine($"[階段 2/4] 過期感測器清除失敗：{ex.Message}");
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
                failures++;
                _console.WriteLine($"[階段 2/4] 語意分類自動填入失敗：{ex.Message}");
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
            if (!rangeResult.Converged)
            {
                failures++;
                _console.WriteLine($"[階段 3/4] ✗ {rangeResult.Error}已寫入 {stateChangesCount} 筆狀態變更，資料不完整。");
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
            failures++;
            _console.WriteLine($"[階段 3/4] 狀態變更同步失敗：{ex.Message}");
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
                    failures++;
                    _console.WriteLine("[階段 4/4] 所有感測器的數值擷取皆失敗，本階段視為失敗。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                _console.WriteLine($"[階段 4/4] 每小時數值擷取失敗：{ex.Message}");
            }
        }
        else
        {
            _console.WriteLine("[階段 4/4] 數值擷取改由觸發式流程執行，本階段略過。");
        }

        return new PrtgFetchResult(devicesCount, sensorsCount, stateChangesCount, valuesCount, failures);
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
        IReadOnlyList<long> deviceObjids, int concurrency, CancellationToken ct)
    {
        var (written, _, failedDevices, emptyDevices) = await FetchSensorsForDevicesAsync(
            deviceObjids, concurrency, DateTime.Now, ct, progress: null);
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
                    Name = Truncate(GetStringProperty(el, "device"), 255) ?? string.Empty,
                    GroupPath = Truncate(GetStringProperty(el, "group"), 512) ?? string.Empty,
                    Ip = Truncate(GetStringProperty(el, "host"), 64),
                    Tags = GetStringProperty(el, "tags"),
                    Status = Truncate(GetStringProperty(el, "status"), 64),
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
    private async Task<(int Written, List<(long Objid, bool Paused)> Targets, List<long> FailedDevices, List<long> EmptyDevices)> FetchSensorsForDevicesAsync(
        IReadOnlyList<long> deviceObjids, int concurrency, DateTime syncedAt, CancellationToken ct,
        Action<string, int, int>? progress)
    {
        var totalDevices = deviceObjids.Count;
        progress?.Invoke(PrtgSyncSensorsPhase, 0, totalDevices);

        var filter = deviceObjids.ToHashSet();
        var maxConcurrency = Math.Max(concurrency, 1);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        // targets／failed／寫入在併發下共用同一把鎖（寫入本身也序列化，避免同一個 store 被並行 upsert）
        var sync = new object();
        var totalWritten = 0;
        var targets = new List<(long Objid, bool Paused)>();
        var failed = new List<long>();
        var empty = new List<long>();
        var completed = 0;

        var tasks = deviceObjids.Select(async deviceObjid =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                // 本台取回（通過範圍過濾）的列數：查詢成功且為 0 才算「PRTG 端確實沒有感測器」
                var deviceRows = 0;
                // 逐台不帶 phase／stageLabel：逐台印分頁進度會洗版，進度改由本方法以「台」回報
                var paged = await FetchSensorPagesAsync($"id={deviceObjid}", filter, syncedAt,
                    batch =>
                    {
                        lock (sync)
                        {
                            deviceRows += batch.Count;
                            totalWritten += _store.UpsertSensors(batch, syncedAt);
                            targets.AddRange(batch.Select(r => (r.Objid, r.Paused)));
                        }
                    },
                    ct, stageLabel: null);
                if (paged.Error != null)
                {
                    lock (sync) failed.Add(deviceObjid);
                }
                else if (deviceRows == 0)
                {
                    lock (sync) empty.Add(deviceObjid);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                lock (sync) failed.Add(deviceObjid);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Invoke(PrtgSyncSensorsPhase, done, totalDevices);
            }
        });

        await Task.WhenAll(tasks);
        return (totalWritten, targets, failed, empty);
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
        Action<IReadOnlyList<PrtgSensorRow>> onBatch, CancellationToken ct, string? stageLabel)
        => RunPagedStageAsync(() => FetchTablePagedAsync<PrtgSensorRow>(
            content: "sensors",
            columns: "objid,parentid,sensor,type,tags,unit,status,paused,dependency",
            extraQuery: extraQuery,
            mapper: el => MapSensorRow(el, scopeDevices, syncedAt),
            onBatch: onBatch,
            ct: ct,
            phase: null,
            progress: null,
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
            Name = Truncate(GetStringProperty(el, "sensor"), 255) ?? string.Empty,
            SensorType = Truncate(GetStringProperty(el, "type"), 128) ?? string.Empty,
            Tags = GetStringProperty(el, "tags"),
            Unit = Truncate(GetStringProperty(el, "unit"), 64),
            Status = Truncate(GetStringProperty(el, "status"), 64),
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
                ? "[階段 3/4] 取數範圍無法取得，略過狀態變更同步。"
                : "[階段 3/4] 取數範圍內沒有任何裝置，略過狀態變更同步。");
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
        var failed = new List<long>();
        var completed = 0;

        _console.WriteLine($"[階段 3/4] 開始同步 PRTG 狀態變更（{from:yyyy-MM-dd} ～ {to:yyyy-MM-dd}，逐裝置查詢 {totalObjects} 台，併發 {maxConcurrency}）...");
        progress?.Invoke(PrtgSyncMessagesPhase, 0, totalObjects);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = objects.Select(async objid =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_guard != null) await _guard.WaitIfBusyAsync(ct);

                // 本物件區間內取到的列數：用來判斷「區間內無訊息」
                var inRangeRows = 0;
                var paged = await RunPagedStageAsync(() => FetchTablePagedAsync<PrtgStateChangeRow>(
                    content: "messages",
                    columns: "objid,datetime,status,message",
                    extraQuery: StateChangesQuery(objid, from),
                    mapper: el =>
                    {
                        var dtStr = GetStringProperty(el, "datetime");
                        if (string.IsNullOrWhiteSpace(dtStr) || !DateTime.TryParse(dtStr, out var changedAt))
                        {
                            Interlocked.Increment(ref unparseableCount);
                            return null;
                        }

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
                            Status = Truncate(GetStringProperty(el, "status"), 64) ?? string.Empty,
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
                            var dtStr = GetStringProperty(el, "datetime");
                            if (string.IsNullOrWhiteSpace(dtStr) || !DateTime.TryParse(dtStr, out var dt))
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
                    }));

                lock (sync)
                {
                    pages += paged.Result?.Pages ?? paged.Exception?.Pages ?? 0;
                    readRows += paged.Result?.ReadRows ?? paged.Exception?.ReadRows ?? 0;
                    if (paged.Result?.StoppedEarly == true) stoppedEarly = true;
                    if (paged.Error != null) failed.Add(objid);
                    else if (inRangeRows == 0) emptyObjects++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                lock (sync) failed.Add(objid);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Invoke(PrtgSyncMessagesPhase, done, totalObjects);
            }
        });

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        if (unparseableCount > 0)
        {
            _console.WriteLine($"  ⚠ 狀態變更中有 {unparseableCount} 筆紀錄無法解析時間，已略過。");
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
            EmptyObjects: emptyObjects);
    }

    /// <summary>
    /// 要對哪些 PRTG 物件查 messages——這是**唯一切換點**。目前回傳範圍內裝置的 objid（由小到大），
    /// 預期 PRTG 的 <c>id=&lt;裝置&gt;</c> 會一併回傳底下感測器的訊息。
    /// 若探測 9d-5 證實 <c>id=&lt;裝置&gt;</c> 不含下層感測器訊息，改成回傳範圍內未暫停感測器的 objid 即可，其餘流程不必動。
    /// </summary>
    private static IReadOnlyList<long> StateChangeQueryObjects(IReadOnlyCollection<long> scopeDeviceObjids)
        => scopeDeviceObjids.OrderBy(id => id).ToList();

    /// <summary>階段 4：對未暫停的 sensor 依併發上限擷取 hourly 聚合數值並逐 sensor 寫入鏡像表</summary>
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

        var sdate = day.Date.ToString("yyyy-MM-dd-00-00-00");
        var edate = day.Date.AddDays(1).ToString("yyyy-MM-dd-00-00-00");
        var maxConcurrency = Math.Max(concurrency, 1);
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var totalValues = 0;
        var totalUnparsed = 0;
        var totalOaFallback = 0;
        var failedSensors = 0;
        var timedOutSensors = 0;
        var completedSensors = 0;
        string? firstFailureMessage = null;

        var tasks = activeSensors.Select(async target =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_guard != null) await _guard.WaitIfBusyAsync(ct);
                var query = $"api/historicdata.json?id={target.Objid}&avg=3600&sdate={sdate}&edate={edate}";
                var json = await _client.GetJsonAsync(query, ct);
                var rows = ParseHistoricData(json, target.Objid, out var unparsed, out var oaFallback);
                if (unparsed > 0) Interlocked.Add(ref totalUnparsed, unparsed);
                if (oaFallback > 0) Interlocked.Add(ref totalOaFallback, oaFallback);
                if (rows.Count > 0)
                {
                    // 逐 sensor 寫入資料庫後立即釋放記憶體，絕不累積成大清單一次寫入
                    var written = _store.UpsertValues(rows);
                    Interlocked.Add(ref totalValues, written);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 單一 sensor 的失敗（逾時、404、PRTG 暫時 5xx）只影響它自己。
                // 讓例外往外逃會被階段層的 catch 接住，於是「三千個 sensor 已寫進去、
                // 第七個逾時」會被回報成「數值 0 筆、階段失敗」——已落地的資料反而看不見。
                Interlocked.Increment(ref failedSensors);
                if (IsTimeoutException(ex, ct))
                {
                    Interlocked.Increment(ref timedOutSensors);
                }
                Interlocked.CompareExchange(ref firstFailureMessage, ex.Message, null);
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completedSensors);
                progress?.Invoke(stage, done, totalSensors);
            }
        });

        await Task.WhenAll(tasks);

        if (failedSensors > 0)
        {
            var timedOutPart = timedOutSensors > 0 ? $"（其中 {timedOutSensors} 個是請求逾時）" : "";
            _console.WriteLine($"  ⚠ 有 {failedSensors} 個感測器的數值擷取失敗{timedOutPart}（其餘感測器不受影響，明日排程會自動再試）。"
                + $"首則錯誤：{firstFailureMessage}");
        }

        // 時間改用 OLE 日期退路的筆數：實機證實 datetime_raw 與顯示字串可能差好幾個小時，
        // 走退路的資料落在哪個小時並不可靠，必須看得見而不是靜默接受。
        if (totalOaFallback > 0)
        {
            _console.WriteLine($"  ⚠ 有 {totalOaFallback} 筆數值的時間是以 PRTG 的原始日期數值推得"
                + "（顯示字串無法解析）。該數值的時間可能與 PRTG 畫面上的時間不同，"
                + "請比對 PRTG 的地區與時間顯示設定。");
        }

        if (totalUnparsed > 0)
        {
            _console.WriteLine($"  ⚠ 有 {totalUnparsed} 筆數值的時間欄位無法解析而略過"
                + "（多半是 PRTG 伺服器的地區日期格式與本機不符，請比對 PRTG 的時間顯示設定）。");
        }

        return (totalValues, failedSensors);
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
    /// 時間欄位無法解析而被略過的筆數。PRTG 依伺服器地區設定輸出時間字串，格式與本機不符時
    /// 整段資料會解析失敗——這種缺口必須被看見，不能靜默略過（無聲的洞會讓後續基線以為那段本來就沒資料）。
    /// </param>
    private static List<PrtgValueRow> ParseHistoricData(
        string json, long sensorObjid, out int unparsedCount, out int oaFallbackCount)
    {
        oaFallbackCount = 0;
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

            if (!TryResolvePeriodStart(item, out var periodStart, out var usedOaDate))
            {
                unparsedCount++;
                continue;
            }
            if (usedOaDate) oaFallbackCount++;

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
    /// 從 historicdata 的一列取出時段起點。
    /// 以 datetime 顯示字串為準（只取「起 - 訖」區間的前段），datetime_raw（OLE Automation 日期數字）
    /// 只用來驗證字串解析沒有讀錯月日，以及在字串解析不了時當退路。兩者皆不可用時回 false，由呼叫端計入略過筆數。
    /// </summary>
    private static bool TryResolvePeriodStart(JsonElement item, out DateTime periodStart, out bool usedOaDate)
    {
        periodStart = default;
        usedOaDate = false;

        // datetime_raw 與顯示字串不是同一個時間基準：實機（PRTG 24.1.92）同一列的 raw 46275.6666666667＝16:00，
        // 字串是「下午 11:00:00 - 上午 12:00:00」＝23:00，差 7 小時。字串是管理者在 PRTG 畫面上看到的那個時間，
        // 鏡像要跟它對齊，否則整份基線會整體平移數小時而沒有任何徵兆。
        // raw 仍然有用：兩者同一天，所以拿它驗證字串解析有沒有把月與日對調——
        // 「10/09/2026」在 d/M 的 PRTG 配上 M/d 的站台文化會被讀成 10 月 9 日，而且解析成功、什麼都不會報。
        DateTime? oaDate = null;
        if (TryGetFirstProperty(item, "datetime_raw", out var rawProp) && TryReadNumber(rawProp, out var oaNumber))
        {
            try
            {
                oaDate = DateTime.FromOADate(oaNumber);
            }
            catch (ArgumentException)
            {
                // OLE 日期超出合法範圍：視為不可用。
            }
        }

        if (item.TryGetProperty("datetime", out var dtProp) && dtProp.ValueKind == JsonValueKind.String)
        {
            var dtStr = dtProp.GetString();
            if (!string.IsNullOrWhiteSpace(dtStr))
            {
                // 「起 - 訖」區間字串只取前段（＝小時起點）
                var sepIndex = dtStr.IndexOf(" - ", StringComparison.Ordinal);
                var head = (sepIndex >= 0 ? dtStr[..sepIndex] : dtStr).Trim();
                if (head.Length > 0)
                {
                    foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
                    {
                        if (!DateTime.TryParse(head, culture, DateTimeStyles.None, out var parsed)) continue;
                        // 有 raw 可對照時，字串結果必須落在 raw 的一天之內；差超過一天就是月日讀反了，換下一個文化再試。
                        if (oaDate.HasValue && Math.Abs((parsed - oaDate.Value).TotalHours) > MaxDisplayVsRawDriftHours) continue;
                        periodStart = parsed;
                        return true;
                    }
                }
            }
        }

        // 退路：字串解析不了、或每種文化解出來的日期都對不上 raw 時才用 OLE 日期。
        // 呼叫端會把用到退路的筆數回報出來——時間可能與 PRTG 畫面差幾小時，要看得見。
        if (oaDate.HasValue)
        {
            periodStart = oaDate.Value;
            usedOaDate = true;
            return true;
        }

        periodStart = default;
        return false;
    }

    /// <summary>
    /// 顯示字串與 datetime_raw 允許的最大差距（小時）。實機兩者差 7 小時（時區基準不同），
    /// 月日對調的錯誤至少差一天；取 24 小時能放過前者、擋下後者。
    /// </summary>
    private const double MaxDisplayVsRawDriftHours = 24;

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
        Func<IReadOnlyList<JsonElement>, bool>? stopAfterPage = null)
        => PrtgTablePager.FetchAsync(
            _client, _console, content, columns, extraQuery, mapper, onBatch, ct, phase, progress, stageLabel,
            rowKey: rowKey, stopAfterPage: stopAfterPage);

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
    /// 依欄位長度上限截斷。PRTG 的字串欄位沒有長度保證，SQL Server 端超長會擲截斷例外，
    /// 讓整批（一次 500 筆）寫入一起失敗；SQLite 端不報錯，兩個後端行為還會分岔。
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value != null && value.Length > maxLength ? value[..maxLength] : value;

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
