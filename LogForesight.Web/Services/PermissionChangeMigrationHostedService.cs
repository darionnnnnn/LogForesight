using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在背景把權限異動自 JSONL log 與 blob 搬進真表。
/// 搬移完成前由 <see cref="Middleware.MigrationGateMiddleware"/> 擋住 /api/permission-changes 的寫入。
/// 註冊在回填之前，讓站台盡快恢復可寫。
/// </summary>
public class PermissionChangeMigrationHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StorageBackend _backend;
    private readonly ISystemSettingsStore _systemSettings;

    public PermissionChangeMigrationHostedService(StorageBackend backend, ISystemSettingsStore systemSettings)
    {
        _backend = backend;
        _systemSettings = systemSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成（同 HandlingMigrationHostedService 的作法）。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
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
                _backend.PermissionChangeMigrator.Run(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SQL] 權限異動遷移失敗，權限異動維持唯讀：{Msg}", ex.Message);
                return;   // 遷移沒完成就重剖會漏掉還沒搬進表的列
            }

            // 遷移完成後接著重剖回填舊列的欄位（一次性）。失敗不影響站台：欄位補不回來
            // 只是顯示退化成降級句，新寫入的資料走的是修好的解析器。
            try
            {
                // 帶上欄位對應：訊息用非標準欄位名的站台，沒帶就等於白跑（重剖是一次性的）
                _backend.PermissionChangeReparser.Run(
                    stoppingToken, PermissionFieldMappings.FromSystemSettings(_systemSettings.Get()));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SQL] 權限異動重剖回填失敗（不影響新資料）：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
