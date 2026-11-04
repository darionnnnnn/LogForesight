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

/// <summary>含 HealthService.GetDetail 狀態斷言：密文解密失敗旗標是行程層級 static，
/// 別的測試類別餵損毀密文就會讓狀態變 degraded，因此放進不並行的 CryptoKeyState 集合並在建構時重設。</summary>
[Collection("CryptoKeyState")]
public class ScheduleFreshnessTests
{
    public ScheduleFreshnessTests()
    {
        CryptoHelper.ResetForTests();
        LogForesight.Web.Services.CryptoKeyBootstrapper.KeyMismatch = false;
    }

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
            null!, // ackedDaysFromNow
            Array.Empty<RunSpec>(),
            false, // expectedStale
            false, // expectedAcked
            null!  // expectedLastSuccessHoursAgo
        };

        // 2. 最近成功在 30 小時前 → Stale=false, Acked=false, LastSuccessAt = now-30h
        yield return new object[]
        {
            "最近成功在 30 小時前",
            true,
            100,
            null!,
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
            null!,
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
            null!,
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
            null!,
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
            null!,
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
            null!,
            Array.Empty<RunSpec>(),
            true,
            false,
            null!
        };

        // 8. 近 14 天無紀錄、排程啟用於 10 小時前 → Stale=false, Acked=false, LastSuccessAt = null
        yield return new object[]
        {
            "近 14 天無紀錄、排程啟用於 10 小時前",
            true,
            10,
            null!,
            Array.Empty<RunSpec>(),
            false,
            false,
            null!
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

    private static string FindRepoRootForScope()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LogForesight.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>排程已啟用 100 小時、依序寫入指定的趟次後算一次新鮮度</summary>
    private static ScheduleFreshnessDto Freshness(params BatchRun[] finished)
    {
        using var fx = new EfSqliteFixture();
        var optionsBlob = fx.Blob("schedule_options");
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var now = DateTime.Now;
        var options = new ScheduleOptions { Enabled = true, EnabledAt = now.AddHours(-100) };
        optionsBlob.Mutate(_ => (JsonSerializer.Serialize(options, LfJsonOptions.Pretty), options));
        foreach (var run in finished)
        {
            runs.StartRun(run);
            runs.FinishRun(run);
        }
        return new ScheduleFreshnessService(runs, new ScheduleOptionsStore(optionsBlob)).GetScheduleFreshness(now);
    }

    private static BatchRun Run(string trigger, string? scope, double hoursAgo, int exitCode = 0, string? jobType = null) => new()
    {
        Trigger = trigger,
        Scope = scope,
        JobType = jobType,
        StartedAt = DateTime.Now.AddHours(-hoursAgo - 1),
        FinishedAt = DateTime.Now.AddHours(-hoursAgo),
        ExitCode = exitCode
    };

    [Theory]
    [InlineData("manual:admin", "full", 0, null, false)]    // 完整手動成功：資料是新的
    [InlineData("manual:admin", "local", 0, null, true)]    // 範圍縮小不算
    [InlineData("manual:admin", "netiq-hosts", 0, null, true)]
    [InlineData("manual:admin", null, 0, null, true)]       // 舊紀錄沒有範圍
    [InlineData("manual:admin", "full", 1, null, true)]     // 完整手動但失敗
    [InlineData("console", "full", 0, null, true)]
    [InlineData("manual:admin", "full", 0, "ai", true)]     // AI 趟不算
    public void 手動趟只有完整範圍且成功才算資料更新(string trigger, string? scope, int exitCode, string? jobType, bool expectedStale)
    {
        var result = Freshness(Run(trigger, scope, 1, exitCode, jobType));

        Assert.Equal(expectedStale, result.Stale);
    }

    [Fact]
    public void 最近排程趟狀態只看排程取數趟_手動趟不影響()
    {
        var result = Freshness(Run("schedule", "full", 60, exitCode: 1), Run("manual:admin", "local", 1));

        Assert.Equal(BatchRunStatus.Failed, result.LastRunStatus);
    }

    [Fact]
    public void 憑證問題旗標跟著解密狀態()
    {
        CryptoHelper.ResetForTests();
        try
        {
            Assert.False(Freshness().CredentialIssue);

            // 金鑰不符但憑證都解得開（重新輸入後、重啟前）：不能再叫人輸入一次
            CryptoKeyBootstrapper.KeyMismatch = true;
            Assert.False(Freshness().CredentialIssue);
            CryptoKeyBootstrapper.KeyMismatch = false;

            Assert.False(CryptoHelper.TryDecrypt("enc:v2:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", out _));
            Assert.True(Freshness().CredentialIssue);
        }
        finally
        {
            CryptoHelper.ResetForTests();
        }
    }

    [Fact]
    public void 卡住沒結束的排程趟不會被更早的成功趟蓋掉()
    {
        var stuck = new BatchRun { Trigger = "schedule", StartedAt = DateTime.Now.AddHours(-10) };
        using var fx = new EfSqliteFixture();
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var done = Run("schedule", "full", 60);
        runs.StartRun(done);
        runs.FinishRun(done);
        runs.StartRun(stuck);
        var optionsBlob = fx.Blob("schedule_options");
        var options = new ScheduleOptions { Enabled = true, EnabledAt = DateTime.Now.AddHours(-100) };
        optionsBlob.Mutate(_ => (JsonSerializer.Serialize(options, LfJsonOptions.Pretty), options));

        var result = new ScheduleFreshnessService(runs, new ScheduleOptionsStore(optionsBlob)).GetScheduleFreshness(DateTime.Now);

        Assert.True(result.Stale);
        Assert.Equal(BatchRunStatus.Stuck, result.LastRunStatus);
    }

    [Fact]
    public void 沒有任何排程趟_最近排程趟狀態為null()
    {
        Assert.Null(Freshness().LastRunStatus);
    }

    [Theory]
    [InlineData(LogForesight.Core.Service.RunScope.Full, "full")]
    [InlineData(LogForesight.Core.Service.RunScope.LocalOnly, "local")]
    [InlineData(LogForesight.Core.Service.RunScope.NetiqHosts, "netiq-hosts")]
    public void 範圍落地字串(LogForesight.Core.Service.RunScope scope, string expected)
    {
        Assert.Equal(expected, BatchRunScopes.Of(scope));
    }

    [Fact]
    public void 範圍經Recorder寫入並可讀回_執行入口有帶()
    {
        using var fx = new EfSqliteFixture();
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        using (var recorder = new LogForesight.Core.Service.BatchRunRecorder(runs, "host", Array.Empty<string>(), "manual:admin", scope: BatchRunScopes.Local))
        {
            recorder.Finish(0);
        }

        Assert.Equal(BatchRunScopes.Local, runs.GetRecentRuns(1, null).Single().Scope);

        // AI 趟不帶範圍：它不是取數，範圍對它沒有意義
        var aiRecorderSource = File.ReadAllText(Path.Combine(FindRepoRootForScope(), "LogForesight.Web", "Services", "AiAnalysisHostedService.cs"));
        Assert.DoesNotContain("scope:", aiRecorderSource);

        var orchestrator = File.ReadAllText(Path.Combine(FindRepoRootForScope(), "LogForesight.Core", "Service", "AnalysisOrchestrator.cs"));
        Assert.Contains("scope: BatchRunScopes.Of(request.Scope)", orchestrator);
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
        _ = scenario;
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
                new MailNotifyStateStore(backend.Blob("mail_notify_state")), freshness, new WorkOrderOutboxStore(backend.Blob(WorkOrderOutboxStore.BlobKey)), new FakeWorkOrderStore(new FakeIssueCaseStore()), new FakeIssueCaseStore(), new FakeRuleStore());

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
            userDisplayNames: displayNames,
            throttle: new LoginThrottle(),
            summaryCache: new SummaryCache(new DataVersionStamp()));
    }
}
