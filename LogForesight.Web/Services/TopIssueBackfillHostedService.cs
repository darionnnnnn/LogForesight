using NLog;
using LogForesight.Core.Persistence;

namespace LogForesight.Web.Services;

/// <summary>
/// 在**背景**補齊 <c>lf_top_issues</c> 的聚合維度（docs/archive/SCALE-ISSUE-FIRST-PLAN.md P4／§8.2 E3）。
///
/// **為什麼不掛在啟動路徑上**：回填要逐筆解析 ContentJson，資料量大時要跑數分鐘；
/// 掛在 <c>StorageBackend</c> 的建構式裡會讓 Windows 服務啟動逾時（預設 30 秒）。
/// 這裡與排程引擎同一個模式——背景執行、進度可查、站台關閉時優雅停止並在下次啟動接續。
///
/// 回填未完成期間，問題聚合的數字會**偏低但看起來正常**，因此
/// <c>/api/health/detail</c> 與畫面都要看得到進度（誠實申報，不靜默給錯數字）。
/// </summary>
public class TopIssueBackfillHostedService : BackgroundService
{
    private readonly DataVersionStamp _dataVersion;
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public const int MaxRetries = 3;
    public const int RetryIntervalMinutes = 30;

    private readonly BackgroundWorkGate _gate;

    private readonly Func<bool> _tryFastReady;
    private readonly Action<CancellationToken> _runAll;

    internal TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(15);
    internal TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(RetryIntervalMinutes);

    public TopIssueBackfillHostedService(
        TopIssueBackfiller backfiller, StorageBackend backend, DataVersionStamp dataVersion, BackgroundWorkGate gate)
        : this(backfiller.TryFastReady, ct =>
        {
            backfiller.Run(ct);
            if (!ct.IsCancellationRequested && backfiller.SourceKeyProgress.Completed)
            {
                backfiller.RunRiskySourceKeys(backend.RiskyEventStore(), ct);
            }
        }, dataVersion, gate)
    {
    }

    internal TopIssueBackfillHostedService(
        Func<bool> tryFastReady, Action<CancellationToken> runAll, DataVersionStamp dataVersion, BackgroundWorkGate gate)
    {
        _gate = gate;
        _dataVersion = dataVersion;
        _tryFastReady = tryFastReady;
        _runAll = runAll;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 讓站台其餘啟動流程先完成（同 SchedulerHostedService 的既有作法）——
        // 回填是背景維護工作，不該與第一批使用者請求搶資料庫
        try
        {
            if (InitialDelay > TimeSpan.Zero) await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 快判在排隊之前：閘門在取數排程執行中會一直等，夜間重啟時不能讓使用者全程走非索引路徑
        try
        {
            if (_tryFastReady()) Log.Info("[SQL] 來源鍵已就緒（啟動快判），問題聚合直接走索引路徑");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "[SQL] 來源鍵啟動快判失敗，改由背景回填處理：{Msg}", ex.Message);
        }

        // 重試等待放在閘門之外，等待期間不佔用背景工作閘
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (stoppingToken.IsCancellationRequested) return;
            bool failed;
            try
            {
                failed = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 站台關閉時仍在排隊（或排隊中被取消），下次啟動接續
                return;
            }
            if (!failed) return;
            if (attempt >= MaxRetries)
            {
                Log.Error("[SQL] lf_top_issues 聚合欄回填已連續失敗達上限 {Max} 次，停止重試", MaxRetries);
                return;
            }
            try
            {
                if (RetryInterval > TimeSpan.Zero) await Task.Delay(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>經共用節流閘排隊跑一次回填；回傳是否失敗（擲非取消例外）。</summary>
    private async Task<bool> RunOnceAsync(CancellationToken stoppingToken)
    {
        var failed = false;
        // 同步工作丟到 thread pool：BackgroundService 的 ExecuteAsync 在返回第一個 await 之前
        // 是站台啟動流程的一部分，直接同步跑會把啟動擋住——正是這個類別要避免的事
        // 經共用節流閘排隊：同一時間只跑一支背景回填，取數排程執行中時先讓路
        await _gate.RunAsync("問題聚合欄回填",
            () => Task.Run(() =>
            {
                try
                {
                    _runAll(stoppingToken);
                    // 資料已被背景改寫，儀表板／報表快取要失效（體檢輪）：背景服務不走 HTTP 管線
                    _dataVersion.Bump();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 回填失敗不影響站台運作（聚合數字偏低，但畫面會標示統計中）——
                    // 記 log 讓它查得到，不要讓背景例外變成未處理例外把行程帶走
                    failed = true;
                    Log.Error(ex, "[SQL] lf_top_issues 聚合欄回填失敗：{Msg}", ex.Message);
                }
            }, stoppingToken),
            stoppingToken);
        return failed;
    }
}
