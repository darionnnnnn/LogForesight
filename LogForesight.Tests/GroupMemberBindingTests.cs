using LogForesight;
using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>批次加入主機群組成員（網段／關鍵字，docs/SCALE-2000-PLAN.md §3）。</summary>
public class GroupMemberBindingTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeGroupAccessStore _access = new();

    private GroupAdminService Create() => new(
        new FakeUserGroupStore(), _hostGroups, _access,
        new FakeUserStore(), _hosts, new FakeAuditService());

    private long AddGroup(string name) => _hostGroups.Upsert(new HostGroup { GroupName = name }).GroupId;

    private WebHost AddHost(string name, string ip, params long[] groups) =>
        _hosts.Upsert(new WebHost { HostName = name, IpAddress = ip, GroupIds = groups.ToList() });

    [Fact]
    public void 網段預覽_命中網段內主機()
    {
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11");
        AddHost("SRV02", "10.1.2.99");
        AddHost("SRV03", "10.9.9.9");   // 不在網段內

        var preview = Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.0/24" });

        Assert.Equal(2, preview.MatchCount);
        Assert.DoesNotContain(preview.Candidates, c => c.HostName == "SRV03");
    }

    [Fact]
    public void 已屬其他群組_顯性通知()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11", other);

        var preview = Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.0/24" });

        Assert.Equal(1, preview.InOtherGroupsCount);
        var c = preview.Candidates[0];
        Assert.True(c.InOtherGroups);
        Assert.Contains("OO部門主機", c.CurrentGroups);
        Assert.False(c.AlreadyInTarget);
    }

    [Fact]
    public void 已在目標群組_標記AlreadyInTarget不重複列現有群組()
    {
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11", target);

        var c = Create()
            .PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.0/24" })
            .Candidates[0];

        Assert.True(c.AlreadyInTarget);
        Assert.False(c.InOtherGroups);           // 目標群組本身不算「其他群組」
        Assert.Empty(c.CurrentGroups);
    }

    [Fact]
    public void 關鍵字預覽_比對主機名與IP()
    {
        var target = AddGroup("DB伺服器");
        AddHost("SRV-DB-01", "10.1.2.11");
        AddHost("SRV-WEB-01", "10.1.2.12");

        var byName = Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Query = "DB" });
        Assert.Equal(1, byName.MatchCount);

        var byIp = Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Query = "10.1.2.12" });
        Assert.Equal(1, byIp.MatchCount);
        Assert.Equal("SRV-WEB-01", byIp.Candidates[0].HostName);
    }

    [Fact]
    public void 加入成員_追加不動原群組()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        var host = AddHost("SRV01", "10.1.2.11", other);

        Create().AddMembers(target, new AddHostGroupMembersRequest { HostIds = new() { host.HostId } });

        var groups = _hosts.Get(host.HostId)!.GroupIds;
        Assert.Contains(other, groups);
        Assert.Contains(target, groups);
    }

    [Fact]
    public void 加入成員_removeFromOthers只保留目標群組()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        var host = AddHost("SRV01", "10.1.2.11", other);

        Create().AddMembers(target, new AddHostGroupMembersRequest
        {
            HostIds = new() { host.HostId },
            RemoveFromOthers = true
        });

        Assert.Equal(new[] { target }, _hosts.Get(host.HostId)!.GroupIds);
    }

    [Fact]
    public void 網段格式非法_擲驗證例外()
    {
        var target = AddGroup("DB伺服器");
        Assert.Throws<DomainException>(() =>
            Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.999" }));
    }

    [Fact]
    public void 墓碑主機_不列入命中()
    {
        var target = AddGroup("DB伺服器");
        var survivor = AddHost("SRV-NEW", "10.1.2.11");
        _hosts.Upsert(new WebHost { HostName = "SRV-OLD", IpAddress = "10.1.2.12", MergedInto = survivor.HostId });

        var preview = Create().PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.0/24" });

        Assert.Equal(1, preview.MatchCount);
        Assert.Equal("SRV-NEW", preview.Candidates[0].HostName);
    }

    // ── 目前成員（docs/NETIQ-WEB-CONFIG-PLAN.md Phase 5）─────────────────────────

    [Fact]
    public void 目前成員_只列本群組且算OtherGroupCount()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11", target, other);   // 屬本群組＋另一群組
        AddHost("SRV02", "10.1.2.12", target);           // 只屬本群組
        AddHost("SRV03", "10.1.2.13", other);             // 不屬本群組

        var members = Create().GetMembers(target);

        Assert.Equal(2, members.Count);
        Assert.Equal(1, members.First(m => m.HostName == "SRV01").OtherGroupCount);
        Assert.Equal(0, members.First(m => m.HostName == "SRV02").OtherGroupCount);
    }

    [Fact]
    public void 目前成員_墓碑主機不列入()
    {
        var target = AddGroup("DB伺服器");
        var survivor = AddHost("SRV-NEW", "10.1.2.11", target);
        var tombstoned = _hosts.Upsert(new WebHost
        {
            HostName = "SRV-OLD", IpAddress = "10.1.2.12", GroupIds = new List<long> { target }
        });
        _hosts.Merge(tombstoned.HostId, survivor.HostId);

        var members = Create().GetMembers(target);

        Assert.Single(members);
        Assert.Equal("SRV-NEW", members[0].HostName);
    }

    [Fact]
    public void 移出成員_只動目標群組不影響其他群組()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        var host = AddHost("SRV01", "10.1.2.11", target, other);

        Create().RemoveMembers(target, new[] { host.HostId });

        var groups = _hosts.Get(host.HostId)!.GroupIds;
        Assert.DoesNotContain(target, groups);
        Assert.Contains(other, groups);
    }

    [Fact]
    public void 移出成員_移出唯一群組後變未分組()
    {
        var target = AddGroup("DB伺服器");
        var host = AddHost("SRV01", "10.1.2.11", target);

        Create().RemoveMembers(target, new[] { host.HostId });

        Assert.Empty(_hosts.Get(host.HostId)!.GroupIds);
    }

    [Fact]
    public void 移出成員_不在群組內的主機安靜略過()
    {
        var target = AddGroup("DB伺服器");
        var other = AddGroup("OO部門主機");
        var host = AddHost("SRV01", "10.1.2.11", other);

        Create().RemoveMembers(target, new[] { host.HostId });

        Assert.Equal(new[] { other }, _hosts.Get(host.HostId)!.GroupIds);
    }
}
