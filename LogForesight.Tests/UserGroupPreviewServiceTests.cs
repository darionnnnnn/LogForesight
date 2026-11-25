using LogForesight.Core.Models;
using LogForesight.Web.Auth;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 81 輪批次 U（F18-1）：使用者編輯視窗依「目前勾選」算出能力、可見主機台數與是否在派工池，不寫入任何資料。</summary>
public class UserGroupPreviewServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _groups = new();
    private readonly FakeHostStore _hosts = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeIssueOwnerStore _issueOwners = new();

    private UserGroupPreviewService Service() =>
        new(_users, _groups, _hosts, _access, new UserCapabilityResolver(_groups, _hosts, _issueOwners));

    private UserGroup Group(string name, UserRole role, bool pool = false, bool active = true) =>
        _groups.Upsert(new UserGroup { GroupName = name, Role = role, DispatchPool = pool, Active = active });

    private HostGroup RoomWith(string name, int hostCount)
    {
        var room = new FakeHostGroupStore().Upsert(new HostGroup { GroupName = name });
        room.GroupId = _roomId++;
        for (var i = 0; i < hostCount; i++)
            _hosts.Upsert(new WebHost { HostName = $"{name}-{i}", GroupIds = new List<long> { room.GroupId } });
        return room;
    }

    private long _roomId = 100;

    [Fact]
    public void 一般使用者群組_有處理能力_可見台數是授權的群組內啟用主機()
    {
        var dept = Group("部門", UserRole.User);
        var room = RoomWith("機房", 3);
        _hosts.Upsert(new WebHost { HostName = "STOPPED", Active = false, GroupIds = new List<long> { room.GroupId } });
        _access.SetForUserGroup(dept.GroupId, new[] { room.GroupId });

        var preview = Service().Preview(null, new[] { dept.GroupId });

        Assert.Equal(new[] { "處理（標記狀態、回覆交辦）", "確認權限異動" }, preview.Capabilities);
        Assert.Equal(3, preview.VisibleHostCount);
        Assert.False(preview.InPool);
        Assert.True(preview.CanHandle);
    }

    [Fact]
    public void 取消勾選群組後摘要隨之改變_沒有勾選就沒有能力與主機()
    {
        var dept = Group("部門", UserRole.User);
        var room = RoomWith("機房", 2);
        _access.SetForUserGroup(dept.GroupId, new[] { room.GroupId });

        var none = Service().Preview(null, Array.Empty<long>());

        Assert.Empty(none.Capabilities);
        Assert.Equal(0, none.VisibleHostCount);
        Assert.False(none.CanHandle);
    }

    [Fact]
    public void 管理員群組_看得到全部啟用主機且列出全部能力()
    {
        var admin = Group("管理員", UserRole.Admin);
        RoomWith("甲", 2);
        RoomWith("乙", 1);

        var preview = Service().Preview(null, new[] { admin.GroupId });

        Assert.Equal(3, preview.VisibleHostCount);
        Assert.Contains("系統維護", preview.Capabilities);
        Assert.Contains("檢視全部主機", preview.Capabilities);
    }

    [Fact]
    public void 派工池_在啟用的派工池群組才算在池_沒有處理能力時標出()
    {
        var pool = Group("池", UserRole.User, pool: true);
        var stoppedPool = Group("停用池", UserRole.User, pool: true, active: false);
        var managerPool = Group("主管池", UserRole.Manager, pool: true);

        Assert.True(Service().Preview(null, new[] { pool.GroupId }).InPool);
        Assert.False(Service().Preview(null, new[] { stoppedPool.GroupId }).InPool);
        var noHandle = Service().Preview(null, new[] { managerPool.GroupId });
        Assert.True(noHandle.InPool);
        Assert.False(noHandle.CanHandle);   // 在池但沒有處理能力：不會被派單
    }

    [Fact]
    public void 停用的群組不給能力也不給可見範圍_並回報被略過的個數()
    {
        var stopped = Group("停用部門", UserRole.User, active: false);
        var room = RoomWith("機房", 2);
        _access.SetForUserGroup(stopped.GroupId, new[] { room.GroupId });

        var preview = Service().Preview(null, new[] { stopped.GroupId, 9999 });   // 不存在的編號略過

        Assert.Empty(preview.Capabilities);
        Assert.Equal(0, preview.VisibleHostCount);
        Assert.Equal(1, preview.InactiveGroupCount);
    }

    [Fact]
    public void 既有使用者_可見台數加上負責的主機_且負責人隱含處理能力()
    {
        var dept = Group("部門", UserRole.User);
        var room = RoomWith("機房", 2);
        _access.SetForUserGroup(dept.GroupId, new[] { room.GroupId });
        var owner = _users.Upsert(new WebUser { Account = "owner" });
        _hosts.Upsert(new WebHost { HostName = "OWNED", OwnerUserIds = new List<long> { owner.UserId } });

        var withoutGroup = Service().Preview(owner.UserId, Array.Empty<long>());
        var withGroup = Service().Preview(owner.UserId, new[] { dept.GroupId });

        Assert.Equal(1, withoutGroup.VisibleHostCount);       // 只有負責的那台
        Assert.True(withoutGroup.CanHandle);                  // 負責人隱含 User 角色
        Assert.Equal(3, withGroup.VisibleHostCount);          // 群組授權 2 台 ∪ 負責 1 台
    }

    [Fact]
    public void 停用帳號_一律是沒有能力_沒有可見主機_不在派工池()
    {
        var pool = Group("池", UserRole.User, pool: true);
        var room = RoomWith("機房", 2);
        _access.SetForUserGroup(pool.GroupId, new[] { room.GroupId });

        var preview = Service().Preview(null, new[] { pool.GroupId }, active: false);

        Assert.Empty(preview.Capabilities);
        Assert.Equal(0, preview.VisibleHostCount);
        Assert.False(preview.InPool);
    }

    [Fact]
    public void 預覽不寫入任何資料()
    {
        var dept = Group("部門", UserRole.User);
        var user = _users.Upsert(new WebUser { Account = "u" });

        Service().Preview(user.UserId, new[] { dept.GroupId });

        Assert.Empty(_users.Get(user.UserId)!.GroupIds);
    }
}
