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

    [Fact]
    public void AI趟只進AI那一路_不算進取數總耗時與起訖()
    {
        var ai = Run(At(7), At(9), Phase(RunTimingPhases.Ai, At(7), At(9)));
        ai.JobType = BatchRun.JobTypeAi;

        var timing = RunDayTimingBuilder.Build(new[] { Run(At(1), At(2), Phase(RunTimingPhases.Local, At(1), At(2))), ai })!;

        Assert.Equal(At(1), timing.FirstStartedAt);
        Assert.Equal(At(2), timing.LastFinishedAt);
        Assert.Equal(3600, timing.TotalSeconds);
        Assert.Equal(7200, timing.Branches.Single(b => b.Key == RunTimingPhases.Ai).Seconds);
    }
}
