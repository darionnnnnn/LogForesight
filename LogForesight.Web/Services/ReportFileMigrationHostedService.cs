using LogForesight.Core.Persistence;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在背景把既有 <c>export\</c> 目錄下的報告 txt 搬進 <c>lf_reports</c>（一次性）。
///
/// 不需要寫入閘門（對照 <see cref="PermissionChangeMigrationHostedService"/>）：報告只由夜間分析
/// 寫入，而遷移是逐筆比對自然鍵補缺、遇到已存在就跳過，遷移期間分析照常寫新報告既不會被覆蓋
/// 也不會讓舊資料被誤判成已搬。搬完之前使用者看到的是「報告已不存在」，那是既有的正常路徑。
/// </summary>
public class ReportFileMigrationHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StorageBackend _backend;

    public ReportFileMigrationHostedService(StorageBackend backend)
    {
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成（同其他遷移背景服務的作法）
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 同步工作丟到 thread pool：避免阻塞啟動流程
        await Task.Run(() =>
        {
            try
            {
                _backend.ReportFileMigrator.Run(stoppingToken);
            }
            catch (Exception ex)
            {
                // 舊報告搬不進來不影響站台任何其他功能，也不影響今晚新產生的報告
                Log.Error(ex, "[SQL] 既有報告檔遷移失敗（不影響新報告）：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
