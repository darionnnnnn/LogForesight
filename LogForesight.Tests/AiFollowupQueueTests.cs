using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// AiFollowupQueue 的佇列機制本身（docs/archive/FEEDBACK-12-PLAN.md §3.2／§3.8-4）：FIFO 保序與
/// 有界背壓。這裡只測佇列機制，不涉及 AI／pipeline 語意（那些在 NetiqPipelineService 的
/// 整合測試裡驗證）。
/// </summary>
public class AiFollowupQueueTests
{
    [Fact]
    public async Task 依寫入順序讀出FIFO()
    {
        var queue = new AiFollowupQueue<int>(capacity: 10);
        for (int i = 1; i <= 5; i++)
        {
            await queue.EnqueueAsync(i);
        }
        queue.Complete();

        var read = new List<int>();
        await foreach (var item in queue.ReadAllAsync())
        {
            read.Add(item);
        }

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, read);
    }

    /// <summary>容量 2 的佇列塞 3 件不死鎖：第 3 件的 EnqueueAsync 要等消費者先取走 1 件才能寫入，
    /// 不是無限阻塞——用逾時保護，測試本身若卡死會逾時失敗而不是掛住整個測試回合。</summary>
    [Fact]
    public async Task 容量已滿時寫入端背壓等待_消費後才能繼續寫入不死鎖()
    {
        var queue = new AiFollowupQueue<int>(capacity: 2);
        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);

        var thirdWrite = queue.EnqueueAsync(3).AsTask();
        Assert.False(thirdWrite.IsCompleted); // 佇列已滿，第三筆卡在背壓

        var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, reader.Current);

        var completed = await Task.WhenAny(thirdWrite, Task.Delay(TimeSpan.FromSeconds(5))) == thirdWrite;
        Assert.True(completed, "消費一筆後，背壓中的寫入應該能繼續完成，而不是持續卡住");

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(2, reader.Current);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, reader.Current);
    }

    [Fact]
    public async Task Complete後讀取端讀完既有項目就結束()
    {
        var queue = new AiFollowupQueue<string>(capacity: 5);
        await queue.EnqueueAsync("a");
        queue.Complete();

        var read = new List<string>();
        await foreach (var item in queue.ReadAllAsync())
        {
            read.Add(item);
        }

        Assert.Equal(new[] { "a" }, read);
    }

    [Fact]
    public async Task 取消token觸發時EnqueueAsync拋出取消例外()
    {
        var queue = new AiFollowupQueue<int>(capacity: 1);
        await queue.EnqueueAsync(1); // 塞滿

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.EnqueueAsync(2, cts.Token).AsTask());
    }

    /// <summary>回饋十三輪 A7：TryEnqueue 是非阻塞探測，供呼叫端在真的要背壓等待前先切換
    /// 進度顯示——有空位時要能立即成功且不消耗多餘的一個位置。</summary>
    [Fact]
    public async Task TryEnqueue_有空位時立即成功()
    {
        var queue = new AiFollowupQueue<int>(capacity: 2);

        Assert.True(queue.TryEnqueue(1));
        Assert.True(queue.TryEnqueue(2));
        queue.Complete();

        var read = new List<int>();
        await foreach (var item in queue.ReadAllAsync()) read.Add(item);
        Assert.Equal(new[] { 1, 2 }, read);
    }

    /// <summary>佇列已滿時 TryEnqueue 立即回 false、不阻塞——這是它與 EnqueueAsync 的關鍵差異，
    /// 呼叫端據此判斷「即將進入背壓等待」而不必真的卡在那裡才知道。</summary>
    [Fact]
    public void TryEnqueue_佇列已滿時立即回false不阻塞()
    {
        var queue = new AiFollowupQueue<int>(capacity: 1);
        Assert.True(queue.TryEnqueue(1));

        Assert.False(queue.TryEnqueue(2));
    }

    /// <summary>回饋二十七輪作業 B：背壓期間畫面要看得出佇列在消化，佇列深度必須拿得到。</summary>
    [Fact]
    public async Task PendingCount反映尚未被取走的件數()
    {
        var queue = new AiFollowupQueue<int>(capacity: 5);
        Assert.Equal(0, queue.PendingCount);

        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);
        Assert.Equal(2, queue.PendingCount);

        var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, queue.PendingCount);
    }
}
