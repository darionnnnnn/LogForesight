using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Core.Service;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 任務 31C step2：重新分析舊日模式接線與 API 驗證測試。
/// 涵蓋：
/// 1. RunRequest 預設值
/// 2. ComposeEffectiveRequest 欄位同步
/// 3. 排程輪詢觸發時維持預設 None / null
/// 4. ScheduleController.Run 驗證（RerunDays 為空、小於 1、超過保留天數上限）
/// </summary>
public class RerunModeWiringTests
{
    private static ScheduleOptions DefaultOptions() =>
        new() { DebugDump = false, LocalAnalysisEnabled = true };

    [Fact]
    public void RunRequest_預設值為None與null()
    {
        var request = new RunRequest();

        Assert.Equal(RerunMode.None, request.RerunMode);
        Assert.Null(request.RerunDays);
    }

    [Theory]
    [InlineData(RerunMode.None, null)]
    [InlineData(RerunMode.Unhandled, 7)]
    [InlineData(RerunMode.UnhandledAndAssigned, 14)]
    [InlineData(RerunMode.All, 30)]
    public void ComposeEffectiveRequest_正確傳遞RerunMode與RerunDays(RerunMode mode, int? days)
    {
        var request = new RunRequest
        {
            Scope = RunScope.Full,
            RerunMode = mode,
            RerunDays = days,
            Trigger = "manual:tester"
        };

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, DefaultOptions());

        Assert.Equal(mode, effective.RerunMode);
        Assert.Equal(days, effective.RerunDays);
    }

    [Fact]
    public void 排程輪詢觸發時_RunRequest維持預設None與null()
    {
        var scheduledRequest = new RunRequest { Scope = RunScope.Full, Trigger = "schedule" };

        Assert.Equal(RerunMode.None, scheduledRequest.RerunMode);
        Assert.Null(scheduledRequest.RerunDays);

        var effective = SchedulerHostedService.ComposeEffectiveRequest(scheduledRequest, DefaultOptions());
        Assert.Equal(RerunMode.None, effective.RerunMode);
        Assert.Null(effective.RerunDays);
    }

    private static ScheduleController CreateController(int retentionDays = 180)
    {
        using var fx = new EfSqliteFixture();
        var hosts = new FakeHostStore();
        var sentinel = new Sentinel { SentinelId = 1, Name = "S1", BaseUrl = "https://x", Username = "u", PasswordEnc = "p", Active = true };
        hosts.Upsert(new WebHost { HostId = 1, HostName = "host1", IpAddress = "10.0.0.1", Os = WebHost.OsWindows, SentinelId = 1, NetiqServer = "S1", Source = "netiq", Active = true });

        var sentinels = new FakeSentinelStore();
        sentinels.Upsert(sentinel);

        var settingsStore = new FakeSystemSettingsStore();
        settingsStore.Update(s => s.RetentionDays = retentionDays);

        return new ScheduleController(
            new ScheduleOptionsStore(fx.Blob("schedule_options")),
            scheduler: null!,
            new SchedulerRunState(),
            hosts,
            sentinels,
            new RecordingAuditService(),
            new FakeCurrentUser(),
            new FakeUserStore(),
            records: new FakeRecordRepository(hosts),
            settingsStore: settingsStore);
    }

    [Fact]
    public async Task ScheduleController_Run_RerunMode非None但RerunDays為null時拒絕()
    {
        var controller = CreateController(180);

        var ex = await Assert.ThrowsAsync<DomainException>(() => controller.Run(new TriggerRunRequest
        {
            Scope = "all",
            RerunMode = RerunMode.Unhandled,
            RerunDays = null
        }));

        Assert.Contains("必須指定重跑天數", ex.Message);
    }

    [Fact]
    public async Task ScheduleController_Run_RerunMode非None但RerunDays小於1時拒絕()
    {
        var controller = CreateController(180);

        var ex = await Assert.ThrowsAsync<DomainException>(() => controller.Run(new TriggerRunRequest
        {
            Scope = "all",
            RerunMode = RerunMode.Unhandled,
            RerunDays = 0
        }));

        Assert.Contains("必須指定重跑天數", ex.Message);
    }

    [Fact]
    public async Task ScheduleController_Run_RerunDays超過RetentionDays上限時拒絕()
    {
        var controller = CreateController(retentionDays: 180);

        var ex = await Assert.ThrowsAsync<DomainException>(() => controller.Run(new TriggerRunRequest
        {
            Scope = "all",
            RerunMode = RerunMode.All,
            RerunDays = 200
        }));

        Assert.Contains("180", ex.Message);
        Assert.Contains("超過保留天數的回補資料會在下次清理時被刪除", ex.Message);
    }
}
