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
    private readonly FakeHostGroupStore _groups = new();
    private readonly RecordingAuditService _audit = new();

    private HostAdminService Create() => new(
        _hosts,
        _groups,
        new FakeUserStore(),
        new FakeNetiqServerCatalog("SENTINEL-A"),
        new FakeNetiqHostServiceForAdmin(),
        _audit);

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

    // ── 未回報篩選（docs/archive/HISTORY.md 定案 9：新主機寬限期）───────────

    [Fact]
    public void GetHosts_剛匯入未滿寬限期的主機_不算未回報()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.1", Active = true, CreatedAt = DateTime.Now.AddHours(-1)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetHosts_建立超過寬限期仍未回報_算未回報()
    {
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.2", Active = true, CreatedAt = DateTime.Now.AddHours(-25)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Single(result.Items);
    }

    [Fact]
    public void GetHosts_已回報過的主機_寬限期不適用_沿用原本兩天判定()
    {
        // 剛建立（1 小時前）但曾經回報過、且那次回報已是 3 天前——寬限期只管「從未回報過」的主機
        _hosts.Upsert(new WebHost
        {
            HostName = "10.1.2.3", Active = true,
            CreatedAt = DateTime.Now.AddHours(-1), LastReportAt = DateTime.Now.AddDays(-3)
        });

        var result = Create().GetHosts(new HostSearchRequest { Status = "silent" });

        Assert.Single(result.Items);
    }

    // ── 表格排序（表頭點擊排序，取代原本獨立的排序下拉）─────────────────

    [Fact]
    public void GetHosts_依IP升冪排序()
    {
        _hosts.Upsert(new WebHost { HostName = "C", IpAddress = "10.0.0.30" });
        _hosts.Upsert(new WebHost { HostName = "A", IpAddress = "10.0.0.10" });
        _hosts.Upsert(new WebHost { HostName = "B", IpAddress = "10.0.0.20" });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "ip", Dir = "asc" });

        Assert.Equal(new[] { "A", "B", "C" }, result.Items.Select(h => h.HostName));
    }

    [Fact]
    public void GetHosts_Dir為desc時整體反轉()
    {
        _hosts.Upsert(new WebHost { HostName = "A" });
        _hosts.Upsert(new WebHost { HostName = "B" });
        _hosts.Upsert(new WebHost { HostName = "C" });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "name", Dir = "desc" });

        Assert.Equal(new[] { "C", "B", "A" }, result.Items.Select(h => h.HostName));
    }

    [Fact]
    public void GetHosts_依最近回報排序_null視為最舊()
    {
        _hosts.Upsert(new WebHost { HostName = "無回報" });
        _hosts.Upsert(new WebHost { HostName = "有回報", LastReportAt = DateTime.Now });

        var result = Create().GetHosts(new HostSearchRequest { Sort = "lastReport", Dir = "desc" });

        Assert.Equal(new[] { "有回報", "無回報" }, result.Items.Select(h => h.HostName));
    }

    // ── 批次改群組（docs/archive/FEEDBACK-5-PLAN.md §8）─────────────────────────────

    [Fact]
    public void SetGroupsBatch_加入模式_與既有群組取聯集()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var deptB = _groups.Upsert(new HostGroup { GroupName = "部門B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", GroupIds = new List<long> { deptA.GroupId } });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        var result = Create().SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptB.GroupId }, "add");

        Assert.Equal(2, result.UpdatedCount);
        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { deptA.GroupId, deptB.GroupId }, _hosts.Get(a.HostId)!.GroupIds);
        Assert.Equal(new[] { deptB.GroupId }, _hosts.Get(b.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_取代模式_改為僅勾選的群組()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var deptB = _groups.Upsert(new HostGroup { GroupName = "部門B" });
        var a = _hosts.Upsert(new WebHost { HostName = "A", GroupIds = new List<long> { deptA.GroupId } });

        Create().SetGroupsBatch(new[] { a.HostId }, new[] { deptB.GroupId }, "replace");

        Assert.Equal(new[] { deptB.GroupId }, _hosts.Get(a.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_已併入其他主機的主機_略過並回報()
    {
        var service = Create();
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });
        service.MergeHost(a.HostId, b.HostId);

        var result = service.SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptA.GroupId }, "add");

        Assert.Equal(1, result.UpdatedCount);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("A", skipped.HostName);
        Assert.Equal(new[] { deptA.GroupId }, _hosts.Get(b.HostId)!.GroupIds);
    }

    [Fact]
    public void SetGroupsBatch_群組不存在_擋下()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(new[] { a.HostId }, new long[] { 999 }, "add"));
    }

    [Fact]
    public void SetGroupsBatch_模式不合法_擋下()
    {
        var a = _hosts.Upsert(new WebHost { HostName = "A" });

        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(new[] { a.HostId }, Array.Empty<long>(), "remove"));
    }

    [Fact]
    public void SetGroupsBatch_未勾選任何主機_擋下()
    {
        Assert.Throws<DomainException>(() => Create().SetGroupsBatch(Array.Empty<long>(), Array.Empty<long>(), "add"));
    }

    [Fact]
    public void SetGroupsBatch_寫入一筆彙總稽核紀錄()
    {
        var deptA = _groups.Upsert(new HostGroup { GroupName = "部門A" });
        var a = _hosts.Upsert(new WebHost { HostName = "A" });
        var b = _hosts.Upsert(new WebHost { HostName = "B" });

        Create().SetGroupsBatch(new[] { a.HostId, b.HostId }, new[] { deptA.GroupId }, "add");

        var entry = Assert.Single(_audit.Entries);
        Assert.Contains("部門A", entry.Summary);
        Assert.Contains("2 台", entry.Summary);
    }
}

// FakeNetiqHostServiceForAdmin／FakeNetiqServerCatalog 已搬到 TestDoubles\NetiqFakes.cs。
