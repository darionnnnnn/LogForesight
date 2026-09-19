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
    [Fact]
    public void GetScheduleTriggerTimes_AiScheduleRun_Excluded()
    {
        var time = new DateTime(2026, 9, 19, 22, 5, 0);
        var runs = new[]
        {
            new BatchRun { Trigger = "schedule", JobType = BatchRun.JobTypeAi, StartedAt = time }
        };

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>());

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

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>());

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

        var result = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>());

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
            runState.RecentScheduleAttempts).ToList();

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

        var triggerTimes = SchedulerHostedService.GetScheduleTriggerTimes(runs, Enumerable.Empty<DateTime>());
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
            var mailService = new MailNotificationService(
                new FakeSystemSettingsStore(),
                capturingSender,
                new FakeHostStore(),
                new FakeUserStore(),
                new FakeUserGroupStore(),
                new FakeGroupAccessStore(),
                new FakeAnalysisRecordQuery(),
                new FakeHandlingStore(),
                mailState);

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
}
