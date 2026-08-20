using LogForesight.Core.Persistence;
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

    public PermissionChangeMigrationHostedService(StorageBackend backend)
    {
        _backend = backend;
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
            }
        }, stoppingToken);
    }
}
