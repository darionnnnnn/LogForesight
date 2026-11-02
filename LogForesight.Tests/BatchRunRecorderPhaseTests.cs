using LogForesight.Core.Service;
using NLog;
using NLog.Config;
using Xunit;

namespace LogForesight.Tests;

/// <summary>執行紀錄階段計時：起訖、隱含結果、快照時機、結束後不再寫入。</summary>
public class BatchRunRecorderPhaseTests : IDisposable
{
    private readonly EfSqliteFixture _fixture = new();
    private readonly BatchRunStore _store;

    public BatchRunRecorderPhaseTests()
    {
        LogManager.Configuration ??= new LoggingConfiguration();
        _store = new BatchRunStore(_fixture.LogStore("runs"), _fixture.LogStore("run_logs"));
    }

    public void Dispose() => _fixture.Dispose();

    private BatchRun Latest(long runId) => _store.GetRecentRuns(1, null).Single(r => r.RunId == runId);

    [Fact]
    public void Complete寫入起訖與結果_第二次Complete無效()
    {
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        var phase = recorder.Phase(RunTimingPhases.Local);
        phase.Complete(RunPhaseOutcomes.Partial, "備註");
        phase.Complete(RunPhaseOutcomes.Success, "不該蓋掉");

        var record = Latest(recorder.RunId).Phases!.Single();
        Assert.Equal(RunTimingPhases.Local, record.Key);
        Assert.Equal(RunPhaseOutcomes.Partial, record.Outcome);
        Assert.Equal("備註", record.Note);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public void 未Complete就Dispose_取消時算停止()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>(), ct: cts.Token);
        recorder.Phase(RunTimingPhases.Netiq).Dispose();
        Assert.Equal(RunPhaseOutcomes.Stopped, Latest(recorder.RunId).Phases!.Single().Outcome);
    }

    [Fact]
    public void 未Complete就Dispose_未取消時算失敗()
    {
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        recorder.Phase(RunTimingPhases.Netiq).Dispose();
        Assert.Equal(RunPhaseOutcomes.Failed, Latest(recorder.RunId).Phases!.Single().Outcome);
    }

    [Fact]
    public void 頂層結束即可讀到_子階段要等結束列才寫出()
    {
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        recorder.Phase(RunTimingPhases.PrtgSync).Complete(RunPhaseOutcomes.Success);
        Assert.Null(Latest(recorder.RunId).Phases);

        recorder.Phase(RunTimingPhases.Local).Complete(RunPhaseOutcomes.Success);
        Assert.Equal(2, Latest(recorder.RunId).Phases!.Count);
        Assert.Null(Latest(recorder.RunId).FinishedAt);
    }

    [Fact]
    public void 結束列收掉還開著的階段_結束後的Complete不追加列也不清掉結束時間()
    {
        var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        var open = recorder.Phase(RunTimingPhases.Prtg);
        recorder.Finish(0);

        var finished = Latest(recorder.RunId);
        Assert.NotNull(finished.FinishedAt);
        Assert.Equal(RunPhaseOutcomes.Failed, finished.Phases!.Single().Outcome);

        open.Complete(RunPhaseOutcomes.Success);
        var late = recorder.Phase(RunTimingPhases.Dispatch);
        late.Complete(RunPhaseOutcomes.Success);

        var again = Latest(recorder.RunId);
        Assert.NotNull(again.FinishedAt);
        Assert.Single(again.Phases!);
        recorder.Dispose();
    }

    [Fact]
    public void 同鍵多實例各自配對()
    {
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        var first = recorder.Phase(RunTimingPhases.PrtgTriggered, "2026-11-01");
        var second = recorder.Phase(RunTimingPhases.PrtgTriggered, "2026-10-31");
        second.Complete(RunPhaseOutcomes.Partial);
        first.Complete(RunPhaseOutcomes.Success);
        recorder.Finish(0);

        var phases = Latest(recorder.RunId).Phases!;
        Assert.Equal(RunPhaseOutcomes.Success, phases.Single(p => p.Note == "2026-11-01").Outcome);
        Assert.Equal(RunPhaseOutcomes.Partial, phases.Single(p => p.Note == "2026-10-31").Outcome);
    }

    [Fact]
    public void 並行開關階段不擲例外且全數落在結束列()
    {
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
        Parallel.For(0, 20, _ => recorder.Phase(RunTimingPhases.Local).Complete(RunPhaseOutcomes.Success));
        recorder.Finish(0);

        var run = Latest(recorder.RunId);
        Assert.Equal(20, run.Phases!.Count);
        Assert.All(run.Phases!, p => Assert.Equal(RunPhaseOutcomes.Success, p.Outcome));
        Assert.NotNull(run.FinishedAt);
    }

    /// <summary>
    /// 死鎖迴歸：結束期間有晚到的階段呼叫，同時另一條執行緒在 scope 內一直 Warn。
    /// Phase／Complete 若在持 _countLock 時寫 log，就會與 NLog target（先持 target 鎖、再取 _countLock）反序而卡死。
    /// </summary>
    [Fact]
    public void 結束期間晚到的階段呼叫遇到並行Warn不死鎖()
    {
        var logger = NLog.LogManager.GetLogger("LogForesight.Tests.PhaseDeadlock");
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>());
            for (var i = 0; i < 1600; i++) recorder.Message("line " + i);   // 讓 Finish 有尾段要寫，拉長結束窗口
            using var stop = new CancellationTokenSource();
            using var go = new ManualResetEventSlim();
            var warner = Task.Run(() => { go.Wait(); while (!stop.IsCancellationRequested) { logger.Warn("w"); Thread.Sleep(1); } });
            var late = Task.Run(() => { go.Wait(); for (var i = 0; i < 40; i++) recorder.Phase(RunTimingPhases.Local).Complete(RunPhaseOutcomes.Success); });
            var finish = Task.Run(() => { go.Wait(); recorder.Finish(0); });
            go.Set();
            var completed = Task.WaitAll(new[] { finish, late }, TimeSpan.FromSeconds(20));
            stop.Cancel();
            warner.Wait(TimeSpan.FromSeconds(5));
            Assert.True(completed, $"第 {iteration} 輪：Finish 與晚到的階段呼叫沒有在 20 秒內完成（疑似死鎖）");
            Assert.NotNull(Latest(recorder.RunId).FinishedAt);
            Assert.All(Latest(recorder.RunId).Phases ?? new List<RunPhaseRecord>(), p => Assert.NotNull(p.FinishedAt));
            recorder.Dispose();
        }
    }

    [Fact]
    public void 傳入起點就寫進紀錄()
    {
        var started = DateTime.Now.AddMinutes(-3);
        using var recorder = new BatchRunRecorder(_store, "H", Array.Empty<string>(), startedAt: started);
        Assert.Equal(started, Latest(recorder.RunId).StartedAt);
    }

    [Fact]
    public void 舊紀錄沒有階段欄_讀出為null()
    {
        var run = System.Text.Json.JsonSerializer.Deserialize<BatchRun>("""{"RunId":1,"HostName":"H"}""")!;
        Assert.Null(run.Phases);
        Assert.Null(run.Snapshot().Phases);
    }
}
