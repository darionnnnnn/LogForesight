using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在**背景**補齊 <c>lf_top_issues</c> 的聚合維度（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／§8.2 E3）。
///
/// **為什麼不掛在啟動路徑上**：回填要逐筆解析 ContentJson，資料量大時要跑數分鐘；
/// 掛在 <c>StorageBackend</c> 的建構式裡會讓 Windows 服務啟動逾時（預設 30 秒）。
/// 這裡與排程引擎同一個模式——背景執行、進度可查、站台關閉時優雅停止並在下次啟動接續。
///
/// 回填未完成期間，問題聚合的數字會**偏低但看起來正常**，因此
/// <c>/api/health/detail</c> 與畫面都要看得到進度（誠實申報，不靜默給錯數字）。
/// </summary>
public class TopIssueBackfillHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly TopIssueBackfiller _backfiller;

    public TopIssueBackfillHostedService(TopIssueBackfiller backfiller)
    {
        _backfiller = backfiller;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成（同 SchedulerHostedService 的既有作法）——
        // 回填是背景維護工作，不該與第一批使用者請求搶資料庫
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 同步工作丟到 thread pool：BackgroundService 的 ExecuteAsync 在返回第一個 await 之前
        // 是站台啟動流程的一部分，直接同步跑會把啟動擋住——正是這個類別要避免的事
        await Task.Run(() =>
        {
            try
            {
                _backfiller.Run(stoppingToken);
            }
            catch (Exception ex)
            {
                // 回填失敗不影響站台運作（聚合數字偏低，但畫面會標示統計中）——
                // 記 log 讓它查得到，不要讓背景例外變成未處理例外把行程帶走
                Log.Error(ex, "[SQL] lf_top_issues 聚合欄回填失敗：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
