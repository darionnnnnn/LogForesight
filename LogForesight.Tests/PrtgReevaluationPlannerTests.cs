using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

public class PrtgReevaluationPlannerTests
{
    private static BatchRun CreateRun(
        DateTime startedAt,
        string trigger = "schedule",
        string? jobType = null,
        DateTime? finishedAt = null,
        bool interrupted = false,
        List<PrtgDayStat>? prtgDays = null,
        long runId = 1)
    {
        return new BatchRun
        {
            RunId = runId,
            StartedAt = startedAt,
            Trigger = trigger,
            JobType = jobType,
            FinishedAt = finishedAt ?? startedAt.AddMinutes(30),
            Interrupted = interrupted,
            PrtgDays = prtgDays
        };
    }

    private static PrtgDayStat CreateStat(
        DateTime date,
        bool needsReeval,
        string outcome = BatchRun.PrtgOutcomeSuccess,
        string? note = null)
    {
        return new PrtgDayStat(
            Date: date,
            Outcome: outcome,
            Findings: 0,
            AttributedHosts: 0,
            MapAvailable: true,
            TriggerHosts: 0,
            TargetSensors: 0,
            FailedSensors: 0,
            Note: note,
            NeedsReevaluation: needsReeval);
    }

    [Fact]
    public void Select_某日NeedsReevaluation為True_入選()
    {
        var today = new DateTime(2026, 10, 20);
        var targetDate = today.AddDays(-3);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2), prtgDays: new List<PrtgDayStat>
            {
                CreateStat(targetDate, needsReeval: true)
            })
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.Contains(targetDate, selected);
    }

    [Fact]
    public void Select_同一日較晚一趟NeedsReevaluation為False_不入選()
    {
        var today = new DateTime(2026, 10, 20);
        var targetDate = today.AddDays(-3);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2).AddHours(10), runId: 1, prtgDays: new List<PrtgDayStat>
            {
                CreateStat(targetDate, needsReeval: true)
            }),
            CreateRun(today.AddDays(-2).AddHours(14), runId: 2, prtgDays: new List<PrtgDayStat>
            {
                CreateStat(targetDate, needsReeval: false)
            })
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.DoesNotContain(targetDate, selected);
    }

    [Fact]
    public void Select_同一日較早False較晚True_入選()
    {
        var today = new DateTime(2026, 10, 20);
        var targetDate = today.AddDays(-3);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2).AddHours(10), runId: 1, prtgDays: new List<PrtgDayStat>
            {
                CreateStat(targetDate, needsReeval: false)
            }),
            CreateRun(today.AddDays(-2).AddHours(14), runId: 2, prtgDays: new List<PrtgDayStat>
            {
                CreateStat(targetDate, needsReeval: true)
            })
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.Contains(targetDate, selected);
    }

    [Fact]
    public void Select_無PrtgDays但有ScheduleInterrupted_其前一日入選()
    {
        var today = new DateTime(2026, 10, 20);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2).AddHours(22), trigger: "schedule", interrupted: true, prtgDays: null)
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        // StartedAt=today-2 22:00 -> StartedAt.Date.AddDays(-1) == today-3
        Assert.Contains(today.AddDays(-3), selected);
    }

    [Fact]
    public void Select_AI排程的Interrupted_不當成排程日()
    {
        var today = new DateTime(2026, 10, 20);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2).AddHours(22), trigger: "schedule", jobType: BatchRun.JobTypeAi, interrupted: true, prtgDays: null)
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.DoesNotContain(today.AddDays(-3), selected);
    }

    [Fact]
    public void Select_手動觸發的Interrupted_不入選()
    {
        var today = new DateTime(2026, 10, 20);
        var runs = new[]
        {
            CreateRun(today.AddDays(-2).AddHours(22), trigger: "manual:admin", interrupted: true, prtgDays: null)
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.Empty(selected);
    }

    [Fact]
    public void Select_Today減1永不入選()
    {
        var today = new DateTime(2026, 10, 20);
        var yesterday = today.AddDays(-1);
        var runs = new[]
        {
            CreateRun(today.AddHours(2), trigger: "schedule", prtgDays: new List<PrtgDayStat>
            {
                CreateStat(yesterday, needsReeval: true)
            }),
            CreateRun(today, trigger: "schedule", interrupted: true, prtgDays: null)
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.DoesNotContain(yesterday, selected);
    }

    [Fact]
    public void Select_早於保留期不入選()
    {
        var today = new DateTime(2026, 10, 20);
        var date4DaysAgo = today.AddDays(-4);
        var runs = new[]
        {
            CreateRun(today.AddDays(-1), prtgDays: new List<PrtgDayStat>
            {
                CreateStat(date4DaysAgo, needsReeval: true)
            })
        };

        // 保留天數只有 3 天，today-4 已早於保留期起點
        var selected = PrtgReevaluationPlanner.Select(runs, today, retentionDays: 3);

        Assert.DoesNotContain(date4DaysAgo, selected);
    }

    [Fact]
    public void Select_回傳由近到遠()
    {
        var today = new DateTime(2026, 10, 20);
        var d2 = today.AddDays(-2);
        var d3 = today.AddDays(-3);
        var d5 = today.AddDays(-5);
        var runs = new[]
        {
            CreateRun(today.AddDays(-1), prtgDays: new List<PrtgDayStat>
            {
                CreateStat(d5, needsReeval: true),
                CreateStat(d2, needsReeval: true),
                CreateStat(d3, needsReeval: true)
            })
        };

        var selected = PrtgReevaluationPlanner.Select(runs, today, 180);

        Assert.Equal(new[] { d2, d3, d5 }, selected);
    }

    [Fact]
    public void Select_空輸入回空()
    {
        var today = new DateTime(2026, 10, 20);

        Assert.Empty(PrtgReevaluationPlanner.Select(Array.Empty<BatchRun>(), today, 180));
        Assert.Empty(PrtgReevaluationPlanner.Select(null!, today, 180));
    }

    [Fact]
    public void LastRunIssue_最近一趟有NoOutput日_回該日()
    {
        var today = new DateTime(2026, 10, 20);
        var targetStat = CreateStat(today.AddDays(-2), needsReeval: false, outcome: BatchRun.PrtgOutcomeNoOutput, note: "無產出");
        var runs = new[]
        {
            CreateRun(today.AddDays(-1), finishedAt: today.AddDays(-1).AddHours(1), prtgDays: new List<PrtgDayStat>
            {
                CreateStat(today.AddDays(-3), needsReeval: false, outcome: BatchRun.PrtgOutcomeSuccess),
                targetStat
            })
        };

        var issue = PrtgReevaluationPlanner.LastRunIssue(runs);

        Assert.NotNull(issue);
        Assert.Equal(targetStat.Date, issue!.Date);
        Assert.Equal(BatchRun.PrtgOutcomeNoOutput, issue.Outcome);
        Assert.Equal("無產出", issue.Note);
    }

    [Fact]
    public void LastRunIssue_全部Success_回Null()
    {
        var today = new DateTime(2026, 10, 20);
        var runs = new[]
        {
            CreateRun(today.AddDays(-1), finishedAt: today.AddDays(-1).AddHours(1), prtgDays: new List<PrtgDayStat>
            {
                CreateStat(today.AddDays(-2), needsReeval: false, outcome: BatchRun.PrtgOutcomeSuccess),
                CreateStat(today.AddDays(-3), needsReeval: false, outcome: BatchRun.PrtgOutcomeSuccess)
            })
        };

        var issue = PrtgReevaluationPlanner.LastRunIssue(runs);

        Assert.Null(issue);
    }

    [Fact]
    public void LastRunIssue_JobType為Ai被忽略()
    {
        var today = new DateTime(2026, 10, 20);
        var nonAiIssue = CreateStat(today.AddDays(-3), needsReeval: false, outcome: BatchRun.PrtgOutcomePartial, note: "部分失敗");
        var aiIssue = CreateStat(today.AddDays(-2), needsReeval: false, outcome: BatchRun.PrtgOutcomeFailed, note: "AI 失敗");

        var runs = new[]
        {
            // 較晚的 AI 執行
            CreateRun(today.AddHours(2), jobType: BatchRun.JobTypeAi, finishedAt: today.AddHours(3), prtgDays: new List<PrtgDayStat>
            {
                aiIssue
            }),
            // 較早的取數執行
            CreateRun(today.AddHours(1), jobType: null, finishedAt: today.AddHours(1).AddMinutes(30), prtgDays: new List<PrtgDayStat>
            {
                nonAiIssue
            })
        };

        var issue = PrtgReevaluationPlanner.LastRunIssue(runs);

        Assert.NotNull(issue);
        Assert.Equal(nonAiIssue.Date, issue!.Date);
        Assert.Equal(BatchRun.PrtgOutcomePartial, issue.Outcome);
    }
}
