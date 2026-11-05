using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>執行總表當天執行時間：聯集不重複算、異常中斷不計入、跳過與舊紀錄不算耗時。</summary>
public class RunDayTimingBuilderTests
{
    private static readonly DateTime Day = new(2026, 11, 2);

    private static DateTime At(int hour, int minute = 0) => Day.AddHours(hour).AddMinutes(minute);

    private static RunPhaseRecord Phase(string key, DateTime from, DateTime? to, string outcome = RunPhaseOutcomes.Success) =>
        new(key, from) { FinishedAt = to, Outcome = to == null ? null : outcome };

    private static BatchRun Run(DateTime from, DateTime? to, params RunPhaseRecord[] phases) => new()
    {
        StartedAt = from,
        FinishedAt = to,
        Phases = phases.Length == 0 ? null : phases.ToList()
    };

    [Fact]
    public void 結束早於開始的髒資料不算負時間()
    {
        var timing = RunDayTimingBuilder.Build(new[]
        {
            Run(At(2), At(1), Phase(RunTimingPhases.Local, At(2), At(1))),
            Run(At(3), At(3, 30), Phase(RunTimingPhases.Local, At(3), At(3, 30)))
        })!;

        Assert.Equal(1800, timing.TotalSeconds);
        Assert.Equal(1800, timing.Branches.Single().Seconds);
    }

    [Fact]
    public void 沒有任何趟回null()
    {
        Assert.Null(RunDayTimingBuilder.Build(Array.Empty<BatchRun>()));
    }

    [Fact]
    public void 兩個窗口_總耗時是實際執行不是起訖跨度_起訖看得到整段()
    {
        var timing = RunDayTimingBuilder.Build(new[]
        {
            Run(At(1), At(2), Phase(RunTimingPhases.Netiq, At(1), At(2))),
            Run(At(18), At(19), Phase(RunTimingPhases.Netiq, At(18), At(19)))
        })!;

        Assert.Equal(At(1), timing.FirstStartedAt);
        Assert.Equal(At(19), timing.LastFinishedAt);
        Assert.Equal(7200, timing.TotalSeconds);
        var netiq = timing.Branches.Single();
        Assert.Equal(At(1), netiq.From);
        Assert.Equal(At(19), netiq.To);
        Assert.Equal(7200, netiq.Seconds);
    }

    [Fact]
    public void 重疊的趟取聯集不相加()
    {
        var timing = RunDayTimingBuilder.Build(new[]
        {
            Run(At(1), At(3), Phase(RunTimingPhases.Local, At(1), At(3))),
            Run(At(2), At(4), Phase(RunTimingPhases.Local, At(2), At(4)))
        })!;

        Assert.Equal(3 * 3600, timing.TotalSeconds);
        Assert.Equal(3 * 3600, timing.Branches.Single().Seconds);
    }

    [Fact]
    public void 異常中斷不計入聯集_留開始與最後活動()
    {
        var interrupted = Run(At(1), At(9), Phase(RunTimingPhases.Local, At(1), At(2)), Phase(RunTimingPhases.Netiq, At(1), null));
        interrupted.Interrupted = true;

        var timing = RunDayTimingBuilder.Build(new[] { interrupted })!;

        Assert.Equal(At(1), timing.FirstStartedAt);
        Assert.Null(timing.TotalSeconds);
        Assert.Null(timing.LastFinishedAt);
        Assert.Equal(1, timing.InterruptedCount);
        Assert.Equal(At(2), timing.LastInterruptedActivityAt);
        Assert.Empty(timing.Branches);
    }

    [Fact]
    public void 跳過的階段與舊紀錄不算耗時_總耗時仍算得出()
    {
        var timing = RunDayTimingBuilder.Build(new[]
        {
            Run(At(1), At(2), Phase(RunTimingPhases.Prtg, At(1), At(1), RunPhaseOutcomes.Skipped)),
            Run(At(5), At(6))
        })!;

        Assert.Empty(timing.Branches);
        Assert.Equal(7200, timing.TotalSeconds);
    }

    private static BatchRun AiRun(DateTime from, DateTime? to, string? trigger, params RunPhaseRecord[] phases)
    {
        var run = Run(from, to, phases);
        run.JobType = BatchRun.JobTypeAi;
        run.Trigger = trigger!;
        return run;
    }

    [Fact]
    public void 有跟隨趟時補跑不進AI明細_明細不會比總計長()
    {
        var day = new DateTime(2026, 11, 1);
        var timing = RunDayTimingBuilder.Build(new[]
        {
            new BatchRun { Trigger = "schedule", StartedAt = day.AddHours(1), FinishedAt = day.AddHours(1).AddMinutes(10) },
            new BatchRun { Trigger = BatchRunTriggers.FetchFollowUp, JobType = BatchRun.JobTypeAi, StartedAt = day.AddHours(1).AddMinutes(10), FinishedAt = day.AddHours(1).AddMinutes(20) },
            new BatchRun { Trigger = "schedule", JobType = BatchRun.JobTypeAi, StartedAt = day.AddHours(14), FinishedAt = day.AddHours(15) }
        })!;

        Assert.Equal(600, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
        Assert.Equal(1, timing.OtherAiRuns);
    }

    [Fact]
    public void 非跟隨AI趟只進AI那一路_不算進取數總耗時與起訖_另列OtherAiRuns()
    {
        var ai = Run(At(7), At(9), Phase(RunTimingPhases.Ai, At(7), At(9)));
        ai.JobType = BatchRun.JobTypeAi;

        var timing = RunDayTimingBuilder.Build(new[] { Run(At(1), At(2), Phase(RunTimingPhases.Local, At(1), At(2))), ai })!;

        Assert.Equal(At(1), timing.FirstStartedAt);
        Assert.Equal(At(2), timing.LastFinishedAt);
        Assert.Equal(3600, timing.TotalSeconds);
        Assert.Equal(7200, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
        Assert.Equal(1, timing.OtherAiRuns);
        Assert.Equal(7200, timing.OtherAiSeconds);
    }

    [Fact]
    public void 取數44秒加跟隨AI87秒不重疊_總計131秒_最晚結束涵蓋AI()
    {
        var fetch = Run(At(1), At(1).AddSeconds(44), Phase(RunTimingPhases.Local, At(1), At(1).AddSeconds(44)));
        var ai = AiRun(At(1).AddSeconds(60), At(1).AddSeconds(147), BatchRunTriggers.FetchFollowUp,
            Phase(RunTimingPhases.Ai, At(1).AddSeconds(60), At(1).AddSeconds(147)));

        var timing = RunDayTimingBuilder.Build(new[] { fetch, ai })!;

        Assert.Equal(131, timing.TotalSeconds);
        Assert.Equal(44, timing.Branches.Single(b => b.Key == RunTimingPhases.Local).Seconds);
        Assert.Equal(87, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
        Assert.Equal(At(1), timing.FirstStartedAt);
        Assert.Equal(At(1).AddSeconds(147), timing.LastFinishedAt);
        Assert.Equal(0, timing.OtherAiRuns);
        Assert.Null(timing.OtherAiSeconds);
    }

    [Fact]
    public void 跟隨AI與取數重疊只算一次()
    {
        var fetch = Run(At(1), At(1, 10), Phase(RunTimingPhases.Local, At(1), At(1, 10)));
        var ai = AiRun(At(1, 5), At(1, 20), BatchRunTriggers.FetchFollowUp, Phase(RunTimingPhases.Ai, At(1, 5), At(1, 20)));

        var timing = RunDayTimingBuilder.Build(new[] { fetch, ai })!;

        Assert.Equal(20 * 60, timing.TotalSeconds);
        Assert.Equal(10 * 60, timing.Branches.Single(b => b.Key == RunTimingPhases.Local).Seconds);
        Assert.Equal(15 * 60, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
    }

    [Fact]
    public void 白天手動AI趟進OtherAiRuns_不進總計與起訖()
    {
        var fetch = Run(At(1), At(2), Phase(RunTimingPhases.Local, At(1), At(2)));
        var manual = AiRun(At(14), At(14, 30), "manual", Phase(RunTimingPhases.Ai, At(14), At(14, 30)));
        var schedule = AiRun(At(15), At(15, 10), BatchRunTriggers.Schedule, Phase(RunTimingPhases.Ai, At(15), At(15, 10)));

        var timing = RunDayTimingBuilder.Build(new[] { fetch, manual, schedule })!;

        Assert.Equal(3600, timing.TotalSeconds);
        Assert.Equal(At(2), timing.LastFinishedAt);
        Assert.Equal(2, timing.OtherAiRuns);
        Assert.Equal(2400, timing.OtherAiSeconds);
    }

    [Fact]
    public void 跟隨AI趟沒有Phases_AI路用整趟起訖()
    {
        var fetch = Run(At(1), At(2), Phase(RunTimingPhases.Local, At(1), At(2)));
        var ai = AiRun(At(2, 5), At(2, 35), BatchRunTriggers.FetchFollowUp);

        var timing = RunDayTimingBuilder.Build(new[] { fetch, ai })!;

        var branch = timing.Branches.Single(b => b.Key == RunTimingPhases.Ai);
        Assert.Equal(At(2, 5), branch.From);
        Assert.Equal(At(2, 35), branch.To);
        Assert.Equal(1800, branch.Seconds);
        Assert.Equal(5400, timing.TotalSeconds);
    }

    [Fact]
    public void 取數趟全無Phases_FetchRunsWithoutPhases等於趟數()
    {
        var timing = RunDayTimingBuilder.Build(new[] { Run(At(1), At(2)), Run(At(5), At(6)) })!;

        Assert.Equal(2, timing.FetchRuns);
        Assert.Equal(2, timing.FetchRunsWithoutPhases);
        Assert.Empty(timing.Branches);
    }

    [Fact]
    public void 新舊混合_只用有Phases的趟算分路_舊趟仍算總計()
    {
        var timing = RunDayTimingBuilder.Build(new[]
        {
            Run(At(1), At(2)),
            Run(At(5), At(6), Phase(RunTimingPhases.Local, At(5), At(5, 30)))
        })!;

        Assert.Equal(2, timing.FetchRuns);
        Assert.Equal(1, timing.FetchRunsWithoutPhases);
        Assert.Equal(1800, timing.Branches.Single().Seconds);
        Assert.Equal(7200, timing.TotalSeconds);
    }

    [Fact]
    public void 異常中斷的跟隨AI趟不計入總計()
    {
        var fetch = Run(At(1), At(2), Phase(RunTimingPhases.Local, At(1), At(2)));
        var ai = AiRun(At(2), At(9), BatchRunTriggers.FetchFollowUp, Phase(RunTimingPhases.Ai, At(2), At(3)));
        ai.Interrupted = true;

        var timing = RunDayTimingBuilder.Build(new[] { fetch, ai })!;

        Assert.Equal(3600, timing.TotalSeconds);
        Assert.Equal(At(2), timing.LastFinishedAt);
        Assert.Equal(1, timing.InterruptedCount);
    }

    [Fact]
    public void PRTG含補數值與子階段_聯集且逐項列出()
    {
        // 站台實況：手動趟的補數值跑了 1,748 秒，只認 prtg 階段會在 PRTG 欄顯示「—」
        var run = Run(At(8), At(8, 30),
            Phase(RunTimingPhases.Local, At(8), At(8).AddSeconds(5)),
            Phase(RunTimingPhases.Netiq, At(8), At(8).AddSeconds(28)),
            Phase(RunTimingPhases.Prtg, At(8), At(8, 1)),
            Phase(RunTimingPhases.PrtgSync, At(8), At(8).AddSeconds(36)),
            Phase(RunTimingPhases.PrtgValueFill, At(8, 1), At(8, 1).AddSeconds(1748)));

        var timing = RunDayTimingBuilder.Build(new[] { run })!;

        var prtg = timing.Branches.Single(b => b.Key == RunTimingPhases.Prtg);
        Assert.Equal(60 + 1748, prtg.Seconds);
        Assert.Equal(new[] { RunTimingPhases.PrtgSync, RunTimingPhases.PrtgValueFill }, prtg.SubPhases.Select(p => p.Key));
        Assert.Equal(1748, prtg.SubPhases.Single(p => p.Key == RunTimingPhases.PrtgValueFill).Seconds);
        Assert.Equal(5, timing.Branches.Single(b => b.Key == RunTimingPhases.Local).Seconds);
        Assert.Equal(28, timing.Branches.Single(b => b.Key == RunTimingPhases.Netiq).Seconds);
        Assert.Empty(timing.Branches.Single(b => b.Key == RunTimingPhases.Local).SubPhases);
    }

    [Fact]
    public void 當天只有AI補跑趟_沒有取數趟()
    {
        var ai = AiRun(At(14), At(14, 30), "manual", Phase(RunTimingPhases.Ai, At(14), At(14, 30)));

        var timing = RunDayTimingBuilder.Build(new[] { ai })!;

        Assert.Null(timing.FirstStartedAt);
        Assert.Null(timing.TotalSeconds);
        Assert.Equal(1, timing.OtherAiRuns);
        Assert.Equal(1800, timing.OtherAiSeconds);
        Assert.Equal(0, timing.FetchRuns);
        Assert.Equal(1800, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
    }
}
