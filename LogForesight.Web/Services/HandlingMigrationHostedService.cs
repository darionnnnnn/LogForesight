using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在**背景**把處理狀態自 blob 搬進真表（docs/SCALE-FIX-PLAN-2026-08-06.md §三／G1）。
///
/// **為什麼不能在啟動路徑上做**：2000 台約 108 萬列／350 MB，搬移是分鐘級；
/// Windows 服務的啟動逾時預設 30 秒，掛在 `StorageBackend` 建構式裡會被 SCM 砍掉——
/// 而被砍掉正是造成資料靜默遺失的那條路徑（體檢 S-1）。
///
/// **與 <see cref="TopIssueBackfillHostedService"/> 的差別**：回填只是讓數字變準，
/// 沒補完只是偏低；遷移沒搬完則**不能讓人寫入**（會落進一個馬上要被覆蓋的表），
/// 因此配一個 <see cref="Middleware.MigrationGateMiddleware"/> 擋住寫入。
/// 也因此這支服務**優先於回填**執行（延遲較短），讓站台盡快恢復可寫。
/// </summary>
public class HandlingMigrationHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StorageBackend _backend;

    public HandlingMigrationHostedService(StorageBackend backend)
    {
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成（同 SchedulerHostedService 的既有作法）。
        // 這裡刻意比回填的 15 秒短——遷移未完成時處理狀態是唯讀的，要盡快解除
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 同步工作丟到 thread pool：ExecuteAsync 在第一個 await 之前屬於啟動流程的一部分，
        // 直接同步跑會把啟動擋住——正是這個類別要避免的事
        await Task.Run(() =>
        {
            try
            {
                _backend.HandlingMigrator.Run(stoppingToken);
            }
            catch (Exception ex)
            {
                // 失敗不讓背景例外把行程帶走。狀態已由 Migrator 記成 pending＋LastError，
                // 因此寫入**仍然被擋著**（不會落進半搬完的表），而畫面看得到原因
                Log.Error(ex, "[SQL] 處理狀態遷移失敗，處理狀態維持唯讀：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
