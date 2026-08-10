namespace LogForesight.Tests;

// ── 測試替身：ISentinelSearchClient（docs/FEEDBACK-12-PLAN.md §3.8-2/§3.8-3）──────────
// 讓 NetiqPipelineService 的整合測試不必真的連 Sentinel。

/// <summary>
/// ISentinelSearchClient 測試替身。預設每次查詢回傳零筆；測試可依 <see cref="Responder"/>
/// 依請求內容（通常看 <c>request.Start</c> 判斷是哪一天）回傳對應的假事件。
///
/// 呼叫紀錄與 <see cref="PeakConcurrency"/> 都用 lock／Interlocked 保護，因為
/// <see cref="NetiqPipelineService"/> 不只平行處理多台 Sentinel，同一台 Sentinel 內部
/// 也可能透過 client pool 平行送出多個批次查詢（回饋十三輪 D）——測試常用
/// <see cref="FakeSentinelSearchClientFactory.Single"/> 讓 pool 裡的每個「client」其實都指向
/// 同一個替身實例，這個類別本身必須真正執行緒安全，而不能只是防禦性寫寫。
/// </summary>
internal sealed class FakeSentinelSearchClient : ISentinelSearchClient
{
    private readonly object _lock = new();
    private readonly List<SentinelSearchRequest> _requests = new();
    private int _active;

    public IReadOnlyList<SentinelSearchRequest> Requests { get { lock (_lock) return _requests.ToList(); } }
    public bool Disposed { get; private set; }

    /// <summary>依請求算回應；預設回傳零筆完成。</summary>
    public Func<SentinelSearchRequest, SentinelSearchResult> Responder { get; set; } = _ =>
        new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };

    /// <summary>模擬查詢耗時（回饋十三輪 D 的併發峰值測試用）：0＝立即完成。真實的
    /// SentinelClient 一次查詢動輒數秒到數十秒，測試裡不需要真的等那麼久，但若完全不等，
    /// 多個平行呼叫可能剛好序列化執行也測不出峰值差異——給一個小延遲讓呼叫真的有機會重疊。</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>觀察到的「同時執行中」SearchAsync 呼叫數峰值——驗證呼叫端有沒有真的平行送出查詢，
    /// 以及有沒有超過呼叫端自己設定的並行度上限（回饋十三輪 D，client pool）。</summary>
    public int PeakConcurrency { get; private set; }

    public async Task<SentinelSearchResult> SearchAsync(SentinelSearchRequest request, CancellationToken ct = default)
    {
        lock (_lock) _requests.Add(request);
        ct.ThrowIfCancellationRequested();

        var active = Interlocked.Increment(ref _active);
        lock (_lock)
        {
            if (active > PeakConcurrency) PeakConcurrency = active;
        }
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return Responder(request);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>建立 <c>Func&lt;Sentinel, ISentinelSearchClient&gt;</c> 工廠的小幫手，
/// 對應 <see cref="NetiqPipelineService"/> 建構子的 <c>clientFactory</c> 參數。</summary>
internal static class FakeSentinelSearchClientFactory
{
    /// <summary>單一 Sentinel 情境：不論傳入哪個 Sentinel 都回同一個替身。</summary>
    public static Func<Sentinel, ISentinelSearchClient> Single(FakeSentinelSearchClient client) => _ => client;

    /// <summary>多 Sentinel 情境：依 Sentinel 名稱（不分大小寫）挑對應的替身。</summary>
    public static Func<Sentinel, ISentinelSearchClient> ByName(
        IReadOnlyDictionary<string, FakeSentinelSearchClient> byName) =>
        sentinel => byName.TryGetValue(sentinel.Name, out var client)
            ? client
            : throw new KeyNotFoundException($"測試替身沒有為 Sentinel「{sentinel.Name}」設定假用戶端。");
}
