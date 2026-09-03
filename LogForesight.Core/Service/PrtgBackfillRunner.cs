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
    /// 由近往遠逐日回填 PRTG 過去 N 天的數值與狀態變更。
    /// </summary>
    /// <param name="fetchService">單日擷取服務</param>
    /// <param name="days">回填天數（由昨天往前計）</param>
    /// <param name="concurrency">hourly 數值抓取併發上限（取自 PrtgFetchConcurrency，與每日擷取共用）</param>
    /// <param name="console">執行歷程輸出</param>
    /// <param name="ct">取消語彙基元</param>
    /// <param name="store">PRTG 鏡像 store（傳入時啟用觸發式過濾）</param>
    /// <param name="records">分析紀錄查詢介面（傳入時啟用觸發式過濾）</param>
    /// <param name="whitelist">sensor type 白名單（null 或空表示不限制）</param>
    /// <param name="dayProgress">天數層進度回呼（已完成天數, 總天數, 當前日期），null＝不回報</param>
    /// <param name="sensorProgress">當日 sensor 進度回呼（已完成數, 總數），null＝不回報</param>
    /// <returns>有任何一天成功回傳 true，全部失敗回傳 false</returns>
    public static async Task<bool> RunAsync(
        PrtgFetchService fetchService, int days, int concurrency, IRunConsole console, CancellationToken ct,
        EfPrtgStore? store = null, IAnalysisRecordQuery? records = null, IReadOnlyCollection<string>? whitelist = null,
        Action<int, int, DateTime?>? dayProgress = null,
        Action<int, int>? sensorProgress = null)
    {
        if (days <= 0)
        {
            console.WriteLine("回填天數必須大於 0。");
            return false;
        }

        var successDays = 0;
        var failedDays = 0;

        console.WriteLine($"開始執行 PRTG 歷史回填（共 {days} 天，由近往遠逐日擷取）...");

        try
        {
            for (var i = 1; i <= days; i++)
            {
                ct.ThrowIfCancellationRequested();

                var day = DateTime.Today.AddDays(-i);
                // 已完成 i-1 天、正在處理第 i 天：回報 i 會讓剛開始第 1 天就顯示 1/N，
                // 最後一天處理中顯示 N/N（看起來已經跑完但其實還在跑）
                dayProgress?.Invoke(i - 1, days, day);
                sensorProgress?.Invoke(0, 0);

                try
                {
                    if (store == null || records == null)
                    {
                        // syncStructure: false —— 結構鏡像永遠是現況，逐日回填不必也不該重跑它
                        // （會對 PRTG 做 N 次全量查詢，並把「最後結構同步時間」改寫成回填當下）
                        var result = await fetchService.FetchDayAsync(
                            day, concurrency, ct, syncStructure: false,
                            progress: (stage, done, total) => sensorProgress?.Invoke(done, total));
                        if (result.Failures > 0 && result.Values == 0 && result.StateChanges == 0)
                        {
                            failedDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：所有擷取階段皆未成功。");
                        }
                        else
                        {
                            successDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）：數值 {result.Values} 筆、狀態變更 {result.StateChanges} 筆");
                        }
                    }
                    else
                    {
                        // 觸發式回填：只回填問題主機命中白名單的 sensor
                        var result = await fetchService.FetchDayAsync(day, concurrency, ct, syncStructure: false, fetchValues: false);
                        var failures = result.Failures;
                        var valuesWritten = 0;
                        var targetSensorsCount = 0;

                        var filter = new RecordQueryFilter
                        {
                            From = day,
                            To = day,
                            RiskLevels = new[] { "高", "中" },
                            Hosts = null
                        };
                        var riskyRecords = records.QueryLightweight(filter);
                        var riskyHostIds = riskyRecords
                            .Select(r => r.HostId)
                            .Distinct()
                            .ToHashSet();

                        var problemHostsCount = riskyHostIds.Count;

                        if (problemHostsCount > 0)
                        {
                            // 該日無對應時退回最近一日的對應：以回填當日為基準往回查，
                            // 單一聚合查詢取代逐日往回的最多 30 次獨立查詢（回填 N 天會放大 N 倍）
                            var hostMapRows = store.GetLatestHostMapWithDate(31, day).Rows;

                            if (hostMapRows.Count > 0)
                            {
                                var deviceObjids = hostMapRows
                                    .Where(m => m.MapStatus == PrtgMapStatus.Ok && m.HostId.HasValue && riskyHostIds.Contains(m.HostId.Value))
                                    .Select(m => m.DeviceObjid)
                                    .Distinct()
                                    .ToList();

                                if (deviceObjids.Count > 0)
                                {
                                    var targets = store.GetValueFetchTargets(whitelist, deviceObjids);
                                    targetSensorsCount = targets.Count;
                                    if (targets.Count > 0)
                                    {
                                        var (written, failedSensors) = await fetchService.FetchValuesForSensorsAsync(
                                            day, targets, concurrency, ct,
                                            progress: (stage, done, total) => sensorProgress?.Invoke(done, total));
                                        valuesWritten = written;
                                        if (failedSensors > 0 && written == 0)
                                        {
                                            failures++;
                                        }
                                    }
                                }
                            }
                        }

                        if (failures > 0 && valuesWritten == 0 && result.StateChanges == 0)
                        {
                            failedDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：所有擷取階段皆未成功。");
                        }
                        else
                        {
                            successDays++;
                            console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）：問題主機 {problemHostsCount} 台、sensor {targetSensorsCount} 個、數值 {valuesWritten} 筆、狀態變更 {result.StateChanges} 筆");
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedDays++;
                    console.WriteLine($"回填 {day:yyyy-MM-dd}（第 {i}/{days} 天）失敗：{ex.Message}");
                }
            }

            dayProgress?.Invoke(days, days, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            console.WriteLine($"回填已中斷。累計完成 {successDays} 天，失敗 {failedDays} 天。");
            throw;
        }

        console.WriteLine($"回填作業完成。共成功 {successDays} 天，失敗 {failedDays} 天。");
        return successDays > 0;
    }
}
