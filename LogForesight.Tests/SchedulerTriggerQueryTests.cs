using LogForesight.Core.Configuration;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LogForesight.Tests;

public class SchedulerTriggerQueryTests
{
    private static PrtgDayStat Day(DateTime date, string outcome = BatchRun.PrtgOutcomeSuccess, int findings = 0) =>
        new(date, outcome, findings, 0, true, 0, 0, 0);

    [Fact]
    public void LastScheduledPrtgDay_只取已結束的排程取數_立即執行與AI不算_取最新資料日()
    {
        var d1 = new DateTime(2026, 10, 20);
        var d2 = new DateTime(2026, 10, 21);
        var runs = new[]
        {
            new BatchRun { RunId = 1, Trigger = "schedule", StartedAt = d1.AddDays(1).AddHours(1), FinishedAt = d1.AddDays(1).AddHours(2),
                PrtgDays = new() { Day(d1, findings: 3) } },
            new BatchRun { RunId = 2, Trigger = "schedule", StartedAt = d2.AddDays(1).AddHours(1), FinishedAt = d2.AddDays(1).AddHours(2),
                PrtgDays = new() { Day(d1), Day(d2, BatchRun.PrtgOutcomePartial, 5) } },
            new BatchRun { RunId = 3, Trigger = "manual:x", StartedAt = d2.AddDays(1).AddHours(9), FinishedAt = d2.AddDays(1).AddHours(10),
                PrtgDays = new() { Day(d2) } },
            new BatchRun { RunId = 4, Trigger = "schedule", JobType = BatchRun.JobTypeAi, StartedAt = d2.AddDays(1).AddHours(11),
                FinishedAt = d2.AddDays(1).AddHours(12), PrtgDays = new() { Day(d2) } },
            new BatchRun { RunId = 5, Trigger = "schedule", StartedAt = d2.AddDays(1).AddHours(13), FinishedAt = null,
                PrtgDays = new() { Day(d2) } }
        };

        var result = SchedulerHostedService.LastScheduledPrtgDay(runs);

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.RunId);
        Assert.Equal(d2, result.Value.Day.Date);
        Assert.Equal(BatchRun.PrtgOutcomePartial, result.Value.Day.Outcome);
        Assert.Equal(5, result.Value.Day.Findings);
    }

    [Fact]
    public void LastScheduledPrtgDay_沒有排程執行或沒有逐日統計時回null()
    {
        Assert.Null(SchedulerHostedService.LastScheduledPrtgDay(Array.Empty<BatchRun>()));
        Assert.Null(SchedulerHostedService.LastScheduledPrtgDay(new[]
        {
            new BatchRun { RunId = 1, Trigger = "schedule", StartedAt = DateTime.Today, FinishedAt = DateTime.Today, PrtgDays = null },
            new BatchRun { RunId = 2, Trigger = "manual:x", StartedAt = DateTime.Today, FinishedAt = DateTime.Today,
                PrtgDays = new() { Day(DateTime.Today.AddDays(-1)) } }
        }));
    }

    [Fact]
    public void GetScheduleTriggerTimes_AiScheduleRun_Excluded()
    {
        var time = new DateTime(2026, 9, 19, 22, 5, 0);
        var runs = new[]
        {
            new BatchRun { Trigger = "schedule", JobType = BatchRun.JobTypeAi, StartedAt = time }
        };

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>(), Array.Empty<ScheduleWindow>());

        Assert.DoesNotContain(time, result);
    }

    [Fact]
    public void GetScheduleTriggerTimes_FetchScheduleRun_Included()
    {
        var time = new DateTime(2026, 9, 19, 22, 5, 0);
        var runs = new[]
        {
            new BatchRun { Trigger = "schedule", JobType = null, StartedAt = time }
        };

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>(), Array.Empty<ScheduleWindow>());

        Assert.Contains(time, result);
    }

    [Fact]
    public void GetScheduleTriggerTimes_ManualRun_Excluded()
    {
        var time = new DateTime(2026, 9, 19, 22, 5, 0);
        var runs = new[]
        {
            new BatchRun { Trigger = "manual:x", JobType = null, StartedAt = time }
        };

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>(), Array.Empty<ScheduleWindow>());

        Assert.DoesNotContain(time, result);
    }

    [Fact]
    public void GetScheduleTriggerTimes_InMemoryScheduleAttempt_IncludedWhenPersistentRunsEmpty()
    {
        var runState = new SchedulerRunState();
        var before = DateTime.Now;
        var started = runState.TryBeginRun("schedule", out _);
        var after = DateTime.Now;
        Assert.True(started);

        var result = SchedulerHostedService.GetScheduleTriggerTimes(
            Enumerable.Empty<BatchRun>(),
            runState.RecentScheduleAttempts,
            Array.Empty<ScheduleWindow>()).ToList();

        Assert.Single(result);
        Assert.InRange(result[0], before, after);
    }

    [Fact]
    public void ShouldTriggerNow_AiScheduleRunInWindow_StillTriggersFetch()
    {
        var now = new DateTime(2026, 9, 19, 23, 0, 0);
        var windows = new[] { new ScheduleWindow { Start = "22:00", End = "06:00" } };
        var runs = new[]
        {
            new BatchRun { Trigger = "schedule", JobType = BatchRun.JobTypeAi, StartedAt = new DateTime(2026, 9, 19, 22, 5, 0) }
        };

        var triggerTimes = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>(), Array.Empty<ScheduleWindow>());
        var shouldTrigger = ScheduleCalculator.ShouldTriggerNow(now, windows, triggerTimes);

        Assert.True(shouldTrigger);
    }

    [Fact]
    public async Task SendTestAsync_WithDefaultToken_PassesTokenThatCanBeCanceled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lf-mail-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(dir, "test.db")}" }, dir);
            var mailState = new MailNotifyStateStore(backend.Blob("mail_notify_state"));
            var capturingSender = new CapturingMailSender();
            var freshness = new ScheduleFreshnessService(
                new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs")),
                new ScheduleOptionsStore(backend.Blob("schedule_options")));
            var mailService = new MailNotificationService(
                new FakeSystemSettingsStore(),
                capturingSender,
                new FakeHostStore(),
                new FakeUserStore(),
                new FakeUserGroupStore(),
                new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(),
                new FakeHandlingStore(),
                mailState,
                freshness,
                new WorkOrderOutboxStore(backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore());

            var spec = new SmtpConnectionSpec("smtp.example.com", 25, false, "user", null);
            await mailService.SendTestAsync(spec, "noreply@example.com", new List<string> { "admin@example.com" },
                "{site} {type}", "intro", ct: default);

            Assert.True(capturingSender.CapturedToken.CanBeCanceled);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class CapturingMailSender : ISmtpMailSender
    {
        public CancellationToken CapturedToken { get; private set; }

        public Task SendAsync(SmtpConnectionSpec connection, MailMessageSpec message, CancellationToken ct = default)
        {
            CapturedToken = ct;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(BatchRunTriggers.Schedule, null, true)]
    [InlineData(BatchRunTriggers.Schedule, BatchRun.JobTypeAi, false)]
    [InlineData("manual:x", null, false)]
    [InlineData(BatchRunTriggers.FetchFollowUp, null, false)]
    public void IsScheduledFetch_只認排程取數_AI排程不算(string trigger, string? jobType, bool expected)
    {
        var run = new BatchRun { Trigger = trigger, JobType = jobType };

        Assert.Equal(expected, run.IsScheduledFetch());
    }

    // ── 窗口實例帶進實際請求（第 64 輪 C-2、C-3）──────────

    [Fact]
    public void 排程請求_窗口實例要帶進實際執行的請求_漏抄會靜默失效()
    {
        var start = new DateTime(2026, 11, 5, 1, 0, 0);
        var request = SchedulerHostedService.ComposeScheduledRequest(null, "note", (start, start.AddHours(2)));

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, new ScheduleOptions());

        Assert.Equal(start, effective.WindowInstanceStart);
        Assert.Equal(start.AddHours(2), effective.WindowEnd);
        Assert.Null(effective.CatchUpNote); // 續跑不走補跑：CatchUpNote 會放大回望並被統計成補跑
    }

    [Fact]
    public void 趟首說明_上一趟留下待補或被窗口停止時講清楚要接什麼()
    {
        var now = new DateTime(2026, 11, 5, 4, 0, 0);
        var windows = new List<ScheduleWindow> { new() { Start = "01:00", End = "03:00" }, new() { Start = "04:00", End = "06:00" } };
        BatchRun Previous(int netiqFailed, bool stopped) => new()
        {
            RunId = 7, StartedAt = now.AddHours(-3), FinishedAt = now.AddHours(-1), Trigger = BatchRunTriggers.Schedule,
            NetiqDaysFailed = netiqFailed, Stopped = stopped
        };

        Assert.Contains("上一趟 #7 留下 NetIQ 4 主機日", SchedulerHostedService.DescribeWindowRun(now, windows, new[] { Previous(4, false) }));
        Assert.Contains("上一趟 #7 被停止", SchedulerHostedService.DescribeWindowRun(now, windows, new[] { Previous(0, true) }));
        Assert.DoesNotContain("續跑", SchedulerHostedService.DescribeWindowRun(now, windows, new[] { Previous(0, false) }));
        Assert.DoesNotContain("續跑", SchedulerHostedService.DescribeWindowRun(now, windows, Array.Empty<BatchRun>()));

        // 跨午夜窗口：00:30 觸發時，前一晚 23:00 那趟留下的也要說出來
        var crossing = new List<ScheduleWindow> { new() { Start = "23:00", End = "01:00" } };
        var afterMidnight = now.Date.AddMinutes(30);
        var lastNight = new BatchRun { RunId = 8, StartedAt = now.Date.AddHours(-1), FinishedAt = now.Date.AddMinutes(-20), Trigger = BatchRunTriggers.Schedule, NetiqDaysFailed = 2 };
        Assert.Contains("上一趟 #8 留下 NetIQ 2 主機日", SchedulerHostedService.DescribeWindowRun(afterMidnight, crossing, new[] { lastNight }));
    }

    [Fact]
    public void 續跑請求_不走補跑_帶窗口與上一趟留下的內容()
    {
        var start = new DateTime(2026, 11, 5, 1, 0, 0);
        var previous = new BatchRun { RunId = 42, StartedAt = start, FinishedAt = start.AddMinutes(30), NetiqDaysFailed = 4 };

        var request = SchedulerHostedService.ComposeContinueRequest(previous, (start, start.AddHours(4)));

        Assert.Null(request.CatchUpNote);
        Assert.Null(request.CatchUpDeadline);
        Assert.Null(request.BackfillOverride);
        Assert.Equal(BatchRunTriggers.Schedule, request.Trigger);
        Assert.Equal(start, request.WindowInstanceStart);
        Assert.Equal(start.AddHours(4), request.WindowEnd);
        Assert.Contains("第 2 趟", request.WindowNote);
        Assert.Contains("#42", request.WindowNote);
        Assert.Contains("NetIQ 4 主機日", request.WindowNote);
    }
}
