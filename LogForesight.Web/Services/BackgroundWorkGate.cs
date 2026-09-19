using NLog;

namespace LogForesight.Web.Services;

/// <summary>
/// 背景回填／一次性遷移共用的節流閘：同一時間最多一支在跑，取數排程執行中時先讓路。
///
/// **為什麼要排隊**：站台啟動後有多支回填服務各自延遲 5～20 秒後同時開跑，全部寫同一個資料庫
/// （SQLite 是單一檔案，寫入本來就是序列化的）；升級當晚還會與取數排程撞在一起。讓它們各自
/// 硬搶只會互相等鎖、拉長整體時間並拖慢排程，所以改成排隊一支一支跑，排程在跑時先等它結束。
///
/// **為什麼處理狀態遷移與權限異動遷移不經過這裡**：<see cref="HandlingMigrationHostedService"/> 與
/// <see cref="PermissionChangeMigrationHostedService"/> 完成前，使用者的寫入是被閘門擋住的——
/// 它們必須最優先跑完，不能排在數分鐘的回填後面，更不能因排程執行中而讓路。
/// </summary>
public class BackgroundWorkGate
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly SchedulerRunState _runState;
    private readonly TimeSpan _schedulerPollInterval;

    private int _activeCount;
    private volatile string? _activeName;

    /// <param name="schedulerPollInterval">取數排程執行中時多久檢查一次是否已結束（DI 傳 30 秒）</param>
    public BackgroundWorkGate(SchedulerRunState runState, TimeSpan schedulerPollInterval)
    {
        _runState = runState;
        _schedulerPollInterval = schedulerPollInterval;
    }

    /// <summary>目前持有閘門的工作數（0 或 1）</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>目前持有閘門的工作名稱（沒有則為 null）</summary>
    public string? ActiveName => _activeName;

    /// <summary>
    /// 等取數排程不在執行中、取得閘門後執行 <paramref name="work"/>。例外不在這裡吞掉，
    /// 交由呼叫端原本的 catch 處理；閘門一律在 finally 釋放。
    /// </summary>
    public async Task RunAsync(string name, Func<Task> work, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        var started = false;
        try
        {
            // 排程檢查放在取得閘門「之後」：排在別支回填後面等了幾分鐘，這段期間排程可能已經開跑，
            // 先檢查再排隊的話輪到自己時會直接與排程撞在一起
            while (_runState.IsRunning)
            {
                await Task.Delay(_schedulerPollInterval, ct);
            }

            started = true;
            Interlocked.Increment(ref _activeCount);
            _activeName = name;
            Log.Info("背景工作 {Name} 開始", name);
            await work();
        }
        finally
        {
            if (started)
            {
                _activeName = null;
                Interlocked.Decrement(ref _activeCount);
                Log.Info("背景工作 {Name} 結束", name);
            }
            _semaphore.Release();
        }
    }
}
