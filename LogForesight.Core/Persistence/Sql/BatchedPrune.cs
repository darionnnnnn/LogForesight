using NLog;

namespace LogForesight.Core.Persistence.Sql;

/// <summary>
/// 分批刪除的共用骨架（docs/archive/SCALE-FIX-PLAN-2026-08-06.md S-4，沿用 P0.5 在
/// <see cref="EfAnalysisRecordStore.Prune(int)"/>／<see cref="EfJsonLogStore.Prune(DateTime)"/>
/// 驗證過的作法）。
///
/// 三個非做不可的性質，抽出來就是為了**不讓第四個清理對象再自己發明一次**：
///
///   1. **只撈主鍵**：整批 <c>ToList()</c> 實體再 <c>RemoveRange</c> 等於把要刪的資料
///      全部先讀進記憶體——6000 台環境下那是數 GB。主鍵是 8 bytes／列，
///      實際刪除交給 <c>ExecuteDelete</c> 在資料庫端完成。
///   2. **分批**：單一交易刪數十萬列會把交易記錄檔撐爆、鎖住整張表，
///      而清理是跟著夜間分析跑的，鎖住＝隔天早上沒人查得動。
///   3. **單次上限**：清理是「順手做的維護」，不該讓一次執行卡在刪資料上。
///      超過上限就留待下次——反正每晚都會再跑一次，而剩幾筆會誠實寫進 log。
/// </summary>
internal static class BatchedPrune
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>單次執行的刪除上限，與 <see cref="EfAnalysisRecordStore"/> 同值</summary>
    internal const int MaxRowsPerRun = 200_000;

    /// <summary>每批刪除的列數</summary>
    internal const int BatchSize = 5_000;

    /// <param name="takeKeys">取下一批要刪的主鍵（已套用過期條件與排序）；參數是本批要取幾筆</param>
    /// <param name="deleteByKeys">依主鍵刪除，回傳實際刪除列數</param>
    /// <param name="countRemaining">本次上限用完後，還有幾筆符合刪除條件（只在真的刪過東西時才查，
    /// 沒刪過就代表沒有符合條件的列，不必為了寫一行 log 多打一次資料庫）</param>
    /// <param name="what">log 用的對象名稱（如「問題處理狀態」）</param>
    internal static int Run<TKey>(
        Func<LfDbContext> contextFactory,
        Func<LfDbContext, int, List<TKey>> takeKeys,
        Func<LfDbContext, List<TKey>, int> deleteByKeys,
        Func<LfDbContext, int> countRemaining,
        string what,
        int maxRows = MaxRowsPerRun,
        int batchSize = BatchSize)
    {
        using var ctx = contextFactory();

        var total = 0;
        while (total < maxRows)
        {
            var keys = takeKeys(ctx, Math.Min(batchSize, maxRows - total));
            if (keys.Count == 0) break;

            total += deleteByKeys(ctx, keys);
        }

        if (total == 0) return 0;

        var remaining = countRemaining(ctx);
        if (remaining > 0)
        {
            Log.Info("[SQL] 清理{What}：本次清除 {Count} 筆，另有 {Remaining} 筆超過單次上限 {Max}，留待下次執行",
                what, total, remaining, maxRows);
        }
        else
        {
            Log.Info("[SQL] 清理{What}：清除 {Count} 筆，已無其他可清除資料", what, total);
        }

        return total;
    }
}
