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
    public void NextEmptyRecheckAt_上一趟結束後60分()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2, minutes: 0);
        Assert.Equal(Today.AddHours(2), ScheduleCalculator.NextEmptyRecheckAt(start.AddMinutes(30), w, new[] { run }));
    }

    [Fact]
    public void NextEmptyRecheckAt_候選已過_取現在()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var now = Today.AddHours(2).AddMinutes(30);
        Assert.Equal(now, ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2, minutes: 0) }));
    }

    [Fact]
    public void NextEmptyRecheckAt_距實例結束不足10分_回null_剛好10分仍回()
    {
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2, minutes: 0);
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
        var two = new[] { Run(start, start, netiqFailed: 1), Run(start.AddMinutes(30), start, emptyPending: 2, minutes: 0) };
        Assert.NotNull(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(2), w, two));
        var three = two.Append(Run(start.AddMinutes(90), start, emptyPending: 2, minutes: 0)).ToArray();
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(3), w, three));
    }

    [Fact]
    public void NextEmptyRecheckAt_被停止或異常中斷或不在窗口或沒有趟_回null()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var now = Today.AddHours(2);
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2, minutes: 0, stopped: true) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(start, start, emptyPending: 2, minutes: 0, interrupted: true) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(8), w, new[] { Run(start, start, emptyPending: 2, minutes: 0) }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, Array.Empty<BatchRun>()));
        // 別的實例（昨天）的趟不算
        var yesterdayStart = start.AddDays(-1);
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { Run(yesterdayStart, yesterdayStart, emptyPending: 2, minutes: 0) }));
    }

    // ── 畫面承諾的時刻與實際觸發是同一個判定 ───────────────────────────────

    [Fact]
    public void NextEmptyRecheckAt_上一趟還留著失敗_不承諾時刻_與RunToContinue一致()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        // 失敗＋待複查，實例內已 2 趟：不會再跑，也不能寫出一個時刻
        var first = Run(start, start, netiqFailed: 1, emptyPending: 2);
        var second = Run(start.AddMinutes(30), start, netiqFailed: 1, emptyPending: 2);
        var now = Today.AddHours(3);
        Assert.Null(ScheduleCalculator.RunToContinue(now, w, new[] { first, second }));
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(now, w, new[] { first, second }));
        // 只有 1 趟且留著失敗：接下來是失敗續跑，複查時刻還不知道
        Assert.Null(ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(1).AddMinutes(25), w, new[] { first }));
        // 上一趟還在跑：以現在估
        var running = Run(start, start, finished: false);
        Assert.Equal(Today.AddHours(2).AddMinutes(30), ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(1).AddMinutes(30), w, new[] { running }));
    }

    [Fact]
    public void 空資料待複查不受每日自動重查上限限制_其他狀態照舊()
    {
        var at = DateTime.UtcNow;
        Assert.False(NetiqAttemptStatus.AutoRetryExhausted(new NetiqSourceAttempt.AttemptHistory(NetiqAttemptStatus.EmptyOnce, 0, 5, at)));
        Assert.True(NetiqAttemptStatus.AutoRetryExhausted(new NetiqSourceAttempt.AttemptHistory(NetiqAttemptStatus.Failed, 0, NetiqAttemptStatus.MaxAutoRetriesPerDay, at)));
        Assert.False(NetiqAttemptStatus.AutoRetryExhausted(new NetiqSourceAttempt.AttemptHistory(NetiqAttemptStatus.Failed, 0, NetiqAttemptStatus.MaxAutoRetriesPerDay - 1, at)));
    }

    [Theory]
    [InlineData("01:00", "07:00", 5, 0)]     // 一般窗口：今天那一列
    [InlineData("01:00", "07:00", 10, 0)]    // 不在窗口內：今天
    [InlineData("22:00", "06:00", 23, 0)]    // 跨午夜窗口，午夜前：開跑日＝今天
    [InlineData("22:00", "06:00", 25, 0)]    // 過了午夜（隔天 01:00）：仍是開跑日那一列，不是「今天」
    [InlineData("22:00", "06:00", 31, 1)]    // 隔天 07:00 窗口已結束：回到當天
    public void 進行中的那一天_跨午夜窗口過了午夜仍是開跑日(string start, string end, int hoursFromToday, int expectedOffset)
    {
        Assert.Equal(Today.AddDays(expectedOffset), ScheduleCalculator.LiveRunDay(Today.AddHours(hoursFromToday), Windows(start, end)));
    }

    // ── ExpectedLastDataDay：報表資料完整度「應有資料的最後一天」 ──────────

    [Theory]
    [InlineData(5, -2)]    // 01:00～07:00 窗口，05:00 還沒跑完：最近結束的是昨天開跑那一個 → 前天
    [InlineData(7, -1)]    // 07:00 整窗口結束 → 昨天
    [InlineData(10, -1)]
    [InlineData(0, -2)]    // 午夜剛過
    public void 應有資料日_一般窗口_跑完才算到昨天(int hour, int expectedOffset)
    {
        var now = Today.AddHours(hour);
        Assert.Equal(Today.AddDays(expectedOffset), ScheduleCalculator.ExpectedLastDataDay(now, true, Windows("01:00", "07:00")));
    }

    [Theory]
    [InlineData(10, -2)]   // 22:00～06:00：昨晚 22:00 開跑的實例目標日＝前天
    [InlineData(23, -2)]   // 今晚 22:00 的實例還沒結束
    [InlineData(30, -1)]   // 隔天 06:00 後，今晚那個實例結束 → 目標日＝今天的昨天（相對隔天是前天）
    public void 應有資料日_跨午夜窗口_目標日是開跑日的昨天(int hoursFromToday, int expectedOffsetFromToday)
    {
        var now = Today.AddHours(hoursFromToday);
        Assert.Equal(Today.AddDays(expectedOffsetFromToday), ScheduleCalculator.ExpectedLastDataDay(now, true, Windows("22:00", "06:00")));
    }

    [Fact]
    public void 應有資料日_排程未啟用或窗口壞掉_退回昨天()
    {
        var now = Today.AddHours(5);
        Assert.Equal(Today.AddDays(-1), ScheduleCalculator.ExpectedLastDataDay(now, false, Windows("01:00", "07:00")));
        Assert.Equal(Today.AddDays(-1), ScheduleCalculator.ExpectedLastDataDay(now, true, Windows("x", "07:00")));
    }

    // ── RunToContinue：只留待複查 ──────────────────────────────────────────

    [Fact]
    public void 續跑_只留待複查_59分不續跑_60分整續跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 2, minutes: 0);
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(1).AddMinutes(59), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(Today.AddHours(2), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(Today.AddHours(2).AddMinutes(1), w, new[] { run }));
    }

    [Fact]
    public void NextEmptyRecheckAt_第一趟跑很久_從結束起算不是開跑()
    {
        // 3000 台第一趟 01:00 開跑、03:00 結束：空資料嘗試散在 01:00～03:00，結束後立刻複查只會全部延後、白跑一趟
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var run = Run(start, start, emptyPending: 500, minutes: 120);
        Assert.Equal(Today.AddHours(4), ScheduleCalculator.NextEmptyRecheckAt(Today.AddHours(3), w, new[] { run }));
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(3).AddMinutes(59), w, new[] { run }));
        Assert.Same(run, ScheduleCalculator.RunToContinue(Today.AddHours(4), w, new[] { run }));
    }

    [Fact]
    public void 續跑_只留待複查_實例內已2趟_仍可跑第3趟_已3趟不再跑()
    {
        var w = Windows("01:00", "07:00");
        var start = Today.AddHours(1);
        var first = Run(start, start, netiqFailed: 3);
        var second = Run(start.AddMinutes(30), start, emptyPending: 2);
        // 第二趟 01:30 開跑、01:50 結束：結束＋60 分＝02:50 才複查
        Assert.Null(ScheduleCalculator.RunToContinue(Today.AddHours(2).AddMinutes(49), w, new[] { first, second }));
        Assert.Same(second, ScheduleCalculator.RunToContinue(Today.AddHours(2).AddMinutes(50), w, new[] { first, second }));
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
