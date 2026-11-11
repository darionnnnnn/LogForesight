using System.Text.Json;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 70 輪 B-1：NetIQ 空資料待複查在同一窗口內複查——執行紀錄欄位、<see cref="RunResume"/>、
/// <see cref="ScheduleCalculator.NextEmptyRecheckAt"/> 與 <see cref="ScheduleCalculator.RunToContinue"/> 的間隔與趟數上限。
/// 管線端的延後條件見 <c>NetiqPipelineBaselineTests</c>。
/// </summary>
public class EmptyRecheckTests
{
    private static readonly DateTime Today = new(2026, 11, 5);

    private static List<ScheduleWindow> Windows(string start, string end) => new() { new ScheduleWindow { Start = start, End = end } };

    private static BatchRun Run(DateTime start, DateTime? instance, int? emptyPending = null, int netiqFailed = 0, int minutes = 20,
        bool stopped = false, bool interrupted = false, bool finished = true) => new()
    {
        RunId = start.Ticks, StartedAt = start, FinishedAt = finished ? start.AddMinutes(minutes) : null,
        Trigger = BatchRunTriggers.Schedule, WindowInstanceStart = instance,
        NetiqDaysFailed = netiqFailed, NetiqEmptyPending = emptyPending, Stopped = stopped, Interrupted = interrupted
    };

    // ── RunResume ──────────────────────────────────────────────────────────

    [Fact]
    public void RunResume_待複查有值_算有事做且只剩待複查_摘要有字樣()
    {
        var resume = RunResume.Of(new BatchRun { NetiqEmptyPending = 2 });
        Assert.True(resume.HasWork);
        Assert.True(resume.OnlyEmptyPending);
        Assert.Contains("NetIQ 待複查 2 主機日", resume.Describe());
    }

    [Fact]
    public void RunResume_欄位為null或0_不算有事做_摘要沒有字樣()
    {
        foreach (var pending in new int?[] { null, 0 })
        {
            var resume = RunResume.Of(new BatchRun { NetiqEmptyPending = pending });
            Assert.False(resume.HasWork);
            Assert.False(resume.OnlyEmptyPending);
            Assert.DoesNotContain("待複查", resume.Describe());
        }
    }

    [Fact]
    public void RunResume_待複查加其他事_不算只剩待複查()
    {
        Assert.False(RunResume.Of(new BatchRun { NetiqEmptyPending = 2, NetiqDaysFailed = 1 }).OnlyEmptyPending);
        Assert.True(RunResume.Of(new BatchRun { NetiqEmptyPending = 2, NetiqDaysFailed = 1 }).HasWork);
        Assert.False(RunResume.Of(new BatchRun
        {
            NetiqEmptyPending = 2,
            Phases = new() { new RunPhaseRecord(RunTimingPhases.Local, Today) { Outcome = RunPhaseOutcomes.Failed } }
        }).OnlyEmptyPending);
        Assert.False(RunResume.Of(new BatchRun { NetiqEmptyPending = 2, PrtgOutcome = BatchRun.PrtgOutcomeFailed }).OnlyEmptyPending);
        Assert.False(RunResume.Of(new BatchRun
        {
            NetiqEmptyPending = 2,
            PrtgDays = new() { new PrtgDayStat(Today.AddDays(-1), "partial", 0, 0, true, 0, 0, 5) }
        }).OnlyEmptyPending);
        Assert.Contains("NetIQ 待複查 2 主機日", RunResume.Of(new BatchRun { NetiqEmptyPending = 2, NetiqDaysFailed = 1 }).Describe());
    }

    // ── NextEmptyRecheckAt ─────────────────────────────────────────────────

    [Fact]
    public void NextEmptyRecheckAt_上一趟開跑後60分()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2);
        Assert.Equal(Today.AddHours(2), ScheduleCalculator.NextEmptyRecheckAt(start.AddMinutes(30), w, new[] { run }));
    }

    [Fact]
    public void NextEmptyRecheckAt_候選已過_取現在()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var now = Today.AddHours(2).AddMinutes(30);
        Assert.Equal(now, ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2) }));
    }

    [Fact]
    public void NextEmptyRecheckAt_距實例結束不足10分_回null_剛好10分仍回()
    {
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2);
        var now = start.AddMinutes(30);
        // 窗口 01:00～02:05：候選 02:00 距結束 5 分
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, Windows("01:00", "02:05"), new[] { run }));
        // 窗口 01:00～02:10：候選 02:00 距結束剛好 10 分
        Assert.Equal(Today.AddHours(2), ScheduleCalculator.NextEmptyRecheckAt(now, Windows("01:00", "02:10"), new[] { run }));
    }

    [Fact]
    public void NextEmptyRecheckAt_趟數上限是3趟()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var two = new[] { Run(start, start, netiqFailed: 1), Run(start.AddMinutes(30), start, emptyPending: 2) };
        Assert.NotNull(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(2), w, two));
        var three = two.Append(Run(start.AddMinutes(90), start, emptyPending: 2)).ToArray();
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(3), w, three));
    }

    [Fact]
    public void NextEmptyRecheckAt_被停止或異常中斷或不在窗口或沒有趟_回null()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var now = Today.AddHours(2);
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2, stopped: true) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2, interrupted: true) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(8), w, new[] { Run(start, start, emptyPending: 2) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, Array.Empty<BatchRun>()));
        // 別的實例（昨天）的趟不算
        var yesterdayStart = start.AddDays(-1);
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(yesterdayStart, yesterdayStart, emptyPending: 2) }));
    }

    // ── RunToContinue：只留待複查 ──────────────────────────────────────────

    [Fact]
    public void 續跑_只留待複查_59分不續跑_60分整續跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2);
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(1).AddMinutes(59), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(Today.AddHours(2), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(Today.AddHours(2).AddMinutes(1), w, new[] { run }));
    }

    [Fact]
    public void 續跑_只留待複查_實例內已2趟_仍可跑第3趟_已3趟不再跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var first = Run(start, start, netiqFailed: 3);
        var second = Run(start.AddMinutes(30), start, emptyPending: 2);
        Assert.Same(second, ScheduleCalculator.RunToContinue(Today.AddHours(2).AddMinutes(30), w, new[] { first, second }));
        var third = Run(start.AddMinutes(90), start, emptyPending: 2);
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(4), w, new[] { first, second, third }));
    }

    [Fact]
    public void 續跑_只留待複查_上一趟未結束_被停止_異常中斷_不續跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var now = Today.AddHours(3);
        Assert.Null(ScheduleCalculator.RunToContinue(now, w, new[] { Run(start, start, emptyPending: 2, finished: false) }));
        Assert.Null(ScheduleCalculator.RunToContinue(now, w, new[] { Run(start, start, emptyPending: 2, stopped: true) }));
        Assert.Null(ScheduleCalculator.RunToContinue(now, w, new[] { Run(start, start, emptyPending: 2, interrupted: true) }));
    }

    [Fact]
    public void 續跑_只留待複查_複查時刻距窗口結束不足10分_不續跑()
    {
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2);
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(2), Windows("01:00", "02:05"), new[] { run }));
    }

    // ── RunToContinue：失敗加待複查沿用 10 分規則 ──────────────────────────

    [Fact]
    public void 續跑_失敗加待複查_沿用10分規則_實例內已2趟不再跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2, netiqFailed: 1);
        Assert.Null(ScheduleCalculator.RunToContinue(start.AddMinutes(9), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(start.AddMinutes(10), w, new[] { run }));

        var earlier = Run(start.AddMinutes(-40), start, netiqFailed: 2);
        var latest = Run(start, start, emptyPending: 2, netiqFailed: 1);
        Assert.Null(ScheduleCalculator.RunToContinue(start.AddMinutes(70), w, new[] { earlier, latest }));
    }

    // ── 執行紀錄欄位 ───────────────────────────────────────────────────────

    [Fact]
    public void 執行紀錄_RecordNetiqOutcome寫入待複查數_讀回相同()
    {
        using var fixture = new EfSqliteFixture();
        var store = new BatchRunStore(fixture.LogStore("runs"), fixture.LogStore("run_logs"));
        using (var recorder = new BatchRunRecorder(store, "test-host", Array.Empty<string>()))
        {
            recorder.RecordNetiqOutcome(4, 0, 1, emptyPending: 3);
            recorder.Finish(0);
        }
        var run = store.GetRun(1);
        Assert.NotNull(run);
        Assert.Equal(3, run!.NetiqEmptyPending);
        Assert.Equal(3, RunResume.Of(run).EmptyPendingDays);
    }

    [Fact]
    public void 執行紀錄_序列化往返不變_舊JSON行沒有這個欄位讀回null()
    {
        var json = JsonSerializer.Serialize(new BatchRun { RunId = 7, NetiqEmptyPending = 5 });
        Assert.Equal(5, JsonSerializer.Deserialize<BatchRun>(json)!.NetiqEmptyPending);

        const string oldJson = "{\"RunId\":42,\"HostName\":\"TEST-HOST\",\"StartedAt\":\"2026-09-04T08:00:00\"," +
                               "\"FinishedAt\":\"2026-09-04T08:15:00\",\"ExitCode\":0,\"NetiqDaysFailed\":0}";
        Assert.Null(JsonSerializer.Deserialize<BatchRun>(oldJson)!.NetiqEmptyPending);

        using var fixture = new EfSqliteFixture();
        var runsLogStore = fixture.LogStore("runs");
        runsLogStore.AppendLine(oldJson);
        var store = new BatchRunStore(runsLogStore, fixture.LogStore("run_logs"));
        Assert.Null(store.GetRun(42)!.NetiqEmptyPending);
    }

    [Fact]
    public void 續跑里程碑文字_只留待複查時寫空資料複查_否則寫實際趟次()
    {
        var start = Today.AddHours(1);
        var window = ((DateTime, DateTime)?)(start, start.AddHours(6));
        var pendingOnly = Run(start, start, emptyPending: 2);
        pendingOnly.RunId = 9;
        var text = LogForesight.Web.Services.SchedulerHostedService.ComposeContinueRequest(pendingOnly, window, 3).WindowNote;
        Assert.Contains("本趟為窗口內空資料複查（上一趟 #9 留下：", text);
        Assert.Contains("NetIQ 待複查 2 主機日", text);

        var failed = Run(start, start, netiqFailed: 2, emptyPending: 1);
        failed.RunId = 9;
        var failedText = LogForesight.Web.Services.SchedulerHostedService.ComposeContinueRequest(failed, window, 3).WindowNote;
        Assert.Contains("本趟為窗口內續跑（第 3 趟，上一趟 #9", failedText);
    }

    [Fact]
    public void 續跑請求_只留待複查時只掃帳本主機_有失敗時掃全部()
    {
        var start = Today.AddHours(1);
        var window = ((DateTime, DateTime)?)(start, start.AddHours(6));
        var pendingOnly = LogForesight.Web.Services.SchedulerHostedService.ComposeContinueRequest(Run(start, start, emptyPending: 2), window, 2);
        Assert.True(pendingOnly.NetiqLedgerRetryOnly);
        Assert.Equal(BatchRunTriggers.Schedule, pendingOnly.Trigger);
        Assert.Equal(start, pendingOnly.WindowInstanceStart);

        var failed = LogForesight.Web.Services.SchedulerHostedService.ComposeContinueRequest(Run(start, start, netiqFailed: 1, emptyPending: 2), window, 2);
        Assert.False(failed.NetiqLedgerRetryOnly);
    }
}
