using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在背景補齊 <c>lf_daily_records</c> 的抽出欄（回饋十九輪批次B）。
/// 骨架與時機同 <see cref="TopIssueBackfillHostedService"/>：不掛在啟動路徑上（大 DB 會讓
/// Windows 服務啟動逾時），延遲後在背景執行，站台關閉時優雅停止、下次啟動接續。
/// </summary>
public class DailyRecordBackfillHostedService : BackgroundService
{
    private readonly DataVersionStamp _dataVersion;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly DailyRecordBackfiller _backfiller;

    public DailyRecordBackfillHostedService(DailyRecordBackfiller backfiller, DataVersionStamp dataVersion)
    {
        _dataVersion = dataVersion;
        _backfiller = backfiller;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 錯開 TopIssueBackfillHostedService 的 15 秒，避免兩個回填同時搶站台剛啟動時的連線
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                _backfiller.Run(stoppingToken);
                // 回填改寫了儀表板聚合的抽出欄，快取要失效（體檢輪）：背景服務不走 HTTP 管線
                _dataVersion.Bump();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SQL] lf_daily_records 抽出欄回填失敗：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
