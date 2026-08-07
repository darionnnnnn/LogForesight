namespace LogForesight.Tests;

// ── 測試替身：ISentinelSearchClient（docs/FEEDBACK-12-PLAN.md §3.8-2/§3.8-3）──────────
// 讓 NetiqPipelineService 的整合測試不必真的連 Sentinel。

/// <summary>
/// ISentinelSearchClient 測試替身。預設每次查詢回傳零筆；測試可依 <see cref="Responder"/>
/// 依請求內容（通常看 <c>request.Start</c> 判斷是哪一天）回傳對應的假事件。
///
/// 呼叫紀錄用 lock 保護：<see cref="NetiqPipelineService"/> 平行處理多台 Sentinel 時，
/// 每台各自持有自己的 client 實例，但單一實例內部（同一台 Sentinel）目前是依序呼叫——
/// 保持執行緒安全純粹是防禦性的，不假設呼叫端的併發模型不會變。
/// </summary>
internal sealed class FakeSentinelSearchClient : ISentinelSearchClient
{
    private readonly object _lock = new();
    private readonly List<SentinelSearchRequest> _requests = new();

    public IReadOnlyList<SentinelSearchRequest> Requests { get { lock (_lock) return _requests.ToList(); } }
    public bool Disposed { get; private set; }

    /// <summary>依請求算回應；預設回傳零筆完成。</summary>
    public Func<SentinelSearchRequest, SentinelSearchResult> Responder { get; set; } = _ =>
        new SentinelSearchResult { Events = Array.Empty<SentinelEvent>(), Found = 0, State = SentinelJobState.Completed };

    public Task<SentinelSearchResult> SearchAsync(SentinelSearchRequest request, CancellationToken ct = default)
    {
        lock (_lock) _requests.Add(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Responder(request));
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
