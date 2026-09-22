using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在**背景**把既有進行中案件整併成交辦單（<see cref="WorkOrderBackfiller"/>）。
///
/// 形狀同 <see cref="TopIssueBackfillHostedService"/>：整併要掃過全部案件與處理歷程，
/// 掛在 <c>StorageBackend</c> 啟動路徑上會讓 Windows 服務啟動逾時（預設 30 秒）。
/// 中途關站不回滾已完成的批次，下次啟動接續。
/// </summary>
public class WorkOrderBackfillHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly BackgroundWorkGate _gate;

    private readonly WorkOrderBackfiller _backfiller;
    private readonly DataVersionStamp _dataVersion;

    public WorkOrderBackfillHostedService(WorkOrderBackfiller backfiller, DataVersionStamp dataVersion, BackgroundWorkGate gate)
    {
        _gate = gate;
        _backfiller = backfiller;
        _dataVersion = dataVersion;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成，不與第一批使用者請求搶資料庫
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 同步工作丟到 thread pool，避免擋住站台啟動
        // 經共用節流閘排隊：同一時間只跑一支背景回填，取數排程執行中時先讓路
        try
        {
            await _gate.RunAsync("交辦單回填",
                () => Task.Run(() =>
                {
                    try
                    {
                        _backfiller.Run(stoppingToken);
                        // 案件的交辦單連結已被背景改寫，快取要失效
                        _dataVersion.Bump();
                    }
                    catch (Exception ex)
                    {
                        // 整併失敗不影響站台運作：記 log，不讓背景例外把行程帶走
                        Log.Error(ex, "[SQL] 交辦單整併失敗：{Msg}", ex.Message);
                    }
                }, stoppingToken),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 站台關閉時仍在排隊（或排隊中被取消），下次啟動接續
        }
    }
}
