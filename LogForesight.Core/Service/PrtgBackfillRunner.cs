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
    /// <param name="console">執行歷程輸出</param>
    /// <param name="ct">取消語彙基元</param>
    /// <returns>有任何一天成功回傳 true，全部失敗回傳 false</returns>
    public static async Task<bool> RunAsync(
        PrtgFetchService fetchService, int days, IRunConsole console, CancellationToken ct)
    {
        if (days <= 0)
        {
            console.WriteLine("回填天數必須大於 0。");
            return false;
        }

        var successDays = 0;
        var failedDays = 0;
        const int concurrency = 2;

        console.WriteLine($"開始執行 PRTG 歷史回填（共 {days} 天，由近往遠逐日擷取）...");

        try
        {
            for (var i = 1; i <= days; i++)
            {
                ct.ThrowIfCancellationRequested();

                var day = DateTime.Today.AddDays(-i);
                try
                {
                    var result = await fetchService.FetchDayAsync(day, concurrency, ct);
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
