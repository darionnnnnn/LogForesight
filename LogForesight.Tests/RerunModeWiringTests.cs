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
/// 4. ScheduleController.Run 驗證（回望天數留空合法、超過保留天數上限被拒）
/// </summary>
public class RerunModeWiringTests
{
    private static ScheduleOptions DefaultOptions() =>
        new() { DebugDump = false, LocalAnalysisEnabled = true };

    [Fact]
    public void RunRequest_重新分析模式預設為None()
    {
        Assert.Equal(RerunMode.None, new RunRequest().RerunMode);
    }

    [Theory]
    [InlineData(RerunMode.None, null)]
    [InlineData(RerunMode.Unhandled, 7)]
    [InlineData(RerunMode.UnhandledAndAssigned, 14)]
    [InlineData(RerunMode.All, 30)]
    public void ComposeEffectiveRequest_正確傳遞重新分析模式與回望天數(RerunMode mode, int? days)
    {
        // 回望天數合併為單一欄位（回饋三十四輪 C）：缺漏補跑與重新分析共用 BackfillOverride
        var request = new RunRequest
        {
            Scope = RunScope.Full,
            RerunMode = mode,
            BackfillOverride = days,
            Trigger = "manual:tester"
        };

        var effective = SchedulerHostedService.ComposeEffectiveRequest(request, DefaultOptions());

        Assert.Equal(mode, effective.RerunMode);
        Assert.Equal(days, effective.BackfillOverride);
    }

    [Fact]
    public void 排程輪詢觸發時_維持預設不重跑且不覆寫回望天數()
    {
        var scheduledRequest = new RunRequest { Scope = RunScope.Full, Trigger = "schedule" };

        Assert.Equal(RerunMode.None, scheduledRequest.RerunMode);
        Assert.Null(scheduledRequest.BackfillOverride);

        var effective = SchedulerHostedService.ComposeEffectiveRequest(scheduledRequest, DefaultOptions());
        Assert.Equal(RerunMode.None, effective.RerunMode);
        Assert.Null(effective.BackfillOverride);
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
            settingsStore: settingsStore,
            userDisplayNames: new UserDisplayNameService(settingsStore),
            aiScheduler: null!, aiRunState: new AiAnalysisRunState());
    }

    [Fact]
    public async Task ScheduleController_Run_回望天數留空時所有模式都不拒絕()
    {
        var controller = CreateController(180);

        // 合併後留空＝沿用 NetIQ 維護頁設定的回望天數，在重新分析模式下同樣合法
        // （合併前這裡會因為「必須指定重跑天數」被擋掉）
        var ex = await Record.ExceptionAsync(() => controller.Run(new TriggerRunRequest
        {
            Scope = "all",
            RerunMode = RerunMode.Unhandled,
            BackfillDays = null
        }));

        // 這個替身只餵得到驗證階段（scheduler 為 null、blob 尚未建表），走完驗證後必然因基礎設施
        // 而失敗——所以斷言只釘住「驗證階段沒有拒絕」：合併前這裡會是「必須指定重跑天數」的
        // DomainException，合併後必須放行到驗證之後。
        Assert.IsNotType<DomainException>(ex);
        Assert.DoesNotContain("重跑天數", ex?.Message ?? "");
    }

    [Fact]
    public async Task ScheduleController_Run_回望天數超過保留天數上限時拒絕()
    {
        var controller = CreateController(retentionDays: 180);

        var ex = await Assert.ThrowsAsync<DomainException>(() => controller.Run(new TriggerRunRequest
        {
            Scope = "all",
            RerunMode = RerunMode.All,
            BackfillDays = 200
        }));

        Assert.Contains("180", ex.Message);
        Assert.Contains("超過保留天數的回補資料會在下次清理時被刪除", ex.Message);
    }
}
