using LogForesight.Sql;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// docs/FEEDBACK-3-PLAN.md #2：多台 Sentinel 平行處理後，<see cref="BatchRunRecorder"/> 的計數
/// 方法可能被多個平行執行的 Task 同時呼叫——驗證 lock 保護後併發呼叫不掉計數。
/// _run 的計數是屬性（不是欄位），無法用 Interlocked，這正是要驗證 lock 確實生效的理由。
/// </summary>
public class BatchRunRecorderConcurrencyTests
{
    [Fact]
    public void RecordDayAnalyzed並發呼叫不掉計數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>());

        const int iterations = 500;
        Parallel.For(0, iterations, _ => recorder.RecordDayAnalyzed());
        recorder.Finish(0);

        var run = store.GetRun(recorder.RunId);
        Assert.NotNull(run);
        Assert.Equal(iterations, run!.DaysAnalyzed);
    }

    [Fact]
    public void RecordAiCall並發呼叫成功與失敗計數皆不掉數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>());

        const int successCount = 300;
        const int failureCount = 200;
        Parallel.Invoke(
            () => Parallel.For(0, successCount, _ => recorder.RecordAiCall(success: true)),
            () => Parallel.For(0, failureCount, _ => recorder.RecordAiCall(success: false)));
        recorder.Finish(0);

        var run = store.GetRun(recorder.RunId);
        Assert.NotNull(run);
        Assert.Equal(successCount + failureCount, run!.AiCalls);
        Assert.Equal(failureCount, run.AiFailures);
    }
}
