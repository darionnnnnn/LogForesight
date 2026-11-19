using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>背景分批修訂歷史弱佐證；重啟可續作且每批失效讀取快取。</summary>
public sealed class PrtgRiskReviewHostedService(StorageBackend backend, BackgroundWorkGate gate,
    DataVersionStamp dataVersion, TimeSpan? startupDelay = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = new PrtgRiskProjectionStore(backend.CreateContext);
        try
        {
            await Task.Delay(startupDelay ?? TimeSpan.FromSeconds(25), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = 0;
                await gate.RunAsync("PRTG 歷史弱佐證重評", () => Task.Run(() =>
                {
                    var batch = store.RunBatchDetailed();
                    changed = batch.Processed;
                    // 畫面看得到的欄位（內容或重評狀態）有變才使讀取快取失效；只更新投影版本欄不推進。
                    if (batch.VisibleChanged > 0) dataVersion.Bump();
                }, stoppingToken), stoppingToken);
                if (changed == 0) return;
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogManager.GetCurrentClassLogger().Error(ex, "歷史弱佐證重評失敗；已提交修訂保留，下次啟動續作");
        }
    }
}
