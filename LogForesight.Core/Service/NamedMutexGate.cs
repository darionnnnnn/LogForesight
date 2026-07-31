namespace LogForesight;

/// <summary>
/// 跨行程互斥的安全包裝（docs/WEB-SCHEDULER-PLAN.md §1.4.2／§1.5）：Web 排程與「有人在伺服器上
/// 手動跑 console」之間唯一的跨行程互斥，取不到就記警告並跳過本次。
///
/// **為什麼不是單純 `using var mutex = new Mutex(...)`（console 的既有寫法）**：console 是
/// one-shot，行程結束時 OS 自動釋放具名 Mutex，不需要顯式 <c>ReleaseMutex()</c>。Web 排程是
/// 長駐行程，同一個 Mutex 要在每次觸發後明確釋放給下一次用，而 <c>Mutex.ReleaseMutex()</c>
/// 要求呼叫執行緒與 <c>WaitOne()</c> 時同一條——`async`/`await` 的延續不保證回到同一條執行緒，
/// 貿然在一般 async 方法裡 acquire/await/release 會間歇性擲
/// <see cref="System.Threading.SynchronizationLockException"/>。
///
/// 解法：把「取得→執行→釋放」整段包進單一 <see cref="Task.Run(Action)"/> 委派、且委派內部用
/// <c>GetAwaiter().GetResult()</c> 同步等待要保護的工作——委派本身固定跑在 Task.Run 配發的
/// 那一條執行緒上直到委派返回，取得與釋放天生同一條，內部工作即使跨執行緒續行也不影響
/// （Mutex 只在乎「這條委派線」拿到與放掉的是不是同一條，不管它內部呼叫了什麼）。
/// </summary>
public class NamedMutexGate
{
    private readonly string _name;

    public NamedMutexGate(string name = @"Global\LogForesight")
    {
        _name = name;
    }

    /// <summary>
    /// 在具名 Mutex 保護下執行 <paramref name="action"/>；<paramref name="timeout"/> 內拿不到鎖
    /// （代表另一個 console 執行個體正在跑）回 <c>false</c> 且不執行 action。
    /// </summary>
    public async Task<bool> RunExclusiveAsync(Func<Task> action, TimeSpan timeout)
    {
        var acquired = false;
        Exception? captured = null;

        await Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, _name);
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // 前一個持有者異常終止（例如行程被強制關閉）而沒釋放——鎖仍然視為取得，
                // 這是 .NET 對 abandoned mutex 的既定語意，不當成錯誤
                acquired = true;
            }

            if (!acquired) return;

            try
            {
                action().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });

        if (captured != null) throw captured;
        return acquired;
    }
}
