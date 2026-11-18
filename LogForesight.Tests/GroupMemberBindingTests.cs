using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>批次加入主機群組成員（網段／關鍵字，docs/archive/HISTORY.md §3）。</summary>
public class GroupMemberBindingTests
{
    private readonly FakeHostStore _hosts = new();
    private readonly FakeHostGroupStore _hostGroups = new();
    private readonly FakeGroupAccessStore _access = new();

    private GroupAdminService Create() => new(
        new FakeUserGroupStore(), _hostGroups, _access,
        new FakeUserStore(), _hosts, new RecordingAuditService(), TestPermissionStamps.Shared);

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

    // ── 目前成員（docs/archive/HISTORY.md Phase 5）─────────────────────────

    [Fact]
    public void 目前成員_只列本群組且算OtherGroupCount()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11", target, other);   // 屬本群組＋另一群組
        AddHost("SRV02", "10.1.2.12", target);           // 只屬本群組
        AddHost("SRV03", "10.1.2.13", other);             // 不屬本群組

        var members = Create().GetMembers(target).Items;

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

        var members = Create().GetMembers(target).Items;

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

    // ── 第 77 輪 F-3：批次寫入與分頁 ───────────────────────────────────────

    [Fact]
    public void 批次加入與移出_整份寫入各只一次且語意同逐台()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        var h1 = AddHost("SRV01", "10.1.2.11", other);
        var h2 = AddHost("SRV02", "10.1.2.12");
        var h3 = AddHost("SRV03", "10.1.2.13", target);          // 已在群組：不加入
        var survivor = AddHost("SRV-NEW", "10.1.2.14");
        var tomb = _hosts.Upsert(new WebHost { HostName = "SRV-OLD", IpAddress = "10.1.2.15", MergedInto = survivor.HostId });

        var before = _hosts.MutateBatchAttempts;
        var added = Create().AddMembers(target, new AddHostGroupMembersRequest
        {
            HostIds = new() { h1.HostId, h2.HostId, h3.HostId, tomb.HostId, 9999 },
            RemoveFromOthers = true
        });

        Assert.Equal(1, _hosts.MutateBatchAttempts - before);
        Assert.Equal(0, _hosts.SetGroupsCalls);
        // removeFromOthers：已在群組的 h3 也走一次（只保留目標群組，與逐台語意相同）；墓碑與查無略過
        Assert.Equal(3, added.AddedCount);
        Assert.Equal(new[] { target }, _hosts.Get(h1.HostId)!.GroupIds);
        Assert.Equal(new[] { target }, _hosts.Get(h2.HostId)!.GroupIds);
        Assert.Empty(_hosts.Get(tomb.HostId)!.GroupIds);

        before = _hosts.MutateBatchAttempts;
        Create().RemoveMembers(target, new[] { h1.HostId, h2.HostId, 9999 });

        Assert.Equal(1, _hosts.MutateBatchAttempts - before);
        Assert.Equal(0, _hosts.SetGroupsCalls);
        Assert.Empty(_hosts.Get(h1.HostId)!.GroupIds);
        Assert.Empty(_hosts.Get(h2.HostId)!.GroupIds);
        Assert.Contains(target, _hosts.Get(h3.HostId)!.GroupIds);
    }

    [Fact]
    public void 預覽分頁_三頁接續無重複且排序正確()
    {
        var other = AddGroup("OO部門主機");
        var target = AddGroup("DB伺服器");
        // 7 台：其中 B、D 已屬其他群組，應排在最前
        foreach (var (name, ip, inOther) in new[]
        {
            ("G", "10.1.2.1", false), ("A", "10.1.2.2", false), ("D", "10.1.2.3", true), ("C", "10.1.2.4", false),
            ("F", "10.1.2.5", false), ("B", "10.1.2.6", true), ("E", "10.1.2.7", false)
        })
            AddHost(name, ip, inOther ? new[] { other } : Array.Empty<long>());
        AddHost("OUT", "10.9.9.9");

        var service = Create();
        var pages = Enumerable.Range(1, 3)
            .Select(p => service.PreviewMembers(target, new HostGroupMemberQueryRequest
                { Pattern = "10.1.2.0/24", Page = p, PageSize = 3 }))
            .ToList();

        Assert.All(pages, p => { Assert.Equal(7, p.MatchCount); Assert.Equal(2, p.InOtherGroupsCount); Assert.Equal(3, p.PageSize); });
        Assert.Equal(new[] { 1, 2, 3 }, pages.Select(p => p.Page));
        var names = pages.SelectMany(p => p.Candidates.Select(c => c.HostName)).ToList();
        Assert.Equal(new[] { "B", "D", "A", "C", "E", "F", "G" }, names);
        Assert.Equal(7, names.Distinct().Count());
        Assert.Single(pages[2].Candidates);
    }

    [Fact]
    public void 全部加入並移出原群組_已在本群組的也移出其他群組_與勾選路徑同規則()
    {
        var target = AddGroup("DB伺服器");
        var other = AddGroup("舊群組");
        var both = AddHost("SRV01", "10.1.3.11", target, other);   // 已在本群組、也在其他群組
        var service = Create();

        var all = service.AddMembers(target, new AddHostGroupMembersRequest { AllMatching = true, Pattern = "10.1.3.0/24", RemoveFromOthers = true });

        Assert.Equal(1, all.AddedCount);
        Assert.Equal(new[] { target }, _hosts.Get(both.HostId)!.GroupIds);

        // 勾選路徑同一條規則
        var picked = AddHost("SRV02", "10.1.4.12", target, other);
        service.AddMembers(target, new AddHostGroupMembersRequest { HostIds = new() { picked.HostId }, RemoveFromOthers = true });
        Assert.Equal(new[] { target }, _hosts.Get(picked.HostId)!.GroupIds);
    }

    [Fact]
    public void 全部加入符合條件_預覽後新增的符合主機也加入且已在群組不重複計()
    {
        var target = AddGroup("DB伺服器");
        AddHost("SRV01", "10.1.2.11");
        AddHost("SRV02", "10.1.2.12");
        AddHost("SRV03", "10.1.2.13", target);   // 已在群組

        var service = Create();
        var preview = service.PreviewMembers(target, new HostGroupMemberQueryRequest { Pattern = "10.1.2.0/24" });
        Assert.Equal(3, preview.MatchCount);
        Assert.Equal(1, preview.AlreadyInTargetCount);

        var late = AddHost("SRV04", "10.1.2.14");   // 預覽之後才新增
        AddHost("SRV-OUT", "10.9.9.9");

        var attemptsBefore = _hosts.MutateBatchAttempts;
        var result = service.AddMembers(target, new AddHostGroupMembersRequest
        {
            AllMatching = true,
            Pattern = "10.1.2.0/24",
            HostIds = new() { 12345 }   // AllMatching 時忽略
        });

        Assert.Equal(3, result.AddedCount);   // SRV01、SRV02、SRV04；SRV03 已在群組不計
        Assert.Contains(target, _hosts.Get(late.HostId)!.GroupIds);
        Assert.Equal(1, _hosts.MutateBatchAttempts - attemptsBefore);
        Assert.Equal(4, _hosts.GetAll().Count(h => h.GroupIds.Contains(target)));
        Assert.Equal(0, service.AddMembers(target, new AddHostGroupMembersRequest { AllMatching = true, Pattern = "10.1.2.0/24" }).AddedCount);
    }

    [Fact]
    public void 目前成員分頁_總數正確依IP排序且第二頁接續()
    {
        var target = AddGroup("DB伺服器");
        AddHost("Z", "10.1.2.100", target);
        AddHost("Y", "10.1.2.9", target);          // 數值排序：9 在 100 之前
        AddHost("NOIP", null!, target);
        AddHost("X", "10.1.1.200", target);
        AddHost("W", "10.1.2.50", target);
        AddHost("OTHER", "10.1.2.1");

        var first = Create().GetMembers(target, 1, 3);
        var second = Create().GetMembers(target, 2, 3);

        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.PageSize);
        Assert.Equal(new[] { "X", "Y", "W" }, first.Items.Select(m => m.HostName));
        Assert.Equal(2, second.Page);
        Assert.Equal(new[] { "Z", "NOIP" }, second.Items.Select(m => m.HostName));
    }
}
