using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 案件逐日同步的背景服務：把「列數超過就地門檻、只存了意圖」的案件分批展開成逐日列與歷程
/// （<see cref="IssueCaseCoordinator.ApplyPendingCaseDays"/>）。
///
/// 有待同步案件時一批接一批跑，整輪清空後才 Bump 一次資料版本（逐批 Bump 會讓儀表板快取反覆失效）；
/// 沒有待同步時每隔一段時間輪詢。單批例外只記 log、稍後重試，不讓背景例外把行程帶走。
/// </summary>
public class CaseDaySyncHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>啟動延遲（暫定）：讓站台其餘啟動流程先完成</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>沒有待同步或單批失敗時的等待（暫定）</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>每批處理的案件數（暫定）</summary>
    private const int BatchSize = 200;

    private readonly IssueCaseCoordinator _coordinator;
    private readonly DataVersionStamp _dataVersion;

    public CaseDaySyncHostedService(IssueCaseCoordinator coordinator, DataVersionStamp dataVersion)
    {
        _coordinator = coordinator;
        _dataVersion = dataVersion;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var processedAny = false;
                try
                {
                    // 同步工作丟到 thread pool（同 TopIssueBackfillHostedService 的理由）
                    await Task.Run(() =>
                    {
                        while (!stoppingToken.IsCancellationRequested && _coordinator.ApplyPendingCaseDays(BatchSize) > 0)
                            processedAny = true;
                    }, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[案件逐日同步] 背景批次失敗，稍後重試：{Msg}", ex.Message);
                }
                finally
                {
                    // 資料已被背景改寫，儀表板／報表快取要失效：背景服務不走 HTTP 管線
                    if (processedAny) _dataVersion.Bump();
                }

                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 站台關閉：正常結束
        }
    }
}
