using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Persistence.Sql;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using LogForesight.Web.Services.Mail;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

public class ScheduleFreshnessTests
{
    public sealed record RunSpec(
        int StartedHoursAgo,
        int? FinishedHoursAgo,
        int ExitCode = 0,
        int WarnCount = 0,
        string? JobType = null,
        string Trigger = "schedule");

    public static IEnumerable<object[]> FreshnessTestData()
    {
        // 1. 排程未啟用 → Stale=false, Acked=false
        yield return new object[]
        {
            "排程未啟用",
            false, // scheduleEnabled
            100,   // scheduleUpdatedHoursAgo
            null,  // ackedDaysFromNow
            Array.Empty<RunSpec>(),
            false, // expectedStale
            false, // expectedAcked
            null   // expectedLastSuccessHoursAgo
        };

        // 2. 最近成功在 30 小時前 → Stale=false, Acked=false, LastSuccessAt = now-30h
        yield return new object[]
        {
            "最近成功在 30 小時前",
            true,
            100,
            null,
            new[] { new RunSpec(31, 30) },
            false,
            false,
            30
        };

        // 3. 最近成功在 72 小時前 → Stale=true, Acked=false, LastSuccessAt = now-72h
        yield return new object[]
        {
            "最近成功在 72 小時前",
            true,
            100,
            null,
            new[] { new RunSpec(73, 72) },
            true,
            false,
            72
        };

        // 4. 最近一筆是 72 小時前成功、但 10 小時前有一筆 JobType=ai 的成功 → Stale=true, Acked=false, LastSuccessAt = now-72h（AI 不算）
        yield return new object[]
        {
            "最近一筆是 72 小時前成功、但 10 小時前有一筆 JobType=ai 的成功",
            true,
            100,
            null,
            new[]
            {
                new RunSpec(73, 72),
                new RunSpec(11, 10, JobType: BatchRun.JobTypeAi)
            },
            true,
            false,
            72
        };

        // 5. 最近一筆 status=warning（WarnCount>0）在 10 小時前 → Stale=false, Acked=false, LastSuccessAt = now-10h
        yield return new object[]
        {
            "最近一筆 status=warning（WarnCount>0）在 10 小時前",
            true,
            100,
            null,
            new[] { new RunSpec(11, 10, WarnCount: 1) },
            false,
            false,
            10
        };

        // 6. 最近一筆 failed 在 10 小時前、成功在 72 小時前 → Stale=true, Acked=false, LastSuccessAt = now-72h
        yield return new object[]
        {
            "最近一筆 failed 在 10 小時前、成功在 72 小時前",
            true,
            100,
            null,
            new[]
            {
                new RunSpec(73, 72),
                new RunSpec(11, 10, ExitCode: 1)
            },
            true,
            false,
            72
        };

        // 7. 近 14 天無紀錄、排程啟用於 72 小時前 → Stale=true, Acked=false, LastSuccessAt = null
        yield return new object[]
        {
            "近 14 天無紀錄、排程啟用於 72 小時前",
            true,
            72,
            null,
            Array.Empty<RunSpec>(),
            true,
            false,
            null
        };

        // 8. 近 14 天無紀錄、排程啟用於 10 小時前 → Stale=false, Acked=false, LastSuccessAt = null
        yield return new object[]
        {
            "近 14 天無紀錄、排程啟用於 10 小時前",
            true,
            10,
            null,
            Array.Empty<RunSpec>(),
            false,
            false,
            null
        };

        // 9. Stale 且 AckedUntil 為明天 → Stale=true, Acked=true
        yield return new object[]
        {
            "Stale 且 AckedUntil 為明天",
            true,
            100,
            1, // ackedDaysFromNow
            new[] { new RunSpec(73, 72) },
            true,
            true,
            72
        };
    }

    [Theory]
    [MemberData(nameof(FreshnessTestData))]
    public void CalculateFreshness_Theory_MatchesExpected(
        string scenario,
        bool scheduleEnabled,
        int? scheduleUpdatedHoursAgo,
        int? ackedDaysFromNow,
        RunSpec[] runSpecs,
        bool expectedStale,
        bool expectedAcked,
        int? expectedLastSuccessHoursAgo)
    {
        using var fx = new EfSqliteFixture();
        var optionsBlob = fx.Blob("schedule_options");
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var freshnessService = new ScheduleFreshnessService(runs, new ScheduleOptionsStore(optionsBlob));

        var now = DateTime.Now;
        var scheduleUpdatedAt = scheduleUpdatedHoursAgo.HasValue ? now.AddHours(-scheduleUpdatedHoursAgo.Value) : (DateTime?)null;
        var ackedUntil = ackedDaysFromNow.HasValue ? now.Date.AddDays(ackedDaysFromNow.Value) : (DateTime?)null;
        var expectedLastSuccessAt = expectedLastSuccessHoursAgo.HasValue ? now.AddHours(-expectedLastSuccessHoursAgo.Value) : (DateTime?)null;

        var options = new ScheduleOptions
        {
            Enabled = scheduleEnabled,
            EnabledAt = scheduleUpdatedAt,
            FreshnessAckUntil = ackedUntil
        };
        optionsBlob.Mutate(_ => (JsonSerializer.Serialize(options, LfJsonOptions.Pretty), options));

        foreach (var spec in runSpecs)
        {
            var run = new BatchRun
            {
                Trigger = spec.Trigger,
                StartedAt = now.AddHours(-spec.StartedHoursAgo),
                FinishedAt = spec.FinishedHoursAgo.HasValue ? now.AddHours(-spec.FinishedHoursAgo.Value) : null,
                ExitCode = spec.ExitCode,
                WarnCount = spec.WarnCount,
                JobType = spec.JobType
            };
            runs.StartRun(run);
            if (run.FinishedAt.HasValue)
            {
                runs.FinishRun(run);
            }
        }

        var result = freshnessService.GetScheduleFreshness(now);

        Assert.Equal(expectedStale, result.Stale);
        Assert.Equal(expectedAcked, result.Acked);
        if (expectedLastSuccessAt.HasValue)
        {
            Assert.NotNull(result.LastSuccessAt);
            Assert.Equal(expectedLastSuccessAt.Value, result.LastSuccessAt.Value);
        }
        else
        {
            Assert.Null(result.LastSuccessAt);
        }
    }

    [Fact]
    public void GetDetail_WhenStaleAndAcked_StatusIsNotDegraded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lf-freshness-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var backend = new StorageBackend(
                new StorageSettings { Type = "Sqlite", ConnectionString = $"Data Source={Path.Combine(tempDir, "test.db")}" },
                tempDir);

            var optionsStore = new ScheduleOptionsStore(backend.Blob("schedule_options"));
            var runs = new BatchRunStore(backend.LogStore("batch_runs"), backend.LogStore("batch_run_logs"));
            var freshness = new ScheduleFreshnessService(runs, optionsStore);

            // 成功在 72 小時前
            var run = new BatchRun
            {
                Trigger = "schedule",
                StartedAt = DateTime.Now.AddHours(-73),
                FinishedAt = DateTime.Now.AddHours(-72),
                ExitCode = 0
            };
            runs.StartRun(run);
            runs.FinishRun(run);

            // 設定排程啟用，AckedUntil 為明天
            var tomorrow = DateTime.Today.AddDays(1);
            backend.Blob("schedule_options").Mutate(_ =>
            {
                var opt = new ScheduleOptions
                {
                    Enabled = true,
                    UpdatedAt = DateTime.Now.AddHours(-72),
                    FreshnessAckUntil = tomorrow
                };
                return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
            });

            var mailService = new MailNotificationService(
                new FakeSystemSettingsStore(), new FakeSmtpMailSender(), new FakeHostStore(), new FakeUserStore(),
                new FakeUserGroupStore(), new FakeGroupAccessStore(), new FakeAnalysisRecordQuery(), new FakeHandlingStore(),
                new MailNotifyStateStore(backend.Blob("mail_notify_state")), freshness);

            var healthService = new HealthService(backend, new SchedulerRunState(), backend.TopIssueBackfiller(), mailService, freshness);

            // 1. AckedUntil 為明天：Stale=true, Acked=true，Status 不因新鮮度變 degraded（為 ok）
            var detail = healthService.GetDetail();
            Assert.True(detail.ScheduleFreshness.Stale);
            Assert.True(detail.ScheduleFreshness.Acked);
            Assert.Equal(HealthStatuses.Ok, detail.Status);

            // 2. 清除 AckedUntil：Stale=true, Acked=false，Status 變為 degraded
            backend.Blob("schedule_options").Mutate(_ =>
            {
                var opt = new ScheduleOptions
                {
                    Enabled = true,
                    UpdatedAt = DateTime.Now.AddHours(-72),
                    FreshnessAckUntil = null
                };
                return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
            });

            var detailUnacked = healthService.GetDetail();
            Assert.True(detailUnacked.ScheduleFreshness.Stale);
            Assert.False(detailUnacked.ScheduleFreshness.Acked);
            Assert.Equal(HealthStatuses.Degraded, detailUnacked.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void FreshnessAck_UntilIsToday_ThrowsValidationException()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out _, out _, FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var ex = Assert.Throws<DomainException>(() =>
            controller.FreshnessAck(new FreshnessAckRequest { Until = DateTime.Today }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void FreshnessAck_UntilIsMoreThan30Days_ThrowsValidationException()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out _, out _, FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var ex = Assert.Throws<DomainException>(() =>
            controller.FreshnessAck(new FreshnessAckRequest { Until = DateTime.Today.AddDays(31) }));
        Assert.Equal(ApiErrorCodes.ValidationFailed, ex.Code);
    }

    [Fact]
    public void FreshnessAck_UntilIsTomorrow_SavesAndAudits()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out var optionsStore, out var audit, FakeCurrentUser.WithCapabilities(Capability.Maintain));
        var tomorrow = DateTime.Today.AddDays(1);
        var response = controller.FreshnessAck(new FreshnessAckRequest { Until = tomorrow });

        Assert.True(response.Success);
        var options = optionsStore.Get();
        Assert.Equal(tomorrow, options.FreshnessAckUntil);

        var auditEntry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.HealthFreshnessAck, auditEntry.Action);
    }

    [Fact]
    public void Freshness_WhenStaleAndNotAckedAndNotMaintainer_PopulatesAdminContacts()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out var optionsStore, out _, FakeCurrentUser.WithCapabilities(), out var groups, out var users, out var runs);

        // 成功在 72 小時前
        var run = new BatchRun
        {
            Trigger = "schedule",
            StartedAt = DateTime.Now.AddHours(-73),
            FinishedAt = DateTime.Now.AddHours(-72),
            ExitCode = 0
        };
        runs.StartRun(run);
        runs.FinishRun(run);

        // 排程啟用且未確認
        fx.Blob("schedule_options").Mutate(_ =>
        {
            var opt = new ScheduleOptions
            {
                Enabled = true,
                UpdatedAt = DateTime.Now.AddHours(-72),
                FreshnessAckUntil = null
            };
            return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
        });

        // 建立 admin 群組與成員
        var adminGroup = groups.Upsert(new UserGroup { GroupName = "管理群組", Role = UserRole.Admin, Active = true });
        users.Upsert(new WebUser { Account = "admin1", DisplayName = "系統管理員甲", Active = true, GroupIds = new List<long> { adminGroup.GroupId } });

        var response = controller.Freshness();
        Assert.True(response.Data!.Stale);
        Assert.False(response.Data.Acked);
        Assert.Single(response.Data.AdminContacts);
        Assert.Equal("系統管理員甲", response.Data.AdminContacts[0]);
    }

    [Fact]
    public void Freshness_WhenMaintainer_AdminContactsIsEmpty()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out _, out _, FakeCurrentUser.WithCapabilities(Capability.Maintain), out var groups, out var users, out var runs);

        // 成功在 72 小時前
        var run = new BatchRun
        {
            Trigger = "schedule",
            StartedAt = DateTime.Now.AddHours(-73),
            FinishedAt = DateTime.Now.AddHours(-72),
            ExitCode = 0
        };
        runs.StartRun(run);
        runs.FinishRun(run);

        fx.Blob("schedule_options").Mutate(_ =>
        {
            var opt = new ScheduleOptions
            {
                Enabled = true,
                UpdatedAt = DateTime.Now.AddHours(-72),
                FreshnessAckUntil = null
            };
            return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
        });

        var adminGroup = groups.Upsert(new UserGroup { GroupName = "管理群組", Role = UserRole.Admin, Active = true });
        users.Upsert(new WebUser { Account = "admin1", DisplayName = "系統管理員甲", Active = true, GroupIds = new List<long> { adminGroup.GroupId } });

        var response = controller.Freshness();
        Assert.True(response.Data!.Stale);
        Assert.False(response.Data.Acked);
        Assert.Empty(response.Data.AdminContacts);
    }

    [Fact]
    public void Freshness_WhenNoAdmins_AdminContactsIsEmpty()
    {
        using var fx = new EfSqliteFixture();
        var controller = CreateHealthController(fx, out _, out _, FakeCurrentUser.WithCapabilities(), out _, out _, out var runs);

        // 成功在 72 小時前
        var run = new BatchRun
        {
            Trigger = "schedule",
            StartedAt = DateTime.Now.AddHours(-73),
            FinishedAt = DateTime.Now.AddHours(-72),
            ExitCode = 0
        };
        runs.StartRun(run);
        runs.FinishRun(run);

        fx.Blob("schedule_options").Mutate(_ =>
        {
            var opt = new ScheduleOptions
            {
                Enabled = true,
                UpdatedAt = DateTime.Now.AddHours(-72),
                FreshnessAckUntil = null
            };
            return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
        });

        var response = controller.Freshness();
        Assert.True(response.Data!.Stale);
        Assert.False(response.Data.Acked);
        Assert.Empty(response.Data.AdminContacts);
    }

    private static HealthController CreateHealthController(
        EfSqliteFixture fx,
        out ScheduleOptionsStore optionsStore,
        out RecordingAuditService audit,
        ICurrentUser currentUser)
    {
        return CreateHealthController(fx, out optionsStore, out audit, currentUser, out _, out _, out _);
    }

    private static HealthController CreateHealthController(
        EfSqliteFixture fx,
        out ScheduleOptionsStore optionsStore,
        out RecordingAuditService audit,
        ICurrentUser currentUser,
        out FakeUserGroupStore groups,
        out FakeUserStore users,
        out BatchRunStore runs)
    {
        optionsStore = new ScheduleOptionsStore(fx.Blob("schedule_options"));
        runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var freshness = new ScheduleFreshnessService(runs, optionsStore);
        audit = new RecordingAuditService();
        groups = new FakeUserGroupStore();
        users = new FakeUserStore();
        var settingsStore = new FakeSystemSettingsStore();
        var displayNames = new UserDisplayNameService(settingsStore);

        return new HealthController(
            health: null!, // Freshness 與 FreshnessAck 不呼叫 _health
            freshness: freshness,
            optionsStore: optionsStore,
            audit: audit,
            currentUser: currentUser,
            users: users,
            groups: groups,
            userDisplayNames: displayNames);
    }
}
