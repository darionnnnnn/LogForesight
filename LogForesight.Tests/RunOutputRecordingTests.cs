using LogForesight.Core.Service;
using NLog;
using NLog.Config;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 執行輸出寫進執行紀錄（BatchRunRecorder.Message／RecordingRunConsole）與致命原因不遺失（BatchRunRecorder.Fatal）。
/// </summary>
public class RunOutputRecordingTests
{
    public RunOutputRecordingTests()
    {
        LogManager.Configuration ??= new LoggingConfiguration();
    }

    private static List<BatchRunLog> OutputLogs(BatchRunStore store, long runId) =>
        store.GetLogs(runId).Where(l => l.Logger == "Output").ToList();

    [Fact]
    public void 寫三行讀回恰好三列Output()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            recorder.Message("第一行");
            recorder.Message("第二行");
            recorder.Message("第三行");
            recorder.Finish(0);
        }

        var logs = OutputLogs(store, runId);
        Assert.Equal(new[] { "第一行", "第二行", "第三行" }, logs.Select(l => l.Message).ToArray());
        Assert.All(logs, l => Assert.Equal("Info", l.Level));
    }

    [Fact]
    public void 超過上限保留前1500行與最後500行並註明略過行數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            for (var i = 1; i <= 2100; i++) recorder.Message(i.ToString());
            recorder.Finish(0);
        }

        var messages = OutputLogs(store, runId).Select(l => l.Message).ToList();
        Assert.Equal(2001, messages.Count);
        Assert.Equal(Enumerable.Range(1, 1500).Select(i => i.ToString()), messages.Take(1500));
        // 真正沒寫進紀錄的是第 1501～1600 行：略過 100 行（不是超出上限的 600 行，其中 500 行在尾段寫出）
        Assert.Equal("（中間略過 100 行輸出）", messages[1500]);
        Assert.Equal(Enumerable.Range(1601, 500).Select(i => i.ToString()), messages.Skip(1501));
    }

    [Fact]
    public void 未呼叫Finish只經Dispose也會寫出尾段且只寫一次()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>());
        runId = recorder.RunId;
        for (var i = 1; i <= 1600; i++) recorder.Message(i.ToString());
        recorder.Dispose();
        recorder.Dispose();

        var messages = OutputLogs(store, runId).Select(l => l.Message).ToList();
        Assert.Equal(1600, messages.Count);
        Assert.Equal("1600", messages[^1]);
    }

    [Fact]
    public void 空白行不記()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            recorder.Message("");
            recorder.Message("   ");
            recorder.Message("\n");
            recorder.Message("有內容");
            recorder.Finish(0);
        }

        var logs = OutputLogs(store, runId);
        Assert.Single(logs);
        Assert.Equal("有內容", logs[0].Message);
    }

    [Fact]
    public void 兩個Recorder並存時輸出各進自己那趟()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        using var a = new BatchRunRecorder(store, "host-a", Array.Empty<string>());
        using var b = new BatchRunRecorder(store, "host-b", Array.Empty<string>());
        a.Message("a1");
        b.Message("b1");
        a.Message("a2");
        b.Message("b2");
        b.Message("b3");
        a.Finish(0);
        b.Finish(0);

        Assert.Equal(new[] { "a1", "a2" }, OutputLogs(store, a.RunId).Select(l => l.Message).ToArray());
        Assert.Equal(new[] { "b1", "b2", "b3" }, OutputLogs(store, b.RunId).Select(l => l.Message).ToArray());
    }

    [Fact]
    public void Fatal寫一列致命紀錄並計入錯誤數()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            recorder.Fatal(new InvalidOperationException("boom"));
        }
        // 模擬最外層 catch：recorder 已 Dispose 後才 Log.Fatal，不得再多出一列
        LogManager.GetLogger("RunOutputFatalTest").Fatal(new InvalidOperationException("boom"), "執行失敗");

        var fatal = store.GetLogs(runId).Where(l => l.Level == "Fatal").ToList();
        Assert.Single(fatal);
        Assert.Contains("boom", fatal[0].Message);
        Assert.Equal("Orchestrator", fatal[0].Logger);
        Assert.Contains("InvalidOperationException", fatal[0].ExceptionText);
        Assert.Equal(1, store.GetRun(runId)!.ErrorCount);
    }

    private sealed class CapturingConsole : IRunConsole
    {
        public List<string> Lines { get; } = new();
        public void WriteLine(string message = "") => Lines.Add(message);
    }

    [Fact]
    public void RecordingRunConsole同時轉給Inner與Recorder()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        var inner = new CapturingConsole();

        long runId;
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            runId = recorder.RunId;
            // 完整限定名：測試專案的 NetiqPipelineBaselineTests.cs 另有同名的測試用替身
            new LogForesight.Core.Service.RecordingRunConsole(inner, recorder).WriteLine("x");
            recorder.Finish(0);
        }

        Assert.Equal(new[] { "x" }, inner.Lines);
        var logs = OutputLogs(store, runId);
        Assert.Single(logs);
        Assert.Equal("x", logs[0].Message);
    }
}
