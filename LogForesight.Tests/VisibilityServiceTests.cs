using LogForesight.Web.Auth;
using LogForesight.Web.Models;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 資料可見範圍（docs/WEB-SPEC.md §7.1 第 3 層）——**Phase 1 的核心驗收項目**。
///
/// 這是不可繞過的最後防線：即使 API 忘了掛 [Permission]，查詢仍只回授權範圍內的資料。
/// §12 要求「每個查詢型 Service 至少一條授權過濾測試」，這裡是那條規則的源頭。
/// </summary>
public class VisibilityServiceTests
{
    private readonly FakeUserStore _users = new();
    private readonly FakeUserGroupStore _userGroups = new();
    private readonly FakeGroupAccessStore _access = new();
    private readonly FakeHostStore _hosts = new();

    private VisibilityService Create(ICurrentUser currentUser) =>
        new(currentUser, _users, _userGroups, _access, _hosts);

    /// <summary>建立「OO 部門使用者 → OO 部門主機」的完整授權鏈，並額外建立一台 XX 部門主機</summary>
    private (WebUser user, WebHost ooHost, WebHost xxHost) SetupTwoDepartments()
    {
        var ooGroup = _userGroups.Upsert(new UserGroup { GroupName = "OO部門", Role = UserRole.User });
        var xxGroup = _userGroups.Upsert(new UserGroup { GroupName = "XX部門", Role = UserRole.User });

        var user = _users.Upsert(new WebUser
        {
            Account = "DOMAIN\\wang",
            GroupIds = new List<long> { ooGroup.GroupId }
        });

        var ooHost = _hosts.Upsert(new WebHost { HostName = "SRV-OO-01", GroupIds = new List<long> { 10 } });
        var xxHost = _hosts.Upsert(new WebHost { HostName = "SRV-XX-01", GroupIds = new List<long> { 20 } });

        _access.ReplaceAll(new[]
        {
            new GroupAccess { UserGroupId = ooGroup.GroupId, HostGroupId = 10 },
            new GroupAccess { UserGroupId = xxGroup.GroupId, HostGroupId = 20 }
        });

        return (user, ooHost, xxHost);
    }

    [Fact]
    public void 一般使用者_只看得到被授權群組的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.DoesNotContain(xxHost.HostId, visible);
    }

    [Fact]
    public void 持有ViewAll_看得到全部主機()
    {
        var (_, ooHost, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll));

        var visible = service.GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 跨部門成員_看得到兩個部門的主機()
    {
        var (user, ooHost, xxHost) = SetupTwoDepartments();
        var xxGroup = _userGroups.FindByName("XX部門")!;
        _users.SetGroups(user.UserId, new[] { user.GroupIds[0], xxGroup.GroupId });

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    [Fact]
    public void 未授權任何主機群組_可見範圍為空()
    {
        SetupTwoDepartments();
        var lonely = _users.Upsert(new WebUser { Account = "DOMAIN\\new" });

        Assert.Empty(Create(FakeCurrentUser.ForUser(lonely.UserId)).GetVisibleHostIds());
    }

    /// <summary>停用群組是「暫時收回這批人的權限」的手段，成員資格還在也不該給範圍</summary>
    [Fact]
    public void 群組已停用_不帶來可見範圍()
    {
        var (user, _, _) = SetupTwoDepartments();
        var ooGroup = _userGroups.FindByName("OO部門")!;
        ooGroup.Active = false;
        _userGroups.Upsert(ooGroup);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    [Fact]
    public void 使用者已停用_可見範圍為空()
    {
        var (user, _, _) = SetupTwoDepartments();
        user.Active = false;
        _users.Upsert(user);

        Assert.Empty(Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds());
    }

    /// <summary>serverAdmin 是維護帳號，刻意不看業務資料（最小授權）</summary>
    [Fact]
    public void serverAdmin_可見範圍為空()
    {
        SetupTwoDepartments();

        var service = Create(FakeCurrentUser.ServerAdmin());

        Assert.Empty(service.GetVisibleHostIds());
    }

    [Fact]
    public void 未登入_可見範圍為空()
    {
        SetupTwoDepartments();

        Assert.Empty(Create(FakeCurrentUser.Anonymous()).GetVisibleHostIds());
    }

    /// <summary>
    /// 對沒有權限的人來說，「不存在」與「看不到」必須是同一件事——
    /// 回 403 等於確認「這台主機存在」，可以用來列舉機房裡有哪些主機。
    /// </summary>
    [Fact]
    public void EnsureVisible_未授權主機_拋出找不到而非權限不足()
    {
        var (user, _, xxHost) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        var ex = Assert.Throws<DomainException>(() => service.EnsureVisible(xxHost.HostId));

        Assert.Equal(ApiErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public void EnsureVisible_已授權主機_通過()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        var service = Create(FakeCurrentUser.ForUser(user.UserId));

        service.EnsureVisible(ooHost.HostId);   // 不應拋例外
    }

    /// <summary>停用主機的資料只留在資料庫，可見範圍必須排除它——這是 N-1 的核心行為</summary>
    [Fact]
    public void 主機已停用_不在ViewAll的可見範圍()
    {
        var (_, ooHost, xxHost) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds();

        Assert.DoesNotContain(ooHost.HostId, visible);
        Assert.Contains(xxHost.HostId, visible);
    }

    /// <summary>群組授權路徑同樣要排除停用主機，不能只有 ViewAll 分支生效</summary>
    [Fact]
    public void 主機已停用_不在群組授權的可見範圍()
    {
        var (user, ooHost, _) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.ForUser(user.UserId)).GetVisibleHostIds();

        Assert.DoesNotContain(ooHost.HostId, visible);
    }

    /// <summary>重新啟用後應完全復原——停用是可逆的，不是刪除</summary>
    [Fact]
    public void 主機重新啟用後_恢復可見()
    {
        var (_, ooHost, _) = SetupTwoDepartments();
        ooHost.Active = false;
        _hosts.Upsert(ooHost);
        ooHost.Active = true;
        _hosts.Upsert(ooHost);

        var visible = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHostIds();

        Assert.Contains(ooHost.HostId, visible);
    }

    [Fact]
    public void GetVisibleHosts_依主機名稱排序()
    {
        _hosts.Upsert(new WebHost { HostName = "SRV-Z", GroupIds = new List<long> { 10 } });
        _hosts.Upsert(new WebHost { HostName = "SRV-A", GroupIds = new List<long> { 10 } });

        var hosts = Create(FakeCurrentUser.WithCapabilities(Capability.ViewAll)).GetVisibleHosts();

        Assert.Equal(new[] { "SRV-A", "SRV-Z" }, hosts.Select(h => h.HostName));
    }
}
