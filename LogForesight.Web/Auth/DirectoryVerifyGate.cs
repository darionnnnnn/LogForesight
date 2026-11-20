namespace LogForesight.Web.Auth;

/// <summary>
/// 目錄服務（AD bind）驗證的並行上限（WEB-SPEC §6.2）。
///
/// AD bind 是同步呼叫，目錄服務不回應時每個驗證會占住執行緒到逾時；上限只加在「呼叫 Provider」這一步，
/// **不加在端點上**：serverAdmin 救援帳號不經 Provider，AD 掛掉時它是唯一入口，不能被一般登入占滿的名額一起擋掉。
/// 同時進行 <see cref="Concurrent"/> 個，再多的最多排 <see cref="Queued"/> 個，滿了直接拒絕、不讓請求無上限堆積。
/// 註冊為 singleton（IdentityService 是 scoped，上限要跨請求共用）。
/// </summary>
public sealed class DirectoryVerifyGate
{
    public const int Concurrent = 8;
    public const int Queued = 32;

    /// <summary>排隊等名額的上限時間：目錄服務完全卡死時，排隊者不會無限等下去（逾時當作忙碌）</summary>
    private static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _slots = new(Concurrent, Concurrent);
    private int _waiting;

    /// <summary>目前排隊中的數量（測試用）</summary>
    internal int Waiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// 取得許可；名額與佇列都滿了、或排隊逾時回 null（呼叫端回「目錄服務忙碌」）。用完要 Dispose 歸還。
    /// 排隊是非同步等待，不占執行緒——AD 卡住時只有取得許可的 <see cref="Concurrent"/> 個請求占著執行緒。
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken cancellationToken = default)
    {
        if (_slots.Wait(0)) return new Releaser(_slots);

        if (Interlocked.Increment(ref _waiting) > Queued)
        {
            Interlocked.Decrement(ref _waiting);
            return null;
        }
        try
        {
            return await _slots.WaitAsync(QueueWait, cancellationToken).ConfigureAwait(false) ? new Releaser(_slots) : null;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _slots;
        public Releaser(SemaphoreSlim slots) => _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
