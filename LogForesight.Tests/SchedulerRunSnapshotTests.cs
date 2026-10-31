using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// <see cref="SchedulerRunState.Snapshot"/>：狀態 API 與全站告示讀同一份鎖內複製的快照，
/// 不逐屬性讀取（逐屬性讀會與 EndRun 的整批清空交錯成混合狀態）。
/// </summary>
public class SchedulerRunSnapshotTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null, "找不到 LogForesight.sln，無法定位專案根目錄");
        return dir!.FullName;
    }

    /// <summary>取出以 signature 開頭的方法本文（從第一個 { 配對到對應的 }）。</summary>
    private static string MethodBody(string relPath, string signature)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), relPath));
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {signature}");
        var open = text.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }

        throw new InvalidOperationException("大括號未配對");
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Snapshot_執行中_各欄位與屬性一致()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportMessage("訊息");
        state.ReportProgress("netiq", 3, 10);
        state.ReportProgress("local", 1, 2);
        state.ReportProgress("prtg-values", 4, 5);
        state.ReportProgress(RunPhases.PrtgDateRange, 7, 2);

        var s = state.Snapshot();

        Assert.True(s.IsRunning);
        Assert.Equal(state.Trigger, s.Trigger);
        Assert.Equal(state.StartedAt, s.StartedAt);
        Assert.Equal("訊息", s.LatestMessage);
        Assert.Equal((state.ProgressPhase, state.ProgressDone, state.ProgressTotal), (s.ProgressPhase, s.ProgressDone, s.ProgressTotal));
        Assert.Equal((state.LocalProgressPhase, state.LocalProgressDone, state.LocalProgressTotal), (s.LocalProgressPhase, s.LocalProgressDone, s.LocalProgressTotal));
        Assert.Equal((state.PrtgProgressPhase, state.PrtgProgressDone, state.PrtgProgressTotal), (s.PrtgProgressPhase, s.PrtgProgressDone, s.PrtgProgressTotal));
        Assert.Equal(7, s.PrtgDayCount);
        Assert.Equal(2, s.PrtgDayIndex);
        Assert.Equal((s.ActivityPhase, s.ActivityDone, s.ActivityTotal), state.LatestActivity());
        Assert.Equal("netiq", s.ActivityPhase);
    }

    [Fact]
    public void Snapshot_EndRun後_執行狀態全部清空()
    {
        var state = new SchedulerRunState();
        Assert.True(state.TryBeginRun("schedule", out _));
        state.ReportProgress("netiq", 3, 10);
        state.ReportProgress("local", 1, 2);
        state.ReportProgress("prtg-values", 4, 5);

        var outcome = new RunOutcome(true, null, "schedule", DateTime.Now);
        state.EndRun(outcome);
        var s = state.Snapshot();

        Assert.False(s.IsRunning);
        Assert.Null(s.Trigger);
        Assert.Null(s.StartedAt);
        Assert.Null(s.ProgressPhase);
        Assert.Null(s.LocalProgressPhase);
        Assert.Null(s.PrtgProgressPhase);
        Assert.Null(s.ActivityPhase);
        Assert.Same(outcome, s.LastOutcome);
    }

    [Fact]
    public void 結構守門_ScheduleController的GetStatus只讀一次Snapshot()
    {
        var body = MethodBody(Path.Combine("LogForesight.Web", "Controllers", "Api", "ScheduleController.cs"),
            "ApiResponse<ScheduleStatusDto> GetStatus(");
        Assert.Equal(1, Count(body, "_runState."));
        Assert.Contains("_runState.Snapshot()", body);
    }

    [Fact]
    public void 結構守門_RunActivity的Get取數分支只讀一次Snapshot()
    {
        var body = MethodBody(Path.Combine("LogForesight.Web", "Controllers", "Api", "DashboardController.cs"),
            "ApiResponse<RunActivityDto> Get(");
        Assert.Equal(1, Count(body, "_runState."));
        Assert.Contains("_runState.Snapshot()", body);
    }
}
