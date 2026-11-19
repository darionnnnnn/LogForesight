using LogForesight.Core.Models;
using LogForesight.Core.Service;
using Xunit;

namespace LogForesight.Tests;

/// <summary>第 79 輪批次 G12：本機 Administrators 監控的基準與身分鍵</summary>
public class PermissionMonitorBaselineTests
{
    private static PermissionSnapshot Snap(params (string Name, string Sid)[] members) => new()
    {
        AdministratorsMembers = members.Select(m => m.Name).ToList(),
        AdministratorsMemberSids = members.ToDictionary(m => m.Sid, m => m.Name)
    };

    [Fact]
    public void 讀取失敗那一輪不覆蓋基準_失敗期間新增的成員下次會告警()
    {
        var baseline = Snap(("HOST\\admin", "S-1-5-21-1-500"));
        var failed = new PermissionSnapshot { AdministratorsMembers = null, AdministratorsMemberSids = null };

        var saved = PermissionMonitorService.BaselineToSave(baseline, failed);
        Assert.Equal(baseline.AdministratorsMembers, saved.AdministratorsMembers);

        var recovered = Snap(("HOST\\admin", "S-1-5-21-1-500"), ("HOST\\intruder", "S-1-5-21-1-1099"));
        var (added, removed) = PermissionMonitorService.CompareAdministrators(saved, recovered);
        Assert.Equal(new[] { "HOST\\intruder" }, added);
        Assert.Empty(removed);
    }

    [Fact]
    public void 讀取成功時存本輪結果()
    {
        var current = Snap(("HOST\\admin", "S-1"));
        Assert.Same(current, PermissionMonitorService.BaselineToSave(Snap(), current));
    }

    [Fact]
    public void 同名但不同SID的帳號互換_有告警()
    {
        var before = Snap(("bob", "S-1-5-21-LOCAL-1001"));
        var after = Snap(("bob", "S-1-5-21-DOMAIN-2002"));
        var (added, removed) = PermissionMonitorService.CompareAdministrators(before, after);
        Assert.Single(added);
        Assert.Single(removed);
    }

    [Fact]
    public void 舊版快照沒有SID_第一次以名稱比對_不產生假告警()
    {
        var legacy = new PermissionSnapshot { AdministratorsMembers = new List<string> { "HOST\\admin" }, AdministratorsMemberSids = null };
        var current = Snap(("HOST\\admin", "S-1-5-21-1-500"));
        var (added, removed) = PermissionMonitorService.CompareAdministrators(legacy, current);
        Assert.Empty(added);
        Assert.Empty(removed);
    }

    [Fact]
    public void 舊版快照只有裸名_新版名稱帶來源_過渡比對不產生假告警()
    {
        var legacy = new PermissionSnapshot { AdministratorsMembers = new List<string> { "admin", "Domain Admins" }, AdministratorsMemberSids = null };
        var current = Snap(("HOST\\admin", "S-1-5-21-1-500"), ("CORP\\Domain Admins", "S-1-5-21-2-512"));
        var (added, removed) = PermissionMonitorService.CompareAdministrators(legacy, current);
        Assert.Empty(added);
        Assert.Empty(removed);
    }
}
