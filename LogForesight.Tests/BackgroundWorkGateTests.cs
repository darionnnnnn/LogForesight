using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="BackgroundWorkGate"/>：背景回填排隊（同時最多一支）、取數排程執行中讓路、例外後仍釋放。
/// 互斥由被保護的工作自己驗證（進入時計數 +1 並斷言 == 1），不靠壓力測試碰運氣。
/// </summary>
public class BackgroundWorkGateTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ThreeConcurrentRuns_ExecuteOneAtATime_AndEachRunsOnce()
    {
        var gate = new BackgroundWorkGate(new SchedulerRunState(), Poll);
        var inside = 0;
        var executed = new int[3];
        var violations = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<Task> Work(int idx) => async () =>
        {
            var now = Interlocked.Increment(ref inside);
            if (now != 1) Interlocked.Increment(ref violations);
            Assert.Equal(1, now);
            Assert.Equal(1, gate.ActiveCount);
            Assert.Equal($"w{idx}", gate.ActiveName);
            Interlocked.Increment(ref executed[idx]);

            // 第一個進來的工作卡住，讓另外兩個確實在閘門外排隊
            if (firstEntered.TrySetResult())
            {
                await releaseFirst.Task;
            }
            else
            {
                await Task.Yield();
            }

            Interlocked.Decrement(ref inside);
        };

        var runs = Enumerable.Range(0, 3)
            .Select(i => Task.Run(() => gate.RunAsync($"w{i}", Work(i), CancellationToken.None)))
            .ToArray();

        await firstEntered.Task.WaitAsync(Timeout);
        // 第一個持有閘門時，其他兩個不可能進入（進入就會讓 violations 增加）
        await Task.Delay(100);
        Assert.Equal(1, executed.Sum());
        releaseFirst.SetResult();

        await Task.WhenAll(runs).WaitAsync(Timeout);

        Assert.Equal(0, violations);
        Assert.All(executed, n => Assert.Equal(1, n));
        Assert.Equal(0, gate.ActiveCount);
        Assert.Null(gate.ActiveName);
    }

    [Fact]
    public async Task SchedulerRunning_WorkDoesNotStartUntilEndRun()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("test", out _));
        var gate = new BackgroundWorkGate(runState, Poll);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = gate.RunAsync("w", () =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        // 經過多個檢查間隔仍不得開始
        await Task.Delay(Poll * 6);
        Assert.False(started.Task.IsCompleted);
        Assert.Equal(0, gate.ActiveCount);

        runState.EndRun();

        await started.Task.WaitAsync(Timeout);
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SchedulerRunning_WaitCanBeCancelled_AndGateIsReleased()
    {
        var runState = new SchedulerRunState();
        Assert.True(runState.TryBeginRun("test", out _));
        var gate = new BackgroundWorkGate(runState, Poll);
        using var cts = new CancellationTokenSource();
        var ran = false;

        var run = gate.RunAsync("w", () => { ran = true; return Task.CompletedTask; }, cts.Token);
        cts.CancelAfter(Poll * 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Timeout));
        Assert.False(ran);

        runState.EndRun();
        var next = false;
        await gate.RunAsync("next", () => { next = true; return Task.CompletedTask; }, CancellationToken.None)
            .WaitAsync(Timeout);
        Assert.True(next);
    }

    [Fact]
    public async Task WorkThrows_GateIsReleased_AndExceptionPropagates()
    {
        var gate = new BackgroundWorkGate(new SchedulerRunState(), Poll);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync("bad", () => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal(0, gate.ActiveCount);
        var ran = false;
        await gate.RunAsync("next", () => { ran = true; return Task.CompletedTask; }, CancellationToken.None)
            .WaitAsync(Timeout);
        Assert.True(ran);
    }
}
