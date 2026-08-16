using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 在背景執行機房首見日的冪等合併。
///
/// 改成冪等合併之後，沒跑完的後果只是「部分問題的首見日還是退化值」，屬於「數字暫時不準」而不是
/// 「不能讓人寫入」——與 TopIssueBackfillHostedService 同一類，不是 HandlingMigrationHostedService
/// 那一類，因此不需要 gate（如 MigrationGateMiddleware 閘門）。
/// </summary>
public class IssueFirstSeenSeedHostedService : BackgroundService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly StorageBackend _backend;

    public IssueFirstSeenSeedHostedService(StorageBackend backend)
    {
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成，避免與其他背景維護工作同時搶資料庫
        // 延遲用 20 秒（比回填的 15 秒晚）
        try
        {
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
                _backend.MergeIssueFirstSeenSeed();
            }
            catch (Exception ex)
            {
                // 回填失敗不影響站台運作，記 log 讓它查得到，不要讓背景例外變成未處理例外
                Log.Error(ex, "[SQL] lf_issue_first_seen 冪等合併失敗：{Msg}", ex.Message);
            }
        }, stoppingToken);
    }
}
