using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;

namespace LogForesight.Core.Service;

/// <summary>
/// PRTG 歷史資料回填服務。
/// </summary>
public static class PrtgBackfillRunner
{
    // 斷點續傳靠冪等：
    // FetchDayAsync 的寫入全部冪等（數值有自然鍵 (sensor_objid, period_start) 覆蓋更新、
    // 狀態變更以 (sensor_objid, changed_at) 去重），因此中斷後重跑同一區間不會產生重複資料，
    // 不需要額外的水位紀錄。
    //
    // 回填不做主機對應：
    // 主機對應為每日作業，歷史對應無法事後重建（歷史 IP 變動、主機異動無法考證），硬造反而是假資料。

    /// <summary>
    /// 只回填明確日期區間及指定主機的 hourly 數值。每一天只採用該日有效的 Ok 主機對應，
    /// 並只從這些裝置的鏡像 sensor 取數；沒有當日對應或符合白名單的 sensor 時回報略過。
    /// </summary>
    /// <returns>至少寫入一筆且全程沒有 sensor 失敗時回傳 true；無目標、部分失敗或全失敗回傳 false。</returns>
    public static async Task<bool> RunValuesForHostsAsync(
        PrtgFetchService fetchService,
        DateTime fromDate,
        DateTime toDate,
        IReadOnlyCollection<long> hostIds,
        int concurrency,
        IReadOnlyCollection<string>? whitelist,
        EfPrtgStore store,
        IRunConsole console,
        CancellationToken ct,
        Action<int, int, DateTime?>? dayProgress = null,
        Action<int, int>? sensorProgress = null)
    {
        ArgumentNullException.ThrowIfNull(fetchService);
        ArgumentNullException.ThrowIfNull(hostIds);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(console);

        var from = fromDate.Date;
        var to = toDate.Date;
        var selectedHosts = hostIds.Where(id => id > 0).ToHashSet();
        if (from > to || selectedHosts.Count == 0 || concurrency <= 0)
        {
            console.WriteLine("局部回填範圍無效：請指定有效日期區間、至少一台有效主機及正併發數。");
            return false;
        }

        var totalDays = (to - from).Days + 1;
        var daysWithTargets = 0;
        var skippedDays = 0;
        var failedSensors = 0;
        var written = 0;
        console.WriteLine($"開始局部 PRTG 數值回填（{from:yyyy-MM-dd}～{to:yyyy-MM-dd}，指定主機 {selectedHosts.Count} 台）...");

        for (var offset = 0; offset < totalDays; offset++)
        {
            ct.ThrowIfCancellationRequested();
            var day = from.AddDays(offset);
            dayProgress?.Invoke(offset, totalDays, day);

            var mapRows = store.GetHostMapForDate(day)
                .Where(row => row.MapStatus == PrtgMapStatus.Ok && row.HostId.HasValue && selectedHosts.Contains(row.HostId.Value))
                .ToList();
            if (mapRows.Count == 0)
            {
                skippedDays++;
                sensorProgress?.Invoke(0, 0);
                console.WriteLine($"局部回填 {day:yyyy-MM-dd} 略過：該日沒有指定主機的有效對應。");
                continue;
            }

            var deviceIds = mapRows.Select(row => row.DeviceObjid).Distinct().ToArray();
            var targets = store.GetValueFetchTargets(whitelist, deviceIds);
            if (targets.Count == 0)
            {
                skippedDays++;
                sensorProgress?.Invoke(0, 0);
                console.WriteLine($"局部回填 {day:yyyy-MM-dd} 略過：指定主機的當日對應裝置沒有符合白名單的鏡像 sensor。");
                continue;
            }

            daysWithTargets++;
            try
            {
                var (dayWritten, dayFailed) = await fetchService.FetchValuesForSensorsAsync(
                    day, targets, concurrency, ct,
                    progress: (stage, done, total) => sensorProgress?.Invoke(done, total));
                written += dayWritten;
                failedSensors += dayFailed;
                console.WriteLine($"局部回填 {day:yyyy-MM-dd}：sensor {targets.Count} 個、數值 {dayWritten} 筆、失敗 {dayFailed} 個。");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedSensors += targets.Count;
                console.WriteLine($"局部回填 {day:yyyy-MM-dd} 失敗：{ex.Message}");
            }
        }

        dayProgress?.Invoke(totalDays, totalDays, null);
        if (written > 0)
            fetchService.RecordFreshness(PrtgFreshnessStore.Values, written);

        console.WriteLine($"局部數值回填完成：有目標 {daysWithTargets} 天、略過 {skippedDays} 天、寫入 {written} 筆、失敗 {failedSensors} 個 sensor。");
        return daysWithTargets > 0 && written > 0 && failedSensors == 0;
    }

    /// <summary>
    /// 由近往遠逐日回填 PRTG 過去 N 天的數值與狀態變更。
    /// </summary>
    /// <param name="fetchService">單日擷取服務</param>
    /// <param name="days">回填天數（由昨天往前計）</param>
    /// <param name="concurrency">hourly 數值抓取併發上限（取自 PrtgFetchConcurrency，與每日擷取共用）</param>
    /// <param name="console">執行歷程輸出</param>
    /// <param name="ct">取消語彙基元</param>
    /// <param name="scopeDeviceObjids">取數範圍裝置集合（呼叫端以 PrtgScopeDevices.Compute 算好；本方法只轉交給擷取服務、不自行過濾）</param>
    /// <param name="store">PRTG 鏡像 store（傳入時啟用觸發式過濾）</param>
    /// <param name="records">分析紀錄查詢介面（傳入時啟用觸發式過濾）</param>
    /// <param name="whitelist">sensor type 白名單（null 或空表示不限制）</param>
    /// <param name="dayProgress">天數層進度回呼（已完成天數, 總天數, 當前日期），null＝不回報</param>
    /// <param name="sensorProgress">當日 sensor 進度回呼（已完成數, 總數），null＝不回報</param>
    /// <param name="stateChangeProgress">觸發式回填翻狀態變更區間的進度回呼（已完成台數, 總台數），null＝不回報</param>
    /// <returns>
    /// 全量分支：有任何一天成功回傳 true。
    /// 觸發式分支：沒有失敗日、狀態變更完整取得、且至少一天成功才回傳 true。
    /// </returns>
    public static async Task<bool> RunAsync(
        PrtgFetchService fetchService, int days, int concurrency, IRunConsole console, CancellationToken ct,
        IReadOnlyCollection<long> scopeDeviceObjids,
        EfPrtgStore? store = null, IAnalysisRecordQuery? records = null, IReadOnlyCollection<string>? whitelist = null,
        Action<int, int, DateTime?>? dayProgress = null,
        Action<int, int>? sensorProgress = null,
        string? scope = null,
        IReadOnlyCollection<long>? extraScopeHosts = null,
        Action<int, int>? stateChangeProgress = null)
    {
        // 白名單為空時 all-mapped 會退回 triggered（第二道防線，見 PrtgValueFetchScope）
        var whitelistEmpty = whitelist == null || whitelist.Count == 0;
        var effectiveScope = PrtgValueFetchScope.EffectiveScope(scope, whitelistEmpty);

        if (PrtgValueFetchScope.ShouldWarnUnsafeAllMapped(scope, whitelistEmpty))
        {
            console.WriteLine("⚠ 取數範圍設為「全部已對應主機」但 sensor type 白名單為空，" +
                              "等於對全部 sensor 取數——本次退回「只抓觸發主機」。請先設定白名單。");
        }
        if (days <= 0)
        {
            console.WriteLine("回填天數必須大於 0。");
            return false;
        }

        var triggered = store != null && records != null;
        var successDays = 0;
        var failedDays = 0;
        var skippedDays = 0;
        var processedDays = 0;
        var stateChangesFailed = false;
        var totalValuesWritten = 0;
        string? stateChangesError = null;

        console.WriteLine($"開始執行 PRTG 歷史回填（共 {days} 天，由近往遠逐日擷取）...");

        try
        {
            if (triggered)
            {
                // 狀態變更整趟只翻一次：逐日各翻一次相對窗，實機一窗約 100 萬筆、N 天就翻 N 次。
                // 區間兩端各放寬一天（最舊回填日的前一天到今天）：規則評估會看前後一天的變更，
                // 只取回填日本身會讓邊界日的評估缺資料。
                try
                {
                    var range = await fetchService.FetchStateChangesRangeAsync(
                        DateTime.Today.AddDays(-days - 1), DateTime.Today, scopeDeviceObjids, concurrency, ct,
                        (stage, done, total) => stateChangeProgress?.Invoke(done, total));
                    console.WriteLine($"狀態變更：查詢 {range.QueriedObjects} 台、讀取 {range.ReadRows} 筆、新增 {range.TotalWritten} 筆（其餘已存在）");
                    if (!range.Converged)
                    {
                        stateChangesFailed = true;
                        stateChangesError = string.IsNullOrWhiteSpace(range.Error) ? "分頁未收斂" : range.Error;
                        console.WriteLine($"⚠ 狀態變更未收斂：{stateChangesError}（逐日數值照常回填）");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 數值與狀態變更互相獨立，前者不因後者失敗而放棄
                    stateChangesFailed = true;
                    stateChangesError = ex.Message;
                    console.WriteLine($"⚠ 狀態變更取得失敗：{ex.Message}（逐日數值照常回填）");
                }
            }

            for (var i = 1; i <= days; i++)
            {
                ct.ThrowIfCancellationRequested();

                var day = DateTime.Today.AddDays(-i);
                // 已完成 i-1 天、正在處理第 i 天：回報 i 會讓剛開始第 1 天就顯示 1/N，
                // 最後一天處理中顯示 N/N（看起來已經跑完但其實還在跑）
                dayProgress?.Invoke(i - 1, days, day);
                sensorProgress?.Invoke(0, 0);

                var dayInterrupted = false;
                try
                {
                    if (!triggered)
                    {
                        // syncStructure: false —— 結構鏡像永遠是現況，逐日回填不必也不該重跑它
                        // （會對 PRTG 做 N 次全量查詢，並把「最後結構同步時間」改寫成回填當下）
                        // 範圍由呼叫端在回填開始前算好（回填不做主機對應）
                        var result = await fetchService.FetchDayAsync(
                            day, concurrency, ct,
                            _ => new PrtgScopeResult(scopeDeviceObjids.ToHashSet(), 0, 0, 0, 0),
                            syncStructure: false,
                            progress: (stage, done, total) => sensorProgress?.Invoke(done, total));
                        if (result.Failures > 0 && result.Values == 0 && result.StateChanges == 0)
                        {
                            failedDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：所有擷取階段皆未成功。");
                        }
                        else
                        {
                            successDays++;
                            totalValuesWritten += result.Values;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）：數值 {result.Values} 筆、狀態變更 {result.StateChanges} 筆");
                        }
                    }
                    else
                    {
                        // 觸發式回填：只回填問題主機命中白名單的 sensor（狀態變更已在迴圈前一次取完）

                        // 該日無對應時退回最近一日的對應：以回填當日為基準往回查，
                        // 單一聚合查詢取代逐日往回的多次獨立查詢（回填 N 天會放大 N 倍）
                        var hostMapRows = store!.GetLatestHostMapWithDate(PrtgTriggeredValueFetcher.HostMapLookbackDays, day).Rows;
                        var mappedHostIds = hostMapRows
                            .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue)
                            .Select(m => m.HostId!.Value)
                            .Distinct()
                            .ToList();

                        if (mappedHostIds.Count == 0)
                        {
                            // 沒有對應就沒有目標 sensor：不是成功（一筆都沒取）也不是失敗（沒有東西壞）
                            skippedDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）略過：該日之前沒有主機對應");
                            continue;
                        }

                        var filter = new RecordQueryFilter
                        {
                            From = day,
                            To = day,
                            RiskLevels = new[] { "高", "中" },
                            Hosts = null
                        };
                        var riskyRecords = records!.QueryLightweight(filter);
                        var triggeredHostIds = riskyRecords
                            .Select(r => r.HostId)
                            .Distinct()
                            .ToList();

                        // 取數範圍與每日擷取共用同一個判定（docs/PRTG-SPEC.md §3a），不各寫一份
                        var selectedHostIds = PrtgValueFetchScope
                            .SelectHosts(effectiveScope, triggeredHostIds, mappedHostIds, extraScopeHosts ?? Array.Empty<long>())
                            .ToHashSet();

                        var problemHostsCount = selectedHostIds.Count;
                        var valuesWritten = 0;
                        var failedSensors = 0;
                        var targetSensorsCount = 0;

                        if (problemHostsCount > 0)
                        {
                            var deviceObjids = hostMapRows
                                .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && selectedHostIds.Contains(m.HostId.Value))
                                .Select(m => m.DeviceObjid)
                                .Distinct()
                                .ToList();

                            if (deviceObjids.Count > 0)
                            {
                                var targets = store.GetValueFetchTargets(whitelist, deviceObjids);
                                targetSensorsCount = targets.Count;
                                if (targets.Count > 0)
                                {
                                    var (written, failed) = await fetchService.FetchValuesForSensorsAsync(
                                        day, targets, concurrency, ct,
                                        progress: (stage, done, total) => sensorProgress?.Invoke(done, total));
                                    valuesWritten = written;
                                    failedSensors = failed;
                                }
                            }
                        }

                        // 失敗＝有目標 sensor 且全部失敗；狀態變更不再逐日取，不參與判定
                        if (failedSensors > 0 && valuesWritten == 0)
                        {
                            failedDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：{failedSensors} 個 sensor 的數值皆未取得。");
                        }
                        else
                        {
                            successDays++;
                            totalValuesWritten += valuesWritten;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）：問題主機 {problemHostsCount} 台、sensor {targetSensorsCount} 個、數值 {valuesWritten} 筆");
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    dayInterrupted = true;
                    throw;
                }
                catch (Exception ex)
                {
                    // 停止當下連線被中止會以 IOException 之類收場而不是 OCE：那一天同樣是被打斷、不算處理完
                    if (ct.IsCancellationRequested) dayInterrupted = true;
                    failedDays++;
                    console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：{ex.Message}");
                }
                finally
                {
                    // 這一天沒有被取消打斷（正常結束或非取消的失敗）才算處理完；
                    // 只看「現在有沒有取消」會把「做完第 i 天之後才按停止」少算一天
                    if (!dayInterrupted) processedDays = i;
                }
            }

            dayProgress?.Invoke(days, days, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            console.WriteLine($"回填已中斷（已停止：完成 {processedDays}／{days} 天）。累計完成 {successDays} 天，失敗 {failedDays} 天。");
            throw;
        }

        // 回填結束（未被停止）且至少一天成功，才算一次成功完成的數值擷取
        if (successDays > 0)
        {
            fetchService.RecordFreshness(PrtgFreshnessStore.Values, totalValuesWritten);
        }

        if (!triggered)
        {
            console.WriteLine($"回填作業完成。共成功 {successDays} 天，失敗 {failedDays} 天。");
            return successDays > 0;
        }

        console.WriteLine($"回填完成：成功 {successDays} 天、失敗 {failedDays} 天、略過 {skippedDays} 天（無主機對應）");
        if (stateChangesFailed)
        {
            console.WriteLine($"⚠ 狀態變更未完整取得：{stateChangesError}");
        }
        if (successDays == 0 && failedDays == 0 && skippedDays > 0)
        {
            console.WriteLine("沒有任何一天有主機對應，未取得數值");
        }

        return failedDays == 0 && !stateChangesFailed && successDays > 0;
    }
}
