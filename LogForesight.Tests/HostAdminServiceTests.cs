using LogForesight.Web.Models;
using LogForesight.Web.Models.Dto;
using LogForesight.Web.Services;
using Xunit;

namespace LogForesight.Tests;

/// <summary>
/// 主機維護的輸入驗證與合併守則（docs/WEB-SPEC.md §9.8）。
/// </summary>
public class HostAdminServiceTests
{
    private readonly FakeHostStore _hosts = new();

    private HostAdminService Create() => new(
        _hosts,
        new FakeHostGroupStore(),
        new FakeUserStore(),
        new NetiqHostServiceTests.FakeNetiqServerCatalog("SENTINEL-A"),
        new FakeNetiqHostServiceForAdmin(),
        new FakeAuditService());

    // ── 輸入驗證 ─────────────────────────────────────────────────────────────
    //
    // 這一條路徑與 NetiqHostService 寫的是同一份資料。驗證只掛在其中一條的話，
    // 從編輯表單就能繞過去存進不合格的值——而不合格的 IP／Sentinel 的後果是
    // 這台主機永遠查無資料，且完全沒有跡象。

    [Fact]
    public void SaveHost_IP格式不合法_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", IpAddress = "10.1" }));

        Assert.Contains("10.1", ex.Message);
    }

    [Fact]
    public void SaveHost_Sentinel不在名單中_擋下()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", NetiqServer = "SENTINEL-X" }));

        Assert.Contains("SENTINEL-A", ex.Message);
    }

    [Fact]
    public void SaveHost_IP與Sentinel皆留空_允許()
    {
        var result = Create().SaveHost(new SaveHostRequest { HostName = "SRV-01", RoleDesc = "檔案伺服器" });

        Assert.Equal("SRV-01", result.HostName);
        Assert.Null(result.IpAddress);
        Assert.Null(result.NetiqServer);
    }

    // ── 合併與解除 ───────────────────────────────────────────────────────────

    [Fact]
    public void MergeHost_來源已併入其他主機_擋下()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var c = _hosts.Upsert(new WebHost { HostName = "C" });
        service.MergeHost(a.HostId, b.HostId);

        Assert.Throws<DomainException>(() => service.MergeHost(a.HostId, c.HostId));
    }

    /// <summary>
    /// 目標本身是墓碑會形成 A→B→C 的鏈。查詢的別名展開認得整條鏈（歷史不會掉），
    /// 但鏈對使用者是純粹的困惑——併入一台已停用的主機，畫面上看不出資料最後去了哪。
    /// </summary>
    [Fact]
    public void MergeHost_目標本身是墓碑_擋下並指向最終主機()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        var c = _hosts.Upsert(new WebHost { HostName = "C" });
        service.MergeHost(b.HostId, c.HostId);

        var ex = Assert.Throws<DomainException>(() => service.MergeHost(a.HostId, b.HostId));

        Assert.Contains("最終", ex.Message);
    }

    [Fact]
    public void MergeHost_來源與目標相同_擋下()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().MergeHost(host.HostId, host.HostId));
    }

    [Fact]
    public void UnmergeHost_恢復啟用並清除墓碑標記()
    {
        var service = Create();
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        service.MergeHost(a.HostId, b.HostId);

        service.UnmergeHost(a.HostId);

        var after = _hosts.Get(a.HostId)!;
        Assert.Null(after.MergedInto);
        Assert.True(after.Active);
    }

    [Fact]
    public void UnmergeHost_未曾合併_擋下()
    {
        var host = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().UnmergeHost(host.HostId));
    }
}

/// <summary>HostAdminService 只需要 GetOverview()（IP 衝突偵測，§5.4 D-4 的 conflict 篩選）——
/// 這裡的測試不涉及 conflict 狀態篩選，回空清單即可</summary>
internal class FakeNetiqHostServiceForAdmin : INetiqHostService
{
    public NetiqOverviewDto GetOverview() => new();
    public HostDto AddHost(AddNetiqHostRequest request) => throw new NotSupportedException();
    public BulkAddResultDto BulkAddHosts(BulkAddNetiqHostsRequest request) => throw new NotSupportedException();
    public HostDto SetActive(long hostId, bool active) => throw new NotSupportedException();
}
