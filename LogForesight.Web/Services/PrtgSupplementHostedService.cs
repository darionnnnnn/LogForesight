using LogForesight.Core.Service;
using LogForesight.Web.Services.Mail;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>重啟後從持久意圖續作；共用背景閘門避開分析及人工維護。</summary>
public sealed class PrtgSupplementHostedService(PrtgSupplementReplay replay, BackgroundWorkGate gate,
    DataVersionStamp stamp, MailNotificationService mail, HostDayWorkflowService? workflow = null,
    IWebAiService? ai = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                var changed = 0;
                await gate.RunAsync("PRTG NetIQ 補追加", () => Task.Run(() =>
                { changed = replay.RunBatch(cancellationToken: stoppingToken, workflow: workflow,
                    aiConfigured: ai?.Available == true); if (changed > 0) stamp.Bump(); }, stoppingToken), stoppingToken);
                if (changed > 0) await mail.NotifyAfterRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* 範圍变更：保留持久意圖，下輪重查。 */ }
            catch (Exception ex) { LogManager.GetCurrentClassLogger().Warn(ex, "PRTG 補追加續作失敗；下輪重試"); }
        }
    }
}
