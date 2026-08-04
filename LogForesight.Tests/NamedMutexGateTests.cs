using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/archive/WEB-SCHEDULER-PLAN.md §1.4.2：Web 長駐行程下具名 Mutex 的 acquire/release 安全包裝。
/// 用隨機名稱（非真正的 Global\LogForesight）避免跟其他測試或真的跑著的 console 撞名。
/// </summary>
public class NamedMutexGateTests
{
    private static string UniqueName() => $"Local\\lf-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task RunExclusiveAsync_單次呼叫成功執行並回傳true()
    {
        var gate = new NamedMutexGate(UniqueName());
        var ran = false;

        var acquired = await gate.RunExclusiveAsync(async () =>
        {
            ran = true;
            await Task.Delay(1);
        }, TimeSpan.FromSeconds(5));

        Assert.True(acquired);
        Assert.True(ran);
    }

    [Fact]
    public async Task RunExclusiveAsync_同一把鎖可重複多次依序取得()
    {
        var gate = new NamedMutexGate(UniqueName());
        var count = 0;

        for (int i = 0; i < 5; i++)
        {
            var acquired = await gate.RunExclusiveAsync(async () =>
            {
                count++;
                await Task.Delay(1);
            }, TimeSpan.FromSeconds(5));
            Assert.True(acquired);
        }

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task RunExclusiveAsync_內部工作跨執行緒續行不影響釋放()
    {
        // 內部 await Task.Delay 續行後大機率換一條執行緒——驗證這不會讓 ReleaseMutex 出錯
        var gate = new NamedMutexGate(UniqueName());

        var acquired = await gate.RunExclusiveAsync(async () =>
        {
            await Task.Delay(50).ConfigureAwait(false);
            await Task.Yield();
        }, TimeSpan.FromSeconds(5));

        Assert.True(acquired);

        // 鎖已釋放：同一把鎖應該能立刻再次取得
        var acquiredAgain = await gate.RunExclusiveAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(5));
        Assert.True(acquiredAgain);
    }

    [Fact]
    public async Task RunExclusiveAsync_另一次執行持有鎖時_逾時內拿不到回false()
    {
        var name = UniqueName();
        var gate1 = new NamedMutexGate(name);
        var gate2 = new NamedMutexGate(name);
        var holderEntered = new TaskCompletionSource();
        var releaseHolder = new TaskCompletionSource();

        var holderTask = gate1.RunExclusiveAsync(async () =>
        {
            holderEntered.SetResult();
            await releaseHolder.Task;
        }, TimeSpan.FromSeconds(10));

        await holderEntered.Task;

        var contenderAcquired = await gate2.RunExclusiveAsync(() => Task.CompletedTask, TimeSpan.FromMilliseconds(200));

        Assert.False(contenderAcquired);

        releaseHolder.SetResult();
        Assert.True(await holderTask);
    }

    [Fact]
    public async Task RunExclusiveAsync_action擲例外仍會釋放鎖並往外拋()
    {
        var name = UniqueName();
        var gate = new NamedMutexGate(name);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunExclusiveAsync(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5)));

        // 鎖仍應被釋放（finally），下一次要能立刻取得
        var acquiredAfter = await gate.RunExclusiveAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(5));
        Assert.True(acquiredAfter);
    }
}
