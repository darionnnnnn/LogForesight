using System.Text.Json;
using LogForesight.Core.Models;
using LogForesight.Core.Persistence;
using LogForesight.Tests.Scenarios;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 第 81 輪批次 U 的整站情境：以空資料庫起站台，就緒卡八項的初始狀態、健康整體狀態與初始設定精靈的步驟對得上。
/// 站台設定走行程層級環境變數，所以與其他整站測試共用不並行的 <c>NLogGlobalConfig</c> 集合。
/// </summary>
[Collection("NLogGlobalConfig")]
public class DispatchReadinessSiteTests
{
    [Fact]
    public void 空資料庫_就緒卡的初始狀態_與精靈的步驟對得上()
    {
        using var site = new ScenarioSite();
        var readiness = site.Get<DispatchReadinessService>();
        using var scope = site.Services.CreateScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupReadinessService>();

        var initial = readiness.Evaluate();
        var wizard = setup.GetStatus().Steps.ToDictionary(s => s.Id);

        // 郵件通知預設關閉：只有六項，兩個 email 項不列入
        Assert.False(initial.MailChecked);
        Assert.Equal(new[] { "host-no-group", "host-group-no-pool", "no-pool", "auto-dispatch-off", "ai-window", "schedule-off" },
            initial.Items.Select(i => i.Id));
        var byId = initial.Items.ToDictionary(i => i.Id);
        Assert.True(byId["host-no-group"].Passed);                 // 沒有主機：沒有東西受影響
        Assert.True(byId["host-group-no-pool"].Skipped);           // 派工池沒人：無法判定，真正的原因在 no-pool
        Assert.False(byId["no-pool"].Passed);
        Assert.False(byId["auto-dispatch-off"].Passed);            // 開關預設關
        Assert.True(byId["ai-window"].Passed);                     // 出廠 AI 窗口 00:00–23:59
        Assert.False(byId["schedule-off"].Passed);                 // 排程預設未啟用
        Assert.Equal(3, initial.FailedCount);

        // 與精靈對得上：排程步驟與「排程已啟用」是同一件事；群組授權步驟未完成時派工池也還沒有
        Assert.False(wizard["schedule"].Done);
        Assert.False(wizard["groups"].Done);
        Assert.False(wizard["dept-groups"].Done);

        // 健康整體狀態：自動派工開關與排程啟用是可以刻意關閉的營運選擇，卡片照列不通過但不併入整體狀態；
        // 只有「沒有可接單的派工池」併入，整體為 degraded
        var detail = site.Get<HealthService>().GetDetail();
        Assert.Equal("degraded", detail.Status);
        var reasons = detail.StatusReasons.Where(r => r.Key == HealthAttentionKeys.DispatchReadiness).ToList();
        Assert.Single(reasons);
        Assert.All(reasons, r => Assert.StartsWith("派工就緒：", r.Text));
        Assert.Equal(3, detail.DispatchReadiness!.FailedCount);
    }

    [Fact]
    public void 依序補齊後_就緒卡逐項轉綠_精靈排程步驟同步完成()
    {
        using var site = new ScenarioSite();
        var readiness = site.Get<DispatchReadinessService>();
        using var scope = site.Services.CreateScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupReadinessService>();

        // 啟用排程並設定執行窗口：就緒卡的排程項與精靈的排程步驟一起轉為完成
        site.Get<ScheduleOptionsStore>().Update(o =>
        {
            o.Enabled = true;
            o.Windows = new List<ScheduleWindow> { new() { Start = "01:00", End = "05:00" } };
        });
        Assert.True(readiness.Evaluate().Items.Single(i => i.Id == "schedule-off").Passed);
        Assert.True(setup.GetStatus().Steps.Single(s => s.Id == "schedule").Done);

        // 開自動派工、建派工池與成員、授權主機群組：全部通過
        site.Get<ISystemSettingsStore>().Update(s => s.AutoDispatchEnabled = true);
        var groups = site.Get<IUserGroupStore>();
        var pool = groups.Upsert(new UserGroup { GroupName = "維運池", Role = UserRole.User, Active = true, DispatchPool = true });
        site.Get<IUserStore>().Upsert(new WebUser { Account = "handler", GroupIds = new List<long> { pool.GroupId } });
        var room = site.Get<IHostGroupStore>().Upsert(new HostGroup { GroupName = "機房" });
        site.Get<IHostStore>().Upsert(new WebHost { HostName = "HOST-1", GroupIds = new List<long> { room.GroupId } });
        site.Get<IGroupAccessStore>().SetForUserGroup(pool.GroupId, new[] { room.GroupId });

        var ready = readiness.Evaluate();
        Assert.Equal(0, ready.FailedCount);
        Assert.False(ready.Items.Single(i => i.Id == "host-group-no-pool").Skipped);
        Assert.DoesNotContain(site.Get<HealthService>().GetDetail().StatusReasons, r => r.Key == HealthAttentionKeys.DispatchReadiness);
    }

    [Fact]
    public async Task 群組預覽端點_管理員可呼叫且回傳摘要()
    {
        using var site = new ScenarioSite();
        var groups = site.Get<IUserGroupStore>();
        var admin = groups.Upsert(new UserGroup { GroupName = "管理員", Role = UserRole.Admin, Active = true });
        var dept = groups.Upsert(new UserGroup { GroupName = "部門", Role = UserRole.User, Active = true, DispatchPool = true });
        site.Get<IUserStore>().Upsert(new WebUser { Account = "boss", GroupIds = new List<long> { admin.GroupId } });
        site.Get<IUserStore>().Upsert(new WebUser { Account = "plain", GroupIds = new List<long> { dept.GroupId } });
        Assert.True(await site.LoginAsync("boss"));
        Assert.True(await site.LoginAsync("plain"));

        var ok = await site.SendAsync("boss", HttpMethod.Get, $"api/admin/users/group-preview?groupIds={dept.GroupId}&active=true");
        var denied = await site.SendAsync("plain", HttpMethod.Get, $"api/admin/users/group-preview?groupIds={dept.GroupId}");

        Assert.Equal(200, ok.StatusCode);
        using var doc = JsonDocument.Parse(ok.Body!);
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("inPool").GetBoolean());
        Assert.True(data.GetProperty("canHandle").GetBoolean());
        Assert.Equal(403, denied.StatusCode);
    }
}
