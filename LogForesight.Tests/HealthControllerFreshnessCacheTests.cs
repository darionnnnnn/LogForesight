using LogForesight.Core;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Web.Auth;
using LogForesight.Web.Controllers.Api;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using System.Text.Json;
using Xunit;

namespace LogForesight.Tests;

/// <summary>/api/health/freshness 的全站快取：重複呼叫只算一次、版本戳推進即重算、共用實例不被使用者別的欄位污染。</summary>
[Collection("CryptoKeyState")]
public class HealthControllerFreshnessCacheTests
{
    private sealed class CountingFreshness : ScheduleFreshnessService
    {
        public int Calls;
        public CountingFreshness(BatchRunStore runs, ScheduleOptionsStore options) : base(runs, options) { }
        public override ScheduleFreshnessDto GetScheduleFreshness(DateTime now)
        {
            Calls++;
            return base.GetScheduleFreshness(now);
        }
    }

    private static HealthController Create(
        ScheduleFreshnessService freshness, ScheduleOptionsStore options, ICurrentUser user,
        SummaryCache cache, FakeUserGroupStore groups, FakeUserStore users)
    {
        return new HealthController(
            health: null!,
            freshness: freshness,
            optionsStore: options,
            audit: new RecordingAuditService(),
            currentUser: user,
            users: users,
            groups: groups,
            userDisplayNames: new UserDisplayNameService(new FakeSystemSettingsStore()),
            throttle: new LoginThrottle(),
            summaryCache: cache);
    }

    [Fact]
    public void Freshness_RepeatedCalls_ComputeOnce_AndRecomputeAfterBump()
    {
        using var fx = new EfSqliteFixture();
        var options = new ScheduleOptionsStore(fx.Blob("schedule_options"));
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var counting = new CountingFreshness(runs, options);
        var stamp = new DataVersionStamp();
        var cache = new SummaryCache(stamp);
        var controller = Create(counting, options, FakeCurrentUser.WithCapabilities(Capability.Maintain), cache, new FakeUserGroupStore(), new FakeUserStore());

        controller.Freshness();
        controller.Freshness();
        Assert.Equal(1, counting.Calls);

        stamp.Bump();
        controller.Freshness();
        Assert.Equal(2, counting.Calls);
    }

    [Fact]
    public void Freshness_VersionUnchanged_RecomputesAfterTtl()
    {
        using var fx = new EfSqliteFixture();
        var options = new ScheduleOptionsStore(fx.Blob("schedule_options"));
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var counting = new CountingFreshness(runs, options);
        var now = new DateTime(2026, 1, 1, 8, 0, 0);
        var cache = new SummaryCache(new DataVersionStamp(), () => now);
        var controller = Create(counting, options, FakeCurrentUser.WithCapabilities(Capability.Maintain), cache, new FakeUserGroupStore(), new FakeUserStore());

        controller.Freshness();
        now = now.AddSeconds(SummaryCache.TtlSeconds - 1);
        controller.Freshness();
        Assert.Equal(1, counting.Calls);

        now = now.AddSeconds(2);   // 背景排程結束不走 HTTP、沒有 Bump：靠 TTL 當上限
        controller.Freshness();
        Assert.Equal(2, counting.Calls);
    }

    [Fact]
    public void Freshness_AdminContactsFilledForNonMaintainer_DoNotLeakToMaintainerViaCache()
    {
        using var fx = new EfSqliteFixture();
        var options = new ScheduleOptionsStore(fx.Blob("schedule_options"));
        var runs = new BatchRunStore(fx.LogStore("batch_runs"), fx.LogStore("batch_run_logs"));
        var freshness = new ScheduleFreshnessService(runs, options);
        var groups = new FakeUserGroupStore();
        var users = new FakeUserStore();
        var cache = new SummaryCache(new DataVersionStamp());

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
            var opt = new ScheduleOptions { Enabled = true, UpdatedAt = DateTime.Now.AddHours(-72), FreshnessAckUntil = null };
            return (JsonSerializer.Serialize(opt, LfJsonOptions.Pretty), opt);
        });
        var adminGroup = groups.Upsert(new UserGroup { GroupName = "管理群組", Role = UserRole.Admin, Active = true });
        users.Upsert(new WebUser { Account = "admin1", DisplayName = "系統管理員甲", Active = true, GroupIds = new List<long> { adminGroup.GroupId } });

        var viewer = Create(freshness, options, FakeCurrentUser.WithCapabilities(), cache, groups, users);
        var maintainer = Create(freshness, options, FakeCurrentUser.WithCapabilities(Capability.Maintain), cache, groups, users);

        var first = viewer.Freshness();
        Assert.True(first.Data!.Stale);
        Assert.Single(first.Data.AdminContacts);

        var second = maintainer.Freshness();
        Assert.True(second.Data!.Stale);
        Assert.Empty(second.Data.AdminContacts);
    }
}
